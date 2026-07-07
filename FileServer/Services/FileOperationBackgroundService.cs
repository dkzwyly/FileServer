using FileServer.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using LiteDB;
using Microsoft.Extensions.Configuration;
using System.IO;

namespace FileServer.Services
{
    public class FileOperationBackgroundService : BackgroundService
    {
        private readonly IJobQueue _jobQueue;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<FileOperationBackgroundService> _logger;
        private readonly string _dbPath;
        private readonly string _connectionString;

        public FileOperationBackgroundService(
            IJobQueue jobQueue,
            IServiceProvider serviceProvider,
            ILogger<FileOperationBackgroundService> logger,
            IConfiguration configuration,
            IFileSystemHelper fileSystemHelper)
        {
            _jobQueue = jobQueue;
            _serviceProvider = serviceProvider;
            _logger = logger;

            var rootPath = fileSystemHelper.GetRootPath();
            var metadataDir = configuration["FileServerConfig:MetadataDirectory"] ?? "系统文件";
            var fullMetadataDir = Path.Combine(rootPath, metadataDir);
            if (!Directory.Exists(fullMetadataDir))
                Directory.CreateDirectory(fullMetadataDir);
            _dbPath = Path.Combine(fullMetadataDir, "file-jobs.db");
            _connectionString = $"Filename={_dbPath};Connection=Shared";
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("文件操作后台服务已启动");

            await RecoverProcessingJobsAsync();

            await foreach (var job in _jobQueue.DequeueAllAsync(stoppingToken))
            {
                if (stoppingToken.IsCancellationRequested)
                    break;

                await ProcessJobAsync(job, stoppingToken);
            }
        }

        private async Task RecoverProcessingJobsAsync()
        {
            try
            {
                using var db = new LiteDatabase(_connectionString);
                var col = db.GetCollection<FileOperationJob>("jobs");
                var processing = col.Find(j => j.Status == "Processing");
                foreach (var job in processing)
                {
                    job.Status = "Queued";
                    col.Update(job);
                    _logger.LogWarning("恢复未完成的任务: {JobId}", job.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "恢复任务状态失败");
            }
        }

        private async Task ProcessJobAsync(FileOperationJob job, CancellationToken cancellationToken)
        {
            // ---- 第一步：更新状态为 Processing，然后立即释放数据库 ----
            try
            {
                using (var db = new LiteDatabase(_connectionString))
                {
                    var col = db.GetCollection<FileOperationJob>("jobs");
                    job.Status = "Processing";
                    col.Update(job);
                }
                _logger.LogInformation("开始处理任务 {JobId}: {OpType} {Src} -> {Dst}",
                    job.Id, job.OperationType, job.SourcePath, job.DestPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新任务状态为 Processing 失败");
                return;
            }

            // ---- 第二步：执行耗时的文件操作（不持有数据库锁） ----
            bool success = false;
            string errorMessage = null;
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var fileService = scope.ServiceProvider.GetRequiredService<IFileService>();

                if (job.OperationType == "Move")
                    success = await fileService.MoveAsync(job.SourcePath, job.DestPath);
                else if (job.OperationType == "Copy")
                    success = await fileService.CopyAsync(job.SourcePath, job.DestPath);
                else
                    throw new InvalidOperationException($"未知操作类型: {job.OperationType}");

                if (!success)
                    throw new Exception("文件操作返回失败");
            }
            catch (Exception ex)
            {
                success = false;
                errorMessage = ex.Message;
                _logger.LogError(ex, "文件操作执行失败");
            }

            // ---- 第三步：更新最终状态，再次打开数据库 ----
            try
            {
                using var db = new LiteDatabase(_connectionString);
                var col = db.GetCollection<FileOperationJob>("jobs");
                // 重新加载最新状态（避免并发冲突）
                var currentJob = col.FindById(job.Id);
                if (currentJob == null)
                {
                    _logger.LogWarning("任务 {JobId} 已被删除", job.Id);
                    return;
                }

                if (success)
                {
                    currentJob.Status = "Completed";
                    currentJob.ProgressPercent = 100;
                    currentJob.CompleteTime = DateTime.UtcNow;
                    col.Update(currentJob);

                    // 刷新树缓存（耗时操作放在这里，但不持有数据库锁太久）
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

                    _logger.LogInformation("任务 {JobId} 完成", job.Id);
                }
                else
                {
                    currentJob.Status = "Failed";
                    currentJob.ErrorMessage = errorMessage ?? "未知错误";
                    currentJob.CompleteTime = DateTime.UtcNow;
                    col.Update(currentJob);
                    _logger.LogError("任务 {JobId} 失败: {Message}", job.Id, errorMessage);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新任务最终状态失败");
            }
        }
    }
}