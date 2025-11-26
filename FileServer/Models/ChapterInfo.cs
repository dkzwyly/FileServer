// 在 Models 文件夹下添加 ChapterInfo.cs
namespace FileServer.Models
{
    public class ChapterInfo
    {
        public string Title { get; set; } = string.Empty;
        public int Page { get; set; }
        public int LineNumber { get; set; }
        public string Preview { get; set; } = string.Empty;
    }

    public class ChapterIndex
    {
        public string FilePath { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public DateTime FileLastModified { get; set; }
        public DateTime IndexTime { get; set; }
        public int TotalChapters { get; set; }
        public List<ChapterInfo> Chapters { get; set; } = new List<ChapterInfo>();
    }
}