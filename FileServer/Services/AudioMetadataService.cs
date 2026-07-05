using FileServer.Models;
using LiteDB;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TagLib;
using SystemFile = System.IO.File;
using TagFile = TagLib.File;

namespace FileServer.Services;

public class AudioMetadataService : IAudioMetadataService, IDisposable
{
    private readonly ILogger<AudioMetadataService> _logger;
    private readonly string _coversDirectory;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<SongMetadata> _collection;
    private static readonly ConcurrentDictionary<string, SongMetadata> _cache = new();

    public AudioMetadataService(ILogger<AudioMetadataService> logger, IConfiguration configuration)
    {
        _logger = logger;

        // 读取根路径并转换为绝对路径
        var rootPath = configuration["FileServerConfig:RootPath"]
            ?? throw new InvalidOperationException("未配置 FileServerConfig:RootPath");
        rootPath = Path.GetFullPath(rootPath);

        // 封面目录
        var coversDir = configuration["FileServerConfig:CoversDirectory"] ?? "系统文件/covers";
        _coversDirectory = Path.Combine(rootPath, coversDir);
        if (!Directory.Exists(_coversDirectory))
            Directory.CreateDirectory(_coversDirectory);

        // 数据库路径（优先使用绝对路径配置，否则基于 RootPath 组合）
        var dbPath = configuration["FileServerConfig:AudioLiteDbPath"];
        if (string.IsNullOrEmpty(dbPath))
            dbPath = Path.Combine(rootPath, "audio-metadata.db");
        else
            dbPath = Path.GetFullPath(Path.Combine(rootPath, dbPath));

        var dbDir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dbDir) && !Directory.Exists(dbDir))
            Directory.CreateDirectory(dbDir);

        _db = new LiteDatabase(dbPath);
        _collection = _db.GetCollection<SongMetadata>("songMetadata");
        _collection.EnsureIndex(x => x.FilePath, unique: true);

        LoadFromDatabase();
        _logger.LogInformation("AudioMetadataService 初始化完成，数据库路径: {DbPath}", dbPath);
    }

    private void LoadFromDatabase()
    {
        try
        {
            var all = _collection.FindAll().ToList();
            foreach (var doc in all)
                _cache.TryAdd(doc.FilePath, doc);
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

    private void DeleteFromDatabase(string filePath)
    {
        try
        {
            _collection.Delete(filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "从 LiteDB 删除失败: {FilePath}", filePath);
        }
    }

    public async Task<SongMetadata> GetMetadataAsync(string filePath)
    {
        try
        {
            if (_cache.TryGetValue(filePath, out var cached))
            {
                if (SystemFile.Exists(filePath))
                {
                    var currentTime = SystemFile.GetLastWriteTimeUtc(filePath);
                    if (cached.LastModified == currentTime)
                    {
                        _logger.LogDebug("从内存缓存命中: {Path}", filePath);
                        return cached;
                    }
                }
            }

            var dbDoc = _collection.FindById(filePath);
            if (dbDoc != null && SystemFile.Exists(filePath))
            {
                var currentTime = SystemFile.GetLastWriteTimeUtc(filePath);
                if (dbDoc.LastModified == currentTime)
                {
                    _cache.AddOrUpdate(filePath, dbDoc, (_, _) => dbDoc);
                    return dbDoc;
                }
            }

            var metadata = await ExtractAndCacheAsync(filePath);
            return metadata;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取音频元数据失败: {Path}", filePath);
            return FallbackMetadata(filePath);
        }
    }

    public async Task<SongMetadata?> GetMetadataMappingAsync(string songPath)
    {
        _cache.TryGetValue(songPath, out var metadata);
        return metadata;
    }

    public async Task<bool> SaveMetadataMappingAsync(string songPath, SongMetadata metadata)
    {
        try
        {
            if (_cache.TryGetValue(songPath, out var existing) && !string.IsNullOrEmpty(existing.CustomCoverPath))
            {
                if (string.IsNullOrEmpty(metadata.CustomCoverPath))
                {
                    metadata.CustomCoverPath = existing.CustomCoverPath;
                    metadata.HasCover = true;
                }
            }

            metadata.FilePath = songPath;
            _cache.AddOrUpdate(songPath, metadata, (_, _) => metadata);
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
            if (_cache.TryRemove(songPath, out var removed) && !string.IsNullOrEmpty(removed.CustomCoverPath))
            {
                var coverFullPath = Path.Combine(_coversDirectory, removed.CustomCoverPath);
                if (SystemFile.Exists(coverFullPath))
                    SystemFile.Delete(coverFullPath);
            }

            DeleteFromDatabase(songPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除元数据映射失败: {SongPath}", songPath);
            return false;
        }
    }

    public async Task<Stream?> GetAlbumCoverAsync(string filePath)
    {
        try
        {
            if (_cache.TryGetValue(filePath, out var metadata) && !string.IsNullOrEmpty(metadata.CustomCoverPath))
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

            var metadata = _cache.GetOrAdd(songPath, _ => new SongMetadata
            {
                FilePath = songPath,
                Title = Path.GetFileNameWithoutExtension(songPath),
                Artist = "未知艺术家",
                Album = "未知专辑",
                HasCover = true
            });
            metadata.CustomCoverPath = safeFileName;
            metadata.HasCover = true;
            SaveToDatabase(metadata);

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
            if (_cache.TryGetValue(songPath, out var metadata) && !string.IsNullOrEmpty(metadata.CustomCoverPath))
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
                                 .Select(f => new
                                 {
                                     Path = f,
                                     LastWrite = SystemFile.GetLastWriteTimeUtc(f),
                                     Size = new FileInfo(f).Length
                                 })
                                 .ToList();

        progress?.Report($"扫描到 {diskFiles.Count} 个音频文件，正在与数据库比对...");
        _logger.LogInformation("增量索引：磁盘文件数 {Count}", diskFiles.Count);

        var dbRecords = _collection.FindAll().ToDictionary(m => m.FilePath);

        var toAdd = new List<string>();
        var toUpdate = new List<string>();
        var toDelete = new List<string>();

        foreach (var diskFile in diskFiles)
        {
            if (dbRecords.TryGetValue(diskFile.Path, out var existing))
            {
                if (existing.LastModified != diskFile.LastWrite)
                    toUpdate.Add(diskFile.Path);
            }
            else
            {
                toAdd.Add(diskFile.Path);
            }
        }

        var diskPaths = new HashSet<string>(diskFiles.Select(f => f.Path));
        foreach (var dbPath in dbRecords.Keys)
        {
            if (!diskPaths.Contains(dbPath))
                toDelete.Add(dbPath);
        }

        int totalChanges = toAdd.Count + toUpdate.Count + toDelete.Count;
        if (totalChanges == 0)
        {
            progress?.Report("没有检测到任何变化，跳过索引。");
            _logger.LogInformation("增量索引：无变化，跳过");
            return;
        }

        progress?.Report($"检测到变化：新增 {toAdd.Count}，更新 {toUpdate.Count}，删除 {toDelete.Count}");

        foreach (var path in toDelete)
        {
            if (_cache.TryRemove(path, out var removed))
            {
                if (!string.IsNullOrEmpty(removed.CustomCoverPath))
                {
                    var coverFullPath = Path.Combine(_coversDirectory, removed.CustomCoverPath);
                    if (SystemFile.Exists(coverFullPath))
                        SystemFile.Delete(coverFullPath);
                }
                DeleteFromDatabase(path);
            }
        }

        var toProcess = toAdd.Concat(toUpdate).ToList();
        int processed = 0;
        foreach (var path in toProcess)
        {
            try
            {
                await ExtractAndCacheAsync(path);
                processed++;
                if (processed % 10 == 0)
                    progress?.Report($"索引进度: {processed}/{toProcess.Count}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "索引文件失败: {Path}", path);
            }
        }

        progress?.Report($"增量索引完成，成功处理 {processed}/{toProcess.Count} 个变化文件");
        _logger.LogInformation("增量索引完成，处理了 {Processed} 个文件，删除了 {Deleted} 个", processed, toDelete.Count);
    }

    private async Task<SongMetadata> ExtractAndCacheAsync(string filePath)
    {
        var metadata = ExtractMetadataFromFile(filePath);
        metadata.FilePath = filePath;
        metadata.LastModified = SystemFile.GetLastWriteTimeUtc(filePath);

        if (_cache.TryGetValue(filePath, out var old) && !string.IsNullOrEmpty(old.CustomCoverPath))
        {
            if (string.IsNullOrEmpty(metadata.CustomCoverPath))
            {
                metadata.CustomCoverPath = old.CustomCoverPath;
                metadata.HasCover = true;
            }
        }

        _cache.AddOrUpdate(filePath, metadata, (_, _) => metadata);
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
    /// <summary>
    /// 批量获取元数据，优先从内存缓存返回，其次从 LiteDB 读取，避免逐文件磁盘检查。
    /// </summary>
    public async Task<Dictionary<string, SongMetadata>> GetBatchMetadataAsync(List<string> paths)
    {
        var sw = Stopwatch.StartNew();
        var result = new Dictionary<string, SongMetadata>();
        var missingPaths = new List<string>();

        // 1. 内存缓存
        foreach (var path in paths)
        {
            if (_cache.TryGetValue(path, out var cached))
                result[path] = cached;
            else
                missingPaths.Add(path);
        }
        _logger.LogInformation("Step1 Cache hit: {HitCount}, missing: {MissingCount}, elapsed: {Elapsed}ms",
            result.Count, missingPaths.Count, sw.ElapsedMilliseconds);

        // 2. LiteDB 批量查询
        if (missingPaths.Any())
        {
            sw.Restart();
            var bsonValues = missingPaths.Select(p => new BsonValue(p)).ToList();
            var bsonArray = new BsonArray(bsonValues);
            var dbResults = _collection.Find(Query.In("FilePath", bsonArray)).ToList();
            _logger.LogInformation("Step2 LiteDB found: {DbCount}, elapsed: {Elapsed}ms",
                dbResults.Count, sw.ElapsedMilliseconds);

            sw.Restart();
            foreach (var meta in dbResults)
            {
                _cache.TryAdd(meta.FilePath, meta);
                result[meta.FilePath] = meta;
                missingPaths.Remove(meta.FilePath);
            }
            _logger.LogInformation("Step2 update cache elapsed: {Elapsed}ms", sw.ElapsedMilliseconds);
        }

        // 3. 实时提取（最昂贵）
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
    /// <summary>
    /// 检查数据库是否为空（无任何元数据记录）
    /// </summary>
    public Task<bool> IsEmptyAsync()
    {
        return Task.FromResult(_collection.Count() == 0);
    }
    public void Dispose()
    {
        _db?.Dispose();
    }
}