using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FileServer.Models;
using LiteDB;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FileServer.Services
{
    public class TrashService : ITrashService
    {
        private readonly IFileSystemHelper _fileSystemHelper;
        private readonly IPhotoMetadataService _photoMetadataService;
        private readonly IAudioMetadataService _audioMetadataService;
        private readonly IFileTreeCacheService _treeCache;   // 新增
        private readonly ILogger<TrashService> _logger;
        private readonly LiteDatabase _db;
        private readonly ILiteCollection<TrashRecord> _collection;
        private readonly string _trashRoot;

        public TrashService(
    IFileSystemHelper fileSystemHelper,
    IPhotoMetadataService photoMetadataService,
    IAudioMetadataService audioMetadataService,
    IFileTreeCacheService treeCache,
    ILogger<TrashService> logger,
    ILiteDbProvider dbProvider)         // 替换 IConfiguration
        {
            _fileSystemHelper = fileSystemHelper;
            _photoMetadataService = photoMetadataService;
            _audioMetadataService = audioMetadataService;
            _treeCache = treeCache;
            _logger = logger;

            var rootPath = _fileSystemHelper.GetRootPath();
            _trashRoot = Path.Combine(rootPath, "系统文件", ".trash");
            if (!Directory.Exists(_trashRoot))
                Directory.CreateDirectory(_trashRoot);

            _db = dbProvider.GetDatabase("trash");
            _collection = _db.GetCollection<TrashRecord>("trash");
            _collection.EnsureIndex(x => x.OriginalPath);
            _collection.EnsureIndex(x => x.DeletedTime);
        }

        public async Task<TrashRecord> MoveToTrashAsync(string relativePath, bool isDirectory)
        {
            var root = _fileSystemHelper.GetRootPath();
            var fullPath = Path.Combine(root, relativePath);

            if (isDirectory && !Directory.Exists(fullPath))
                throw new DirectoryNotFoundException($"目录不存在: {relativePath}");
            if (!isDirectory && !File.Exists(fullPath))
                throw new FileNotFoundException($"文件不存在: {relativePath}");

            var parentDir = Path.GetDirectoryName(relativePath) ?? "";
            var fileName = Path.GetFileName(relativePath);
            var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            var newName = $"{nameWithoutExt}_{timestamp}{ext}";
            var trashRelativePath = Path.Combine(parentDir, newName);
            var trashFullPath = Path.Combine(_trashRoot, trashRelativePath);

            var trashDir = Path.GetDirectoryName(trashFullPath);
            if (!Directory.Exists(trashDir))
                Directory.CreateDirectory(trashDir);

            // 物理移动
            if (isDirectory)
                Directory.Move(fullPath, trashFullPath);
            else
                File.Move(fullPath, trashFullPath);

            // 指纹（仅文件）
            string fingerprint = null;
            long fileSize = 0;
            if (!isDirectory)
            {
                var fi = new FileInfo(trashFullPath);
                fileSize = fi.Length;
                fingerprint = $"{fi.Length}_{fi.LastWriteTimeUtc.Ticks}";
            }

            var record = new TrashRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                OriginalPath = relativePath,
                TrashPath = trashRelativePath,
                IsDirectory = isDirectory,
                DeletedTime = DateTime.Now,
                FileSize = fileSize,
                Fingerprint = fingerprint
            };

            _collection.Insert(record);
            _logger.LogInformation("已移至回收站: {OriginalPath} -> {TrashPath}", relativePath, trashRelativePath);

            // ===== 新增：主动刷新树缓存 =====
            await RefreshTreeCacheAsync(relativePath, removeOnly: true);

            return record;
        }

        public async Task<bool> RestoreFromTrashAsync(string recordId, string targetDir = null)
        {
            var record = _collection.FindById(recordId);
            if (record == null)
                return false;

            var root = _fileSystemHelper.GetRootPath();
            var trashFullPath = Path.Combine(_trashRoot, record.TrashPath);
            if (!File.Exists(trashFullPath) && !Directory.Exists(trashFullPath))
                return false;

            // 确定目标路径
            string targetRelativePath;
            if (!string.IsNullOrEmpty(targetDir))
            {
                targetRelativePath = Path.Combine(targetDir, Path.GetFileName(record.OriginalPath));
            }
            else
            {
                targetRelativePath = record.OriginalPath;
            }

            var targetFullPath = Path.Combine(root, targetRelativePath);

            // 自动处理重名：加 _恢复_时间戳
            if (File.Exists(targetFullPath) || Directory.Exists(targetFullPath))
            {
                var dir = Path.GetDirectoryName(targetRelativePath) ?? "";
                var name = Path.GetFileNameWithoutExtension(targetRelativePath);
                var ext = Path.GetExtension(targetRelativePath);
                var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                var newName = $"{name}_恢复_{timestamp}{ext}";
                targetRelativePath = Path.Combine(dir, newName);
                targetFullPath = Path.Combine(root, targetRelativePath);
                _logger.LogInformation("目标已存在，自动改名: {TargetPath}", targetRelativePath);
            }

            var targetDirPath = Path.GetDirectoryName(targetFullPath);
            if (!Directory.Exists(targetDirPath))
                Directory.CreateDirectory(targetDirPath);

            // 移动回主存储
            if (record.IsDirectory)
                Directory.Move(trashFullPath, targetFullPath);
            else
                File.Move(trashFullPath, targetFullPath);

            // 更新元数据路径（按指纹）
            if (!record.IsDirectory && !string.IsNullOrEmpty(record.Fingerprint))
            {
                var photoMeta = await _photoMetadataService.GetMetadataByFingerprintAsync(record.Fingerprint);
                if (photoMeta != null)
                {
                    photoMeta.RelativePath = targetRelativePath;
                    await _photoMetadataService.SaveMetadataByFingerprintAsync(record.Fingerprint, targetRelativePath, photoMeta);
                }

                var audioMeta = await _audioMetadataService.GetMetadataByFingerprintAsync(record.Fingerprint);
                if (audioMeta != null)
                {
                    audioMeta.FilePath = targetRelativePath;
                    await _audioMetadataService.SaveMetadataByFingerprintAsync(record.Fingerprint, targetRelativePath, audioMeta);
                }
            }

            _collection.Delete(record.Id);
            _logger.LogInformation("恢复成功: {OriginalPath} -> {TargetPath}", record.OriginalPath, targetRelativePath);

            // ===== 新增：主动刷新树缓存 =====
            await RefreshTreeCacheAsync(targetRelativePath, removeOnly: false);

            return true;
        }

        public Task<List<TrashRecord>> GetAllRecordsAsync()
        {
            var records = _collection.Query().OrderByDescending(x => x.DeletedTime).ToList();
            return Task.FromResult(records);
        }

        public Task<TrashRecord> GetRecordAsync(string recordId)
        {
            return Task.FromResult(_collection.FindById(recordId));
        }

        public async Task<int> EmptyTrashAsync()
        {
            var records = _collection.Query().ToList();
            int deletedCount = 0;

            foreach (var record in records)
            {
                try
                {
                    var trashFullPath = Path.Combine(_trashRoot, record.TrashPath);

                    if (record.IsDirectory && Directory.Exists(trashFullPath))
                        Directory.Delete(trashFullPath, true);
                    else if (!record.IsDirectory && File.Exists(trashFullPath))
                        File.Delete(trashFullPath);

                    if (!record.IsDirectory && !string.IsNullOrEmpty(record.Fingerprint))
                    {
                        await _photoMetadataService.DeleteMetadataByFingerprintAsync(record.Fingerprint);
                        await _audioMetadataService.DeleteMetadataByFingerprintAsync(record.Fingerprint);
                    }

                    _collection.Delete(record.Id);
                    deletedCount++;
                    _logger.LogInformation("永久删除: {TrashPath}", record.TrashPath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "清空回收站失败: {TrashPath}", record.TrashPath);
                }
            }

            // 空回收站只影响 .trash 目录，不影响主存储树，无需刷树
            return deletedCount;
        }

        public async Task<bool> PermanentDeleteAsync(string recordId)
        {
            var record = _collection.FindById(recordId);
            if (record == null)
                return false;

            var trashFullPath = Path.Combine(_trashRoot, record.TrashPath);
            if (record.IsDirectory && Directory.Exists(trashFullPath))
                Directory.Delete(trashFullPath, true);
            else if (!record.IsDirectory && File.Exists(trashFullPath))
                File.Delete(trashFullPath);

            if (!record.IsDirectory && !string.IsNullOrEmpty(record.Fingerprint))
            {
                await _photoMetadataService.DeleteMetadataByFingerprintAsync(record.Fingerprint);
                await _audioMetadataService.DeleteMetadataByFingerprintAsync(record.Fingerprint);
            }

            _collection.Delete(record.Id);
            _logger.LogInformation("永久删除: {TrashPath}", record.TrashPath);
            return true;
        }

        // ===== 新增：统一的树缓存刷新 =====
        /// <summary>
        /// 主动刷新主存储树缓存。
        /// removeOnly=true: 删除场景，先移除节点（目录会递归移除子树），再刷新父目录
        /// removeOnly=false: 恢复场景，直接刷新父目录（触发重新扫描）
        /// 不依赖 Windows 目录 mtime 的延迟更新，主动失效缓存。
        /// </summary>
        private async Task RefreshTreeCacheAsync(string relativePath, bool removeOnly)
        {
            try
            {
                var normalized = (relativePath ?? "").Replace('\\', '/').Trim('/');

                if (removeOnly)
                {
                    await _treeCache.RemoveNodeAsync(normalized);
                }

                var parent = Path.GetDirectoryName(normalized)?.Replace("\\", "/") ?? "";
                await _treeCache.GetDirectoryContentAsync(parent);

                _logger.LogInformation("树缓存已刷新: {Path} (removeOnly={RemoveOnly})",
                    normalized, removeOnly);
            }
            catch (Exception ex)
            {
                // 刷新失败不阻断业务，只记录日志
                _logger.LogWarning(ex, "刷新树缓存失败: {Path}", relativePath);
            }
        }
    }
}