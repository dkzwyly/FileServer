using System.IO;
using System.Threading.Tasks;
using FileServer.Models;

namespace FileServer.Services
{
    public interface IAudioMetadataService
    {
        /// <summary>
        /// 获取歌曲元数据（优先返回映射，否则自动解析文件标签）
        /// </summary>
        Task<SongMetadata> GetMetadataAsync(string filePath);

        /// <summary>
        /// 保存元数据映射（标题、艺术家、专辑）
        /// </summary>
        Task<bool> SaveMetadataMappingAsync(string songPath, SongMetadata metadata);

        /// <summary>
        /// 获取已保存的元数据映射
        /// </summary>
        Task<SongMetadata?> GetMetadataMappingAsync(string songPath);

        /// <summary>
        /// 获取专辑封面（优先返回自定义封面，否则返回内嵌封面）
        /// </summary>
        Task<Stream?> GetAlbumCoverAsync(string filePath);

        /// <summary>
        /// 保存自定义封面图片（上传文件）
        /// </summary>
        Task<string?> SaveCustomCoverAsync(string songPath, Stream coverStream, string fileName);

        /// <summary>
        /// 删除自定义封面映射
        /// </summary>
        Task<bool> DeleteCustomCoverAsync(string songPath);

        /// <summary>
        /// 删除整个元数据映射（包括自定义封面文件）
        /// </summary>
        Task<bool> DeleteMetadataMappingAsync(string songPath);
    }
}