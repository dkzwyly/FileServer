using FileServer.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace FileServer.Services
{
    public class VideoThumbnailService : IVideoThumbnailService, IHostedService
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly ILogger<VideoThumbnailService> _logger;
        private string _thumbnailsRoot;
        private readonly ConcurrentDictionary<string, byte> _processingVideos;
        private readonly ConcurrentQueue<string> _generationQueue;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private Task[] _workerTasks;
        private const int MAX_CONCURRENT_WORKERS = 4;

        private int _generatedCount = 0;
        private bool _workersStarted = false;

        // 支持的视频格式
        private readonly string[] _videoExtensions = {
            ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm",
            ".m4v", ".3gp", ".ts", ".mts", ".m2ts", ".mpeg", ".mpg"
        };

        // 硬编码 FFmpeg 路径
        private const string FFmpegPath = @"D:\ffmpeg-release-full\ffmpeg-7.1.1-full_build\bin\ffmpeg.exe";
        private const string FFprobePath = @"D:\ffmpeg-release-full\ffmpeg-7.1.1-full_build\bin\ffprobe.exe";

        public VideoThumbnailService(IServiceScopeFactory serviceScopeFactory, ILogger<VideoThumbnailService> logger)
        {
            _serviceScopeFactory = serviceScopeFactory;
            _logger = logger;

            _processingVideos = new ConcurrentDictionary<string, byte>();
            _generationQueue = new ConcurrentQueue<string>();
            _cancellationTokenSource = new CancellationTokenSource();

            ValidateFFmpegTools();
            _logger.LogInformation("VideoThumbnailService 构造函数完成");
        }

        #region IHostedService 实现

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("启动视频缩略图服务 - 4并发工作模式");

            try
            {
                // 在 StartAsync 中初始化路径（需要作用域的部分）
                using var scope = _serviceScopeFactory.CreateScope();
                var fileService = scope.ServiceProvider.GetRequiredService<IFileService>();

                var rootPath = fileService.GetRootPath();
                if (string.IsNullOrEmpty(rootPath))
                {
                    _logger.LogWarning("文件服务根路径为空，使用临时目录");
                    rootPath = Path.GetTempPath();
                }

                _thumbnailsRoot = Path.Combine(rootPath, "_thumbnails", "videos");
                if (!Directory.Exists(_thumbnailsRoot))
                {
                    Directory.CreateDirectory(_thumbnailsRoot);
                    _logger.LogInformation("创建视频缩略图目录: {ThumbnailsRoot}", _thumbnailsRoot);
                }

                // 立即启动工作线程
                StartWorkers();

                // 启动后台扫描
                _ = Task.Run(async () => await ScanAndPreGenerateThumbnails(), cancellationToken);

                _logger.LogInformation("视频缩略图服务启动完成，启动 {WorkerCount} 个工作线程", MAX_CONCURRENT_WORKERS);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "启动视频缩略图服务时发生异常");
                // 即使初始化失败也启动工作线程
                StartWorkers();
            }
        }

        private void StartWorkers()
        {
            if (_workersStarted) return;

            _workerTasks = new Task[MAX_CONCURRENT_WORKERS];
            for (int i = 0; i < MAX_CONCURRENT_WORKERS; i++)
            {
                int workerId = i;
                _workerTasks[i] = Task.Run(async () => await ProcessQueueWorker(workerId), _cancellationTokenSource.Token);
                _logger.LogDebug("工作线程 {WorkerId} 已创建", workerId);
            }
            _workersStarted = true;
            _logger.LogInformation("所有 {WorkerCount} 个工作线程已启动", MAX_CONCURRENT_WORKERS);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("停止视频缩略图服务");
            _cancellationTokenSource.Cancel();

            if (_workerTasks != null)
            {
                await Task.WhenAll(_workerTasks);
            }

            _logger.LogInformation("视频缩略图服务已停止，总共生成了 {GeneratedCount} 个缩略图", _generatedCount);
        }

        #endregion

        #region 工作线程逻辑

        private async Task ProcessQueueWorker(int workerId)
        {
            _logger.LogInformation("工作线程 {WorkerId} 开始运行", workerId);
            int processedCount = 0;

            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    if (_generationQueue.TryDequeue(out var videoPath))
                    {
                        if (_processingVideos.TryAdd(videoPath, 0))
                        {
                            processedCount++;
                            _logger.LogDebug("工作线程 {WorkerId} 开始处理任务 [{ProcessedCount}]: {VideoPath}",
                                workerId, processedCount, videoPath);

                            await GenerateThumbnailInBackground(videoPath);

                            if (processedCount % 10 == 0)
                            {
                                _logger.LogInformation("工作线程 {WorkerId} 已处理 {ProcessedCount} 个任务",
                                    workerId, processedCount);
                            }
                        }
                    }
                    else
                    {
                        await Task.Delay(100, _cancellationTokenSource.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("工作线程 {WorkerId} 被取消", workerId);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "工作线程 {WorkerId} 处理任务时发生异常", workerId);
                    await Task.Delay(1000, _cancellationTokenSource.Token);
                }
            }

            _logger.LogInformation("工作线程 {WorkerId} 退出，总共处理了 {ProcessedCount} 个任务", workerId, processedCount);
        }

        public void QueueVideoForGeneration(string videoPath)
        {
            if (string.IsNullOrEmpty(videoPath))
            {
                _logger.LogWarning("尝试加入空视频路径到队列");
                return;
            }

            if (!_workersStarted)
            {
                _logger.LogWarning("工作线程未启动，立即启动");
                StartWorkers();
            }

            if (_processingVideos.ContainsKey(videoPath))
            {
                _logger.LogDebug("视频已在处理中，跳过: {VideoPath}", videoPath);
                return;
            }

            if (_generationQueue.Any(x => x == videoPath))
            {
                _logger.LogDebug("视频已在队列中，跳过: {VideoPath}", videoPath);
                return;
            }

            _generationQueue.Enqueue(videoPath);

            _logger.LogDebug("视频加入生成队列: {VideoPath} (队列长度: {QueueCount})",
                videoPath, _generationQueue.Count);
        }

        #endregion

        #region 扫描和初始化

        private async Task ScanAndPreGenerateThumbnails()
        {
            try
            {
                _logger.LogInformation("开始扫描视频文件并预生成缩略图...");

                using var scope = _serviceScopeFactory.CreateScope();
                var fileService = scope.ServiceProvider.GetRequiredService<IFileService>();

                var videoLibraryPath = Path.Combine(fileService.GetRootPath(), "data", "影视");
                _logger.LogInformation("扫描目录: {VideoLibraryPath}", videoLibraryPath);

                if (!Directory.Exists(videoLibraryPath))
                {
                    _logger.LogWarning("视频库目录不存在: {VideoLibraryPath}", videoLibraryPath);
                    return;
                }

                var videoFiles = await Task.Run(() => ScanVideoFiles(videoLibraryPath, fileService));
                _logger.LogInformation("找到 {VideoCount} 个视频文件", videoFiles.Count);

                int queuedCount = 0;
                foreach (var videoFile in videoFiles)
                {
                    if (!ThumbnailExists(videoFile, fileService))
                    {
                        QueueVideoForGeneration(videoFile);
                        queuedCount++;

                        if (queuedCount % 100 == 0)
                        {
                            _logger.LogInformation("扫描进度: 已加入队列 {QueuedCount}/{TotalCount}",
                                queuedCount, videoFiles.Count);
                        }
                    }
                }

                _logger.LogInformation("预生成队列初始化完成，队列长度: {QueueLength}, 已存在: {ExistingCount}",
                    queuedCount, videoFiles.Count - queuedCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "扫描视频文件失败");
            }
        }

        private List<string> ScanVideoFiles(string directory, IFileService fileService)
        {
            var videoFiles = new List<string>();

            try
            {
                var files = Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories)
                    .Where(file => _videoExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                    .Select(file => file.Replace(fileService.GetRootPath() + Path.DirectorySeparatorChar, ""))
                    .ToList();

                videoFiles.AddRange(files);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "扫描目录失败: {Directory}", directory);
            }

            return videoFiles;
        }

        #endregion

        #region 后台生成

        private async Task GenerateThumbnailInBackground(string videoPath)
        {
            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var fileService = scope.ServiceProvider.GetRequiredService<IFileService>();

                if (ThumbnailExists(videoPath, fileService))
                {
                    _logger.LogDebug("缩略图已存在，跳过: {VideoPath}", videoPath);
                    return;
                }

                _logger.LogInformation("开始生成缩略图: {VideoPath}", videoPath);

                var request = new VideoThumbnailRequest
                {
                    VideoPath = videoPath,
                    Width = 320,
                    Height = 180,
                    OutputFormat = "jpg"
                };

                var result = await GenerateThumbnailAsync(request);

                if (result.Success)
                {
                    Interlocked.Increment(ref _generatedCount);
                    _logger.LogInformation("缩略图生成成功: {VideoPath}", videoPath);
                }
                else
                {
                    _logger.LogWarning("缩略图生成失败: {VideoPath}, 错误: {Error}",
                        videoPath, result.Message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "生成缩略图异常: {VideoPath}", videoPath);
            }
            finally
            {
                _processingVideos.TryRemove(videoPath, out _);
            }
        }

        #endregion

        #region IVideoThumbnailService 实现

        public async Task<VideoThumbnailResponse> GenerateThumbnailAsync(VideoThumbnailRequest request)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var fileService = scope.ServiceProvider.GetRequiredService<IFileService>();

            try
            {
                _logger.LogDebug("生成视频缩略图: {VideoPath}", request.VideoPath);

                if (!await fileService.FileExistsAsync(request.VideoPath))
                {
                    return new VideoThumbnailResponse
                    {
                        Success = false,
                        Message = "视频文件不存在"
                    };
                }

                var thumbnailPath = GetThumbnailFilePath(request, fileService);

                if (string.IsNullOrEmpty(thumbnailPath))
                {
                    return new VideoThumbnailResponse
                    {
                        Success = false,
                        Message = "无法生成缩略图路径"
                    };
                }

                if (File.Exists(thumbnailPath) && IsThumbnailUpToDate(request.VideoPath, thumbnailPath, fileService))
                {
                    return new VideoThumbnailResponse
                    {
                        Success = true,
                        ThumbnailPath = thumbnailPath,
                        PositionPercentage = 50,
                        Message = "缩略图已存在"
                    };
                }

                var duration = await GetVideoDurationAsync(request.VideoPath, fileService);
                TimeSpan thumbnailTime;
                TimeSpan? videoDuration = duration;

                if (duration != null)
                {
                    var random = new Random();
                    var positionPercentage = request.PositionPercentage ?? random.Next(30, 71);
                    thumbnailTime = TimeSpan.FromSeconds(duration.Value.TotalSeconds * positionPercentage / 100.0);

                    _logger.LogDebug("视频时长: {Duration}, 缩略图位置: {Position}% ({Time})",
                        duration.Value.ToString(@"hh\:mm\:ss"), positionPercentage, thumbnailTime.ToString(@"hh\:mm\:ss"));
                }
                else
                {
                    thumbnailTime = TimeSpan.FromSeconds(10);
                    _logger.LogDebug("无法获取视频时长，使用默认位置: {Time}", thumbnailTime.ToString(@"hh\:mm\:ss"));
                }

                var success = await GenerateThumbnailWithFFmpeg(request.VideoPath, thumbnailPath, thumbnailTime,
                    request.Width, request.Height, fileService);

                if (success)
                {
                    return new VideoThumbnailResponse
                    {
                        Success = true,
                        ThumbnailPath = thumbnailPath,
                        PositionPercentage = 50,
                        VideoDuration = videoDuration,
                        ThumbnailTime = thumbnailTime,
                        Message = "缩略图生成成功"
                    };
                }
                else
                {
                    if (await GeneratePlaceholderThumbnail(thumbnailPath, request.Width, request.Height))
                    {
                        return new VideoThumbnailResponse
                        {
                            Success = true,
                            ThumbnailPath = thumbnailPath,
                            PositionPercentage = 50,
                            VideoDuration = videoDuration,
                            ThumbnailTime = thumbnailTime,
                            Message = "使用占位图"
                        };
                    }

                    return new VideoThumbnailResponse
                    {
                        Success = false,
                        Message = "缩略图生成失败"
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "生成视频缩略图失败: {VideoPath}", request.VideoPath);
                return new VideoThumbnailResponse
                {
                    Success = false,
                    Message = $"缩略图生成失败: {ex.Message}"
                };
            }
        }
        

        public async Task<bool> VideoFileExistsAsync(string videoPath)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var fileService = scope.ServiceProvider.GetRequiredService<IFileService>();
            return await fileService.FileExistsAsync(videoPath);
        }

        public async Task<TimeSpan?> GetVideoDurationAsync(string videoPath)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var fileService = scope.ServiceProvider.GetRequiredService<IFileService>();
            return await GetVideoDurationAsync(videoPath, fileService);
        }

        private async Task<TimeSpan?> GetVideoDurationAsync(string videoPath, IFileService fileService)
        {
            try
            {
                if (!File.Exists(FFprobePath))
                {
                    _logger.LogWarning("FFprobe工具不存在: {FFprobePath}", FFprobePath);
                    return null;
                }

                var physicalPath = Path.Combine(fileService.GetRootPath(), videoPath);

                var arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{physicalPath}\"";

                var processStartInfo = new ProcessStartInfo
                {
                    FileName = FFprobePath,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var process = new Process { StartInfo = processStartInfo };

                var outputBuilder = new StringBuilder();
                var errorBuilder = new StringBuilder();

                process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        outputBuilder.AppendLine(e.Data);
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        errorBuilder.AppendLine(e.Data);
                    }
                };

                process.Start();

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var processExited = await Task.Run(() => process.WaitForExit(15000));

                if (!processExited)
                {
                    _logger.LogWarning("获取视频时长超时: {VideoPath}", videoPath);
                    try { process.Kill(); } catch { }
                    return null;
                }

                await Task.Delay(100);

                if (process.ExitCode == 0 && double.TryParse(outputBuilder.ToString().Trim(), out double seconds))
                {
                    return TimeSpan.FromSeconds(seconds);
                }
                else
                {
                    var error = errorBuilder.ToString();
                    _logger.LogWarning("获取视频时长失败: {Error}", error);
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取视频时长异常: {VideoPath}", videoPath);
                return null;
            }
        }

        public async Task<Stream> GetThumbnailStreamAsync(string thumbnailPath)
        {
            try
            {
                if (File.Exists(thumbnailPath))
                {
                    return new FileStream(thumbnailPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取缩略图流失败: {ThumbnailPath}", thumbnailPath);
                return null;
            }
        }

        public bool IsVideoFile(string extension)
        {
            return _videoExtensions.Contains(extension.ToLowerInvariant());
        }

        public bool ThumbnailExists(string videoPath, int width = 320, int height = 180)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var fileService = scope.ServiceProvider.GetRequiredService<IFileService>();
            return ThumbnailExists(videoPath, fileService, width, height);
        }

        private bool ThumbnailExists(string videoPath, IFileService fileService, int width = 320, int height = 180)
        {
            try
            {
                if (string.IsNullOrEmpty(videoPath))
                {
                    _logger.LogWarning("检查缩略图存在性时视频路径为空");
                    return false;
                }

                var request = new VideoThumbnailRequest
                {
                    VideoPath = videoPath,
                    Width = width,
                    Height = height,
                    OutputFormat = "jpg"
                };

                var thumbnailPath = GetThumbnailFilePath(request, fileService);

                if (string.IsNullOrEmpty(thumbnailPath))
                {
                    return false;
                }

                return File.Exists(thumbnailPath) && IsThumbnailUpToDate(videoPath, thumbnailPath, fileService);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检查缩略图存在性失败: {VideoPath}", videoPath);
                return false;
            }
        }

        public string GetThumbnailPath(string videoPath, int width = 320, int height = 180)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var fileService = scope.ServiceProvider.GetRequiredService<IFileService>();

            var request = new VideoThumbnailRequest
            {
                VideoPath = videoPath,
                Width = width,
                Height = height,
                OutputFormat = "jpg"
            };

            var thumbnailPath = GetThumbnailFilePath(request, fileService);
            return File.Exists(thumbnailPath) ? thumbnailPath : null;
        }

        public (int QueueLength, int GeneratedCount) GetGenerationStatus()
        {
            return (_generationQueue.Count, _generatedCount);
        }

        public async Task<List<VideoThumbnailResponse>> GenerateThumbnailsBatchAsync(List<VideoThumbnailRequest> requests)
        {
            _logger.LogInformation("开始批量生成缩略图，数量: {RequestCount}", requests.Count);

            var results = new List<VideoThumbnailResponse>();
            var tasks = new List<Task<VideoThumbnailResponse>>();

            var semaphore = new SemaphoreSlim(4);

            foreach (var request in requests)
            {
                tasks.Add(ProcessThumbnailRequestWithSemaphore(request, semaphore));
            }

            var responses = await Task.WhenAll(tasks);
            results.AddRange(responses);

            _logger.LogInformation("批量生成缩略图完成，成功: {SuccessCount}, 失败: {FailedCount}",
                results.Count(r => r.Success), results.Count(r => !r.Success));

            return results;
        }

        private async Task<VideoThumbnailResponse> ProcessThumbnailRequestWithSemaphore(VideoThumbnailRequest request, SemaphoreSlim semaphore)
        {
            await semaphore.WaitAsync();
            try
            {
                var result = await GenerateThumbnailAsync(request);
                if (result.Success)
                {
                    Interlocked.Increment(ref _generatedCount);
                }
                return result;
            }
            finally
            {
                semaphore.Release();
            }
        }

        public void CleanupOrphanedThumbnails()
        {
            try
            {
                _logger.LogInformation("开始清理过期的缩略图");

                if (!Directory.Exists(_thumbnailsRoot))
                {
                    _logger.LogInformation("缩略图目录不存在，无需清理");
                    return;
                }

                var thumbnailFiles = Directory.GetFiles(_thumbnailsRoot, "*.*", SearchOption.AllDirectories);
                int deletedCount = 0;
                int skippedCount = 0;

                foreach (var thumbnailFile in thumbnailFiles)
                {
                    try
                    {
                        var fileInfo = new FileInfo(thumbnailFile);

                        if (fileInfo.LastWriteTime < DateTime.Now.AddDays(-30))
                        {
                            fileInfo.Delete();
                            deletedCount++;
                            _logger.LogDebug("删除过期缩略图: {ThumbnailFile}", thumbnailFile);
                        }
                        else
                        {
                            skippedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "删除缩略图失败: {ThumbnailFile}", thumbnailFile);
                    }
                }

                _logger.LogInformation("清理完成: 删除了 {DeletedCount} 个过期的缩略图, 跳过了 {SkippedCount} 个",
                    deletedCount, skippedCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清理缩略图时发生异常");
            }
        }

        #endregion

        #region 工具方法

        private async Task<bool> GenerateThumbnailWithFFmpeg(string videoPath, string thumbnailPath, TimeSpan time, int width, int height, IFileService fileService)
        {
            try
            {
                if (!File.Exists(FFmpegPath))
                {
                    _logger.LogError("FFmpeg工具不存在: {FfmpegPath}", FFmpegPath);
                    return false;
                }

                var physicalVideoPath = Path.Combine(fileService.GetRootPath(), videoPath);
                var timeString = time.ToString(@"hh\:mm\:ss");

                var outputDir = Path.GetDirectoryName(thumbnailPath);
                if (!Directory.Exists(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                }

                var arguments = $"-ss {timeString} -i \"{physicalVideoPath}\" -vframes 1 -s {width}x{height} -q:v 2 -f image2 -y \"{thumbnailPath}\"";

                _logger.LogDebug("执行FFmpeg命令: {FfmpegPath} {Arguments}", FFmpegPath, arguments);

                var processStartInfo = new ProcessStartInfo
                {
                    FileName = FFmpegPath,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var process = new Process { StartInfo = processStartInfo };

                var outputBuilder = new StringBuilder();
                var errorBuilder = new StringBuilder();

                process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        outputBuilder.AppendLine(e.Data);
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        errorBuilder.AppendLine(e.Data);
                    }
                };

                process.Start();

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var processExited = await Task.Run(() => process.WaitForExit(30000));

                if (!processExited)
                {
                    _logger.LogWarning("FFmpeg进程超时，尝试终止: {VideoPath}", videoPath);
                    try { process.Kill(); } catch { }
                    return false;
                }

                await Task.Delay(100);

                _logger.LogDebug("FFmpeg退出代码: {ExitCode}", process.ExitCode);

                if (process.ExitCode == 0 && File.Exists(thumbnailPath))
                {
                    return true;
                }
                else
                {
                    var error = errorBuilder.ToString();
                    _logger.LogWarning("FFmpeg缩略图生成失败，退出代码: {ExitCode}", process.ExitCode);
                    if (!string.IsNullOrEmpty(error))
                    {
                        _logger.LogWarning("FFmpeg错误详情: {Error}", error);
                    }
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FFmpeg缩略图生成异常: {VideoPath}", videoPath);
                return false;
            }
        }

        private async Task<bool> GeneratePlaceholderThumbnail(string thumbnailPath, int width, int height)
        {
            try
            {
                var outputDir = Path.GetDirectoryName(thumbnailPath);
                if (!Directory.Exists(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                }

                var placeholderText = $"Video\n{width}x{height}";
                var svgContent = $@"<svg width='{width}' height='{height}' xmlns='http://www.w3.org/2000/svg'>
                    <rect width='100%' height='100%' fill='#2F4F4F'/>
                    <text x='50%' y='50%' font-family='Arial' font-size='{Math.Min(width, height) / 10}' 
                          fill='white' text-anchor='middle' dominant-baseline='middle'>{placeholderText}</text>
                    <polygon points='{width/2-20},{height/2-20} {width/2+20},{height/2} {width/2-20},{height/2+20}' 
                          fill='white'/>
                </svg>";

                await File.WriteAllTextAsync(thumbnailPath.Replace(".jpg", ".svg"), svgContent);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "生成占位图失败");
                return false;
            }
        }

        private string GetThumbnailFilePath(VideoThumbnailRequest request, IFileService fileService)
        {
            try
            {
                // 添加空值检查
                if (string.IsNullOrEmpty(request.VideoPath))
                {
                    _logger.LogWarning("视频路径为空");
                    return Path.Combine(_thumbnailsRoot ?? string.Empty, "default.jpg");
                }

                var videoFileName = Path.GetFileNameWithoutExtension(request.VideoPath);
                var videoDir = Path.GetDirectoryName(request.VideoPath);

                // 处理可能的空目录
                if (string.IsNullOrEmpty(videoDir))
                {
                    videoDir = "root";
                }
                else
                {
                    videoDir = videoDir.Replace(Path.DirectorySeparatorChar, '_');
                }

                var uniqueKey = $"{videoDir}_{videoFileName}_{request.Width}x{request.Height}";

                using var md5 = System.Security.Cryptography.MD5.Create();
                var hash = BitConverter.ToString(md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(uniqueKey)))
                    .Replace("-", "").ToLower();

                var fileName = $"{hash}.{request.OutputFormat.ToLowerInvariant()}";

                // 确保_thumbnailsRoot不为null
                if (string.IsNullOrEmpty(_thumbnailsRoot))
                {
                    _logger.LogWarning("_thumbnailsRoot 未初始化，使用临时目录");
                    return Path.Combine(Path.GetTempPath(), "thumbnails", fileName);
                }

                return Path.Combine(_thumbnailsRoot, fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取缩略图文件路径失败");
                // 返回一个安全的默认路径
                return Path.Combine(Path.GetTempPath(), "thumbnails", $"default_{Guid.NewGuid()}.jpg");
            }
        }

        private bool IsThumbnailUpToDate(string videoPath, string thumbnailPath, IFileService fileService)
        {
            try
            {
                var videoFile = new FileInfo(Path.Combine(fileService.GetRootPath(), videoPath));
                var thumbnailFile = new FileInfo(thumbnailPath);

                return thumbnailFile.Exists && videoFile.Exists &&
                       thumbnailFile.LastWriteTime >= videoFile.LastWriteTime;
            }
            catch
            {
                return false;
            }
        }

        private void ValidateFFmpegTools()
        {
            try
            {
                _logger.LogInformation("验证FFmpeg工具...");

                if (File.Exists(FFmpegPath))
                {
                    var processStartInfo = new ProcessStartInfo
                    {
                        FileName = FFmpegPath,
                        Arguments = "-version",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(processStartInfo);
                    if (process != null)
                    {
                        process.WaitForExit(5000);
                        if (process.ExitCode == 0)
                        {
                            _logger.LogInformation("FFmpeg工具验证成功: {FfmpegPath}", FFmpegPath);
                        }
                    }
                }
                else
                {
                    _logger.LogWarning("FFmpeg工具不存在: {FfmpegPath}", FFmpegPath);
                }

                if (File.Exists(FFprobePath))
                {
                    var processStartInfo = new ProcessStartInfo
                    {
                        FileName = FFprobePath,
                        Arguments = "-version",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(processStartInfo);
                    if (process != null)
                    {
                        process.WaitForExit(5000);
                        if (process.ExitCode == 0)
                        {
                            _logger.LogInformation("FFprobe工具验证成功: {FfprobePath}", FFprobePath);
                        }
                    }
                }
                else
                {
                    _logger.LogWarning("FFprobe工具不存在: {FfprobePath}", FFprobePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "验证FFmpeg工具时发生异常");
            }
        }

        #endregion
    }
}