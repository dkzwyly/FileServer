using System.Collections.Concurrent;
using System.Text.Json;
using FileServer.Models;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Jpeg;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Directory = MetadataExtractor.Directory;

namespace FileServer.Services
{
    public class PhotoMetadataService : IPhotoMetadataService, IDisposable
    {
        private readonly ILogger<PhotoMetadataService> _logger;
        private readonly IConfiguration _configuration;

        // ========== 静态字段：全局唯一，跨实例共享 ==========
        private static readonly ConcurrentDictionary<string, PhotoMetadata> _cache = new();
        private static readonly SemaphoreSlim _fileLock = new(1, 1);
        private static Timer? _autoSaveTimer;
        private static bool _isDirty;
        private static readonly object _timerInitLock = new();
        private static string _rootPath = string.Empty;
        private static string _metadataFilePath = string.Empty;

        public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".tiff", ".bmp", ".webp", ".heic", ".heif"
        };

        public PhotoMetadataService(IFileService fileService, ILogger<PhotoMetadataService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;

            // 静态根路径和元数据文件路径只初始化一次
            if (string.IsNullOrEmpty(_rootPath))
            {
                _rootPath = fileService.GetRootPath();
                _metadataFilePath = Path.Combine(_rootPath, ".metadata", "photos.json");
                var dir = Path.GetDirectoryName(_metadataFilePath);
                if (!string.IsNullOrEmpty(dir))
                    System.IO.Directory.CreateDirectory(dir);
            }

            // 加载已有持久化数据（只加载一次）
            if (_cache.IsEmpty)
            {
                LoadFromFile();
            }

            // 初始化静态定时器（仅创建一次）
            if (_autoSaveTimer == null)
            {
                lock (_timerInitLock)
                {
                    if (_autoSaveTimer == null)
                    {
                        _autoSaveTimer = new Timer(async _ => await SaveIfDirty(), null,
                            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
                        _logger.LogInformation("全局元数据自动保存定时器已启动");
                    }
                }
            }
        }

        // ---------- 静态方法：加载与保存 ----------
        private static void LoadFromFile()
        {
            if (!System.IO.File.Exists(_metadataFilePath))
                return;

            _fileLock.Wait();
            try
            {
                var json = System.IO.File.ReadAllText(_metadataFilePath);
                var dict = JsonSerializer.Deserialize<Dictionary<string, PhotoMetadata>>(json);
                if (dict != null)
                {
                    foreach (var kvp in dict)
                        _cache.TryAdd(kvp.Key, kvp.Value);
                    // 日志只能借助实例logger，但这是静态方法；此处忽略日志或传入logger
                }
            }
            catch (Exception ex)
            {
                // 静态方法无法使用实例logger，捕获后不做处理，或输出到控制台
                System.Diagnostics.Debug.WriteLine($"加载元数据文件失败: {ex.Message}");
            }
            finally
            {
                _fileLock.Release();
            }
        }

        private static async Task SaveToFileAsync()
        {
            await _fileLock.WaitAsync();
            try
            {
                var snapshot = _cache.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                await System.IO.File.WriteAllTextAsync(_metadataFilePath, json);
                _isDirty = false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存元数据文件失败: {ex.Message}");
            }
            finally
            {
                _fileLock.Release();
            }
        }

        private static async Task SaveIfDirty()
        {
            if (!_isDirty) return;
            await SaveToFileAsync();
        }

        private static void MarkDirty() => _isDirty = true;

        // ---------- 实例方法：扫描接口 ----------
        public async Task ScanConfiguredDirectoriesAsync()
        {
            var indexDirs = _configuration.GetSection("FileServerConfig:PhotoIndexDirectories")
                                          .Get<List<string>>() ?? new List<string>();
            _logger.LogInformation("开始扫描配置的图片索引目录：{Dirs}", string.Join(", ", indexDirs));

            foreach (var dir in indexDirs)
            {
                await ScanDirectoryAsync(dir);
            }
        }

        public async Task ScanDirectoryAsync(string relativeDir, IProgress<string>? progress = null)
        {
            var fullDir = Path.Combine(_rootPath, relativeDir);
            if (!System.IO.Directory.Exists(fullDir))
            {
                _logger.LogWarning("图片索引目录不存在: {Dir}", fullDir);
                return;
            }

            var allFiles = System.IO.Directory.GetFiles(fullDir, "*.*", SearchOption.AllDirectories)
                .Where(f => ImageExtensions.Contains(Path.GetExtension(f)))
                .Select(f => MakeRelativePath(f))
                .ToList();

            _logger.LogInformation("扫描目录 [{Dir}]，发现 {Count} 个文件", relativeDir, allFiles.Count);
            int processed = 0;
            foreach (var relPath in allFiles)
            {
                await ExtractAndCacheAsync(relPath);
                processed++;
                if (processed % 50 == 0)
                    _logger.LogInformation("已扫描 {Processed}/{Total} 张图片", processed, allFiles.Count);
            }

            await SaveToFileAsync();
            _logger.LogInformation("目录 [{Dir}] 扫描完成，新增/更新 {Count} 条元数据", relativeDir, processed);
        }

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

        public async Task<Dictionary<string, PhotoMetadata>> GetBatchMetadataAsync(IEnumerable<string> relativePaths)
        {
            var result = new Dictionary<string, PhotoMetadata>();
            foreach (var path in relativePaths)
            {
                var meta = await GetOrExtractMetadataAsync(path);
                if (meta != null) result[path] = meta;
            }
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
            // 仅扫描配置目录，保持与自动扫描一致
            await ScanConfiguredDirectoriesAsync();
            progress?.Report("全量索引完成（仅扫描配置目录）");
        }

        // ---------- 私有方法：提取、路径转换 ----------
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

                // ===== 提取拍摄日期（依次尝试多个 EXIF 标签）=====
                DateTime? dateTaken = null;

                // 1. Exif SubIFD 的原始拍摄时间
                if (exifSubIfd != null && exifSubIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dt1))
                    dateTaken = dt1;

                // 2. Exif SubIFD 的数字化时间
                if (!dateTaken.HasValue && exifSubIfd != null && exifSubIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeDigitized, out var dt2))
                    dateTaken = dt2;

                // 3. IFD0 的主日期时间
                if (!dateTaken.HasValue && ifd0Dir != null && ifd0Dir.TryGetDateTime(ExifIfd0Directory.TagDateTime, out var dt3))
                    dateTaken = dt3;

                // 4. 尝试 PNG 文件的最后修改时间（如果文件是 PNG）
                if (!dateTaken.HasValue)
                {
                    var pngDir = directories.OfType<MetadataExtractor.Formats.Png.PngDirectory>().FirstOrDefault();
                    if (pngDir != null && pngDir.TryGetDateTime(MetadataExtractor.Formats.Png.PngDirectory.TagLastModificationTime, out var dt4))
                        dateTaken = dt4;
                }

                // 5. 若所有标签均无，回退到文件修改时间，保证排序可用
                if (!dateTaken.HasValue)
                {
                    var fileInfoTemp = new FileInfo(fullPath);
                    dateTaken = fileInfoTemp.LastWriteTime;
                }

                // ===== GPS 信息 =====
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

                // ===== 相机型号 =====
                string? cameraModel = ifd0Dir?.GetString(ExifIfd0Directory.TagModel);

                // ===== 图片尺寸（安全读取，防止缺失标签时崩溃）=====
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
                    try
                    {
                        width ??= ifd0Dir.GetInt32(ExifDirectoryBase.TagImageWidth);
                        height ??= ifd0Dir.GetInt32(ExifDirectoryBase.TagImageHeight);
                    }
                    catch (MetadataException) { /* 忽略缺失的标签 */ }
                }

                // ===== 构造元数据对象 =====
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

        private string MakeRelativePath(string fullPath)
        {
            var root = _rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var relative = fullPath.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return relative.Replace('\\', '/');
        }

        public void Dispose()
        {
            // 实例释放时不销毁静态定时器和锁，全局资源由应用程序生命周期管理
            // 仅做最后一次保存（如果当前实例修改了缓存）
            SaveToFileAsync().GetAwaiter().GetResult();
        }
    }
}