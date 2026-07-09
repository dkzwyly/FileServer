using LiteDB;

namespace FileServer.Models
{
    public class PhotoMetadata
    {
        [BsonId]
        public int Id { get; set; }

        // 文件指纹（唯一索引）
        public string Fingerprint { get; set; }

        // 文件相对路径（用于显示和搜索）
        public string RelativePath { get; set; }

        // 文件名（缓存，便于显示）
        public string FileName { get; set; }

        // 文件大小（字节）
        public long FileSize { get; set; }

        // 文件最后修改时间（UTC）
        public DateTime LastModified { get; set; }

        // 元数据最后更新时间（UTC）
        public DateTime LastMetadataUpdate { get; set; }

        // EXIF 数据
        public DateTime? DateTaken { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public string CameraMake { get; set; }
        public string CameraModel { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
    }

    public class PhotoSearchOptions
    {
        public string? DirectoryPath { get; set; }
        public string? SortBy { get; set; } = "dateTaken";
        public bool SortAscending { get; set; } = true;
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public double? MinLatitude { get; set; }
        public double? MaxLatitude { get; set; }
        public double? MinLongitude { get; set; }
        public double? MaxLongitude { get; set; }
        public int Skip { get; set; } = 0;
        public int Take { get; set; } = 100;
    }
}