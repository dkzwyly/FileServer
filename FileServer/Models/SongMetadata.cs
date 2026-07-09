using LiteDB;

namespace FileServer.Models
{
    public class SongMetadata
    {
        [BsonId]
        public int Id { get; set; }

        // 文件指纹
        public string Fingerprint { get; set; }

        // 文件路径（绝对或相对，取决于使用场景）
        public string FilePath { get; set; }

        public string Title { get; set; }
        public string Artist { get; set; }
        public string Album { get; set; }
        public bool HasCover { get; set; }
        public string CustomCoverPath { get; set; }

        // 文件最后修改时间（UTC），用于检测变化
        public DateTime? LastModified { get; set; }

        // 元数据最后更新时间（UTC）
        public DateTime LastMetadataUpdate { get; set; }
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