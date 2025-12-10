using Microsoft.AspNetCore.Mvc.Filters;

namespace FileServer.Models
{
    public class PhotoMetadata
    {
        public string RelativePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public DateTime? DateTaken { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public string? CameraModel { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public long FileSize { get; set; }
        public DateTime LastModified { get; set; }      // 文件最后修改时间（用于检测变化）
        public DateTime LastMetadataUpdate { get; set; } // 元数据最后更新时间
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

    // 扩展文件列表项，增加元数据（用于排序后返回）
    public class FileListItemWithMetadata : FileItem
    {
        public PhotoMetadata? Metadata { get; set; }
    }
}