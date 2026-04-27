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

        // 新增：扫描配置中指定的图片目录
        Task ScanConfiguredDirectoriesAsync();
    }
}