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

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("PhotoMetadataHostedService 启动，将在后台触发图片增量索引...");

            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var photoService = scope.ServiceProvider.GetRequiredService<IPhotoMetadataService>();
                    await photoService.ScanConfiguredDirectoriesAsync();
                    _logger.LogInformation("图片后台增量索引完成");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "PhotoMetadataHostedService 后台索引执行失败");
                }
            }, cancellationToken);

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}