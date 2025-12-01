// LyricsMappingRequest.cs
public class LyricsMappingRequest
{
    public string SongPath { get; set; }
    public string LyricsPath { get; set; }
}

// LyricsFileInfo.cs
public class LyricsFileInfo
{
    public string Path { get; set; }
    public string Name { get; set; }
    public long Size { get; set; }
    public string SizeFormatted { get; set; }
    public DateTime ModifiedTime { get; set; }
}