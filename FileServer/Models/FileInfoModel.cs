namespace FileServer.Models
{
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
        public bool HasThumbnail { get; set; } // 新增：是否有缩略图
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

    public class UploadResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public List<string> Files { get; set; } = new();
        public long TotalSize { get; set; }
        public string TotalSizeFormatted { get; set; } = string.Empty;
    }
}