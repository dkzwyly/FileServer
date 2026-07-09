using System.Collections.Concurrent;
using System.Diagnostics;
using FileServer.Models;
using LiteDB;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TagLib;
using SystemFile = System.IO.File;
using TagFile = TagLib.File;

namespace FileServer.Services;

public class AudioMetadataService : IAudioMetadataService, IDisposable
{
    private readonly ILogger<AudioMetadataService> _logger;
    private readonly string _coversDirectory;
    private readonly string _rootPath;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<SongMetadata> _collection;

    // 内存缓存：路径 -> 元数据
    private static readonly ConcurrentDictionary<string, SongMetadata> _cacheByPath = new();
    // 辅助映射：指纹 -> 路径
    private static readonly ConcurrentDictionary<string, string> _fingerprintToPath = new();

    public AudioMetadataService(ILogger<AudioMetadataService> logger, IConfiguration configuration)
    {
        _logger = logger;

        var rootPath = configuration["FileServerConfig:RootPath"]
            ?? throw new InvalidOperationException("未配置 FileServerConfig:RootPath");
        _rootPath = Path.GetFullPath(rootPath);

        var coversDir = configuration["FileServerConfig:CoversDirectory"] ?? "系统文件/covers";
        _coversDirectory = Path.Combine(_rootPath, coversDir);
        if (!Directory.Exists(_coversDirectory))
            Directory.CreateDirectory(_coversDirectory);

        var dbPath = configuration["FileServerConfig:AudioLiteDbPath"];
        if (string.IsNullOrEmpty(dbPath))
            dbPath = Path.Combine(_rootPath, "audio-metadata.db");
        else
            dbPath = Path.GetFullPath(Path.Combine(_rootPath, dbPath));

        var dbDir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dbDir) && !Directory.Exists(dbDir))
            Directory.CreateDirectory(dbDir);

        _db = new LiteDatabase(dbPath);
        _collection = _db.GetCollection<SongMetadata>("songMetadata");
        // 以 Fingerprint 作为唯一索引
        _collection.EnsureIndex(x => x.Fingerprint, unique: true);
        // 保留 FilePath 索引用于搜索
        _collection.EnsureIndex(x => x.FilePath);

        LoadFromDatabase();
        _logger.LogInformation("AudioMetadataService 初始化完成，数据库路径: {DbPath}", dbPath);
    }

    private void LoadFromDatabase()
    {
        try
        {
            var all = _collection.FindAll().ToList();
            foreach (var doc in all)
            {
                _cacheByPath[doc.FilePath] = doc;
                if (!string.IsNullOrEmpty(doc.Fingerprint))
                    _fingerprintToPath[doc.Fingerprint] = doc.FilePath;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "从 LiteDB 加载数据失败");
        }
    }

    private void SaveToDatabase(SongMetadata metadata)
    {
        try
        {
            _collection.Upsert(metadata);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存元数据到 LiteDB 失败: {FilePath}", metadata.FilePath);
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
            _logger.LogError(ex, "从 LiteDB 删除失败: {Fingerprint}", fingerprint);
        }
    }

    private string ComputeFingerprint(long size, DateTime lastWriteUtc)
    {
        return $"{size}_{lastWriteUtc.Ticks}";
    }

    // ----- 实现 IAudioMetadataService -----

    public async Task<SongMetadata?> GetMetadataByFingerprintAsync(string fingerprint)
    {
        if (_fingerprintToPath.TryGetValue(fingerprint, out var path) && _cacheByPath.TryGetValue(path, out var cached))
            return cached;
        var doc = _collection.FindOne(x => x.Fingerprint == fingerprint);
        if (doc != null)
        {
            _cacheByPath[doc.FilePath] = doc;
            _fingerprintToPath[fingerprint] = doc.FilePath;
            return doc;
        }
        return null;
    }

    public async Task SaveMetadataByFingerprintAsync(string fingerprint, string path, SongMetadata metadata)
    {
        metadata.Fingerprint = fingerprint;
        metadata.FilePath = path;
        metadata.LastModified = SystemFile.GetLastWriteTimeUtc(path);
        _cacheByPath[path] = metadata;
        _fingerprintToPath[fingerprint] = path;
        SaveToDatabase(metadata);
        await Task.CompletedTask;
    }

    public async Task<SongMetadata> GetMetadataAsync(string filePath)
    {
        if (!SystemFile.Exists(filePath))
            return FallbackMetadata(filePath);

        var fi = new FileInfo(filePath);
        var lastWrite = fi.LastWriteTimeUtc;
        var fingerprint = ComputeFingerprint(fi.Length, lastWrite);

        // 尝试通过指纹获取
        var existing = await GetMetadataByFingerprintAsync(fingerprint);
        if (existing != null)
        {
            // 如果路径不同，更新路径
            if (existing.FilePath != filePath)
            {
                _cacheByPath.TryRemove(existing.FilePath, out _);
                existing.FilePath = filePath;
                _cacheByPath[filePath] = existing;
                _fingerprintToPath[fingerprint] = filePath;
                SaveToDatabase(existing);
            }
            return existing;
        }

        // 不存在则提取
        return await ExtractAndCacheAsync(filePath);
    }

    public async Task<SongMetadata?> GetMetadataMappingAsync(string songPath)
    {
        _cacheByPath.TryGetValue(songPath, out var meta);
        return meta;
    }

    public async Task<bool> SaveMetadataMappingAsync(string songPath, SongMetadata metadata)
    {
        try
        {
            // 如果已经有自定义封面，保留
            if (_cacheByPath.TryGetValue(songPath, out var existing) && !string.IsNullOrEmpty(existing.CustomCoverPath))
            {
                metadata.CustomCoverPath = existing.CustomCoverPath;
                metadata.HasCover = true;
            }
            // 计算指纹
            var fi = new FileInfo(songPath);
            var fingerprint = ComputeFingerprint(fi.Length, fi.LastWriteTimeUtc);
            metadata.Fingerprint = fingerprint;
            metadata.FilePath = songPath;
            _cacheByPath[songPath] = metadata;
            _fingerprintToPath[fingerprint] = songPath;
            SaveToDatabase(metadata);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存元数据映射失败: {SongPath}", songPath);
            return false;
        }
    }

    public async Task<bool> DeleteMetadataMappingAsync(string songPath)
    {
        try
        {
            if (_cacheByPath.TryGetValue(songPath, out var meta))
            {
                var fp = meta.Fingerprint;
                if (!string.IsNullOrEmpty(meta.CustomCoverPath))
                {
                    var coverFullPath = Path.Combine(_coversDirectory, meta.CustomCoverPath);
                    if (SystemFile.Exists(coverFullPath))
                        SystemFile.Delete(coverFullPath);
                }
                _cacheByPath.TryRemove(songPath, out _);
                _fingerprintToPath.TryRemove(fp, out _);
                DeleteFromDatabase(fp);
            }
            else
            {
                // 通过数据库查找
                var doc = _collection.FindOne(x => x.FilePath == songPath);
                if (doc != null)
                {
                    if (!string.IsNullOrEmpty(doc.CustomCoverPath))
                    {
                        var coverFullPath = Path.Combine(_coversDirectory, doc.CustomCoverPath);
                        if (SystemFile.Exists(coverFullPath))
                            SystemFile.Delete(coverFullPath);
                    }
                    DeleteFromDatabase(doc.Fingerprint);
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除元数据映射失败: {SongPath}", songPath);
            return false;
        }
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

    public async Task<Stream?> GetAlbumCoverAsync(string filePath)
    {
        try
        {
            if (_cacheByPath.TryGetValue(filePath, out var metadata) && !string.IsNullOrEmpty(metadata.CustomCoverPath))
            {
                var coverFullPath = Path.Combine(_coversDirectory, metadata.CustomCoverPath);
                if (SystemFile.Exists(coverFullPath))
                {
                    _logger.LogInformation("返回自定义封面: {Path}", coverFullPath);
                    return new FileStream(coverFullPath, FileMode.Open, FileAccess.Read);
                }
                _logger.LogWarning("自定义封面文件丢失: {Path}", coverFullPath);
            }

            using var file = TagFile.Create(filePath);
            var picture = file.Tag.Pictures?.FirstOrDefault();
            if (picture != null)
            {
                _logger.LogInformation("返回内嵌封面: {Path}", filePath);
                return new MemoryStream(picture.Data.Data);
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取专辑封面失败: {Path}", filePath);
            return null;
        }
    }

    public async Task<string?> SaveCustomCoverAsync(string songPath, Stream coverStream, string fileName)
    {
        try
        {
            var ext = Path.GetExtension(fileName);
            var safeFileName = $"{Guid.NewGuid():N}{ext}";
            var coverFullPath = Path.Combine(_coversDirectory, safeFileName);

            await using (var fileStream = new FileStream(coverFullPath, FileMode.Create))
            {
                await coverStream.CopyToAsync(fileStream);
            }

            // 获取或创建元数据
            var meta = await GetMetadataAsync(songPath);
            meta.CustomCoverPath = safeFileName;
            meta.HasCover = true;
            await SaveMetadataMappingAsync(songPath, meta);

            _logger.LogInformation("保存自定义封面成功: {SongPath} -> {CoverPath}", songPath, safeFileName);
            return safeFileName;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存自定义封面失败: {SongPath}", songPath);
            return null;
        }
    }

    public async Task<bool> DeleteCustomCoverAsync(string songPath)
    {
        try
        {
            if (_cacheByPath.TryGetValue(songPath, out var metadata) && !string.IsNullOrEmpty(metadata.CustomCoverPath))
            {
                var coverFullPath = Path.Combine(_coversDirectory, metadata.CustomCoverPath);
                if (SystemFile.Exists(coverFullPath))
                    SystemFile.Delete(coverFullPath);

                metadata.CustomCoverPath = null;
                metadata.HasCover = false;
                SaveToDatabase(metadata);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除自定义封面失败: {SongPath}", songPath);
            return false;
        }
    }

    public async Task ScanAndIndexAllAsync(string musicDirectory, IProgress<string>? progress = null)
    {
        if (!Directory.Exists(musicDirectory))
        {
            progress?.Report($"音乐目录不存在: {musicDirectory}");
            _logger.LogWarning("增量索引失败：目录不存在 {Directory}", musicDirectory);
            return;
        }

        var audioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".flac", ".wav", ".m4a", ".ogg", ".wma", ".aac", ".ape", ".wv", ".opus"
        };

        var diskFiles = Directory.GetFiles(musicDirectory, "*.*", SearchOption.AllDirectories)
            .Where(f => audioExtensions.Contains(Path.GetExtension(f)))
            .Select(f =>
            {
                var fi = new FileInfo(f);
                return new
                {
                    Path = f,
                    LastWrite = fi.LastWriteTimeUtc,
                    Size = fi.Length,
                    Fingerprint = ComputeFingerprint(fi.Length, fi.LastWriteTimeUtc)
                };
            })
            .ToList();

        progress?.Report($"扫描到 {diskFiles.Count} 个音频文件，正在与数据库比对...");
        _logger.LogInformation("增量索引：磁盘文件数 {Count}", diskFiles.Count);

        var dbRecords = _collection.FindAll().ToDictionary(m => m.Fingerprint);
        var diskFingerprints = new HashSet<string>(diskFiles.Select(f => f.Fingerprint));
        var diskPathByFingerprint = diskFiles.ToDictionary(f => f.Fingerprint, f => f.Path);

        var toAdd = new List<string>();
        var toDelete = new List<string>();

        foreach (var fp in diskFingerprints)
        {
            if (!dbRecords.ContainsKey(fp))
                toAdd.Add(fp);
        }
        foreach (var fp in dbRecords.Keys)
        {
            if (!diskFingerprints.Contains(fp))
                toDelete.Add(fp);
        }

        int totalChanges = toAdd.Count + toDelete.Count;
        if (totalChanges == 0)
        {
            progress?.Report("没有检测到任何变化，跳过索引。");
            _logger.LogInformation("增量索引：无变化，跳过");
            return;
        }

        progress?.Report($"检测到变化：新增 {toAdd.Count}，删除 {toDelete.Count}");

        foreach (var fp in toDelete)
        {
            if (_fingerprintToPath.TryGetValue(fp, out var path))
            {
                if (!string.IsNullOrEmpty(_cacheByPath[path]?.CustomCoverPath))
                {
                    var coverFullPath = Path.Combine(_coversDirectory, _cacheByPath[path].CustomCoverPath);
                    if (SystemFile.Exists(coverFullPath))
                        SystemFile.Delete(coverFullPath);
                }
                _cacheByPath.TryRemove(path, out _);
                _fingerprintToPath.TryRemove(fp, out _);
            }
            DeleteFromDatabase(fp);
        }

        int processed = 0;
        foreach (var fp in toAdd)
        {
            if (diskPathByFingerprint.TryGetValue(fp, out var path))
            {
                try
                {
                    await ExtractAndCacheAsync(path);
                    processed++;
                    if (processed % 10 == 0)
                        progress?.Report($"索引进度: {processed}/{toAdd.Count}");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "索引文件失败: {Path}", path);
                }
            }
        }

        progress?.Report($"增量索引完成，成功处理 {processed}/{toAdd.Count} 个新增文件");
        _logger.LogInformation("增量索引完成，处理了 {Processed} 个文件，删除了 {Deleted} 个", processed, toDelete.Count);
    }

    private async Task<SongMetadata> ExtractAndCacheAsync(string filePath)
    {
        var metadata = ExtractMetadataFromFile(filePath);
        var fi = new FileInfo(filePath);
        var fingerprint = ComputeFingerprint(fi.Length, fi.LastWriteTimeUtc);
        metadata.Fingerprint = fingerprint;
        metadata.FilePath = filePath;
        metadata.LastModified = fi.LastWriteTimeUtc;

        // 如果有旧的自定义封面，保留
        if (_cacheByPath.TryGetValue(filePath, out var old) && !string.IsNullOrEmpty(old.CustomCoverPath))
        {
            metadata.CustomCoverPath = old.CustomCoverPath;
            metadata.HasCover = true;
        }

        _cacheByPath[filePath] = metadata;
        _fingerprintToPath[fingerprint] = filePath;
        SaveToDatabase(metadata);
        return metadata;
    }

    private SongMetadata ExtractMetadataFromFile(string filePath)
    {
        using var file = TagFile.Create(filePath);
        return new SongMetadata
        {
            Title = string.IsNullOrEmpty(file.Tag.Title)
                ? Path.GetFileNameWithoutExtension(filePath)
                : file.Tag.Title,
            Artist = string.IsNullOrEmpty(file.Tag.FirstPerformer)
                ? "未知艺术家"
                : file.Tag.FirstPerformer,
            Album = string.IsNullOrEmpty(file.Tag.Album)
                ? "未知专辑"
                : file.Tag.Album,
            HasCover = file.Tag.Pictures != null && file.Tag.Pictures.Length > 0,
            CustomCoverPath = null,
        };
    }

    private SongMetadata FallbackMetadata(string filePath)
    {
        return new SongMetadata
        {
            FilePath = filePath,
            Title = Path.GetFileNameWithoutExtension(filePath),
            Artist = "未知艺术家",
            Album = "未知专辑",
            HasCover = false,
            CustomCoverPath = null,
            LastModified = null
        };
    }

    public async Task<Dictionary<string, SongMetadata>> GetBatchMetadataAsync(List<string> paths)
    {
        var sw = Stopwatch.StartNew();
        var result = new Dictionary<string, SongMetadata>();
        var missingPaths = new List<string>();

        // 1. 内存缓存
        foreach (var path in paths)
        {
            if (_cacheByPath.TryGetValue(path, out var cached))
                result[path] = cached;
            else
                missingPaths.Add(path);
        }
        _logger.LogInformation("Step1 Cache hit: {HitCount}, missing: {MissingCount}, elapsed: {Elapsed}ms",
            result.Count, missingPaths.Count, sw.ElapsedMilliseconds);

        // 2. 批量查询数据库（通过指纹）
        if (missingPaths.Any())
        {
            sw.Restart();
            var fingerprints = new List<string>();
            var pathToFingerprint = new Dictionary<string, string>();
            foreach (var path in missingPaths)
            {
                if (SystemFile.Exists(path))
                {
                    var fi = new FileInfo(path);
                    var fp = ComputeFingerprint(fi.Length, fi.LastWriteTimeUtc);
                    fingerprints.Add(fp);
                    pathToFingerprint[path] = fp;
                }
            }

            var dbResults = _collection.Find(x => fingerprints.Contains(x.Fingerprint)).ToList();
            _logger.LogInformation("Step2 LiteDB found: {DbCount}, elapsed: {Elapsed}ms",
                dbResults.Count, sw.ElapsedMilliseconds);

            sw.Restart();
            foreach (var meta in dbResults)
            {
                // 如果路径变了，更新
                var currentPath = pathToFingerprint.FirstOrDefault(kv => kv.Value == meta.Fingerprint).Key;
                if (!string.IsNullOrEmpty(currentPath) && meta.FilePath != currentPath)
                {
                    meta.FilePath = currentPath;
                    SaveToDatabase(meta);
                }
                _cacheByPath[meta.FilePath] = meta;
                _fingerprintToPath[meta.Fingerprint] = meta.FilePath;
                result[meta.FilePath] = meta;
                missingPaths.Remove(meta.FilePath);
            }
            _logger.LogInformation("Step2 update cache elapsed: {Elapsed}ms", sw.ElapsedMilliseconds);
        }

        // 3. 实时提取
        if (missingPaths.Any())
        {
            sw.Restart();
            foreach (var path in missingPaths)
            {
                try
                {
                    var meta = await ExtractAndCacheAsync(path);
                    result[path] = meta;
                }
                catch
                {
                    result[path] = FallbackMetadata(path);
                }
            }
            _logger.LogInformation("Step3 Extract & cache {Count} files, elapsed: {Elapsed}ms",
                missingPaths.Count, sw.ElapsedMilliseconds);
        }

        return result;
    }

    public Task<bool> IsEmptyAsync()
    {
        return Task.FromResult(_collection.Count() == 0);
    }

    public void Dispose()
    {
        _db?.Dispose();
    }
}