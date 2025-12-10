using System.Collections.Concurrent;
using System.Text.Json;
using FileServer.Models;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Jpeg;
using Directory = MetadataExtractor.Directory; // 消除歧义

namespace FileServer.Services
{
    public class PhotoMetadataService : IPhotoMetadataService, IDisposable
    {
        private readonly IFileService _fileService;
        private readonly ILogger<PhotoMetadataService> _logger;
        private readonly string _rootPath;
        private readonly string _metadataFilePath;
        private readonly ConcurrentDictionary<string, PhotoMetadata> _cache;
        private readonly SemaphoreSlim _fileLock = new(1, 1);
        private readonly Timer _autoSaveTimer;
        private bool _isDirty;

        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".tiff", ".bmp", ".webp", ".heic", ".heif"
        };

        public PhotoMetadataService(IFileService fileService, ILogger<PhotoMetadataService> logger)
        {
            _fileService = fileService;
            _logger = logger;
            _rootPath = _fileService.GetRootPath();
            _metadataFilePath = Path.Combine(_rootPath, ".metadata", "photos.json");
            _cache = new ConcurrentDictionary<string, PhotoMetadata>();

            var dir = Path.GetDirectoryName(_metadataFilePath);
            if (!string.IsNullOrEmpty(dir))
                System.IO.Directory.CreateDirectory(dir);

            LoadFromFile();
            _autoSaveTimer = new Timer(async _ => await SaveIfDirty(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        private void LoadFromFile()
        {
            if (!System.IO.File.Exists(_metadataFilePath)) return;
            try
            {
                var json = System.IO.File.ReadAllText(_metadataFilePath);
                var dict = JsonSerializer.Deserialize<Dictionary<string, PhotoMetadata>>(json);
                if (dict != null)
                {
                    foreach (var kvp in dict)
                        _cache.TryAdd(kvp.Key, kvp.Value);
                    _logger.LogInformation("已加载 {Count} 条图片元数据", _cache.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载元数据文件失败");
            }
        }

        private async Task SaveIfDirty()
        {
            if (!_isDirty) return;
            await SaveToFile();
        }

        private async Task SaveToFile()
        {
            await _fileLock.WaitAsync();
            try
            {
                var snapshot = _cache.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                await System.IO.File.WriteAllTextAsync(_metadataFilePath, json);
                _isDirty = false;
                _logger.LogDebug("元数据已保存");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存元数据文件失败");
            }
            finally
            {
                _fileLock.Release();
            }
        }

        private void MarkDirty() => _isDirty = true;

        public async Task<PhotoMetadata?> GetOrExtractMetadataAsync(string relativePath)
        {
            if (_cache.TryGetValue(relativePath, out var cached))
            {
                var fullPath = Path.Combine(_rootPath, relativePath);
                var fileInfo = new FileInfo(fullPath);
                if (fileInfo.Exists && (fileInfo.Length != cached.FileSize || fileInfo.LastWriteTimeUtc != cached.LastModified))
                {
                    _logger.LogInformation("文件已变更，重新提取: {Path}", relativePath);
                    return await ExtractAndCacheAsync(relativePath);
                }
                return cached;
            }
            return await ExtractAndCacheAsync(relativePath);
        }

        private async Task<PhotoMetadata?> ExtractAndCacheAsync(string relativePath)
        {
            var fullPath = Path.Combine(_rootPath, relativePath);
            if (!System.IO.File.Exists(fullPath))
                return null;

            var extension = Path.GetExtension(fullPath);
            if (!ImageExtensions.Contains(extension))
                return null;

            try
            {
                IReadOnlyList<Directory> directories = ImageMetadataReader.ReadMetadata(fullPath);
                var exifSubIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
                var gpsDir = directories.OfType<GpsDirectory>().FirstOrDefault();
                var ifd0Dir = directories.OfType<ExifIfd0Directory>().FirstOrDefault();

                DateTime? dateTaken = null;
                if (exifSubIfd != null && exifSubIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dt))
                    dateTaken = dt;

                double? lat = null, lng = null;
                if (gpsDir != null)
                {
                    var location = gpsDir.GetGeoLocation();
                    if (location.HasValue)  // GeoLocation 是 struct，用 HasValue 判断
                    {
                        lat = location.Value.Latitude;
                        lng = location.Value.Longitude;
                    }
                }

                string? cameraModel = ifd0Dir?.GetString(ExifIfd0Directory.TagModel);

                // 获取图片宽高
                int? width = null, height = null;
                var jpegDir = directories.OfType<JpegDirectory>().FirstOrDefault();
                if (jpegDir != null)
                {
                    try
                    {
                        width = jpegDir.GetImageWidth();
                        height = jpegDir.GetImageHeight();
                    }
                    catch { /* 忽略 */ }
                }
                if ((!width.HasValue || !height.HasValue) && ifd0Dir != null)
                {
                    width ??= ifd0Dir.GetInt32(ExifDirectoryBase.TagImageWidth);
                    height ??= ifd0Dir.GetInt32(ExifDirectoryBase.TagImageHeight);
                }

                var fileInfo = new FileInfo(fullPath);
                var metadata = new PhotoMetadata
                {
                    RelativePath = relativePath,
                    FileName = Path.GetFileName(relativePath),
                    DateTaken = dateTaken,
                    Latitude = lat,
                    Longitude = lng,
                    CameraModel = cameraModel,
                    Width = width,
                    Height = height,
                    FileSize = fileInfo.Length,
                    LastModified = fileInfo.LastWriteTimeUtc,
                    LastMetadataUpdate = DateTime.UtcNow
                };

                _cache.AddOrUpdate(relativePath, metadata, (_, _) => metadata);
                MarkDirty();
                _logger.LogInformation("已提取并缓存: {Path}", relativePath);
                return metadata;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "提取图片元数据失败: {Path}", relativePath);
                return null;
            }
        }

        public async Task<Dictionary<string, PhotoMetadata>> GetBatchMetadataAsync(IEnumerable<string> relativePaths)
        {
            var result = new Dictionary<string, PhotoMetadata>();
            foreach (var path in relativePaths)
            {
                if (_cache.TryGetValue(path, out var meta))
                    result[path] = meta;
            }
            await Task.CompletedTask;
            return result;
        }

        public async Task<(IEnumerable<PhotoMetadata> Items, int TotalCount)> SearchPhotosAsync(PhotoSearchOptions options)
        {
            var query = _cache.Values.AsQueryable();

            if (!string.IsNullOrEmpty(options.DirectoryPath))
            {
                var dirPrefix = options.DirectoryPath.Replace('\\', '/');
                if (!dirPrefix.EndsWith('/')) dirPrefix += '/';
                query = query.Where(m => m.RelativePath.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase));
            }
            if (options.StartDate.HasValue)
                query = query.Where(m => m.DateTaken >= options.StartDate.Value);
            if (options.EndDate.HasValue)
                query = query.Where(m => m.DateTaken <= options.EndDate.Value);
            if (options.MinLatitude.HasValue && options.MaxLatitude.HasValue)
                query = query.Where(m => m.Latitude >= options.MinLatitude && m.Latitude <= options.MaxLatitude);
            if (options.MinLongitude.HasValue && options.MaxLongitude.HasValue)
                query = query.Where(m => m.Longitude >= options.MinLongitude && m.Longitude <= options.MaxLongitude);

            var totalCount = query.Count();
            var sortBy = options.SortBy?.ToLowerInvariant() ?? "datetaken";
            var ordered = sortBy switch
            {
                "name" => options.SortAscending ? query.OrderBy(m => m.FileName) : query.OrderByDescending(m => m.FileName),
                "size" => options.SortAscending ? query.OrderBy(m => m.FileSize) : query.OrderByDescending(m => m.FileSize),
                "modified" => options.SortAscending ? query.OrderBy(m => m.LastModified) : query.OrderByDescending(m => m.LastModified),
                _ => options.SortAscending ? query.OrderBy(m => m.DateTaken) : query.OrderByDescending(m => m.DateTaken),
            };

            var items = ordered.Skip(options.Skip).Take(options.Take).ToList();
            await Task.CompletedTask;
            return (items, totalCount);
        }

        public async Task RefreshMetadataAsync(string relativePath)
        {
            _cache.TryRemove(relativePath, out _);
            MarkDirty();
            await ExtractAndCacheAsync(relativePath);
        }

        public async Task ScanAndIndexAllPhotosAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            var allFiles = System.IO.Directory.GetFiles(_rootPath, "*.*", SearchOption.AllDirectories)
                .Where(f => ImageExtensions.Contains(Path.GetExtension(f)))
                .Select(f => Path.GetRelativePath(_rootPath, f).Replace('\\', '/'));
            var total = allFiles.Count();
            var processed = 0;
            foreach (var relPath in allFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await ExtractAndCacheAsync(relPath);
                    if (++processed % 10 == 0)
                        progress?.Report($"已处理 {processed}/{total}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "扫描处理失败: {Path}", relPath);
                }
            }
            await SaveToFile();
            progress?.Report($"扫描完成，共 {total} 张图片");
        }

        public void Dispose()
        {
            _autoSaveTimer?.Dispose();
            SaveToFile().GetAwaiter().GetResult();
            _fileLock?.Dispose();
        }
    }
}