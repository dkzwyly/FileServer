using System.Threading.Tasks;

namespace FileServer.Services
{
    /// <summary>
    /// 歌词映射服务（存储歌曲路径 ↔ 歌词文件路径）
    /// </summary>
    public interface ILyricsMappingService
    {
        /// <summary>
        /// 保存或更新歌词映射
        /// </summary>
        /// <param name="songPath">歌曲绝对路径（作为主键）</param>
        /// <param name="lyricsPath">歌词文件路径，或特殊值 "NO_LYRICS"</param>
        Task<bool> SaveMappingAsync(string songPath, string lyricsPath);

        /// <summary>
        /// 获取歌词映射
        /// </summary>
        Task<string?> GetMappingAsync(string songPath);

        /// <summary>
        /// 删除歌词映射
        /// </summary>
        Task<bool> DeleteMappingAsync(string songPath);
    }
}