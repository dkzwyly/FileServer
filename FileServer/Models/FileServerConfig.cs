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
    }
}