using System.Threading.Channels;
using FileServer.Models;

namespace FileServer.Services
{
    public class JobQueue : IJobQueue
    {
        private readonly Channel<FileOperationJob> _channel = Channel.CreateUnbounded<FileOperationJob>();

        public void Enqueue(FileOperationJob job)
        {
            _channel.Writer.TryWrite(job);
        }

        public async IAsyncEnumerable<FileOperationJob> DequeueAllAsync(CancellationToken cancellationToken)
        {
            await foreach (var job in _channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return job;
            }
        }
    }
}