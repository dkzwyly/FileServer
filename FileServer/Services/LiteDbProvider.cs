using System.Collections.Concurrent;
using LiteDB;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FileServer.Services
{
    public class LiteDbProvider : ILiteDbProvider, IDisposable
    {
        private readonly ConcurrentDictionary<string, Lazy<LiteDatabase>> _databases = new();
        private readonly string _rootPath;
        private readonly string _metadataDir;
        private readonly ILogger<LiteDbProvider> _logger;

        public LiteDbProvider(
            IFileSystemHelper fileSystemHelper,
            IConfiguration configuration,
            ILogger<LiteDbProvider> logger)
        {
            _rootPath = fileSystemHelper.GetRootPath();
            _metadataDir = configuration["FileServerConfig:MetadataDirectory"] ?? "系统文件";
            _logger = logger;
        }

        public LiteDatabase GetDatabase(string name, string? customPath = null, BsonMapper? mapper = null)
        {
            var lazy = _databases.GetOrAdd(name, key => new Lazy<LiteDatabase>(() =>
            {
                string dbPath;
                if (!string.IsNullOrEmpty(customPath))
                {
                    dbPath = Path.IsPathRooted(customPath)
                        ? customPath
                        : Path.GetFullPath(Path.Combine(_rootPath, customPath));
                }
                else if (key == "trash" || key == "file-tree" || key == "file-jobs")
                {
                    // 元数据目录下的数据库
                    dbPath = Path.Combine(_rootPath, _metadataDir, $"{key}.db");
                }
                else
                {
                    // 根目录下的数据库
                    dbPath = Path.Combine(_rootPath, $"{key}.db");
                }

                var dir = Path.GetDirectoryName(dbPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                _logger.LogInformation("创建 LiteDB 单例: {Name} -> {Path}", name, dbPath);

                return mapper != null
                    ? new LiteDatabase($"Filename={dbPath};Connection=Shared", mapper)
                    : new LiteDatabase($"Filename={dbPath};Connection=Shared");
            }, LazyThreadSafetyMode.ExecutionAndPublication));

            return lazy.Value;
        }

        public void Dispose()
        {
            foreach (var kv in _databases.Values)
            {
                try { kv.Value.Dispose(); }
                catch (Exception ex) { _logger.LogWarning(ex, "释放 LiteDB 失败"); }
            }
            _databases.Clear();
        }
    }
}