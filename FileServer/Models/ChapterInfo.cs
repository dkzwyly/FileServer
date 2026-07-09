namespace FileServer.Models
{
    public class ChapterInfo
    {
        public string Title { get; set; } = string.Empty;
        public int StartCharOffset { get; set; }   // 章节标题在全文中的起始字符索引
        public string Preview { get; set; } = string.Empty;
    }

    public class ChapterIndex
    {
        // ===== 新增：指纹作为唯一标识 =====
        public string Fingerprint { get; set; } = string.Empty;

        // 文件路径（仅用于展示，不作为主键）
        public string FilePath { get; set; } = string.Empty;

        public long FileSize { get; set; }
        public DateTime FileLastModified { get; set; }
        public DateTime IndexTime { get; set; }
        public int TotalChapters { get; set; }
        public List<ChapterInfo> Chapters { get; set; } = new List<ChapterInfo>();
    }
}