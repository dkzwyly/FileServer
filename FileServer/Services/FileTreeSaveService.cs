using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FileServer.Services
{
    public class FileTreeSaveService : BackgroundService
    {
        private readonly IFileTreeCacheService _cache;
        private readonly ILogger<FileTreeSaveService> _logger;
        private Timer _timer;

        public FileTreeSaveService(IFileTreeCacheService cache, ILogger<FileTreeSaveService> logger)
        {
            _cache = cache;
            _logger = logger;
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var now = DateTime.Now;
            var nextRun = now.Date.AddDays(1).AddHours(5);
            if (nextRun <= now) nextRun = nextRun.AddDays(1);
            var dueTime = nextRun - now;

            _timer = new Timer(async _ =>
            {
                _logger.LogInformation("执行每日5点增量同步...");
                await _cache.ApplyChangesToDatabaseAsync();

                // 可选：每周日执行一次全量覆盖校验
                if (DateTime.Now.DayOfWeek == DayOfWeek.Sunday)
                {
                    _logger.LogInformation("执行每周日全量覆盖...");
                    await _cache.FullSaveToDatabaseAsync();
                }
            }, null, dueTime, TimeSpan.FromDays(1));

            return Task.CompletedTask;
        }

        public override async Task StopAsync(CancellationToken stoppingToken)
        {
            _timer?.Dispose();
            _logger.LogInformation("应用关闭，执行最终同步...");
            await _cache.ApplyChangesToDatabaseAsync();
            await base.StopAsync(stoppingToken);
        }
    }
}