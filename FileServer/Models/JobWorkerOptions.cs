namespace FileServer.Models
{
    public class JobWorkerOptions
    {
        /// <summary>最大并发任务数</summary>
        public int MaxConcurrentJobs { get; set; } = 2;

        /// <summary>失败最大重试次数</summary>
        public int MaxRetryCount { get; set; } = 3;

        /// <summary>写入进度的最小百分比间隔（如 5 表示进度变化 ≥5% 才写 LiteDB）</summary>
        public int ProgressWriteThreshold { get; set; } = 5;

        /// <summary>检查 CancelRequested 的间隔（毫秒）</summary>
        public int CancelCheckIntervalMs { get; set; } = 2000;
    }
}