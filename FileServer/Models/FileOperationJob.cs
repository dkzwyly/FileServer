using LiteDB;
using System;

namespace FileServer.Models
{
    public class FileOperationJob
    {
        [BsonId]
        public Guid Id { get; set; } = Guid.NewGuid();
        public string SourcePath { get; set; }
        public string DestPath { get; set; }
        public string OperationType { get; set; } // "Move" 或 "Copy"
        public string Status { get; set; } = "Queued"; // Queued, Processing, Completed, Failed, Cancelled
        public int ProgressPercent { get; set; } = 0;
        public string ErrorMessage { get; set; }
        public DateTime QueueTime { get; set; } = DateTime.UtcNow;
        public DateTime? CompleteTime { get; set; }

        // ===== 新增字段 =====
        /// <summary>重试次数。Worker 失败时递增，超过阈值改为 Failed</summary>
        public int RetryCount { get; set; } = 0;

        /// <summary>客户端请求取消。Worker 周期性检查此字段</summary>
        public bool CancelRequested { get; set; } = false;

        /// <summary>本次操作总字节数（用于进度条，可为 0 表示未知）</summary>
        public long TotalBytes { get; set; } = 0;

        /// <summary>已处理字节数</summary>
        public long CopiedBytes { get; set; } = 0;
    }
}