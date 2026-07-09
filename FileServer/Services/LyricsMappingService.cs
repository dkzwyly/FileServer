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

        public LyricsMappingService(
            ILogger<LyricsMappingService> logger,
            IConfiguration configuration,
            IAudioMetadataService audioMetadataService)
        {
            _logger = logger;
            _audioMetadataService = audioMetadataService;

            var rootPath = configuration["FileServerConfig:RootPath"]
                ?? throw new InvalidOperationException("未配置 FileServerConfig:RootPath");
            rootPath = Path.GetFullPath(rootPath);

            var dbPath = configuration["FileServerConfig:LyricsLiteDbPath"];
            if (string.IsNullOrEmpty(dbPath))
                dbPath = Path.Combine(rootPath, "lyrics-mappings.db");
            else
                dbPath = Path.GetFullPath(Path.Combine(rootPath, dbPath));

            var dbDir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dbDir) && !Directory.Exists(dbDir))
                Directory.CreateDirectory(dbDir);

            _db = new LiteDatabase(dbPath);
            _collection = _db.GetCollection<LyricsMappingRecord>("lyricsMappings");
            _collection.EnsureIndex(x => x.Fingerprint, unique: true);
            _collection.EnsureIndex(x => x.FilePath); // 非唯一索引，用于按路径查询

            _logger.LogInformation("LyricsMappingService 初始化完成，数据库路径: {DbPath}", dbPath);
        }

        public async Task<bool> SaveMappingAsync(string fingerprint, string lyricsPath)
        {
            try
            {
                // 通过指纹从音频元数据服务获取路径（用于存储 FilePath）
                var audioMeta = await _audioMetadataService.GetMetadataByFingerprintAsync(fingerprint);
                var filePath = audioMeta?.FilePath ?? string.Empty;

                var record = new LyricsMappingRecord
                {
                    Fingerprint = fingerprint,
                    FilePath = filePath,
                    LyricsPath = lyricsPath
                };
                _collection.Upsert(record);
                _logger.LogDebug("歌词映射已保存: {Fingerprint} -> {LyricsPath}", fingerprint, lyricsPath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存歌词映射失败: {Fingerprint}", fingerprint);
                return false;
            }
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
                // 先通过音频元数据服务获取指纹
                var meta = await _audioMetadataService.GetMetadataAsync(songPath);
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
            var meta = await _audioMetadataService.GetMetadataAsync(songPath);
            if (meta == null || string.IsNullOrEmpty(meta.Fingerprint))
                return false;
            return await DeleteMappingByFingerprintAsync(meta.Fingerprint);
        }

        public void Dispose()
        {
            _db?.Dispose();
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