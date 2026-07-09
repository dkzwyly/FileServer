using System.Threading.Tasks;

namespace FileServer.Services
{
    public interface ILyricsMappingService
    {
        /// <summary>按指纹保存或更新歌词映射</summary>
        Task<bool> SaveMappingAsync(string fingerprint, string lyricsPath);

        /// <summary>按指纹获取歌词路径</summary>
        Task<string?> GetMappingByFingerprintAsync(string fingerprint);

        /// <summary>按歌曲路径获取歌词映射（内部转为指纹）</summary>
        Task<string?> GetMappingByPathAsync(string songPath);

        /// <summary>按指纹删除映射</summary>
        Task<bool> DeleteMappingByFingerprintAsync(string fingerprint);

        /// <summary>按路径删除映射（兼容旧调用）</summary>
        Task<bool> DeleteMappingByPathAsync(string songPath);
    }
}