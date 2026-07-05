using System.Collections.Concurrent;
using System.Text.Json;
using FileServer.Models;
using LiteDB;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Jpeg;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Directory = MetadataExtractor.Directory;
using Dir = System.IO.Directory;

namespace FileServer.Services
{
    public class PhotoMetadataService : IPhotoMetadataService, IDisposable
    {
        private readonly ILogger<PhotoMetadataService> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _rootPath;
        private readonly LiteDatabase _db;
        private readonly ILiteCollection<PhotoMetadata> _collection;

        private static readonly ConcurrentDictionary<string, PhotoMetadata> _cache = new();

        public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".tiff", ".bmp", ".webp", ".heic", ".heif", ".gif"
        };

        public PhotoMetadataService(IFileService fileService, ILogger<PhotoMetadataService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;

            var rawRoot = fileService.GetRootPath();
            _rootPath = Path.GetFullPath(rawRoot);

            var dbPath = configuration["FileServerConfig:PhotoLiteDbPath"];
            if (string.IsNullOrEmpty(dbPath))
                dbPath = Path.Combine(_rootPath, "photo-metadata.db");
            else
                dbPath = Path.GetFullPath(Path.Combine(_rootPath, dbPath));

            var dbDir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dbDir) && !Dir.Exists(dbDir))
                Dir.CreateDirectory(dbDir);

            _db = new LiteDatabase(dbPath);
            _collection = _db.GetCollection<PhotoMetadata>("photoMetadata");
            _collection.EnsureIndex(x => x.RelativePath, unique: true);
            _collection.EnsureIndex(x => x.DateTaken);
            _collection.EnsureIndex(x => x.Latitude);
            _collection.EnsureIndex(x => x.Longitude);

            LoadFromDatabase();
            _logger.LogInformation("PhotoMetadataService 初始化完成，数据库路径: {DbPath}", dbPath);
        }

        private void LoadFromDatabase()
        {
            try
            {
                var all = _collection.FindAll().ToList();
                foreach (var doc in all)
                    _cache.TryAdd(doc.RelativePath, doc);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "从 LiteDB 加载图片元数据失败");
            }
        }

        private void SaveToDatabase(PhotoMetadata metadata)
        {
            try
            {
                _collection.Upsert(metadata);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存图片元数据到 LiteDB 失败: {Path}", metadata.RelativePath);
                throw;
            }
        }

        private void DeleteFromDatabase(string relativePath)
        {
            try
            {
                _collection.Delete(relativePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "从 LiteDB 删除图片元数据失败: {Path}", relativePath);
            }
        }

        // 辅助方法：将 DateTime 截断到毫秒，并确保 Kind 为 UTC
        private static DateTime TruncateToMillisecondUtc(DateTime dt)
        {
            if (dt.Kind != DateTimeKind.Utc)
                dt = dt.ToUniversalTime();
            return new DateTime(dt.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        }

        // 获取 UTC 毫秒时间戳（long）
        private static long ToUtcMilliseconds(DateTime dt)
        {
            if (dt.Kind != DateTimeKind.Utc)
                dt = dt.ToUniversalTime();
            return new DateTimeOffset(dt).ToUnixTimeMilliseconds();
        }

        // 标准化相对路径：统一使用 '/'，去除前导 '/'
        private static string NormalizeRelativePath(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
                return relativePath;

            var normalized = relativePath.Replace('\\', '/');
            if (normalized.StartsWith('/'))
                normalized = normalized.TrimStart('/');
            return normalized;
        }

        public async Task<PhotoMetadata?> GetOrExtractMetadataAsync(string relativePath)
        {
            relativePath = NormalizeRelativePath(relativePath);

            if (_cache.TryGetValue(relativePath, out var cached))
            {
                var fullPath = Path.Combine(_rootPath, relativePath);
                var fileInfo = new FileInfo(fullPath);
                if (fileInfo.Exists)
                {
                    var fileModified = TruncateToMillisecondUtc(fileInfo.LastWriteTimeUtc);
                    var cachedModified = TruncateToMillisecondUtc(cached.LastModified);
                    long fileTicks = ToUtcMilliseconds(fileModified);
                    long cachedTicks = ToUtcMilliseconds(cachedModified);

                    // 调试日志
                    _logger.LogDebug(
                        "比较: [{Path}] 文件时间={FileTime}({FileTicks}), 缓存时间={CacheTime}({CacheTicks})",
                        relativePath, fileModified, fileTicks, cachedModified, cachedTicks);

                    if (fileTicks != cachedTicks)
                    {
                        _logger.LogInformation(
                            "文件修改时间变更，重新提取: {Path} (文件时间戳 {FileTicks} vs 缓存时间戳 {CacheTicks})",
                            relativePath, fileTicks, cachedTicks);
                        return await ExtractAndCacheAsync(relativePath);
                    }
                    return cached;
                }
                else
                {
                    // 文件已被删除，清理缓存和数据库
                    _cache.TryRemove(relativePath, out _);
                    DeleteFromDatabase(relativePath);
                    return null;
                }
            }

            var dbDoc = _collection.FindById(relativePath);
            if (dbDoc != null)
            {
                _cache.TryAdd(relativePath, dbDoc);
                return dbDoc;
            }

            return await ExtractAndCacheAsync(relativePath);
        }

        public async Task<Dictionary<string, PhotoMetadata>> GetBatchMetadataAsync(IEnumerable<string> relativePaths)
        {
            var result = new Dictionary<string, PhotoMetadata>();
            foreach (var path in relativePaths)
            {
                var normalized = NormalizeRelativePath(path);
                var meta = await GetOrExtractMetadataAsync(normalized);
                if (meta != null)
                    result[normalized] = meta;
            }
            return result;
        }

        public async Task<(IEnumerable<PhotoMetadata> Items, int TotalCount)> SearchPhotosAsync(PhotoSearchOptions options)
        {
            var query = _cache.Values.AsQueryable();

            if (!string.IsNullOrEmpty(options.DirectoryPath))
            {
                var dirPrefix = NormalizeRelativePath(options.DirectoryPath);
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
            relativePath = NormalizeRelativePath(relativePath);
            _cache.TryRemove(relativePath, out _);
            DeleteFromDatabase(relativePath);
            await ExtractAndCacheAsync(relativePath);
        }

        public async Task ScanAndIndexAllPhotosAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            await ScanConfiguredDirectoriesAsync();
            progress?.Report("全量索引完成（仅扫描配置目录）");
        }

        public async Task ScanConfiguredDirectoriesAsync()
        {
            var indexDirs = _configuration.GetSection("FileServerConfig:PhotoIndexDirectories")
                                          .Get<List<string>>() ?? new List<string>();
            _logger.LogInformation("开始扫描配置的图片索引目录：{Dirs}", string.Join(", ", indexDirs));

            foreach (var dir in indexDirs)
            {
                await ScanDirectoryIncrementallyAsync(dir);
            }
        }

        private async Task ScanDirectoryIncrementallyAsync(string relativeDir, IProgress<string>? progress = null)
        {
            var fullDir = Path.Combine(_rootPath, relativeDir);
            if (!Dir.Exists(fullDir))
            {
                _logger.LogWarning("图片索引目录不存在: {Dir}", fullDir);
                return;
            }

            // 1. 获取磁盘文件信息
            var diskFiles = Dir.GetFiles(fullDir, "*.*", SearchOption.AllDirectories)
                .Where(f => ImageExtensions.Contains(Path.GetExtension(f)))
                .Select(f =>
                {
                    var fi = new FileInfo(f);
                    return new
                    {
                        Path = MakeRelativePath(f),
                        LastWrite = TruncateToMillisecondUtc(fi.LastWriteTimeUtc),
                        Size = fi.Length
                    };
                })
                .ToList();

            _logger.LogInformation("扫描目录 [{Dir}]，发现 {Count} 个图片文件", relativeDir, diskFiles.Count);

            // 2. 从数据库加载该目录下的记录
            var dirPrefix = NormalizeRelativePath(relativeDir);
            if (!dirPrefix.EndsWith('/')) dirPrefix += '/';
            var dbRecords = _collection.Find(x => x.RelativePath.StartsWith(dirPrefix))
                                       .ToDictionary(m => m.RelativePath);

            // 3. 分类变化（仅比较修改时间戳）
            var toAdd = new List<string>();
            var toUpdate = new List<string>();
            var toDelete = new List<string>();

            var diskPaths = new HashSet<string>(diskFiles.Select(f => f.Path));
            foreach (var diskFile in diskFiles)
            {
                if (dbRecords.TryGetValue(diskFile.Path, out var existing))
                {
                    long diskTime = ToUtcMilliseconds(diskFile.LastWrite);
                    long dbTime = ToUtcMilliseconds(existing.LastModified);
                    if (diskTime != dbTime)
                        toUpdate.Add(diskFile.Path);
                }
                else
                {
                    toAdd.Add(diskFile.Path);
                }
            }

            foreach (var dbPath in dbRecords.Keys)
            {
                if (!diskPaths.Contains(dbPath))
                    toDelete.Add(dbPath);
            }

            int totalChanges = toAdd.Count + toUpdate.Count + toDelete.Count;
            if (totalChanges == 0)
            {
                _logger.LogInformation("目录 [{Dir}] 无变化，跳过", relativeDir);
                return;
            }

            _logger.LogInformation("目录 [{Dir}] 变化：新增 {Add}，更新 {Update}，删除 {Delete}",
                relativeDir, toAdd.Count, toUpdate.Count, toDelete.Count);

            // 4. 处理删除
            foreach (var path in toDelete)
            {
                _cache.TryRemove(path, out _);
                DeleteFromDatabase(path);
            }

            // 5. 处理新增和更新
            var toProcess = toAdd.Concat(toUpdate).ToList();
            int processed = 0;
            foreach (var path in toProcess)
            {
                try
                {
                    await ExtractAndCacheAsync(path);
                    processed++;
                    if (processed % 50 == 0)
                        _logger.LogInformation("已处理 {Processed}/{Total} 张图片", processed, toProcess.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "提取图片元数据失败: {Path}", path);
                }
            }

            _logger.LogInformation("目录 [{Dir}] 扫描完成，成功处理 {Processed} 个文件", relativeDir, processed);
        }

        public async Task<bool> DeleteMetadataAsync(string relativePath)
        {
            relativePath = NormalizeRelativePath(relativePath);
            try
            {
                if (_cache.TryRemove(relativePath, out _))
                {
                    DeleteFromDatabase(relativePath);
                    _logger.LogInformation("图片元数据已删除: {Path}", relativePath);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除图片元数据失败: {Path}", relativePath);
                return false;
            }
        }

        public Task<bool> IsEmptyAsync()
        {
            return Task.FromResult(_collection.Count() == 0);
        }

        private async Task<PhotoMetadata?> ExtractAndCacheAsync(string relativePath)
        {
            relativePath = NormalizeRelativePath(relativePath);
            var fullPath = Path.Combine(_rootPath, relativePath);
            if (!File.Exists(fullPath))
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
                if (exifSubIfd != null && exifSubIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dt1))
                    dateTaken = dt1;
                if (!dateTaken.HasValue && exifSubIfd != null && exifSubIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeDigitized, out var dt2))
                    dateTaken = dt2;
                if (!dateTaken.HasValue && ifd0Dir != null && ifd0Dir.TryGetDateTime(ExifIfd0Directory.TagDateTime, out var dt3))
                    dateTaken = dt3;
                if (!dateTaken.HasValue)
                {
                    var pngDir = directories.OfType<MetadataExtractor.Formats.Png.PngDirectory>().FirstOrDefault();
                    if (pngDir != null && pngDir.TryGetDateTime(MetadataExtractor.Formats.Png.PngDirectory.TagLastModificationTime, out var dt4))
                        dateTaken = dt4;
                }
                if (!dateTaken.HasValue)
                {
                    var fileInfoTemp = new FileInfo(fullPath);
                    dateTaken = fileInfoTemp.LastWriteTime;
                }

                double? lat = null, lng = null;
                if (gpsDir != null)
                {
                    var location = gpsDir.GetGeoLocation();
                    if (location.HasValue)
                    {
                        lat = location.Value.Latitude;
                        lng = location.Value.Longitude;
                    }
                }

                string? cameraModel = ifd0Dir?.GetString(ExifIfd0Directory.TagModel);

                int? width = null, height = null;
                var jpegDir = directories.OfType<JpegDirectory>().FirstOrDefault();
                if (jpegDir != null)
                {
                    try { width = jpegDir.GetImageWidth(); height = jpegDir.GetImageHeight(); } catch { }
                }
                if ((!width.HasValue || !height.HasValue) && ifd0Dir != null)
                {
                    try
                    {
                        width ??= ifd0Dir.GetInt32(ExifDirectoryBase.TagImageWidth);
                        height ??= ifd0Dir.GetInt32(ExifDirectoryBase.TagImageHeight);
                    }
                    catch (MetadataException) { }
                }

                var fileInfo = new FileInfo(fullPath);
                var metadata = new PhotoMetadata
                {
                    RelativePath = relativePath,
                    FileName = Path.GetFileName(relativePath),
                    DateTaken = dateTaken.HasValue ? TruncateToMillisecondUtc(dateTaken.Value) : null,
                    Latitude = lat,
                    Longitude = lng,
                    CameraModel = cameraModel,
                    Width = width,
                    Height = height,
                    FileSize = fileInfo.Length,
                    LastModified = TruncateToMillisecondUtc(fileInfo.LastWriteTimeUtc),
                    LastMetadataUpdate = TruncateToMillisecondUtc(DateTime.UtcNow)
                };

                _cache.AddOrUpdate(relativePath, metadata, (_, _) => metadata);
                SaveToDatabase(metadata);

                _logger.LogDebug("已提取并缓存: {Path}", relativePath);
                return metadata;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "提取图片元数据失败: {Path}", relativePath);
                return null;
            }
        }

        private string MakeRelativePath(string fullPath)
        {
            var root = _rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var relative = fullPath.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return NormalizeRelativePath(relative);
        }

        public void Dispose()
        {
            _db?.Dispose();
        }
    }
}