using FileServer.Models;

namespace FileServer.Services
{
    public interface IFileService
    {
        Task<FileListResponse> GetFileListAsync(string relativePath);
        Task<(Stream Stream, string ContentType, string FileName)> DownloadFileAsync(string filePath);
        Task<FileInfoModel> GetFileInfoAsync(string filePath); // 新增方法
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

        public FileService(IConfiguration configuration, ILogger<FileService> logger, IThumbnailService thumbnailService)
        {
            _rootPath = configuration["FileServer:RootPath"] ?? @"D:\FileServer";
            _logger = logger;
            _thumbnailService = thumbnailService;

            if (!Directory.Exists(_rootPath))
            {
                Directory.CreateDirectory(_rootPath);
                _logger.LogInformation("创建根目录: {RootPath}", _rootPath);
            }
        }

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
                    Size = fileInfo.Length, // 这里使用 FileInfo.Length 赋值给 FileInfoModel.Size
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


        public async Task<FileListResponse> GetFileListAsync(string relativePath)
        {
            var physicalPath = Path.Combine(_rootPath, relativePath);

            if (!physicalPath.StartsWith(_rootPath))
            {
                physicalPath = _rootPath;
            }

            if (!Directory.Exists(physicalPath))
            {
                throw new DirectoryNotFoundException($"目录不存在: {relativePath}");
            }

            var response = new FileListResponse
            {
                CurrentPath = relativePath,
                ParentPath = GetParentPath(relativePath)
            };

            try
            {
                // 处理目录
                var directories = Directory.GetDirectories(physicalPath);
                foreach (var dir in directories)
                {
                    try
                    {
                        var dirInfo = new DirectoryInfo(dir);
                        response.Directories.Add(new DirectoryInfoModel
                        {
                            Name = dirInfo.Name,
                            Path = Path.Combine(relativePath, dirInfo.Name).Replace("\\", "/")
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "处理目录失败: {Directory}", dir);
                    }
                }

                // 处理文件
                var files = Directory.GetFiles(physicalPath);
                foreach (var file in files)
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        var extension = Path.GetExtension(file).ToLowerInvariant();
                        var relativeFilePath = Path.Combine(relativePath, fileInfo.Name).Replace("\\", "/");

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
                            HasThumbnail = false // 默认没有缩略图
                        };

                        // 如果是图片文件，检查是否有缩略图
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
                    relativePath, response.Directories.Count, response.Files.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "读取文件列表失败: {Path}", relativePath);
                throw;
            }

            return response;
        }

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

            return (stream, contentType, fileInfo.Name);
        }

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

            var uploadedFiles = new List<string>();
            long totalSize = 0;

            foreach (var file in files)
            {
                if (file.Length == 0) continue;

                var fileName = MakeValidFileName(file.FileName);
                var filePath = Path.Combine(uploadPath, fileName);
                var relativeFilePath = string.IsNullOrEmpty(targetPath)
                    ? fileName
                    : Path.Combine(targetPath, fileName).Replace("\\", "/");

                try
                {
                    using (var stream = new FileStream(filePath, FileMode.Create))
                    {
                        await file.CopyToAsync(stream);
                    }

                    var fileInfo = new FileInfo(filePath);
                    totalSize += fileInfo.Length;
                    uploadedFiles.Add(fileName);

                    // 如果是图片文件，生成缩略图
                    var extension = Path.GetExtension(fileName).ToLowerInvariant();
                    if (IsImageFile(extension))
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _thumbnailService.GenerateThumbnailAsync(relativeFilePath);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "生成缩略图失败: {FileName}", fileName);
                            }
                        });
                    }

                    _logger.LogInformation("文件上传成功: {FileName} -> {FilePath} (大小: {Size})",
                        fileName, filePath, FormatFileSize(fileInfo.Length));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "文件保存失败: {FileName}", fileName);
                }
            }

            if (uploadedFiles.Count > 0)
            {
                _logger.LogInformation("上传完成: {FileCount} 个文件，总大小: {TotalSize}",
                    uploadedFiles.Count, FormatFileSize(totalSize));
            }

            return new UploadResponse
            {
                Success = uploadedFiles.Count > 0,
                Message = uploadedFiles.Count > 0 ? $"成功上传 {uploadedFiles.Count} 个文件" : "没有文件被上传",
                Files = uploadedFiles,
                TotalSize = totalSize,
                TotalSizeFormatted = FormatFileSize(totalSize)
            };
        }
        // 添加删除文件方法（支持删除缩略图）
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

                // 如果是图片文件，删除对应的缩略图
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

        // 添加缩略图获取方法
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

        // 添加图片文件判断方法
        private bool IsImageFile(string extension)
        {
            var imageExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" };
            return imageExtensions.Contains(extension.ToLowerInvariant());
        }

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

        private string MakeValidFileName(string name)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            return new string(name.Where(ch => !invalidChars.Contains(ch)).ToArray());
        }
    }
}