namespace FileServer.Models
{
    /// <summary>
    /// 文件操作进度。Copy 用来向 Worker 上报字节进度。
    /// </summary>
    public class FileOperationProgress
    {
        public long CopiedBytes { get; }
        public long TotalBytes { get; }

        public FileOperationProgress(long copiedBytes, long totalBytes)
        {
            CopiedBytes = copiedBytes;
            TotalBytes = totalBytes;
        }

        public int Percent =>
            TotalBytes > 0 ? (int)(CopiedBytes * 100 / TotalBytes) : 0;
    }
}