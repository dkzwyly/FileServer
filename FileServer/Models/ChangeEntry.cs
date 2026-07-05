namespace FileServer.Models
{
    public enum ChangeType
    {
        Add,
        Remove
    }

    public class ChangeEntry
    {
        public ChangeType Type { get; set; }
        public FileNode Node { get; set; }   // 仅当 Type == Add 时有效
        public string Path { get; set; }     // 仅当 Type == Remove 时有效
    }
}