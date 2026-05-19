#nullable enable
namespace FileServer.Models
{
    public class FileServerConfig
    {
        // 基础配置（必须有明确值，配置文件缺失时由配置系统负责报错）
        public string RootPath { get; set; } = null!;               // 必须配置
        public int HttpPort { get; set; } = 8080;
        public int HttpsPort { get; set; } = 8081;
        public int QuicPort { get; set; } = 8082;
        public long MaxFileSize { get; set; } = 10L * 1024 * 1024 * 1024;
        public bool EnableQuic { get; set; } = true;
        public string CertificatePath { get; set; } = "";
        public string CertificatePassword { get; set; } = "";

        // FFmpeg 工具路径（必须配置，否则视频缩略图功能不可用）
        public string FFmpegPath { get; set; } = null!;
        public string FFprobePath { get; set; } = null!;

        // 缩略图生成参数
        public int ThumbnailWidth { get; set; } = 320;
        public int ThumbnailHeight { get; set; } = 180;
        public string ThumbnailFormat { get; set; } = "jpg";
        public int MaxConcurrentThumbnailWorkers { get; set; } = 4;
        public int ThumbnailPositionPercentage { get; set; } = 50;

        // ========== 以下是新增的可配置子目录（必须配置） ==========
        // 图片索引目录（相对于 RootPath）
        public List<string> PhotoIndexDirectories { get; set; } = new();
        // 音频库根目录（相对 RootPath，如 "data/音乐"）
        public string AudioIndexDirectory { get; set; } = null!;
        // 视频库根目录（相对 RootPath，如 "data/影视"）
        public string VideoLibraryDirectory { get; set; } = null!;
        // 缩略图存储根目录（相对 RootPath，如 "_thumbnails"）
        public string ThumbnailDirectory { get; set; } = null!;
        // 元数据文件存储目录（相对 RootPath，如 ".metadata"）
        public string MetadataDirectory { get; set; } = null!;
        // 自定义封面存储目录（相对 RootPath，如 "covers"）
        public string CoversDirectory { get; set; } = null!;
        // 歌词映射文件名称（直接存放于 RootPath 下）
        public string LyricsMappingFile { get; set; } = null!;
        // 歌曲元数据映射文件名称（直接存放于 RootPath 下）
        public string SongMetadataMappingFile { get; set; } = null!;

        // 冲突解决配置
        public ConflictResolutionConfig ConflictResolution { get; set; } = new();
    }

    public class ConflictResolutionConfig
    {
        public string Strategy { get; set; } = "AddCounter";
        public string CounterFormat { get; set; } = " ({0})";
        public int MaxAttempts { get; set; } = 1000;
        public bool EnableLogging { get; set; } = true;
        public bool KeepOriginalExtension { get; set; } = true;
        public bool SkipEmptyFiles { get; set; } = true;
        public int MaxFilenameLength { get; set; } = 255;
        public bool GenerateBackup { get; set; } = false;
        public string BackupDirectory { get; set; } = ".backups";
        public int KeepBackupDays { get; set; } = 30;
    }
}