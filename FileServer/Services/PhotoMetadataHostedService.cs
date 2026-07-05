using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

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
            _logger.LogInformation("PhotoMetadataHostedService 启动，检查数据库状态...");

            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var photoService = scope.ServiceProvider.GetRequiredService<IPhotoMetadataService>();

                    var isEmpty = await photoService.IsEmptyAsync();

                    if (isEmpty)
                    {
                        _logger.LogInformation("数据库为空，执行全量索引（扫描配置目录）...");
                        await photoService.ScanConfiguredDirectoriesAsync();
                        _logger.LogInformation("图片全量索引完成");
                    }
                    else
                    {
                        _logger.LogInformation("数据库已有数据，跳过扫描，按需更新（访问图片时自动检查变动）。");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "PhotoMetadataHostedService 启动检查或索引执行失败");
                }
            }, cancellationToken);

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}