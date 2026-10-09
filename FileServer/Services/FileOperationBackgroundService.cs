using FileServer.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Threading;
using System.Threading.Tasks;
using LiteDB;
using Microsoft.Extensions.Configuration;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace FileServer.Services
{
    public class FileOperationBackgroundService : BackgroundService
    {
        private readonly IJobQueue _jobQueue;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<FileOperationBackgroundService> _logger;
        private readonly JobWorkerOptions _options;
        private readonly string _connectionString;

        public FileOperationBackgroundService(
            IJobQueue jobQueue,
            IServiceProvider serviceProvider,
            ILogger<FileOperationBackgroundService> logger,
            IConfiguration configuration,
            IOptions<JobWorkerOptions> options,
            IFileSystemHelper fileSystemHelper)
        {
            _jobQueue = jobQueue;
            _serviceProvider = serviceProvider;
            _logger = logger;
            _options = options.Value;

            var rootPath = fileSystemHelper.GetRootPath();
            var metadataDir = configuration["FileServerConfig:MetadataDirectory"] ?? "系统文件";
            var fullMetadataDir = Path.Combine(rootPath, metadataDir);
            if (!Directory.Exists(fullMetadataDir))
                Directory.CreateDirectory(fullMetadataDir);
            var dbPath = Path.Combine(fullMetadataDir, "file-jobs.db");
            _connectionString = $"Filename={dbPath};Connection=Shared";
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("文件操作后台服务已启动（并发={Max}，重试={Retry}）",
                _options.MaxConcurrentJobs, _options.MaxRetryCount);

            // 1. 恢复未完成任务
            await RecoverJobsAsync(stoppingToken);

            // 2. 并发消费
            var semaphore = new SemaphoreSlim(_options.MaxConcurrentJobs);

            try
            {
                await foreach (var job in _jobQueue.DequeueAllAsync(stoppingToken))
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    await semaphore.WaitAsync(stoppingToken);

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await ProcessJobAsync(job, stoppingToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "任务处理未捕获异常: {JobId}", job.Id);
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("文件操作后台服务停止中...");
            }
        }

        /// <summary>
        /// 启动时从 LiteDB 恢复所有未完成的 job 并重新入队。
        /// 这一步解决"服务器重启后队列丢失"的问题。
        /// </summary>
        private async Task RecoverJobsAsync(CancellationToken ct)
        {
            try
            {
                List<FileOperationJob> pending;
                using (var db = new LiteDatabase(_connectionString))
                {
                    var col = db.GetCollection<FileOperationJob>("jobs");

                    // Queued 和 Processing 都需要恢复
                    pending = col.Find(j => j.Status == "Queued" || j.Status == "Processing").ToList();

                    // Processing 说明上次跑到一半崩了，改回 Queued
                    foreach (var job in pending.Where(j => j.Status == "Processing"))
                    {
                        job.Status = "Queued";
                        job.ErrorMessage = "服务重启，任务重新排队";
                        col.Update(job);
                    }
                }

                foreach (var job in pending)
                {
                    if (!_jobQueue.Enqueue(job))
                    {
                        _logger.LogError("恢复任务入队失败: {JobId}", job.Id);
                    }
                    else
                    {
                        _logger.LogWarning("恢复未完成的任务: {JobId} ({Op}) {Src} -> {Dst}",
                            job.Id, job.OperationType, job.SourcePath, job.DestPath);
                    }
                }

                _logger.LogInformation("任务恢复完成，共 {Count} 个", pending.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "恢复任务失败");
            }

            await Task.CompletedTask;
        }

        private async Task ProcessJobAsync(FileOperationJob job, CancellationToken stoppingToken)
        {
            // ---- 1. 更新状态为 Processing ----
            try
            {
                using var db = new LiteDatabase(_connectionString);
                var col = db.GetCollection<FileOperationJob>("jobs");
                var current = col.FindById(job.Id);
                if (current == null)
                {
                    _logger.LogWarning("任务已被删除: {JobId}", job.Id);
                    return;
                }
                current.Status = "Processing";
                current.ErrorMessage = null;
                col.Update(current);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新状态为 Processing 失败: {JobId}", job.Id);
                return;
            }

            // ---- 2. 启动取消监听 ----
            using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var cancelWatcher = StartCancelWatcher(job.Id, jobCts);

            // ---- 3. 进度节流回调 ----
            int lastWrittenPercent = -1;
            var progressThrottle = new Progress<FileOperationProgress>(p =>
            {
                try
                {
                    int percent = p.Percent;
                    if (percent - lastWrittenPercent >= _options.ProgressWriteThreshold
                        || percent == 100)
                    {
                        lastWrittenPercent = percent;
                        using var db = new LiteDatabase(_connectionString);
                        var col = db.GetCollection<FileOperationJob>("jobs");
                        var cur = col.FindById(job.Id);
                        if (cur != null)
                        {
                            cur.ProgressPercent = percent;
                            cur.CopiedBytes = p.CopiedBytes;
                            cur.TotalBytes = p.TotalBytes;
                            col.Update(cur);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "写进度失败: {JobId}", job.Id);
                }
            });

            // ---- 4. 执行 ----
            bool success = false;
            string errorMessage = null;
            bool cancelled = false;
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var fileService = scope.ServiceProvider.GetRequiredService<IFileService>();

                if (job.OperationType == "Move")
                {
                    success = await fileService.MoveAsync(job.SourcePath, job.DestPath, jobCts.Token);
                }
                else if (job.OperationType == "Copy")
                {
                    success = await fileService.CopyAsync(job.SourcePath, job.DestPath, progressThrottle, jobCts.Token);
                }
                else
                {
                    throw new InvalidOperationException($"未知操作类型: {job.OperationType}");
                }

                if (!success)
                    throw new Exception("文件操作返回失败");
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                errorMessage = "任务已取消";
                _logger.LogInformation("任务取消: {JobId}", job.Id);
            }
            catch (Exception ex)
            {
                success = false;
                errorMessage = ex.Message;
                _logger.LogError(ex, "文件操作执行失败: {JobId}", job.Id);
            }
            finally
            {
                cancelWatcher?.Dispose();
            }

            // ---- 5. 写最终状态 / 重试 ----
            try
            {
                using var db = new LiteDatabase(_connectionString);
                var col = db.GetCollection<FileOperationJob>("jobs");
                var current = col.FindById(job.Id);
                if (current == null)
                {
                    _logger.LogWarning("任务已被删除，跳过最终状态写入: {JobId}", job.Id);
                    return;
                }

                if (cancelled)
                {
                    current.Status = "Cancelled";
                    current.ErrorMessage = errorMessage;
                    current.CompleteTime = DateTime.UtcNow;
                    col.Update(current);
                    return;
                }

                if (success)
                {
                    current.Status = "Completed";
                    current.ProgressPercent = 100;
                    current.CompleteTime = DateTime.UtcNow;
                    col.Update(current);

                    // 刷新树缓存（尽力而为）
                    try
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var treeCache = scope.ServiceProvider.GetRequiredService<IFileTreeCacheService>();
                        var srcParent = Path.GetDirectoryName(job.SourcePath)?.Replace("\\", "/") ?? "";
                        var dstParent = Path.GetDirectoryName(job.DestPath)?.Replace("\\", "/") ?? "";
                        await treeCache.GetDirectoryContentAsync(srcParent);
                        await treeCache.GetDirectoryContentAsync(dstParent);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "刷新树缓存失败，但任务已完成");
                    }

                    _logger.LogInformation("任务完成: {JobId}", job.Id);
                }
                else
                {
                    // 重试判断
                    if (current.RetryCount < _options.MaxRetryCount)
                    {
                        current.RetryCount++;
                        current.Status = "Queued";
                        current.ErrorMessage = $"{errorMessage}（第 {current.RetryCount} 次重试）";
                        col.Update(current);

                        if (!_jobQueue.Enqueue(current))
                        {
                            _logger.LogError("重试入队失败: {JobId}", job.Id);
                            current.Status = "Failed";
                            col.Update(current);
                        }
                        else
                        {
                            _logger.LogWarning("任务失败将重试 ({Retry}/{Max}): {JobId}, 原因: {Msg}",
                                current.RetryCount, _options.MaxRetryCount, job.Id, errorMessage);
                        }
                    }
                    else
                    {
                        current.Status = "Failed";
                        current.ErrorMessage = errorMessage ?? "未知错误";
                        current.CompleteTime = DateTime.UtcNow;
                        col.Update(current);
                        _logger.LogError("任务彻底失败: {JobId}, 原因: {Msg}", job.Id, errorMessage);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "写最终状态失败: {JobId}", job.Id);
            }
        }

        /// <summary>
        /// 周期性检查 LiteDB 里的 CancelRequested，置位则触发取消。
        /// 返回的 IDisposable 用于停止监视。
        /// </summary>
        private IDisposable StartCancelWatcher(Guid jobId, CancellationTokenSource jobCts)
        {
            var inner = CancellationTokenSource.CreateLinkedTokenSource(jobCts.Token);
            var task = Task.Run(async () =>
            {
                try
                {
                    while (!inner.Token.IsCancellationRequested)
                    {
                        await Task.Delay(_options.CancelCheckIntervalMs, inner.Token);

                        using var db = new LiteDatabase(_connectionString);
                        var col = db.GetCollection<FileOperationJob>("jobs");
                        var j = col.FindById(jobId);
                        if (j == null) break;
                        if (j.Status == "Completed" || j.Status == "Failed" || j.Status == "Cancelled") break;

                        if (j.CancelRequested)
                        {
                            _logger.LogInformation("检测到取消请求: {JobId}", jobId);
                            jobCts.Cancel();
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "取消监视异常: {JobId}", jobId);
                }
            });

            return new DisposeAction(() => { inner.Cancel(); });
        }

        private class DisposeAction : IDisposable
        {
            private readonly Action _action;
            public DisposeAction(Action action) { _action = action; }
            public void Dispose() => _action();
        }
    }
}