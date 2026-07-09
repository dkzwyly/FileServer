using LiteDB;

namespace FileServer.Models
{
    public class TrashRecord
    {
        [BsonId]
        public string Id { get; set; }  // Guid 字符串

        public string OriginalPath { get; set; }   // 原始相对路径
        public string TrashPath { get; set; }      // 回收站内相对路径
        public bool IsDirectory { get; set; }
        public DateTime DeletedTime { get; set; }
        public long FileSize { get; set; }

        // 保存文件的指纹，用于恢复时更新元数据的 FilePath
        public string Fingerprint { get; set; }
    }
}