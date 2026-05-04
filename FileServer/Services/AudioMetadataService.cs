using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TagLib;
using FileServer.Models;

namespace FileServer.Services;

public class AudioMetadataService : IAudioMetadataService, IDisposable
{
    private readonly ILogger<AudioMetadataService> _logger;
    private readonly string _mappingFilePath;
    private readonly string _coversDirectory;

    // ========== 静态全局内存缓存（类似 PhotoMetadataService） ==========
    private static readonly ConcurrentDictionary<string, SongMetadata> _cache = new();
    private static readonly SemaphoreSlim _fileLock = new(1, 1);
    private static Timer? _autoSaveTimer;
    private static bool _isDirty;
    private static readonly object _timerInitLock = new();
    private static string _mappingFilePathStatic = ""; // 静态字段供定时器使用

    public AudioMetadataService(ILogger<AudioMetadataService> logger, IConfiguration configuration)
    {
        _logger = logger;
        var rootPath = configuration.GetValue<string>("FileServerConfig:RootPath") ?? @"D:\FileServer";
        _mappingFilePath = Path.Combine(rootPath, "song-metadata-mappings.json");
        _coversDirectory = Path.Combine(rootPath, "covers");

        if (!Directory.Exists(_coversDirectory))
            Directory.CreateDirectory(_coversDirectory);

        // 静态路径只初始化一次
        if (string.IsNullOrEmpty(_mappingFilePathStatic))
        {
            _mappingFilePathStatic = _mappingFilePath;
            LoadFromFile(); // 从 JSON 加载已有数据到内存
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
                    _logger.LogInformation("全局音频元数据自动保存定时器已启动");
                }
            }
        }
    }

    // ---------- 静态方法：文件持久化 ----------
    private static void LoadFromFile()
    {
        if (!System.IO.File.Exists(_mappingFilePathStatic))
            return;

        _fileLock.Wait();
        try
        {
            var json = System.IO.File.ReadAllText(_mappingFilePathStatic);
            var dict = JsonSerializer.Deserialize<Dictionary<string, SongMetadata>>(json);
            if (dict != null)
            {
                foreach (var kvp in dict)
                    _cache.TryAdd(kvp.Key, kvp.Value);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载音频元数据文件失败: {ex.Message}");
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
            await System.IO.File.WriteAllTextAsync(_mappingFilePathStatic, json);
            _isDirty = false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"保存音频元数据文件失败: {ex.Message}");
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

    // ---------- 公共接口实现 ----------

    public async Task<SongMetadata> GetMetadataAsync(string filePath)
    {
        try
        {
            // 1. 检查内存缓存（如果有并且文件时间未变，直接返回）
            if (_cache.TryGetValue(filePath, out var cached))
            {
                // 检查实际文件是否被修改
                if (System.IO.File.Exists(filePath))
                {
                    var currentFileTime = System.IO.File.GetLastWriteTimeUtc(filePath);
                    if (cached.LastModified == currentFileTime)
                    {
                        _logger.LogDebug("从内存缓存命中: {Path}", filePath);
                        return cached;
                    }
                }
            }

            // 2. 缓存失效或不存在，进入锁保护，重新提取并更新
            // 为避免多线程同时提取同一个文件，使用文件路径作为细粒度键锁（可选简单方案：加全局锁，但耗时操作在锁外）
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
        // 直接从内存缓存返回
        _cache.TryGetValue(songPath, out var metadata);
        return metadata;
    }

    public async Task<bool> SaveMetadataMappingAsync(string songPath, SongMetadata metadata)
    {
        try
        {
            // 保存手动映射时，更新内存缓存（同时保留封面信息）
            if (_cache.TryGetValue(songPath, out var existing))
            {
                // 保留自定义封面
                if (string.IsNullOrEmpty(metadata.CustomCoverPath) && !string.IsNullOrEmpty(existing.CustomCoverPath))
                {
                    metadata.CustomCoverPath = existing.CustomCoverPath;
                    metadata.HasCover = true;
                }
            }
            _cache.AddOrUpdate(songPath, metadata, (_, _) => metadata);
            MarkDirty();
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
            _cache.TryRemove(songPath, out var removed);
            if (removed != null && !string.IsNullOrEmpty(removed.CustomCoverPath))
            {
                var coverFullPath = Path.Combine(_coversDirectory, removed.CustomCoverPath);
                if (System.IO.File.Exists(coverFullPath))
                    System.IO.File.Delete(coverFullPath);
            }
            MarkDirty();
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
            // 检查内存缓存中的自定义封面
            _cache.TryGetValue(filePath, out var metadata);
            if (metadata != null && !string.IsNullOrEmpty(metadata.CustomCoverPath))
            {
                var coverFullPath = Path.Combine(_coversDirectory, metadata.CustomCoverPath);
                if (System.IO.File.Exists(coverFullPath))
                {
                    _logger.LogInformation("返回自定义封面: {Path}", coverFullPath);
                    return new FileStream(coverFullPath, FileMode.Open, FileAccess.Read);
                }
                _logger.LogWarning("自定义封面文件丢失: {Path}", coverFullPath);
            }

            // 内嵌封面
            using var file = TagLib.File.Create(filePath);
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

            // 更新内存缓存
            var metadata = _cache.GetOrAdd(songPath, _ => new SongMetadata
            {
                Title = Path.GetFileNameWithoutExtension(songPath),
                Artist = "未知艺术家",
                Album = "未知专辑",
                HasCover = true
            });
            metadata.CustomCoverPath = safeFileName;
            metadata.HasCover = true;
            MarkDirty();

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
                if (System.IO.File.Exists(coverFullPath))
                    System.IO.File.Delete(coverFullPath);

                metadata.CustomCoverPath = null;
                metadata.HasCover = false;
                MarkDirty();
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
            _logger.LogWarning("全量索引失败：目录不存在 {Directory}", musicDirectory);
            return;
        }

        var audioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".flac", ".wav", ".m4a", ".ogg", ".wma", ".aac", ".ape", ".wv", ".opus"
        };

        var files = Directory.GetFiles(musicDirectory, "*.*", SearchOption.AllDirectories)
                            .Where(f => audioExtensions.Contains(Path.GetExtension(f)))
                            .ToList();

        progress?.Report($"找到 {files.Count} 个音频文件，开始建立元数据索引...");
        _logger.LogInformation("开始全量索引，共 {Count} 个文件", files.Count);

        int indexed = 0;
        foreach (var filePath in files)
        {
            try
            {
                await ExtractAndCacheAsync(filePath);
                indexed++;
                if (indexed % 10 == 0)
                    progress?.Report($"进度: {indexed}/{files.Count}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "索引文件失败: {Path}", filePath);
            }
        }

        progress?.Report($"全量索引完成，成功索引 {indexed}/{files.Count} 个文件");
        _logger.LogInformation("全量索引完成，共处理 {Indexed}/{Total} 个文件", indexed, files.Count);
    }

    // ---------- 私有辅助方法 ----------

    /// <summary>
    /// 提取元数据并更新内存缓存（标记脏，下一次定时器会持久化）
    /// </summary>
    private async Task<SongMetadata> ExtractAndCacheAsync(string filePath)
    {
        // 使用细粒度锁避免同一文件同时提取两次（可选，但并发不高时也可不加）
        var metadata = ExtractMetadataFromFile(filePath);
        metadata.LastModified = System.IO.File.GetLastWriteTimeUtc(filePath);

        // 添加或更新缓存
        _cache.AddOrUpdate(filePath, metadata, (_, old) =>
        {
            // 保留旧的封面信息（如果新提取没有封面）
            if (string.IsNullOrEmpty(metadata.CustomCoverPath) && !string.IsNullOrEmpty(old.CustomCoverPath))
            {
                metadata.CustomCoverPath = old.CustomCoverPath;
                metadata.HasCover = true;
            }
            return metadata;
        });
        MarkDirty();
        return metadata;
    }

    private SongMetadata ExtractMetadataFromFile(string filePath)
    {
        using var file = TagLib.File.Create(filePath);
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
            // LastModified 由调用方设置
        };
    }

    private SongMetadata FallbackMetadata(string filePath)
    {
        return new SongMetadata
        {
            Title = Path.GetFileNameWithoutExtension(filePath),
            Artist = "未知艺术家",
            Album = "未知专辑",
            HasCover = false,
            CustomCoverPath = null,
            LastModified = null
        };
    }

    public void Dispose()
    {
        // 应用关闭时最后一次保存
        SaveToFileAsync().GetAwaiter().GetResult();
    }
}