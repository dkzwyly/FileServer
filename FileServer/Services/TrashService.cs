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
        private readonly ILogger<TrashService> _logger;
        private readonly LiteDatabase _db;
        private readonly ILiteCollection<TrashRecord> _collection;
        private readonly string _trashRoot;

        public TrashService(
            IFileSystemHelper fileSystemHelper,
            IPhotoMetadataService photoMetadataService,
            IAudioMetadataService audioMetadataService,
            ILogger<TrashService> logger,
            IConfiguration configuration)
        {
            _fileSystemHelper = fileSystemHelper;
            _photoMetadataService = photoMetadataService;
            _audioMetadataService = audioMetadataService;
            _logger = logger;

            var rootPath = _fileSystemHelper.GetRootPath();
            _trashRoot = Path.Combine(rootPath, "系统文件", ".trash");
            if (!Directory.Exists(_trashRoot))
                Directory.CreateDirectory(_trashRoot);

            var metadataDir = configuration["FileServerConfig:MetadataDirectory"] ?? "系统文件";
            var fullMetadataDir = Path.Combine(rootPath, metadataDir);
            if (!Directory.Exists(fullMetadataDir))
                Directory.CreateDirectory(fullMetadataDir);

            var dbPath = Path.Combine(fullMetadataDir, "trash.db");
            _db = new LiteDatabase($"Filename={dbPath};Connection=Shared");
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

            // 在回收站内保留相对目录结构，文件名加时间戳
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

            // 移动
            if (isDirectory)
                Directory.Move(fullPath, trashFullPath);
            else
                File.Move(fullPath, trashFullPath);

            // 获取指纹（仅文件）
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
                // 用户指定目标目录
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

            // 确保目标目录存在
            var targetDirPath = Path.GetDirectoryName(targetFullPath);
            if (!Directory.Exists(targetDirPath))
                Directory.CreateDirectory(targetDirPath);

            // 移动回主存储
            if (record.IsDirectory)
                Directory.Move(trashFullPath, targetFullPath);
            else
                File.Move(trashFullPath, targetFullPath);

            // 更新元数据路径（如果指纹非空）
            if (!record.IsDirectory && !string.IsNullOrEmpty(record.Fingerprint))
            {
                // 更新照片元数据
                var photoMeta = await _photoMetadataService.GetMetadataByFingerprintAsync(record.Fingerprint);
                if (photoMeta != null)
                {
                    photoMeta.RelativePath = targetRelativePath;
                    await _photoMetadataService.SaveMetadataByFingerprintAsync(record.Fingerprint, targetRelativePath, photoMeta);
                }

                // 更新音频元数据
                var audioMeta = await _audioMetadataService.GetMetadataByFingerprintAsync(record.Fingerprint);
                if (audioMeta != null)
                {
                    audioMeta.FilePath = targetRelativePath;
                    await _audioMetadataService.SaveMetadataByFingerprintAsync(record.Fingerprint, targetRelativePath, audioMeta);
                }
            }

            _collection.Delete(record.Id);
            _logger.LogInformation("恢复成功: {OriginalPath} -> {TargetPath}", record.OriginalPath, targetRelativePath);
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

                    // 物理删除
                    if (record.IsDirectory && Directory.Exists(trashFullPath))
                        Directory.Delete(trashFullPath, true);
                    else if (!record.IsDirectory && File.Exists(trashFullPath))
                        File.Delete(trashFullPath);

                    // 删除元数据
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
    }
}