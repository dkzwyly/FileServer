using System.Threading.Channels;
using FileServer.Models;

namespace FileServer.Services
{
    public interface IJobQueue
    {
        void Enqueue(FileOperationJob job);
        IAsyncEnumerable<FileOperationJob> DequeueAllAsync(CancellationToken cancellationToken);
    }
}