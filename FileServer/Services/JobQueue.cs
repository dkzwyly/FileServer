using System.Threading.Channels;
using FileServer.Models;
using Microsoft.Extensions.Logging;

namespace FileServer.Services
{
    public class JobQueue : IJobQueue
    {
        private readonly Channel<FileOperationJob> _channel =
            Channel.CreateUnbounded<FileOperationJob>(new UnboundedChannelOptions
            {
                SingleReader = false,   // 支持多消费者
                SingleWriter = false
            });

        private readonly ILogger<JobQueue> _logger;

        public JobQueue(ILogger<JobQueue> logger)
        {
            _logger = logger;
        }

        public bool Enqueue(FileOperationJob job)
        {
            if (job == null) return false;
            var ok = _channel.Writer.TryWrite(job);
            if (!ok)
                _logger.LogWarning("任务入队失败（Channel 已关闭？）: {JobId}", job.Id);
            return ok;
        }

        public async IAsyncEnumerable<FileOperationJob> DequeueAllAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            await foreach (var job in _channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return job;
            }
        }
    }
}