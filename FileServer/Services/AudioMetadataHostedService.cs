using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
        _logger.LogInformation("AudioMetadataHostedService 启动，将在后台触发音乐增量索引...");

        // 后台执行，不阻塞启动
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var audioService = scope.ServiceProvider.GetRequiredService<IAudioMetadataService>();
                var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

                var rootPath = config.GetValue<string>("FileServerConfig:RootPath")!;
                var audioDir = config.GetValue<string>("FileServerConfig:AudioIndexDirectory");
                if (string.IsNullOrEmpty(audioDir))
                    throw new InvalidOperationException("配置缺少 FileServerConfig:AudioIndexDirectory");

                var musicRoot = Path.Combine(rootPath, audioDir);
                await audioService.ScanAndIndexAllAsync(musicRoot);
                _logger.LogInformation("音乐元数据后台增量索引完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AudioMetadataHostedService 后台索引执行失败");
            }
        }, cancellationToken);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}