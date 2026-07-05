using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileServer.Services;

public class AudioMetadataHostedService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AudioMetadataHostedService> _logger;

    public AudioMetadataHostedService(IServiceScopeFactory scopeFactory, ILogger<AudioMetadataHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("AudioMetadataHostedService 启动，检查数据库状态...");

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var audioService = scope.ServiceProvider.GetRequiredService<IAudioMetadataService>();
                var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

                var isEmpty = await audioService.IsEmptyAsync();

                if (isEmpty)
                {
                    _logger.LogInformation("数据库为空，执行全量索引...");

                    var rootPath = config.GetValue<string>("FileServerConfig:RootPath")!;
                    var audioDir = config.GetValue<string>("FileServerConfig:AudioIndexDirectory");
                    if (string.IsNullOrEmpty(audioDir))
                        throw new InvalidOperationException("配置缺少 FileServerConfig:AudioIndexDirectory");

                    var musicRoot = Path.Combine(rootPath, audioDir);
                    await audioService.ScanAndIndexAllAsync(musicRoot);
                    _logger.LogInformation("全量索引完成");
                }
                else
                {
                    _logger.LogInformation("数据库已有数据，跳过全量扫描，按需更新（访问文件时自动检查变动）。");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AudioMetadataHostedService 启动检查或索引执行失败");
            }
        }, cancellationToken);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}