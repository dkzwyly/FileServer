using FileServer.Models;

namespace FileServer.Services
{
    public interface ITrashService
    {
        // 将文件/目录移到回收站
        Task<TrashRecord> MoveToTrashAsync(string relativePath, bool isDirectory);

        // 从回收站恢复（可指定目标目录）
        Task<bool> RestoreFromTrashAsync(string recordId, string targetDir = null);

        // 获取所有回收站记录
        Task<List<TrashRecord>> GetAllRecordsAsync();

        // 获取单个记录
        Task<TrashRecord> GetRecordAsync(string recordId);

        // 清空回收站（物理删除所有文件并删除元数据）
        Task<int> EmptyTrashAsync();

        // 永久删除单个记录
        Task<bool> PermanentDeleteAsync(string recordId);
    }
}