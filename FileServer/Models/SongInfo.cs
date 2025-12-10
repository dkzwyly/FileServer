namespace FileServer.Models
{
    public class SongInfo
    {
        public string FileName { get; set; }
        public string CleanedFileName { get; set; }
        public string SongName { get; set; }
        public string CleanedSongName { get; set; }
        public string Artist { get; set; }
        public string CleanedArtist { get; set; }
    }

    public class BestLyricsMatch
    {
        public LyricsFileInfo LyricsFile { get; set; }
        public double MatchScore { get; set; }
        public bool HasLyricsKeyword { get; set; }
        public List<string> MatchedChars { get; set; } = new List<string>();
        public List<string> MatchedMultiChars { get; set; } = new List<string>();
    }

    public class MatchResult
    {
        public double MatchScore { get; set; }
        public bool HasLyricsKeyword { get; set; }
        public List<string> MatchedChars { get; set; } = new List<string>();
        public List<string> MatchedMultiChars { get; set; } = new List<string>();
    }
}