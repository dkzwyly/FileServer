using FileServer.Models;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

namespace FileServer.Services
{
    public interface IFileService
    {
        Task<FileListResponse> GetFileListAsync(string relativePath);
        Task<(Stream Stream, string ContentType, string FileName)> DownloadFileAsync(string filePath);
        Task<FileInfoModel> GetFileInfoAsync(string filePath);
        Task<UploadResponse> UploadFilesAsync(string targetPath, IFormFileCollection files);
        Task<bool> FileExistsAsync(string filePath);
        Task<bool> DirectoryExistsAsync(string directoryPath);
        string FormatFileSize(long bytes);
        string GetMimeType(string extension);
        string GetRootPath();

        Task<bool> DeleteFileAsync(string filePath);
        Task<(Stream Stream, string ContentType, string FileName)> GetThumbnailAsync(string filePath);
    }

    public class FileService : IFileService
    {
        private readonly IThumbnailService _thumbnailService;
        private readonly string _rootPath;
        private readonly ILogger<FileService> _logger;
        private readonly IFileConflictService _conflictService;

        // ---------- 视频缓存相关 ----------
        private readonly ConcurrentDictionary<string, VideoCacheEntry> _videoCache = new();
        private readonly string _videoCacheFilePath;
        private readonly object _videoCacheLock = new();
        private bool _videoCacheLoaded = false;

        private class VideoCacheEntry
        {
            public FileListResponse Response { get; set; } = null!;
            public DateTime LastWriteTimeUtc { get; set; }
        }

        public FileService(IConfiguration configuration,
                   ILogger<FileService> logger,
                   ThumbnailService thumbnailService,
                   IFileConflictService conflictService)
        {
            _rootPath = configuration["FileServerConfig:RootPath"]
                ?? throw new InvalidOperationException("配置缺少 FileServerConfig:RootPath");
            _rootPath = Path.GetFullPath(_rootPath);

            _logger = logger;
            _thumbnailService = thumbnailService;
            _conflictService = conflictService;

            if (!Directory.Exists(_rootPath))
            {
                Directory.CreateDirectory(_rootPath);
                _logger.LogInformation("创建根目录: {RootPath}", _rootPath);
            }

            // 初始化视频缓存文件路径（仍保留，但不强制依赖磁盘文件）
            var cacheDir = configuration["FileServerConfig:MetadataDirectory"] ?? "系统文件";
            var fullCacheDir = Path.Combine(_rootPath, cacheDir);
            if (!Directory.Exists(fullCacheDir))
                Directory.CreateDirectory(fullCacheDir);
            _videoCacheFilePath = Path.Combine(fullCacheDir, "video_cache.json");

            // ===== 新增：启动时同步预加载视频目录缓存 =====
            try
            {
                PreloadVideoCache().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "启动预加载视频缓存失败，将在首次请求时按需扫描");
            }
        }

        // ==================== 视频缓存方法 ====================
        /// <summary>
        /// 启动时预加载 video 目录下所有子目录的文件列表缓存
        /// </summary>
        private async Task PreloadVideoCache()
        {
            var videoRoot = Path.Combine(_rootPath, "data", "影视");
            if (!Directory.Exists(videoRoot))
            {
                _logger.LogWarning("视频根目录不存在，跳过预加载: {Path}", videoRoot);
                return;
            }

            // 获取所有子目录（包括深层目录）
            var directories = Directory.GetDirectories(videoRoot, "*", SearchOption.AllDirectories);

            _logger.LogInformation("开始预加载视频目录缓存，共 {Count} 个目录", directories.Length);

            int loadedCount = 0;
            foreach (var dir in directories)
            {
                try
                {
                    var relativePath = Path.GetRelativePath(_rootPath, dir).Replace('\\', '/');
                    var response = await BuildFileListResponse(relativePath);

                    _videoCache[relativePath] = new VideoCacheEntry
                    {
                        Response = response,
                        LastWriteTimeUtc = Directory.GetLastWriteTimeUtc(dir)
                    };

                    loadedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "预加载目录失败，跳过: {Dir}", dir);
                }
            }

            _logger.LogInformation("预加载完成，成功缓存 {Loaded}/{Total} 个视频目录",
                loadedCount, directories.Length);
        }

        private void LoadVideoCache()
        {
            try
            {
                if (File.Exists(_videoCacheFilePath))
                {
                    var json = File.ReadAllText(_videoCacheFilePath);
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        IncludeFields = true
                    };
                    var cacheData = JsonSerializer.Deserialize<Dictionary<string, VideoCacheEntry>>(json, options);
                    if (cacheData != null)
                    {
                        foreach (var kv in cacheData)
                            _videoCache.TryAdd(kv.Key, kv.Value);
                        _logger.LogInformation("从磁盘加载视频缓存，共 {Count} 个条目", _videoCache.Count);
                    }
                }
                _videoCacheLoaded = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载视频缓存失败，将在首次访问时重新扫描");
                if (File.Exists(_videoCacheFilePath))
                {
                    try { File.Delete(_videoCacheFilePath); } catch { }
                }
                _videoCacheLoaded = false;
            }
        }

        private void SaveVideoCache()
        {
            try
            {
                var cacheData = _videoCache.ToDictionary(
                    kv => kv.Key,
                    kv => new
                    {
                        // 这里需要 CachedResponse 属性，如果你未修改 VideoCacheEntry，
                        // 可以暂时注释掉 SaveVideoCache 的调用，或者只序列化部分字段
                        kv.Value.Response,
                        kv.Value.LastWriteTimeUtc
                    }
                );

                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(cacheData, options);

                var tempFile = _videoCacheFilePath + ".tmp";
                File.WriteAllText(tempFile, json);
                File.Move(tempFile, _videoCacheFilePath, true);

                _logger.LogInformation("✅ 视频缓存已保存");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 保存视频缓存失败（不影响正常运行）");
                // 不要 throw
            }
        }

        // ==================== 公共方法 ====================

        public async Task<FileListResponse> GetFileListAsync(string relativePath)
        {
            var normalized = relativePath?.Replace('\\', '/').Trim('/') ?? "";
            _logger.LogInformation("GetFileListAsync 原始路径: {Raw}, 规范化后: {Normalized}", relativePath, normalized);

            // 视频目录专用缓存逻辑
            if (normalized.StartsWith("data/影视", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("★★★ 进入视频缓存分支 ★★★ 路径: {Path}", normalized);

                // 1. 尝试从内存缓存直接返回
                if (_videoCache.TryGetValue(normalized, out var cachedEntry))
                {
                    _logger.LogInformation("📀 从启动缓存直接返回（无IO）: {Path}", normalized);
                    return cachedEntry.Response;
                }

                // 2. 缓存未命中（可能是启动后新增的目录），执行扫描并加入缓存
                _logger.LogInformation("⚠️ 缓存未命中，执行磁盘扫描并加入缓存: {Path}", normalized);
                var physicalPath = Path.Combine(_rootPath, normalized);
                if (!Directory.Exists(physicalPath))
                {
                    _logger.LogWarning("视频目录不存在: {Path}", physicalPath);
                    throw new DirectoryNotFoundException($"目录不存在: {normalized}");
                }

                var response = await BuildFileListResponse(normalized);

                var newEntry = new VideoCacheEntry
                {
                    Response = response,
                    LastWriteTimeUtc = Directory.GetLastWriteTimeUtc(physicalPath)
                };
                _videoCache[normalized] = newEntry;

                // 可选：将新增的缓存持久化到磁盘（如果你仍想保留磁盘缓存）
                try
                {
                    SaveVideoCache();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "持久化新缓存失败，但不影响当前请求");
                }

                return response;
            }
            else
            {
                // 非视频目录，保持原有实时扫描逻辑
                _logger.LogInformation("普通路径（非视频目录），实时扫描: {Path}", normalized);
                return await BuildFileListResponse(normalized);
            }
        }

        /// <summary>
        /// 构建文件列表响应（不缓存，实时扫描）
        /// </summary>
        private async Task<FileListResponse> BuildFileListResponse(string normalizedPath)
        {
            var physicalPath = Path.Combine(_rootPath, normalizedPath);

            if (!physicalPath.StartsWith(_rootPath))
                physicalPath = _rootPath;

            if (!Directory.Exists(physicalPath))
                throw new DirectoryNotFoundException($"目录不存在: {normalizedPath}");

            var response = new FileListResponse
            {
                CurrentPath = normalizedPath,
                ParentPath = GetParentPath(normalizedPath)
            };

            try
            {
                var directories = await Task.Run(() => Directory.GetDirectories(physicalPath));
                var files = await Task.Run(() => Directory.GetFiles(physicalPath));

                foreach (var dir in directories)
                {
                    try
                    {
                        var dirInfo = new DirectoryInfo(dir);
                        response.Directories.Add(new DirectoryInfoModel
                        {
                            Name = dirInfo.Name,
                            Path = Path.Combine(normalizedPath, dirInfo.Name).Replace("\\", "/")
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "处理目录失败: {Directory}", dir);
                    }
                }

                foreach (var file in files)
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        var extension = Path.GetExtension(file).ToLowerInvariant();
                        var relativeFilePath = Path.Combine(normalizedPath, fileInfo.Name).Replace("\\", "/");

                        var fileModel = new FileInfoModel
                        {
                            Name = fileInfo.Name,
                            Path = relativeFilePath,
                            Size = fileInfo.Length,
                            SizeFormatted = FormatFileSize(fileInfo.Length),
                            Extension = extension,
                            LastModified = fileInfo.LastWriteTime,
                            IsVideo = IsVideoFile(extension),
                            IsAudio = IsAudioFile(extension),
                            MimeType = GetMimeType(extension),
                            Encoding = IsTextFile(extension) ? "utf-8" : "",
                            HasThumbnail = false
                        };

                        if (IsImageFile(extension))
                        {
                            fileModel.HasThumbnail = await _thumbnailService.ThumbnailExistsAsync(relativeFilePath);
                        }

                        response.Files.Add(fileModel);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "处理文件失败: {File}", file);
                    }
                }

                _logger.LogInformation("返回目录列表: {Path} - {DirCount} 目录, {FileCount} 文件",
                    normalizedPath, response.Directories.Count, response.Files.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "读取文件列表失败: {Path}", normalizedPath);
                throw;
            }

            return response;
        }

        // ==================== 文件信息 ====================

        public async Task<FileInfoModel> GetFileInfoAsync(string filePath)
        {
            var physicalPath = Path.Combine(_rootPath, filePath);

            if (!File.Exists(physicalPath))
            {
                throw new FileNotFoundException($"文件不存在: {filePath}");
            }

            return await Task.Run(() =>
            {
                var fileInfo = new FileInfo(physicalPath);
                var extension = Path.GetExtension(physicalPath).ToLowerInvariant();

                return new FileInfoModel
                {
                    Name = fileInfo.Name,
                    Path = filePath,
                    Size = fileInfo.Length,
                    SizeFormatted = FormatFileSize(fileInfo.Length),
                    Extension = extension,
                    LastModified = fileInfo.LastWriteTime,
                    IsVideo = IsVideoFile(extension),
                    IsAudio = IsAudioFile(extension),
                    MimeType = GetMimeType(extension),
                    Encoding = IsTextFile(extension) ? "utf-8" : ""
                };
            });
        }

        // ==================== 下载文件 ====================

        public async Task<(Stream Stream, string ContentType, string FileName)> DownloadFileAsync(string filePath)
        {
            var physicalPath = Path.Combine(_rootPath, filePath);

            if (!File.Exists(physicalPath))
            {
                throw new FileNotFoundException($"文件不存在: {filePath}");
            }

            var fileInfo = new FileInfo(physicalPath);
            var extension = Path.GetExtension(physicalPath).ToLowerInvariant();
            var contentType = GetMimeType(extension);

            if (extension == ".wmv")
            {
                contentType = "video/x-ms-wmv";
            }

            var stream = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read);

            _logger.LogInformation("文件下载: {FileName} (大小: {Size})", fileInfo.Name, FormatFileSize(fileInfo.Length));

            return await Task.FromResult((stream, contentType, fileInfo.Name));
        }

        // ==================== 上传文件 ====================

        public async Task<UploadResponse> UploadFilesAsync(string targetPath, IFormFileCollection files)
        {
            var uploadPath = string.IsNullOrEmpty(targetPath)
                ? _rootPath
                : Path.Combine(_rootPath, targetPath);

            if (!Directory.Exists(uploadPath))
            {
                Directory.CreateDirectory(uploadPath);
                _logger.LogInformation("创建上传目录: {UploadPath}", uploadPath);
            }

            var uploadedFiles = new List<UploadedFileInfo>();
            var resolvedConflicts = new List<ConflictResolutionInfo>();
            long totalSize = 0;
            int renameCount = 0;
            int failedCount = 0;

            _logger.LogInformation("开始上传文件，目标路径: {TargetPath}", targetPath);
            _logger.LogInformation("接收到的文件数量: {FileCount}", files.Count);

            foreach (var file in files)
            {
                if (file.Length == 0)
                {
                    _logger.LogWarning("跳过空文件: {FileName}", file.FileName);
                    failedCount++;
                    continue;
                }

                _logger.LogInformation("处理上传文件 - 原始文件名: {OriginalFileName}, " +
                                       "ContentType: {ContentType}, " +
                                       "大小: {Size}, " +
                                       "字段名: {FieldName}",
                    file.FileName, file.ContentType, file.Length, file.Name);

                var originalFileName = file.FileName;
                var fileName = MakeValidFileName(file.FileName);

                // 使用冲突服务获取唯一文件名
                var originalFileNameForConflict = fileName;
                fileName = await _conflictService.GenerateUniqueFileNameAsync(uploadPath, fileName);

                var filePath = Path.Combine(uploadPath, fileName);
                var relativeFilePath = string.IsNullOrEmpty(targetPath)
                    ? fileName
                    : Path.Combine(targetPath, fileName).Replace("\\", "/");

                // 记录文件名变化
                if (originalFileName != fileName)
                {
                    var reason = originalFileName != originalFileNameForConflict
                        ? "非法字符处理"
                        : "重名冲突";

                    _logger.LogInformation("文件名处理: '{Original}' -> '{New}' (原因: {Reason})",
                        originalFileName, fileName, reason);

                    if (reason == "重名冲突")
                    {
                        renameCount++;
                        resolvedConflicts.Add(new ConflictResolutionInfo
                        {
                            OriginalName = originalFileName,
                            FinalName = fileName,
                            Reason = reason,
                            Timestamp = DateTime.UtcNow,
                            ResolutionStrategy = "AddCounter"
                        });
                    }
                }

                try
                {
                    // 保存文件
                    using (var stream = new FileStream(filePath, FileMode.Create))
                    {
                        await file.CopyToAsync(stream);
                    }

                    // 验证文件内容
                    var fileInfo = new FileInfo(filePath);
                    if (!fileInfo.Exists || fileInfo.Length == 0)
                    {
                        _logger.LogError("文件保存失败或大小为0: {FilePath}", filePath);
                        failedCount++;
                        continue;
                    }

                    // 验证文件格式
                    var extension = Path.GetExtension(fileName).ToLowerInvariant();
                    await ValidateFileFormat(filePath, extension, originalFileName);

                    totalSize += fileInfo.Length;

                    uploadedFiles.Add(new UploadedFileInfo
                    {
                        OriginalName = originalFileName,
                        SavedName = fileName,
                        Path = relativeFilePath,
                        Size = fileInfo.Length,
                        WasRenamed = originalFileName != fileName,
                        RenameReason = originalFileName != fileName ?
                            (originalFileName != originalFileNameForConflict ?
                                "非法字符处理" : "重名冲突") : ""
                    });

                    _logger.LogInformation("文件上传成功: {FileName} -> {FilePath} (大小: {Size})",
                        fileName, filePath, FormatFileSize(fileInfo.Length));

                    // 如果是图片文件，生成缩略图
                    if (IsImageFile(extension))
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _thumbnailService.GenerateThumbnailAsync(relativeFilePath);
                                _logger.LogDebug("缩略图生成成功: {FileName}", fileName);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "生成缩略图失败: {FileName}", fileName);
                            }
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "文件保存失败: {FileName}", fileName);
                    failedCount++;

                    uploadedFiles.Add(new UploadedFileInfo
                    {
                        OriginalName = originalFileName,
                        SavedName = fileName,
                        Path = relativeFilePath,
                        Size = file.Length,
                        WasRenamed = false,
                        RenameReason = $"上传失败: {ex.Message}"
                    });
                }
            }

            if (uploadedFiles.Count > 0)
            {
                _logger.LogInformation("上传完成: {FileCount} 个文件成功，{FailedCount} 个失败，" +
                                      "总大小: {TotalSize}，重命名: {RenameCount}",
                    uploadedFiles.Count - failedCount, failedCount,
                    FormatFileSize(totalSize), renameCount);
            }
            else
            {
                _logger.LogWarning("没有文件成功上传");
            }

            var response = new UploadResponse
            {
                Success = uploadedFiles.Count - failedCount > 0,
                Message = uploadedFiles.Count - failedCount > 0
                    ? $"成功上传 {uploadedFiles.Count - failedCount} 个文件"
                    : "上传失败",
                TotalSize = totalSize,
                UploadedFiles = uploadedFiles,
                ResolvedConflicts = resolvedConflicts,
                TotalFiles = files.Count,
                SuccessfulUploads = uploadedFiles.Count - failedCount,
                ConflictsResolved = renameCount,
                FailedUploads = failedCount
            };

            // 向后兼容
            response.Files = uploadedFiles
                .Where(f => string.IsNullOrEmpty(f.RenameReason) || !f.RenameReason.StartsWith("上传失败"))
                .Select(f => f.SavedName)
                .ToList();

            response.CalculateFormattedSize();

            return response;
        }

        // ==================== 文件验证辅助 ====================

        private async Task ValidateFileFormat(string filePath, string extension, string originalFileName)
        {
            try
            {
                if (extension == ".gif")
                {
                    await ValidateGifFile(filePath, originalFileName);
                }
                else if (IsImageFile(extension))
                {
                    await ValidateImageFile(filePath, originalFileName, extension);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "文件格式验证失败: {FileName}", originalFileName);
            }
        }

        private async Task ValidateGifFile(string filePath, string originalFileName)
        {
            using var fileStream = File.OpenRead(filePath);
            var buffer = new byte[6];
            await fileStream.ReadAsync(buffer, 0, 6);

            var header = Encoding.ASCII.GetString(buffer);
            if (header == "GIF87a" || header == "GIF89a")
            {
                _logger.LogInformation("GIF 文件验证成功: {FileName}", originalFileName);
            }
            else
            {
                _logger.LogWarning("GIF 文件可能已损坏或格式不正确: {FileName} (头部: {Header})",
                    originalFileName, header);
            }
        }

        private async Task ValidateImageFile(string filePath, string originalFileName, string extension)
        {
            try
            {
                using var fileStream = File.OpenRead(filePath);
                var buffer = new byte[8];
                await fileStream.ReadAsync(buffer, 0, 8);

                var hex = BitConverter.ToString(buffer).Replace("-", "");
                _logger.LogDebug("图像文件 {FileName} 头部: {Header}", originalFileName, hex);
            }
            catch
            {
                // 忽略验证错误
            }
        }

        // ==================== 删除文件 ====================

        public async Task<bool> DeleteFileAsync(string filePath)
        {
            try
            {
                var physicalPath = Path.Combine(_rootPath, filePath);
                if (!File.Exists(physicalPath))
                {
                    return false;
                }

                File.Delete(physicalPath);

                var extension = Path.GetExtension(filePath).ToLowerInvariant();
                if (IsImageFile(extension))
                {
                    await _thumbnailService.DeleteThumbnailAsync(filePath);
                }

                _logger.LogInformation("删除文件成功: {FilePath}", filePath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除文件失败: {FilePath}", filePath);
                return false;
            }
        }

        // ==================== 缩略图 ====================

        public async Task<(Stream Stream, string ContentType, string FileName)> GetThumbnailAsync(string filePath)
        {
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            if (!IsImageFile(extension))
            {
                throw new InvalidOperationException("非图片文件不支持缩略图");
            }

            var stream = await _thumbnailService.GetThumbnailStreamAsync(filePath);
            return (stream, "image/jpeg", $"{Path.GetFileNameWithoutExtension(filePath)}_thumb.jpg");
        }

        // ==================== 其他基础方法 ====================

        public Task<bool> FileExistsAsync(string filePath)
        {
            var physicalPath = Path.Combine(_rootPath, filePath);
            return Task.FromResult(File.Exists(physicalPath));
        }

        public Task<bool> DirectoryExistsAsync(string directoryPath)
        {
            var physicalPath = Path.Combine(_rootPath, directoryPath);
            return Task.FromResult(Directory.Exists(physicalPath));
        }

        public string FormatFileSize(long bytes)
        {
            if (bytes == 0) return "0 B";

            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            double len = bytes;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        public string GetMimeType(string extension)
        {
            var mimeTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { ".txt", "text/plain" },
                { ".html", "text/html" },
                { ".htm", "text/html" },
                { ".css", "text/css" },
                { ".js", "application/javascript" },
                { ".json", "application/json" },
                { ".xml", "application/xml" },
                { ".jpg", "image/jpeg" },
                { ".jpeg", "image/jpeg" },
                { ".png", "image/png" },
                { ".gif", "image/gif" },
                { ".bmp", "image/bmp" },
                { ".webp", "image/webp" },
                { ".mp3", "audio/mpeg" },
                { ".wav", "audio/wav" },
                { ".flac", "audio/flac" },
                { ".aac", "audio/aac" },
                { ".ogg", "audio/ogg" },
                { ".mp4", "video/mp4" },
                { ".avi", "video/x-msvideo" },
                { ".mov", "video/quicktime" },
                { ".mkv", "video/x-matroska" },
                { ".wmv", "video/x-ms-wmv" },
                { ".flv", "video/x-flv" },
                { ".webm", "video/webm" },
                { ".m4v", "video/mp4" },
                { ".pdf", "application/pdf" },
                { ".zip", "application/zip" },
                { ".rar", "application/x-rar-compressed" },
                { ".7z", "application/x-7z-compressed" },
                { ".doc", "application/msword" },
                { ".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
                { ".xls", "application/vnd.ms-excel" },
                { ".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" },
                { ".ppt", "application/vnd.ms-powerpoint" },
                { ".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation" },
                { ".md", "text/markdown" },
                { ".csv", "text/csv" },
                { ".yml", "text/yaml" },
                { ".yaml", "text/yaml" },
                { ".ini", "text/plain" },
                { ".config", "text/xml" },
                { ".sql", "application/sql" }
            };

            return mimeTypes.TryGetValue(extension, out string mimeType)
                ? mimeType
                : "application/octet-stream";
        }

        public string GetRootPath()
        {
            return _rootPath;
        }

        // ==================== 私有辅助 ====================

        private string GetParentPath(string currentPath)
        {
            if (string.IsNullOrEmpty(currentPath))
                return string.Empty;

            var parts = currentPath.Split('/');
            if (parts.Length <= 1)
                return string.Empty;

            return string.Join("/", parts, 0, parts.Length - 1);
        }

        private bool IsVideoFile(string extension)
        {
            var videoExtensions = new[] { ".mp4", ".avi", ".mov", ".mkv", ".wmv", ".flv", ".webm", ".m4v" };
            return videoExtensions.Contains(extension.ToLowerInvariant());
        }

        private bool IsAudioFile(string extension)
        {
            var audioExtensions = new[] { ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".wma" };
            return audioExtensions.Contains(extension.ToLowerInvariant());
        }

        private bool IsTextFile(string extension)
        {
            var textExtensions = new[] {
                ".txt", ".log", ".xml", ".json", ".csv", ".html", ".htm", ".css", ".js",
                ".md", ".cs", ".java", ".cpp", ".c", ".py", ".php", ".rb", ".config",
                ".yml", ".yaml", ".ini", ".sql"
            };
            return textExtensions.Contains(extension.ToLowerInvariant());
        }

        private bool IsImageFile(string extension)
        {
            var imageExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" };
            return imageExtensions.Contains(extension.ToLowerInvariant());
        }

        private string MakeValidFileName(string name)
        {
            try
            {
                _logger.LogInformation("开始处理文件名: '{OriginalName}'", name);

                if (string.IsNullOrWhiteSpace(name))
                {
                    var guidName = $"upload_{Guid.NewGuid():N}{GetDefaultExtension(name)}";
                    _logger.LogWarning("文件名为空或空白，生成新文件名: '{GuidName}'", guidName);
                    return guidName;
                }

                var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(name);
                var extension = Path.GetExtension(name);

                _logger.LogDebug("分离文件名和扩展名 - 文件名部分: '{FileNamePart}', 扩展名: '{Extension}'",
                    fileNameWithoutExtension, extension);

                if (string.IsNullOrEmpty(fileNameWithoutExtension))
                {
                    fileNameWithoutExtension = $"upload_{Guid.NewGuid():N}";
                    _logger.LogWarning("文件名部分为空，使用GUID替换: '{FileNameWithoutExtension}'",
                        fileNameWithoutExtension);
                }

                _logger.LogDebug("原始文件名字符分析 - 长度: {Length}, 包含中文: {HasChinese}, 包含特殊字符: {HasSpecialChars}",
                    fileNameWithoutExtension.Length,
                    fileNameWithoutExtension.Any(c => c >= 0x4E00 && c <= 0x9FFF),
                    fileNameWithoutExtension.Any(c => Path.GetInvalidFileNameChars().Contains(c)));

                var invalidChars = Path.GetInvalidFileNameChars();
                var validFileName = new string(fileNameWithoutExtension
                    .Where(ch => !invalidChars.Contains(ch))
                    .ToArray());

                _logger.LogDebug("移除非法字符后 - 有效文件名部分: '{ValidFileName}', 原始长度: {OriginalLength}, 新长度: {NewLength}",
                    validFileName, fileNameWithoutExtension.Length, validFileName.Length);

                if (string.IsNullOrWhiteSpace(validFileName))
                {
                    validFileName = $"upload_{Guid.NewGuid():N}";
                    _logger.LogWarning("移除非法字符后文件名为空，使用GUID替换: '{ValidFileName}'",
                        validFileName);
                }

                var validExtension = string.IsNullOrEmpty(extension)
                    ? GetDefaultExtension(name)
                    : extension;

                var finalFileName = validFileName + validExtension;

                _logger.LogInformation("文件名处理完成 - 原始: '{OriginalName}' -> 最终: '{FinalName}'",
                    name, finalFileName);

                return finalFileName;
            }
            catch (Exception ex)
            {
                var guidName = $"upload_{Guid.NewGuid():N}{GetDefaultExtension(name)}";
                _logger.LogError(ex, "处理文件名时发生异常，原始文件名: '{OriginalName}'，使用默认文件名: '{GuidName}'",
                    name, guidName);
                return guidName;
            }
        }

        private string GetDefaultExtension(string originalName)
        {
            var originalExtension = Path.GetExtension(originalName);
            if (!string.IsNullOrEmpty(originalExtension))
            {
                return originalExtension;
            }
            return ".dat";
        }
    }
}