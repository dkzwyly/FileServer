using LiteDB;

namespace FileServer.Models
{
    public class SongMetadata
    {
        [BsonId]
        public string FilePath { get; set; } = null!;
        public string Title { get; set; } = null!;
        public string Artist { get; set; } = null!;
        public string Album { get; set; } = null!;
        public bool HasCover { get; set; }          // 是否有内嵌封面（自动解析）
        public string? CustomCoverPath { get; set; } // 用户上传的自定义封面路径（相对路径）
        public DateTime? LastModified { get; set; } // 元数据提取时文件的最后修改时间（UTC）
    }

    public class SaveMetadataMappingRequest
    {
        public string SongPath { get; set; } = "";
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }
    }

    public class CoverUploadRequest
    {
        public string SongPath { get; set; } = "";
        public IFormFile CoverFile { get; set; } = null!;
    }
}