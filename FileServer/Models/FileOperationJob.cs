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
        public string Status { get; set; } = "Queued"; // Queued, Processing, Completed, Failed
        public int ProgressPercent { get; set; } = 0;
        public string ErrorMessage { get; set; }
        public DateTime QueueTime { get; set; } = DateTime.UtcNow;
        public DateTime? CompleteTime { get; set; }
    }
}