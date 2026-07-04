using FileServer.Models;

namespace FileServer.Services
{
    public interface IPhotoMetadataService
    {
        Task<PhotoMetadata?> GetOrExtractMetadataAsync(string relativePath);
        Task<Dictionary<string, PhotoMetadata>> GetBatchMetadataAsync(IEnumerable<string> relativePaths);
        Task<(IEnumerable<PhotoMetadata> Items, int TotalCount)> SearchPhotosAsync(PhotoSearchOptions options);
        Task RefreshMetadataAsync(string relativePath);
        Task ScanAndIndexAllPhotosAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);
        Task ScanConfiguredDirectoriesAsync();

        // 新增：删除指定图片的元数据（同时清理内存缓存和数据库）
        Task<bool> DeleteMetadataAsync(string relativePath);
    }
}