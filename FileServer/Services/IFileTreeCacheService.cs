using FileServer.Models;

namespace FileServer.Services
{
    public interface IFileTreeCacheService
    {
        /// <summary>
        /// 获取指定目录的内容（直接子文件和目录），优先从内存返回，若内存缺失或磁盘变动则扫描更新
        /// </summary>
        Task<FileListResponse> GetDirectoryContentAsync(string relativePath);

        /// <summary>
        /// 添加一个新节点（文件或目录），必须保证物理文件/目录已存在
        /// </summary>
        Task AddNodeAsync(FileNode node);

        /// <summary>
        /// 删除节点及其所有子孙节点（递归），物理删除应已提前完成
        /// </summary>
        Task RemoveNodeAsync(string path);

        /// <summary>
        /// 将所有积压的变更应用到数据库（增量同步），若无可变更则跳过
        /// </summary>
        Task ApplyChangesToDatabaseAsync();

        /// <summary>
        /// 启动时从数据库加载所有节点到内存
        /// </summary>
        Task LoadAllFromDatabaseAsync();

        /// <summary>
        /// 全量覆盖（可选，用于灾难恢复或每周校验），使用时请谨慎
        /// </summary>
        Task FullSaveToDatabaseAsync();
        /// <summary>
        /// 从内存缓存中获取指定目录下所有文件路径（递归），不访问磁盘。
        /// 如果树缓存中该目录不存在或已过期，会触发增量扫描（访问磁盘）来修复缓存。
        /// </summary>
        Task<List<string>> GetAllFilePathsAsync(string relativePath);
    }
}