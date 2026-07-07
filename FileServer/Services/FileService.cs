using FileServer.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;

namespace FileServer.Services
{
    public class FileService : IFileService
    {
        private readonly IThumbnailService _thumbnailService;
        private readonly ILogger<FileService> _logger;
        private readonly IFileConflictService _conflictService;
        private readonly IFileTreeCacheService _treeCache;
        private readonly IFileSystemHelper _fileSystemHelper;  // 新增

        public FileService(
            IConfiguration configuration,
            ILogger<FileService> logger,
            IThumbnailService thumbnailService,
            IFileConflictService conflictService,
            IFileTreeCacheService treeCache,
            IFileSystemHelper fileSystemHelper)  // 注入 helper
        {
            _logger = logger;
            _thumbnailService = thumbnailService;
            _conflictService = conflictService;
            _treeCache = treeCache;
            _fileSystemHelper = fileSystemHelper;

            // 确保根目录存在
            var rootPath = _fileSystemHelper.GetRootPath();
            if (!Directory.Exists(rootPath))
            {
                Directory.CreateDirectory(rootPath);
                _logger.LogInformation("创建根目录: {RootPath}", rootPath);
            }
        }

        // ==================== 实现接口 ====================

        public async Task<FileListResponse> GetFileListAsync(string relativePath)
        {
            return await _treeCache.GetDirectoryContentAsync(relativePath);
        }

        public async Task<UploadResponse> UploadFilesAsync(string targetPath, IFormFileCollection files)
        {
            var rootPath = _fileSystemHelper.GetRootPath();
            var uploadPath = string.IsNullOrEmpty(targetPath)
                ? rootPath
                : Path.Combine(rootPath, targetPath);

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
                    using (var stream = new FileStream(filePath, FileMode.Create))
                    {
                        await file.CopyToAsync(stream);
                    }

                    var fileInfo = new FileInfo(filePath);
                    if (!fileInfo.Exists || fileInfo.Length == 0)
                    {
                        _logger.LogError("文件保存失败或大小为0: {FilePath}", filePath);
                        failedCount++;
                        continue;
                    }

                    var extension = Path.GetExtension(fileName).ToLowerInvariant();
                    await ValidateFileFormat(filePath, extension, originalFileName);

                    // ===== 添加节点到缓存 =====
                    var node = new FileNode
                    {
                        Path = relativeFilePath,
                        Name = fileName,
                        ParentPath = string.IsNullOrEmpty(targetPath) ? "" : targetPath.Replace("\\", "/"),
                        IsDirectory = false,
                        Size = fileInfo.Length,
                        LastModified = fileInfo.LastWriteTimeUtc,
                        MimeType = _fileSystemHelper.GetMimeType(extension),
                        IsVideo = IsVideoFile(extension),
                        IsAudio = IsAudioFile(extension),
                        IsImage = IsImageFile(extension)
                    };
                    await _treeCache.AddNodeAsync(node);

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
                        fileName, filePath, _fileSystemHelper.FormatFileSize(fileInfo.Length));
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
                    _fileSystemHelper.FormatFileSize(totalSize), renameCount);
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

            response.Files = uploadedFiles
                .Where(f => string.IsNullOrEmpty(f.RenameReason) || !f.RenameReason.StartsWith("上传失败"))
                .Select(f => f.SavedName)
                .ToList();

            response.CalculateFormattedSize();

            return response;
        }

        public async Task<bool> DeleteFileAsync(string filePath)
        {
            try
            {
                var rootPath = _fileSystemHelper.GetRootPath();
                var physicalPath = Path.Combine(rootPath, filePath);
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

                await _treeCache.RemoveNodeAsync(filePath);

                _logger.LogInformation("删除文件成功: {FilePath}", filePath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除文件失败: {FilePath}", filePath);
                return false;
            }
        }

        public async Task CreateDirectoryAsync(string relativePath)
        {
            var rootPath = _fileSystemHelper.GetRootPath();
            var physicalPath = Path.Combine(rootPath, relativePath);
            if (Directory.Exists(physicalPath))
                throw new InvalidOperationException($"目录已存在: {relativePath}");

            Directory.CreateDirectory(physicalPath);
            var dirInfo = new DirectoryInfo(physicalPath);

            var node = new FileNode
            {
                Path = relativePath.Replace("\\", "/"),
                Name = Path.GetFileName(relativePath),
                ParentPath = GetParentPath(relativePath),
                IsDirectory = true,
                LastModified = dirInfo.LastWriteTimeUtc,
                MimeType = ""
            };
            await _treeCache.AddNodeAsync(node);
            _logger.LogInformation("创建目录成功: {RelativePath}", relativePath);
        }

        public async Task<(Stream Stream, string ContentType, string FileName)> DownloadFileAsync(string filePath)
        {
            var rootPath = _fileSystemHelper.GetRootPath();
            var physicalPath = Path.Combine(rootPath, filePath);
            if (!File.Exists(physicalPath))
                throw new FileNotFoundException($"文件不存在: {filePath}");

            var fileInfo = new FileInfo(physicalPath);
            var extension = Path.GetExtension(physicalPath).ToLowerInvariant();
            var contentType = _fileSystemHelper.GetMimeType(extension);

            if (extension == ".wmv")
            {
                contentType = "video/x-ms-wmv";
            }

            // 修改点：使用 FileShare.Read | FileShare.Delete
            var stream = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

            _logger.LogInformation("文件下载: {FileName} (大小: {Size})", fileInfo.Name, _fileSystemHelper.FormatFileSize(fileInfo.Length));

            return await Task.FromResult((stream, contentType, fileInfo.Name));
        }
        public async Task<bool> RenameAsync(string oldPath, string newName)
        {
            if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newName))
                throw new ArgumentException("路径和名称不能为空");

            var root = _fileSystemHelper.GetRootPath();
            var oldFull = Path.Combine(root, oldPath);
            var dir = Path.GetDirectoryName(oldFull);
            var newFull = Path.Combine(dir!, newName);

            // 检查旧路径是否存在
            if (!File.Exists(oldFull) && !Directory.Exists(oldFull))
                return false;

            // 检查新名称是否已被占用
            if (File.Exists(newFull) || Directory.Exists(newFull))
                throw new InvalidOperationException($"目标 '{newName}' 已存在");

            try
            {
                // 执行重命名
                if (File.Exists(oldFull))
                    File.Move(oldFull, newFull);
                else
                    Directory.Move(oldFull, newFull);

                // 更新缓存：移除旧节点，添加新节点
                var oldRel = oldPath.Replace('\\', '/');
                var newRel = Path.Combine(Path.GetDirectoryName(oldRel) ?? "", newName).Replace('\\', '/');

                // 移除旧节点（会递归移除子节点）
                await _treeCache.RemoveNodeAsync(oldRel);

                // 重新扫描新路径所在目录以添加新节点（或手动构建节点）
                // 简便做法：重新扫描该目录
                var parent = Path.GetDirectoryName(newRel) ?? "";
                await _treeCache.GetDirectoryContentAsync(parent); // 内部会触发扫描更新

                _logger.LogInformation("重命名成功: {Old} -> {New}", oldPath, newRel);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "重命名失败: {Old} -> {NewName}", oldPath, newName);
                throw;
            }
        }

        public async Task<bool> MoveAsync(string sourcePath, string destPath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destPath))
                throw new ArgumentException("源路径和目标路径不能为空");

            var root = _fileSystemHelper.GetRootPath();
            var srcFull = Path.Combine(root, sourcePath);
            var dstFull = Path.Combine(root, destPath);

            if (!File.Exists(srcFull) && !Directory.Exists(srcFull))
                return false;

            if (File.Exists(dstFull) || Directory.Exists(dstFull))
                throw new InvalidOperationException($"目标路径 '{destPath}' 已存在");

            // 确保目标目录存在
            var dstDir = Path.GetDirectoryName(dstFull);
            if (!Directory.Exists(dstDir))
                Directory.CreateDirectory(dstDir);

            try
            {
                // 移动
                if (File.Exists(srcFull))
                    File.Move(srcFull, dstFull);
                else
                    Directory.Move(srcFull, dstFull);

                // 更新缓存
                var srcRel = sourcePath.Replace('\\', '/');
                var dstRel = destPath.Replace('\\', '/');

                // 删除旧节点（及子树）
                await _treeCache.RemoveNodeAsync(srcRel);
                // 刷新目标父目录缓存
                var parent = Path.GetDirectoryName(dstRel) ?? "";
                await _treeCache.GetDirectoryContentAsync(parent);

                _logger.LogInformation("移动成功: {Src} -> {Dst}", sourcePath, destPath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "移动失败: {Src} -> {Dst}", sourcePath, destPath);
                throw;
            }
        }

        public async Task<bool> CopyAsync(string sourcePath, string destPath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destPath))
                throw new ArgumentException("源路径和目标路径不能为空");

            var root = _fileSystemHelper.GetRootPath();
            var srcFull = Path.Combine(root, sourcePath);
            var dstFull = Path.Combine(root, destPath);

            if (!File.Exists(srcFull) && !Directory.Exists(srcFull))
                return false;

            if (File.Exists(dstFull) || Directory.Exists(dstFull))
                throw new InvalidOperationException($"目标路径 '{destPath}' 已存在");

            // 确保目标目录存在
            var dstDir = Path.GetDirectoryName(dstFull);
            if (!Directory.Exists(dstDir))
                Directory.CreateDirectory(dstDir);

            try
            {
                // 复制
                if (File.Exists(srcFull))
                    File.Copy(srcFull, dstFull);
                else
                    CopyDirectory(srcFull, dstFull); // 递归复制目录

                // 更新缓存：刷新目标父目录
                var dstRel = destPath.Replace('\\', '/');
                var parent = Path.GetDirectoryName(dstRel) ?? "";
                await _treeCache.GetDirectoryContentAsync(parent);

                _logger.LogInformation("复制成功: {Src} -> {Dst}", sourcePath, destPath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "复制失败: {Src} -> {Dst}", sourcePath, destPath);
                throw;
            }
        }

        // 辅助：递归复制目录
        private void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var destFile = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, destFile);
            }

            foreach (var subDir in Directory.GetDirectories(sourceDir))
            {
                var destSubDir = Path.Combine(destDir, Path.GetFileName(subDir));
                CopyDirectory(subDir, destSubDir);
            }
        }

        public async Task<FileInfoModel> GetFileInfoAsync(string filePath)
        {
            var rootPath = _fileSystemHelper.GetRootPath();
            var physicalPath = Path.Combine(rootPath, filePath);

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
                    SizeFormatted = _fileSystemHelper.FormatFileSize(fileInfo.Length),
                    Extension = extension,
                    LastModified = fileInfo.LastWriteTime,
                    IsVideo = IsVideoFile(extension),
                    IsAudio = IsAudioFile(extension),
                    MimeType = _fileSystemHelper.GetMimeType(extension),
                    Encoding = IsTextFile(extension) ? "utf-8" : ""
                };
            });
        }

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

        public Task<bool> FileExistsAsync(string filePath)
        {
            var rootPath = _fileSystemHelper.GetRootPath();
            var physicalPath = Path.Combine(rootPath, filePath);
            return Task.FromResult(File.Exists(physicalPath));
        }

        public Task<bool> DirectoryExistsAsync(string directoryPath)
        {
            var rootPath = _fileSystemHelper.GetRootPath();
            var physicalPath = Path.Combine(rootPath, directoryPath);
            return Task.FromResult(Directory.Exists(physicalPath));
        }

        // ===== IFileService 接口的这3个方法，直接调用 helper =====
        public string FormatFileSize(long bytes) => _fileSystemHelper.FormatFileSize(bytes);
        public string GetMimeType(string extension) => _fileSystemHelper.GetMimeType(extension);
        public string GetRootPath() => _fileSystemHelper.GetRootPath();

        // ==================== 私有辅助方法 ====================

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
        public async Task<bool> DeleteDirectoryAsync(string relativePath)
        {
            try
            {
                if (string.IsNullOrEmpty(relativePath))
                    throw new InvalidOperationException("不能删除根目录");

                var rootPath = _fileSystemHelper.GetRootPath();
                var physicalPath = Path.Combine(rootPath, relativePath);

                if (!Directory.Exists(physicalPath))
                    return false;

                // ----- 1. 先遍历所有文件，清理相关元数据（在物理删除前） -----
                var allFiles = Directory.GetFiles(physicalPath, "*", SearchOption.AllDirectories);
                _logger.LogInformation("准备删除文件夹 {RelativePath}，包含 {FileCount} 个文件",
                    relativePath, allFiles.Length);

                foreach (var filePath in allFiles)
                {
                    // 计算相对路径（用于元数据服务）
                    var relativeFilePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
                    var extension = Path.GetExtension(filePath).ToLowerInvariant();

                    try
                    {
                        // 图片元数据清理
                        if (IsImageFile(extension))
                        {
                            // 删除缩略图（如果有）
                            await _thumbnailService.DeleteThumbnailAsync(relativeFilePath);
                            // 如果有 PhotoMetadataService，则删除其索引
                            // await _photoMetadataService.DeleteMetadataAsync(relativeFilePath);
                        }
                        // 音频元数据清理
                        else if (IsAudioFile(extension))
                        {
                            // await _audioMetadataService.DeleteMetadataMappingAsync(filePath);
                        }
                        // 其他可能的索引清理...
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "清理文件元数据失败: {FilePath}", relativeFilePath);
                        // 继续删除，不中断
                    }
                }

                // ----- 2. 递归删除物理文件夹（包含所有子文件和子目录） -----
                Directory.Delete(physicalPath, true);

                // ----- 3. 从树缓存中移除节点（应递归删除所有子节点） -----
                await _treeCache.RemoveNodeAsync(relativePath);

                _logger.LogInformation("删除文件夹成功: {RelativePath}，共删除 {FileCount} 个文件",
                    relativePath, allFiles.Length);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除文件夹失败: {RelativePath}", relativePath);
                return false;
            }
        }
    }
}