using System;

namespace FileServer.Models
{
    public class VideoThumbnailResponse
    {
        public bool Success { get; set; }
        public string ThumbnailPath { get; set; }
        public int PositionPercentage { get; set; }
        public string Message { get; set; }

        // 改为可空类型
        public TimeSpan? VideoDuration { get; set; }
        public TimeSpan? ThumbnailTime { get; set; }

        // 添加格式化属性以便前端使用
        public string VideoDurationFormatted =>
            VideoDuration?.ToString(@"hh\:mm\:ss") ?? "未知";
        public string ThumbnailTimeFormatted =>
            ThumbnailTime?.ToString(@"hh\:mm\:ss") ?? "未知";
    }

    public class VideoThumbnailRequest
    {
        public string VideoPath { get; set; }
        public int? PositionPercentage { get; set; }
        public int Width { get; set; } = 320;
        public int Height { get; set; } = 180;
        public string OutputFormat { get; set; } = "jpg";
    }
}