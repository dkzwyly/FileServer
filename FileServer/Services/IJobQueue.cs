using System.Threading.Channels;
using FileServer.Models;

namespace FileServer.Services
{
    public interface IJobQueue
    {
        /// <summary>入队。返回 false 表示写入失败（Channel 已关闭等）</summary>
        bool Enqueue(FileOperationJob job);

        IAsyncEnumerable<FileOperationJob> DequeueAllAsync(CancellationToken cancellationToken);
    }
}