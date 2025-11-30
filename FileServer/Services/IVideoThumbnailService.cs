using FileServer.Models;

namespace FileServer.Services
{
    public interface IVideoThumbnailService
    {
        // 原有的方法
        Task<VideoThumbnailResponse> GenerateThumbnailAsync(VideoThumbnailRequest request);
        Task<bool> VideoFileExistsAsync(string videoPath);
        Task<TimeSpan?> GetVideoDurationAsync(string videoPath);
        Task<Stream> GetThumbnailStreamAsync(string thumbnailPath);
        bool IsVideoFile(string extension);

        // 新增的方法
        bool ThumbnailExists(string videoPath, int width = 320, int height = 180);
        string GetThumbnailPath(string videoPath, int width = 320, int height = 180);
        void QueueVideoForGeneration(string videoPath);
        (int QueueLength, int GeneratedCount) GetGenerationStatus();

        // 批量处理和清理方法
        Task<List<VideoThumbnailResponse>> GenerateThumbnailsBatchAsync(List<VideoThumbnailRequest> requests);
        void CleanupOrphanedThumbnails();
    }
}