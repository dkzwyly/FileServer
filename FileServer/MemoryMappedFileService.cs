using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;

namespace FileServer.Services
{
    public interface IMemoryMappedFileService
    {
        Task<(MemoryMappedFile MappedFile, string ContentType)> OpenMemoryMappedFile(string filePath);
        Task<MemoryMappedFile> CreateMemoryMappedFile(string filePath);
        void CloseMemoryMappedFile(MemoryMappedFile mappedFile);
    }

    public class MemoryMappedFileService : IMemoryMappedFileService, IDisposable
    {
        private readonly string _rootPath;
        private readonly ILogger<MemoryMappedFileService> _logger;
        private readonly ConcurrentDictionary<string, (MemoryMappedFile MappedFile, DateTime LastAccess)> _cache;
        private readonly Timer _cleanupTimer;
        private readonly long _maxCacheSize = 10L * 1024 * 1024 * 1024; // 10GB 最大缓存
        private readonly TimeSpan _cacheTimeout = TimeSpan.FromMinutes(30); // 30分钟缓存

        public MemoryMappedFileService(IConfiguration configuration, ILogger<MemoryMappedFileService> logger)
        {
            _rootPath = configuration["FileServer:RootPath"] ?? @"D:\FileServer";
            _logger = logger;
            _cache = new ConcurrentDictionary<string, (MemoryMappedFile, DateTime)>();

            // 每5分钟清理一次过期缓存
            _cleanupTimer = new Timer(CleanupCache, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }

        public async Task<(MemoryMappedFile MappedFile, string ContentType)> OpenMemoryMappedFile(string filePath)
        {
            try
            {
                var physicalPath = Path.Combine(_rootPath, filePath);

                if (!File.Exists(physicalPath))
                    throw new FileNotFoundException($"文件不存在: {filePath}");

                // 检查缓存
                if (_cache.TryGetValue(physicalPath, out var cached) &&
                    DateTime.UtcNow - cached.LastAccess < _cacheTimeout)
                {
                    _cache[physicalPath] = (cached.MappedFile, DateTime.UtcNow);
                    _logger.LogDebug("从缓存获取内存映射文件: {FilePath}", filePath);

                    var extension = Path.GetExtension(filePath).ToLowerInvariant();
                    var cachedContentType = GetMimeType(extension); // 重命名变量
                    return (cached.MappedFile, cachedContentType);
                }

                // 创建新的内存映射
                var fileInfo = new FileInfo(physicalPath);
                var mappedFile = await CreateMemoryMappedFileInternal(physicalPath, fileInfo.Length);

                // 更新缓存
                _cache[physicalPath] = (mappedFile, DateTime.UtcNow);

                var ext = Path.GetExtension(filePath).ToLowerInvariant();
                var newContentType = GetMimeType(ext); // 重命名变量

                _logger.LogInformation("创建内存映射文件: {FilePath} (大小: {Size})",
                    filePath, FormatFileSize(fileInfo.Length));

                return (mappedFile, newContentType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "打开内存映射文件失败: {FilePath}", filePath);
                throw;
            }
        }

        public async Task<MemoryMappedFile> CreateMemoryMappedFile(string filePath)
        {
            var physicalPath = Path.Combine(_rootPath, filePath);
            var fileInfo = new FileInfo(physicalPath);
            return await CreateMemoryMappedFileInternal(physicalPath, fileInfo.Length);
        }

        private Task<MemoryMappedFile> CreateMemoryMappedFileInternal(string physicalPath, long fileSize)
        {
            return Task.Run(() =>
            {
                try
                {
                    // 使用文件流创建内存映射文件，支持只读访问
                    var fileStream = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    return MemoryMappedFile.CreateFromFile(
                        fileStream,
                        null, // 不指定映射名称
                        fileSize,
                        MemoryMappedFileAccess.Read,
                        HandleInheritability.None,
                        false // 不保留文件流
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "创建内存映射文件失败: {PhysicalPath}", physicalPath);
                    throw;
                }
            });
        }

        public void CloseMemoryMappedFile(MemoryMappedFile mappedFile)
        {
            try
            {
                mappedFile?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "关闭内存映射文件时发生错误");
            }
        }

        private void CleanupCache(object state)
        {
            try
            {
                var now = DateTime.UtcNow;
                var expiredKeys = new List<string>();

                // 找出过期的缓存项
                foreach (var kvp in _cache)
                {
                    if (now - kvp.Value.LastAccess > _cacheTimeout)
                    {
                        expiredKeys.Add(kvp.Key);
                    }
                }

                // 移除过期缓存
                foreach (var key in expiredKeys)
                {
                    if (_cache.TryRemove(key, out var cached))
                    {
                        cached.MappedFile?.Dispose();
                        _logger.LogDebug("清理过期内存映射缓存: {Key}", key);
                    }
                }

                // 如果缓存仍然过大，清理最久未使用的
                if (GetCacheSize() > _maxCacheSize)
                {
                    var oldestEntries = _cache.OrderBy(x => x.Value.LastAccess)
                                            .Take(_cache.Count / 4) // 清理25%的最旧条目
                                            .ToList();

                    foreach (var entry in oldestEntries)
                    {
                        if (_cache.TryRemove(entry.Key, out var cached))
                        {
                            cached.MappedFile?.Dispose();
                            _logger.LogDebug("清理LRU内存映射缓存: {Key}", entry.Key);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清理内存映射缓存时发生错误");
            }
        }

        private long GetCacheSize()
        {
            long totalSize = 0;
            foreach (var kvp in _cache)
            {
                try
                {
                    var fileInfo = new FileInfo(kvp.Key);
                    if (fileInfo.Exists)
                    {
                        totalSize += fileInfo.Length;
                    }
                }
                catch
                {
                    // 忽略无法访问的文件
                }
            }
            return totalSize;
        }

        // 添加 MIME 类型检测方法
        private string GetMimeType(string extension)
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

        // 添加文件大小格式化方法
        private string FormatFileSize(long bytes)
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

        public void Dispose()
        {
            _cleanupTimer?.Dispose();

            // 清理所有缓存
            foreach (var kvp in _cache)
            {
                kvp.Value.MappedFile?.Dispose();
            }
            _cache.Clear();

            GC.SuppressFinalize(this);
        }
    }
}