using FileServer.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace FileServer.Services;

public interface IThumbnailGenerationManager
{
    Task StartBackgroundGeneration();
    Task<bool> IsThumbnailReady(string videoPath);
    Task<string> GetThumbnailPath(string videoPath);
    void QueueVideoForGeneration(string videoPath);
    int GetQueueLength();
    int GetGeneratedCount();
}

public class ThumbnailGenerationManager : IThumbnailGenerationManager, IHostedService
{
    private readonly IVideoThumbnailService _thumbnailService;
    private readonly IFileService _fileService;
    private readonly ILogger<ThumbnailGenerationManager> _logger;
    private readonly FileServerConfig _config;
    private readonly ConcurrentDictionary<string, bool> _generatedThumbnails;
    private readonly ConcurrentQueue<string> _generationQueue;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private Task _backgroundTask;
    private int _generatedCount;

    public ThumbnailGenerationManager(
        IVideoThumbnailService thumbnailService,
        IFileService fileService,
        ILogger<ThumbnailGenerationManager> logger,
        IOptions<FileServerConfig> config)
    {
        _thumbnailService = thumbnailService;
        _fileService = fileService;
        _logger = logger;
        _config = config.Value;

        _generatedThumbnails = new ConcurrentDictionary<string, bool>();
        _generationQueue = new ConcurrentQueue<string>();
        _cancellationTokenSource = new CancellationTokenSource();
        _generatedCount = 0;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("启动缩略图生成管理器");
        _backgroundTask = Task.Run(async () => await ProcessGenerationQueue(), _cancellationTokenSource.Token);
        _ = Task.Run(async () => await PreGenerateThumbnails(), cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("停止缩略图生成管理器");
        _cancellationTokenSource.Cancel();
        if (_backgroundTask != null) await _backgroundTask;
    }

    public async Task StartBackgroundGeneration()
    {
        await PreGenerateThumbnails();
    }

    public async Task<bool> IsThumbnailReady(string videoPath)
    {
        if (_generatedThumbnails.ContainsKey(videoPath)) return true;

        var request = new VideoThumbnailRequest
        {
            VideoPath = videoPath,
            Width = _config.ThumbnailWidth,
            Height = _config.ThumbnailHeight,
            OutputFormat = _config.ThumbnailFormat
        };
        var thumbnailPath = GetThumbnailFilePath(request);
        return File.Exists(thumbnailPath);
    }

    public async Task<string> GetThumbnailPath(string videoPath)
    {
        if (await IsThumbnailReady(videoPath))
        {
            var request = new VideoThumbnailRequest
            {
                VideoPath = videoPath,
                Width = _config.ThumbnailWidth,
                Height = _config.ThumbnailHeight,
                OutputFormat = _config.ThumbnailFormat
            };
            return GetThumbnailFilePath(request);
        }
        return null;
    }

    public void QueueVideoForGeneration(string videoPath)
    {
        if (!_generatedThumbnails.ContainsKey(videoPath))
        {
            _generationQueue.Enqueue(videoPath);
            _logger.LogDebug("视频已加入生成队列: {VideoPath}", videoPath);
        }
    }

    public int GetQueueLength() => _generationQueue.Count;
    public int GetGeneratedCount() => _generatedCount;

    private async Task PreGenerateThumbnails()
    {
        try
        {
            _logger.LogInformation("开始预生成视频缩略图...");

            var videoLibDir = _config.VideoLibraryDirectory;
            if (string.IsNullOrEmpty(videoLibDir))
            {
                _logger.LogWarning("配置中未设置 VideoLibraryDirectory，跳过预生成");
                return;
            }

            var videoLibraryPath = Path.Combine(_fileService.GetRootPath(), videoLibDir);
            if (!Directory.Exists(videoLibraryPath))
            {
                _logger.LogWarning("视频库目录不存在: {VideoLibraryPath}", videoLibraryPath);
                return;
            }

            var videoExtensions = new[] { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".3gp" };
            var videoFiles = ScanVideoFiles(videoLibraryPath, videoExtensions);
            _logger.LogInformation("找到 {VideoCount} 个视频文件", videoFiles.Count);

            foreach (var videoFile in videoFiles)
            {
                QueueVideoForGeneration(videoFile);
            }

            _logger.LogInformation("预生成缩略图队列初始化完成，队列长度: {QueueLength}", _generationQueue.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "预生成缩略图扫描失败");
        }
    }

    private List<string> ScanVideoFiles(string directory, string[] extensions)
    {
        var videoFiles = new List<string>();
        try
        {
            var files = Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories)
                .Where(file => extensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                .Select(file => file.Replace(_fileService.GetRootPath() + Path.DirectorySeparatorChar, ""))
                .ToList();
            videoFiles.AddRange(files);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "扫描目录失败: {Directory}", directory);
        }
        return videoFiles;
    }

    private async Task ProcessGenerationQueue()
    {
        _logger.LogInformation("开始处理缩略图生成队列");
        while (!_cancellationTokenSource.Token.IsCancellationRequested)
        {
            try
            {
                if (_generationQueue.TryDequeue(out var videoPath))
                {
                    await GenerateThumbnailForVideo(videoPath);
                }
                else
                {
                    await Task.Delay(5000, _cancellationTokenSource.Token);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理缩略图生成队列时发生错误");
                await Task.Delay(1000, _cancellationTokenSource.Token);
            }
        }
        _logger.LogInformation("缩略图生成队列处理结束");
    }

    private async Task GenerateThumbnailForVideo(string videoPath)
    {
        try
        {
            if (await IsThumbnailReady(videoPath))
            {
                _generatedThumbnails[videoPath] = true;
                return;
            }

            _logger.LogInformation("生成视频缩略图: {VideoPath}", videoPath);
            var request = new VideoThumbnailRequest
            {
                VideoPath = videoPath,
                Width = _config.ThumbnailWidth,
                Height = _config.ThumbnailHeight,
                OutputFormat = _config.ThumbnailFormat
            };
            var result = await _thumbnailService.GenerateThumbnailAsync(request);

            if (result.Success)
            {
                _generatedThumbnails[videoPath] = true;
                Interlocked.Increment(ref _generatedCount);
                _logger.LogInformation("视频缩略图生成成功: {VideoPath}", videoPath);
            }
            else
            {
                _logger.LogWarning("视频缩略图生成失败: {VideoPath}, 错误: {Error}", videoPath, result.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "生成视频缩略图异常: {VideoPath}", videoPath);
        }
    }

    private string GetThumbnailFilePath(VideoThumbnailRequest request)
    {
        var videoFileName = Path.GetFileNameWithoutExtension(request.VideoPath);
        var videoDir = Path.GetDirectoryName(request.VideoPath)?.Replace(Path.DirectorySeparatorChar, '_');
        var uniqueKey = $"{videoDir}_{videoFileName}_{request.Width}x{request.Height}";

        using var md5 = System.Security.Cryptography.MD5.Create();
        var hash = BitConverter.ToString(md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(uniqueKey)))
            .Replace("-", "").ToLower();

        var fileName = $"{hash}.{request.OutputFormat.ToLowerInvariant()}";
        var thumbnailsRoot = Path.Combine(_fileService.GetRootPath(), _config.ThumbnailDirectory);
        return Path.Combine(thumbnailsRoot, fileName);
    }
}