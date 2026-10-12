using System;
using System.IO;
using System.Threading.Tasks;
using LiteDB;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FileServer.Services
{
    public class LyricsMappingService : ILyricsMappingService, IDisposable
    {
        private readonly ILogger<LyricsMappingService> _logger;
        private readonly IAudioMetadataService _audioMetadataService;
        private readonly LiteDatabase _db;
        private readonly ILiteCollection<LyricsMappingRecord> _collection;
        private readonly string _rootPath;

        public LyricsMappingService(
    ILogger<LyricsMappingService> logger,
    IConfiguration configuration,
    IAudioMetadataService audioMetadataService,
    ILiteDbProvider dbProvider)         // 新增
        {
            _logger = logger;
            _audioMetadataService = audioMetadataService;

            var rootPath = configuration["FileServerConfig:RootPath"]
                ?? throw new InvalidOperationException("未配置 FileServerConfig:RootPath");
            _rootPath = Path.GetFullPath(rootPath);

            var dbPath = configuration["FileServerConfig:LyricsLiteDbPath"];
            _db = dbProvider.GetDatabase("lyrics-mappings", dbPath);
            _collection = _db.GetCollection<LyricsMappingRecord>("lyricsMappings");
            _collection.EnsureIndex(x => x.Fingerprint, unique: true);
            _collection.EnsureIndex(x => x.FilePath);

            _logger.LogInformation("LyricsMappingService 初始化完成（数据库由 ILiteDbProvider 管理）");
        }

        public async Task<bool> SaveMappingAsync(string songPath, string lyricsPath)
        {
            try
            {
                var meta = await _audioMetadataService.GetMetadataAsync(ToFullPath(songPath));
                if (meta == null || string.IsNullOrEmpty(meta.Fingerprint))
                {
                    _logger.LogWarning("无法获取歌曲指纹，映射保存失败: {SongPath}", songPath);
                    return false;
                }

                var record = new LyricsMappingRecord
                {
                    Fingerprint = meta.Fingerprint,
                    FilePath = songPath,
                    LyricsPath = lyricsPath
                };
                _collection.Upsert(record);
                _logger.LogDebug("歌词映射已保存: {Fingerprint} -> {LyricsPath}", meta.Fingerprint, lyricsPath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存歌词映射失败: {SongPath}", songPath);
                return false;
            }
        }
        /// <summary>
        /// 把相对路径（如 data/音乐/xxx.flac）拼成完整物理路径。
        /// IAudioMetadataService.GetMetadataAsync 需要完整路径。
        /// 如果传入的已经是绝对路径，则原样返回。
        /// </summary>
        private string ToFullPath(string songPath)
        {
            if (string.IsNullOrEmpty(songPath))
                return songPath;

            return Path.IsPathRooted(songPath)
                ? songPath
                : Path.Combine(_rootPath, songPath);
        }
        public async Task<string?> GetMappingByFingerprintAsync(string fingerprint)
        {
            try
            {
                var record = _collection.FindOne(x => x.Fingerprint == fingerprint);
                return record?.LyricsPath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "按指纹获取歌词映射失败: {Fingerprint}", fingerprint);
                return null;
            }
        }

        public async Task<string?> GetMappingByPathAsync(string songPath)
        {
            try
            {
                var meta = await _audioMetadataService.GetMetadataAsync(ToFullPath(songPath));
                if (meta == null || string.IsNullOrEmpty(meta.Fingerprint))
                    return null;

                return await GetMappingByFingerprintAsync(meta.Fingerprint);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "按路径获取歌词映射失败: {SongPath}", songPath);
                return null;
            }
        }

        public async Task<bool> DeleteMappingByFingerprintAsync(string fingerprint)
        {
            try
            {
                var deleted = _collection.DeleteMany(x => x.Fingerprint == fingerprint) > 0;
                if (deleted)
                    _logger.LogDebug("歌词映射已删除: {Fingerprint}", fingerprint);
                return deleted;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除歌词映射失败: {Fingerprint}", fingerprint);
                return false;
            }
        }

        public async Task<bool> DeleteMappingByPathAsync(string songPath)
        {
            try
            {
                var meta = await _audioMetadataService.GetMetadataAsync(ToFullPath(songPath));
                if (meta == null || string.IsNullOrEmpty(meta.Fingerprint))
                    return false;

                return await DeleteMappingByFingerprintAsync(meta.Fingerprint);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "按路径删除歌词映射失败: {SongPath}", songPath);
                return false;
            }
        }

        public void Dispose()
        {
            // 数据库由 ILiteDbProvider 统一释放
        }

        private class LyricsMappingRecord
        {
            [BsonId]
            public string Fingerprint { get; set; } = null!;
            public string FilePath { get; set; } = null!;
            public string LyricsPath { get; set; } = null!;
        }
    }
}