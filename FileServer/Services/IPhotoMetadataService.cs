using FileServer.Models;

namespace FileServer.Services
{
    public interface IPhotoMetadataService
    {
        // ----- 指纹主键操作 -----
        Task<PhotoMetadata?> GetMetadataByFingerprintAsync(string fingerprint);
        Task SaveMetadataByFingerprintAsync(string fingerprint, string path, PhotoMetadata metadata);

        // ----- 常规操作 -----
        Task<PhotoMetadata?> GetOrExtractMetadataAsync(string relativePath);
        Task<Dictionary<string, PhotoMetadata>> GetBatchMetadataAsync(IEnumerable<string> relativePaths);
        Task RefreshMetadataAsync(string relativePath);

        // ----- 删除 -----
        Task<bool> DeleteMetadataAsync(string relativePath);               // 按路径（兼容旧代码）
        Task DeleteMetadataByFingerprintAsync(string fingerprint);         // 按指纹（清空回收站用）

        // ----- 搜索 -----
        Task<(IEnumerable<PhotoMetadata> Items, int TotalCount)> SearchPhotosAsync(PhotoSearchOptions options);

        // ----- 扫描/索引 -----
        Task ScanAndIndexAllPhotosAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);
        Task ScanConfiguredDirectoriesAsync();

        // ----- 工具 -----
        Task<bool> IsEmptyAsync();
    }
}