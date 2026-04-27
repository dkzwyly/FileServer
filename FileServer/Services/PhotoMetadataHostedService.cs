using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FileServer.Services
{
    public class PhotoMetadataHostedService : IHostedService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<PhotoMetadataHostedService> _logger;

        public PhotoMetadataHostedService(IServiceScopeFactory scopeFactory, ILogger<PhotoMetadataHostedService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("PhotoMetadataHostedService 启动，开始触发图片索引...");
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var photoService = scope.ServiceProvider.GetRequiredService<IPhotoMetadataService>();
                await photoService.ScanConfiguredDirectoriesAsync();
                _logger.LogInformation("图片索引任务完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PhotoMetadataHostedService 执行失败");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}