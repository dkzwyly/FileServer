namespace FileServer.Models
{
    public class FileServerConfig
    {
        public string RootPath { get; set; } = @"E:\FileServer";
        public int HttpPort { get; set; } = 8080;
        public int HttpsPort { get; set; } = 8081;
        public int QuicPort { get; set; } = 8082;
        public long MaxFileSize { get; set; } = 10L * 1024 * 1024 * 1024; // 10GB
        public bool EnableQuic { get; set; } = true;
        public string CertificatePath { get; set; } = "";
        public string CertificatePassword { get; set; } = "";

        // 添加FFmpeg相关配置
        public string FFmpegPath { get; set; } = @"D:\ffmpeg-release-full\ffmpeg-7.1.1-full_build\bin\ffmpeg.exe";
        public string FFprobePath { get; set; } = @"D:\ffmpeg-release-full\ffmpeg-7.1.1-full_build\bin\ffprobe.exe";

        // 缩略图配置
        public int ThumbnailWidth { get; set; } = 320;
        public int ThumbnailHeight { get; set; } = 180;
        public string ThumbnailFormat { get; set; } = "jpg";
        public int MaxConcurrentThumbnailWorkers { get; set; } = 4;
        public int ThumbnailPositionPercentage { get; set; } = 50;

        // ====== 新增：冲突解决配置 ======
        public ConflictResolutionConfig ConflictResolution { get; set; } = new ConflictResolutionConfig();
    }

    // 新增：冲突解决配置类
    public class ConflictResolutionConfig
    {
        public string Strategy { get; set; } = "AddCounter";
        public string CounterFormat { get; set; } = " ({0})";
        public int MaxAttempts { get; set; } = 1000;
        public bool EnableLogging { get; set; } = true;
        public bool KeepOriginalExtension { get; set; } = true;
        public bool SkipEmptyFiles { get; set; } = true;
        public int MaxFilenameLength { get; set; } = 255;
        public bool GenerateBackup { get; set; } = false; // 是否生成备份
        public string BackupDirectory { get; set; } = ".backups"; // 备份目录
        public int KeepBackupDays { get; set; } = 30; // 保留备份天数
    }
}