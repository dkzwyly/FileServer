using FileServer.Models;

namespace FileServer.Services
{
    public interface IAudioMetadataService
    {
        // ----- 指纹主键操作 -----
        Task<SongMetadata?> GetMetadataByFingerprintAsync(string fingerprint);
        Task SaveMetadataByFingerprintAsync(string fingerprint, string path, SongMetadata metadata);

        // ----- 常规操作 -----
        Task<SongMetadata> GetMetadataAsync(string filePath);                  // 自动提取或从缓存
        Task<SongMetadata?> GetMetadataMappingAsync(string songPath);          // 仅获取已保存的
        Task<bool> SaveMetadataMappingAsync(string songPath, SongMetadata metadata);
        Task<bool> DeleteMetadataMappingAsync(string songPath);               // 按路径删除

        // ----- 删除（指纹） -----
        Task DeleteMetadataByFingerprintAsync(string fingerprint);

        // ----- 封面 -----
        Task<Stream?> GetAlbumCoverAsync(string filePath);
        Task<string?> SaveCustomCoverAsync(string songPath, Stream coverStream, string fileName);
        Task<bool> DeleteCustomCoverAsync(string songPath);

        // ----- 批量 -----
        Task<Dictionary<string, SongMetadata>> GetBatchMetadataAsync(List<string> paths);

        // ----- 扫描 -----
        Task ScanAndIndexAllAsync(string musicDirectory, IProgress<string>? progress = null);

        // ----- 工具 -----
        Task<bool> IsEmptyAsync();
    }
}