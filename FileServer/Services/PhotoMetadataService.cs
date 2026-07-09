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
        private readonly IFileTreeCacheService _fileTreeCacheService;

        // 内存缓存：以路径为键，方便快速获取
        private static readonly ConcurrentDictionary<string, PhotoMetadata> _cacheByPath = new();

        // 辅助映射：指纹 -> 路径（用于快速更新缓存）
        private static readonly ConcurrentDictionary<string, string> _fingerprintToPath = new();

        public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".tiff", ".bmp", ".webp", ".heic", ".heif", ".gif"
        };

        public PhotoMetadataService(IFileService fileService, ILogger<PhotoMetadataService> logger, IFileTreeCacheService fileTreeCacheService, IConfiguration configuration)
        {
            _logger = logger;
            _fileTreeCacheService = fileTreeCacheService;
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
            // 以 Fingerprint 作为唯一索引
            _collection.EnsureIndex(x => x.Fingerprint, unique: true);
            // 保留 RelativePath 索引用于搜索
            _collection.EnsureIndex(x => x.RelativePath);
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
                {
                    _cacheByPath[doc.RelativePath] = doc;
                    if (!string.IsNullOrEmpty(doc.Fingerprint))
                        _fingerprintToPath[doc.Fingerprint] = doc.RelativePath;
                }
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

        private void DeleteFromDatabase(string fingerprint)
        {
            try
            {
                _collection.DeleteMany(x => x.Fingerprint == fingerprint);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "从 LiteDB 删除图片元数据失败: {Fingerprint}", fingerprint);
            }
        }

        private static DateTime TruncateToMillisecondUtc(DateTime dt)
        {
            if (dt.Kind != DateTimeKind.Utc)
                dt = dt.ToUniversalTime();
            return new DateTime(dt.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        }

        private static long ToUtcMilliseconds(DateTime dt)
        {
            if (dt.Kind != DateTimeKind.Utc)
                dt = dt.ToUniversalTime();
            return new DateTimeOffset(dt).ToUnixTimeMilliseconds();
        }

        private static string NormalizeRelativePath(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
                return relativePath;
            var normalized = relativePath.Replace('\\', '/');
            if (normalized.StartsWith('/'))
                normalized = normalized.TrimStart('/');
            return normalized;
        }

        // 计算指纹
        private string ComputeFingerprint(long size, DateTime lastWriteUtc)
        {
            var ticks = lastWriteUtc.Ticks;
            return $"{size}_{ticks}";
        }

        // ----- 实现 IPhotoMetadataService 接口 -----

        public async Task<PhotoMetadata?> GetMetadataByFingerprintAsync(string fingerprint)
        {
            // 先从缓存找路径，再取元数据
            if (_fingerprintToPath.TryGetValue(fingerprint, out var path))
            {
                if (_cacheByPath.TryGetValue(path, out var meta))
                    return meta;
            }
            // 查数据库
            var doc = _collection.FindOne(x => x.Fingerprint == fingerprint);
            if (doc != null)
            {
                _cacheByPath[doc.RelativePath] = doc;
                _fingerprintToPath[fingerprint] = doc.RelativePath;
                return doc;
            }
            return null;
        }

        public async Task SaveMetadataByFingerprintAsync(string fingerprint, string path, PhotoMetadata metadata)
        {
            path = NormalizeRelativePath(path);
            metadata.Fingerprint = fingerprint;
            metadata.RelativePath = path;
            metadata.LastMetadataUpdate = TruncateToMillisecondUtc(DateTime.UtcNow);
            // 更新缓存
            _cacheByPath[path] = metadata;
            _fingerprintToPath[fingerprint] = path;
            SaveToDatabase(metadata);
            await Task.CompletedTask;
        }

        public async Task<PhotoMetadata?> GetOrExtractMetadataAsync(string relativePath)
        {
            relativePath = NormalizeRelativePath(relativePath);
            var fullPath = Path.Combine(_rootPath, relativePath);
            if (!File.Exists(fullPath))
                return null;

            var fileInfo = new FileInfo(fullPath);
            var size = fileInfo.Length;
            var lastWrite = TruncateToMillisecondUtc(fileInfo.LastWriteTimeUtc);
            var fingerprint = ComputeFingerprint(size, lastWrite);

            // 尝试从缓存/数据库获取
            var existing = await GetMetadataByFingerprintAsync(fingerprint);
            if (existing != null)
            {
                // 如果路径不同，更新路径并保存
                if (existing.RelativePath != relativePath)
                {
                    // 移除旧路径缓存
                    _cacheByPath.TryRemove(existing.RelativePath, out _);
                    // 更新路径
                    existing.RelativePath = relativePath;
                    _cacheByPath[relativePath] = existing;
                    _fingerprintToPath[fingerprint] = relativePath;
                    SaveToDatabase(existing);
                }
                return existing;
            }

            // 不存在则提取
            var metadata = await ExtractAndCacheAsync(relativePath);
            return metadata;
        }

        public async Task<Dictionary<string, PhotoMetadata>> GetBatchMetadataAsync(IEnumerable<string> relativePaths)
        {
            var result = new Dictionary<string, PhotoMetadata>();
            var toProcess = new List<string>();
            foreach (var rawPath in relativePaths)
            {
                var path = NormalizeRelativePath(rawPath);
                if (_cacheByPath.TryGetValue(path, out var cached))
                {
                    result[path] = cached;
                }
                else
                {
                    toProcess.Add(path);
                }
            }

            if (toProcess.Any())
            {
                // 计算所有文件的指纹并批量查询
                var fingerprints = new List<string>();
                var pathToFingerprint = new Dictionary<string, string>();
                foreach (var path in toProcess)
                {
                    var fullPath = Path.Combine(_rootPath, path);
                    if (File.Exists(fullPath))
                    {
                        var fi = new FileInfo(fullPath);
                        var fp = ComputeFingerprint(fi.Length, TruncateToMillisecondUtc(fi.LastWriteTimeUtc));
                        fingerprints.Add(fp);
                        pathToFingerprint[path] = fp;
                    }
                }

                var dbResults = _collection.Find(x => fingerprints.Contains(x.Fingerprint)).ToList();
                foreach (var doc in dbResults)
                {
                    // 更新路径（如果数据库中的路径和当前请求路径不同，以当前请求为准）
                    var currentPath = pathToFingerprint.FirstOrDefault(kv => kv.Value == doc.Fingerprint).Key;
                    if (!string.IsNullOrEmpty(currentPath) && doc.RelativePath != currentPath)
                    {
                        doc.RelativePath = currentPath;
                        SaveToDatabase(doc);
                    }
                    _cacheByPath[doc.RelativePath] = doc;
                    _fingerprintToPath[doc.Fingerprint] = doc.RelativePath;
                    result[doc.RelativePath] = doc;
                }

                // 对未在数据库中的文件进行提取
                foreach (var path in toProcess)
                {
                    if (!result.ContainsKey(path))
                    {
                        var meta = await ExtractAndCacheAsync(path);
                        if (meta != null)
                            result[path] = meta;
                    }
                }
            }

            return result;
        }

        public async Task<(IEnumerable<PhotoMetadata> Items, int TotalCount)> SearchPhotosAsync(PhotoSearchOptions options)
        {
            // 1. 获取目标路径列表（从树缓存）
            List<string> targetPaths = new List<string>();
            if (!string.IsNullOrEmpty(options.DirectoryPath))
            {
                // 从树缓存获取所有文件路径（纯内存，或触发按需增量扫描）
                var allPaths = await _fileTreeCacheService.GetAllFilePathsAsync(options.DirectoryPath);
                // 只保留图片文件（按扩展名过滤）
                targetPaths = allPaths
                    .Where(p => ImageExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
                    .ToList();
            }
            else
            {
                // 未指定目录：可以选择从根目录获取所有，或抛出异常要求必须指定。
                // 这里采用从根目录获取所有图片（若数据量极大可能耗时，但可接受）
                var allPaths = await _fileTreeCacheService.GetAllFilePathsAsync("");
                targetPaths = allPaths
                    .Where(p => ImageExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
                    .ToList();
            }

            // 2. 批量获取这些路径的元数据（内部会走缓存 + 数据库，不会触发磁盘提取除非文件从未索引过）
            var metadataDict = await GetBatchMetadataAsync(targetPaths);

            // 3. 内存过滤（日期、GPS）
            var query = metadataDict.Values.AsQueryable();

            if (options.StartDate.HasValue)
                query = query.Where(m => m.DateTaken >= options.StartDate.Value);
            if (options.EndDate.HasValue)
                query = query.Where(m => m.DateTaken <= options.EndDate.Value);
            if (options.MinLatitude.HasValue && options.MaxLatitude.HasValue)
                query = query.Where(m => m.Latitude >= options.MinLatitude && m.Latitude <= options.MaxLatitude);
            if (options.MinLongitude.HasValue && options.MaxLongitude.HasValue)
                query = query.Where(m => m.Longitude >= options.MinLongitude && m.Longitude <= options.MaxLongitude);

            // 4. 排序
            var sortBy = options.SortBy?.ToLowerInvariant() ?? "datetaken";
            var ordered = sortBy switch
            {
                "name" => options.SortAscending ? query.OrderBy(m => m.FileName) : query.OrderByDescending(m => m.FileName),
                "size" => options.SortAscending ? query.OrderBy(m => m.FileSize) : query.OrderByDescending(m => m.FileSize),
                "modified" => options.SortAscending ? query.OrderBy(m => m.LastModified) : query.OrderByDescending(m => m.LastModified),
                _ => options.SortAscending ? query.OrderBy(m => m.DateTaken) : query.OrderByDescending(m => m.DateTaken),
            };

            var totalCount = ordered.Count();
            var items = ordered.Skip(options.Skip).Take(options.Take).ToList();

            // 注意：GetBatchMetadataAsync 已经更新了内存缓存，此处无需再手动更新
            return (items, totalCount);
        }

        public async Task RefreshMetadataAsync(string relativePath)
        {
            relativePath = NormalizeRelativePath(relativePath);
            var fullPath = Path.Combine(_rootPath, relativePath);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"文件不存在: {relativePath}");

            // 先删除旧记录（按指纹）
            var fi = new FileInfo(fullPath);
            var fingerprint = ComputeFingerprint(fi.Length, TruncateToMillisecondUtc(fi.LastWriteTimeUtc));
            // 移除缓存
            _cacheByPath.TryRemove(relativePath, out _);
            _fingerprintToPath.TryRemove(fingerprint, out _);
            DeleteFromDatabase(fingerprint);

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
                        Size = fi.Length,
                        Fingerprint = ComputeFingerprint(fi.Length, TruncateToMillisecondUtc(fi.LastWriteTimeUtc))
                    };
                })
                .ToList();

            _logger.LogInformation("扫描目录 [{Dir}]，发现 {Count} 个图片文件", relativeDir, diskFiles.Count);

            // 2. 从数据库加载该目录下的记录（以指纹为键）
            var dirPrefix = NormalizeRelativePath(relativeDir);
            if (!dirPrefix.EndsWith('/')) dirPrefix += '/';
            var dbRecords = _collection.Find(x => x.RelativePath.StartsWith(dirPrefix))
                                       .ToDictionary(m => m.Fingerprint);

            // 3. 分类变化
            var toAdd = new List<string>();      // 指纹
            var toUpdate = new List<string>();   // 指纹
            var toDelete = new List<string>();   // 指纹

            var diskFingerprints = new HashSet<string>(diskFiles.Select(f => f.Fingerprint));
            var diskPathByFingerprint = diskFiles.ToDictionary(f => f.Fingerprint, f => f.Path);

            foreach (var diskFile in diskFiles)
            {
                if (dbRecords.TryGetValue(diskFile.Fingerprint, out var existing))
                {
                    // 如果路径变了，更新路径
                    if (existing.RelativePath != diskFile.Path)
                        toUpdate.Add(diskFile.Fingerprint);
                    // 如果修改时间变了，重新提取（但指纹包含了修改时间，所以指纹变了就是新文件）
                    // 由于指纹包含了修改时间，如果文件内容变化，指纹会变，这里不会走到，因为指纹变了就是新文件
                }
                else
                {
                    toAdd.Add(diskFile.Fingerprint);
                }
            }

            foreach (var dbFp in dbRecords.Keys)
            {
                if (!diskFingerprints.Contains(dbFp))
                    toDelete.Add(dbFp);
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
            foreach (var fp in toDelete)
            {
                if (_fingerprintToPath.TryGetValue(fp, out var path))
                {
                    _cacheByPath.TryRemove(path, out _);
                    _fingerprintToPath.TryRemove(fp, out _);
                }
                DeleteFromDatabase(fp);
            }

            // 5. 处理新增和更新
            var toProcess = toAdd.Concat(toUpdate).ToList();
            int processed = 0;
            foreach (var fp in toProcess)
            {
                if (diskPathByFingerprint.TryGetValue(fp, out var path))
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
            }

            _logger.LogInformation("目录 [{Dir}] 扫描完成，成功处理 {Processed} 个文件", relativeDir, processed);
        }

        public async Task<bool> DeleteMetadataAsync(string relativePath)
        {
            relativePath = NormalizeRelativePath(relativePath);
            // 获取指纹
            if (_cacheByPath.TryGetValue(relativePath, out var meta))
            {
                var fp = meta.Fingerprint;
                _cacheByPath.TryRemove(relativePath, out _);
                _fingerprintToPath.TryRemove(fp, out _);
                DeleteFromDatabase(fp);
                return true;
            }
            else
            {
                // 通过数据库查询
                var doc = _collection.FindOne(x => x.RelativePath == relativePath);
                if (doc != null)
                {
                    DeleteFromDatabase(doc.Fingerprint);
                    return true;
                }
            }
            return false;
        }

        public async Task DeleteMetadataByFingerprintAsync(string fingerprint)
        {
            if (_fingerprintToPath.TryGetValue(fingerprint, out var path))
            {
                _cacheByPath.TryRemove(path, out _);
                _fingerprintToPath.TryRemove(fingerprint, out _);
            }
            DeleteFromDatabase(fingerprint);
            await Task.CompletedTask;
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
                var lastWrite = TruncateToMillisecondUtc(fileInfo.LastWriteTimeUtc);
                var fingerprint = ComputeFingerprint(fileInfo.Length, lastWrite);

                var metadata = new PhotoMetadata
                {
                    Fingerprint = fingerprint,
                    RelativePath = relativePath,
                    FileName = Path.GetFileName(relativePath),
                    DateTaken = dateTaken.HasValue ? TruncateToMillisecondUtc(dateTaken.Value) : null,
                    Latitude = lat,
                    Longitude = lng,
                    CameraModel = cameraModel,
                    Width = width,
                    Height = height,
                    FileSize = fileInfo.Length,
                    LastModified = lastWrite,
                    LastMetadataUpdate = TruncateToMillisecondUtc(DateTime.UtcNow)
                };

                // 更新缓存
                _cacheByPath[relativePath] = metadata;
                _fingerprintToPath[fingerprint] = relativePath;
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