namespace FileServer.Models
{
    /// <summary>
    /// 影视库树节点
    /// </summary>
    public class VideoLibraryNode
    {
        /// <summary>显示名称</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>相对路径</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>节点类型：root / season / video</summary>
        public string Type { get; set; } = string.Empty;

        /// <summary>子节点列表（文件夹类型）</summary>
        public List<VideoLibraryNode>? Children { get; set; }

        /// <summary>文件大小（字节）</summary>
        public long? Size { get; set; }

        /// <summary>文件大小（格式化）</summary>
        public string? SizeFormatted { get; set; }
    }
}