using LiteDB;

namespace FileServer.Services
{
    public interface ILiteDbProvider
    {
        /// <summary>
        /// 获取（或创建）一个命名的 LiteDatabase 单例。
        /// </summary>
        /// <param name="name">逻辑名，用于区分不同的数据库，如 "trash"、"audio-metadata"</param>
        /// <param name="customPath">可选自定义路径（相对于 RootPath 或绝对路径）。为空则按 name 推导。</param>
        /// <param name="mapper">可选自定义 BsonMapper</param>
        LiteDatabase GetDatabase(string name, string? customPath = null, BsonMapper? mapper = null);
    }
}