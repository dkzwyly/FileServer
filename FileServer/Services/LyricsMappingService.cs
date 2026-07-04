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
        private readonly LiteDatabase _db;
        private readonly ILiteCollection<LyricsMappingRecord> _collection;

        public LyricsMappingService(ILogger<LyricsMappingService> logger, IConfiguration configuration)
        {
            _logger = logger;

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
            _collection.EnsureIndex(x => x.FilePath, unique: true);

            _logger.LogInformation("LyricsMappingService 初始化完成，数据库路径: {DbPath}", dbPath);
        }

        public async Task<bool> SaveMappingAsync(string songPath, string lyricsPath)
        {
            try
            {
                var record = new LyricsMappingRecord
                {
                    FilePath = songPath,
                    LyricsPath = lyricsPath
                };
                _collection.Upsert(record);
                _logger.LogDebug("歌词映射已保存: {Song} -> {Lyrics}", songPath, lyricsPath);
                return await Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存歌词映射失败: {SongPath}", songPath);
                return false;
            }
        }

        public async Task<string?> GetMappingAsync(string songPath)
        {
            try
            {
                var record = _collection.FindById(songPath);
                return record?.LyricsPath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取歌词映射失败: {SongPath}", songPath);
                return null;
            }
        }

        public async Task<bool> DeleteMappingAsync(string songPath)
        {
            try
            {
                var deleted = _collection.Delete(songPath);
                if (deleted)
                    _logger.LogDebug("歌词映射已删除: {SongPath}", songPath);
                return await Task.FromResult(deleted);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除歌词映射失败: {SongPath}", songPath);
                return false;
            }
        }

        public void Dispose()
        {
            _db?.Dispose();
        }

        private class LyricsMappingRecord
        {
            [BsonId]
            public string FilePath { get; set; } = null!;
            public string LyricsPath { get; set; } = null!;
        }
    }
}