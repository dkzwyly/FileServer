namespace FileServer.Models
{
    public class FileItem
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public long Size { get; set; }
        public string SizeFormatted { get; set; } = string.Empty;
        public DateTime LastModified { get; set; }
        public bool IsDirectory { get; set; }
    }

    // 原有的 FileInfoModel（保持不变）
    public class FileInfoModel
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public long Size { get; set; }
        public string SizeFormatted { get; set; } = string.Empty;
        public string Extension { get; set; } = string.Empty;
        public DateTime LastModified { get; set; }
        public bool IsVideo { get; set; }
        public bool IsAudio { get; set; }
        public string MimeType { get; set; } = string.Empty;
        public string Encoding { get; set; } = string.Empty;
        public bool HasThumbnail { get; set; }
        public PhotoMetadata? Metadata { get; set; }
    }

    public class DirectoryInfoModel
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
    }

    public class FileListResponse
    {
        public string CurrentPath { get; set; } = string.Empty;
        public string ParentPath { get; set; } = string.Empty;
        public List<DirectoryInfoModel> Directories { get; set; } = new();
        public List<FileInfoModel> Files { get; set; } = new();
    }

    // ====== 新增：上传文件详细信息 ======
    public class UploadedFileInfo
    {
        public string OriginalName { get; set; } = string.Empty;
        public string SavedName { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public long Size { get; set; }
        public bool WasRenamed { get; set; }
        public string RenameReason { get; set; } = string.Empty;
        public DateTime UploadTime { get; set; } = DateTime.UtcNow;
        public bool Success { get; set; } = true;
        public string ErrorMessage { get; set; } = string.Empty;
    }

    // ====== 新增：冲突解决信息 ======
    public class ConflictResolutionInfo
    {
        public string OriginalName { get; set; } = string.Empty;
        public string FinalName { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string ResolutionStrategy { get; set; } = string.Empty;
        public string Action { get; set; } = "Renamed"; // Renamed, Overwritten, Skipped, etc.
    }

    // ====== 修改后的 UploadResponse ======
    public class UploadResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;

        // 保持向后兼容：原有字段
        [Obsolete("请使用 UploadedFiles 字段替代")]
        public List<string> Files { get; set; } = new();

        public long TotalSize { get; set; }
        public string TotalSizeFormatted { get; set; } = string.Empty;

        // ====== 新增字段 ======
        public List<UploadedFileInfo> UploadedFiles { get; set; } = new();
        public List<ConflictResolutionInfo> ResolvedConflicts { get; set; } = new();
        public int TotalFiles { get; set; }
        public int SuccessfulUploads { get; set; }
        public int ConflictsResolved { get; set; }
        public int FailedUploads { get; set; }
        public DateTime UploadTime { get; set; }
        public string RequestId { get; set; } = string.Empty;
        public TimeSpan ProcessingTime { get; set; }

        // 构造函数
        public UploadResponse()
        {
            UploadTime = DateTime.UtcNow;
        }

        // 辅助方法：自动计算格式化的总大小
        public void CalculateFormattedSize()
        {
            TotalSizeFormatted = FormatFileSize(TotalSize);
        }

        private string FormatFileSize(long bytes)
        {
            if (bytes == 0) return "0 B";

            string[] sizes = { "B", "KB", "MB", "GB", "TB", "PB" };
            double len = bytes;
            int order = 0;

            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }

            // 根据大小选择合适的格式
            return order > 1 ? $"{len:0.##} {sizes[order]}" : $"{len:0} {sizes[order]}";
        }

        // 辅助方法：生成请求ID
        public void GenerateRequestId()
        {
            RequestId = Guid.NewGuid().ToString("N");
        }
    }
}