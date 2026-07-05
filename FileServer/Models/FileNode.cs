using LiteDB;

namespace FileServer.Models
{
    public class FileNode
    {
        [BsonId]
        public string Path { get; set; }               // 相对路径，如 "data/影视/第一季/01.mp4"
        public string Name { get; set; }
        public string ParentPath { get; set; }        // 父目录路径，根目录为 ""
        public bool IsDirectory { get; set; }
        public long? Size { get; set; }                // 仅文件有效
        public DateTime LastModified { get; set; }     // 文件或目录的修改时间（UTC）
        public string MimeType { get; set; }
        public bool IsVideo { get; set; }
        public bool IsAudio { get; set; }
        public bool IsImage { get; set; }
        // 注意：不存储 HasThumbnail
    }
}