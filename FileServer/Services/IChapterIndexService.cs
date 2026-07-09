using FileServer.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FileServer.Services
{
    public interface IChapterIndexService
    {
        /// <summary>按指纹获取章节索引（从磁盘加载）</summary>
        Task<ChapterIndex?> GetChapterIndexByFingerprintAsync(string fingerprint);

        /// <summary>按路径获取章节索引（内部转为指纹）</summary>
        Task<ChapterIndex?> GetChapterIndexByPathAsync(string filePath);

        /// <summary>构建并保存索引（需传入指纹）</summary>
        Task<ChapterIndex> BuildChapterIndexAsync(string filePath, string content, string fingerprint);

        /// <summary>保存索引对象（对象中必须包含 Fingerprint）</summary>
        Task SaveChapterIndexAsync(ChapterIndex index);

        /// <summary>按指纹删除索引文件</summary>
        Task<bool> DeleteChapterIndexByFingerprintAsync(string fingerprint);

        /// <summary>按路径删除索引（兼容旧接口）</summary>
        bool DeleteChapterIndex(string filePath);

        /// <summary>获取所有索引文件信息（用于管理）</summary>
        List<string> GetAllIndexFilesInfo();

        /// <summary>强制重建索引（按路径）</summary>
        Task<ChapterIndex> ForceRebuildChapterIndexAsync(string filePath, string content);

        /// <summary>获取索引文件信息（用于调试）</summary>
        string GetIndexFileInfo(string filePath);

        /// <summary>尝试从缓存加载索引（按路径），不重新构建</summary>
        Task<ChapterIndex?> GetCachedChapterIndexAsync(string filePath);
    }
}