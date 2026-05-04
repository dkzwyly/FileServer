// Services/AudioMetadataHostedService.cs
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

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("AudioMetadataHostedService 启动，开始触发音乐索引...");
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var audioService = scope.ServiceProvider.GetRequiredService<IAudioMetadataService>();
            var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
            var rootPath = config.GetValue<string>("FileServerConfig:RootPath")!;
            var musicRoot = Path.Combine(rootPath, "data", "音乐");
            await audioService.ScanAndIndexAllAsync(musicRoot);
            _logger.LogInformation("音乐元数据索引任务完成");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AudioMetadataHostedService 执行失败");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}