// FileConflictService.cs
using FileServer.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileServer.Services
{
    public interface IFileConflictService
    {
        Task<string> ResolveConflictAsync(string directory, string fileName);
        Task<string> GenerateUniqueFileNameAsync(string directory, string fileName);
        Task<ConflictResolutionInfo> GetConflictResolutionInfoAsync(string directory, string fileName);
    }

    public class FileConflictService : IFileConflictService
    {
        private readonly ILogger<FileConflictService> _logger;
        private readonly FileServerOptions _options;

        public FileConflictService(
            ILogger<FileConflictService> logger,
            IOptions<FileServerOptions> options)
        {
            _logger = logger;
            _options = options.Value;
        }

        /// <summary>
        /// 解决文件冲突，生成唯一的文件名
        /// </summary>
        public async Task<string> ResolveConflictAsync(string directory, string fileName)
        {
            return await GenerateUniqueFileNameAsync(directory, fileName);
        }

        /// <summary>
        /// 生成唯一的文件名（添加序号）
        /// </summary>
        public async Task<string> GenerateUniqueFileNameAsync(string directory, string fileName)
        {
            try
            {
                // 确保目录存在
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                    _logger.LogDebug("创建目录: {Directory}", directory);
                    return fileName; // 目录不存在，直接使用原文件名
                }

                // 分离文件名和扩展名
                var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
                var extension = Path.GetExtension(fileName);

                // 检查文件是否已存在
                var fullPath = Path.Combine(directory, fileName);
                if (!File.Exists(fullPath))
                {
                    _logger.LogDebug("文件不存在，使用原文件名: {FileName}", fileName);
                    return fileName;
                }

                _logger.LogInformation("检测到重名文件: {FileName} 在目录 {Directory}", fileName, directory);

                // 解析文件名中的序号（如果已有）
                var (baseName, startCounter) = ParseFileNameWithCounter(fileNameWithoutExtension);

                // 寻找可用的序号
                var counter = startCounter;
                string newFileName;
                var maxAttempts = 1000; // 防止无限循环

                do
                {
                    newFileName = $"{baseName} ({counter}){extension}";
                    fullPath = Path.Combine(directory, newFileName);
                    counter++;

                    if (counter > startCounter + maxAttempts)
                    {
                        throw new InvalidOperationException(
                            $"无法为文件生成唯一文件名，尝试次数过多: {fileName}，目录: {directory}");
                    }

                } while (File.Exists(fullPath));

                _logger.LogInformation("文件重名，生成新文件名: {Original} -> {New}",
                    fileName, newFileName);

                return newFileName;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "生成唯一文件名失败: {FileName}，目录: {Directory}",
                    fileName, directory);
                throw;
            }
        }

        /// <summary>
        /// 获取冲突解决信息
        /// </summary>
        public async Task<ConflictResolutionInfo> GetConflictResolutionInfoAsync(string directory, string fileName)
        {
            var originalName = fileName;
            var finalName = await GenerateUniqueFileNameAsync(directory, fileName);

            return new ConflictResolutionInfo
            {
                OriginalName = originalName,
                FinalName = finalName,
                Reason = originalName != finalName ? "重名冲突" : "无冲突",
                Timestamp = DateTime.UtcNow,
                ResolutionStrategy = "AddCounter"
            };
        }

        /// <summary>
        /// 解析文件名中的序号
        /// </summary>
        private (string baseName, int startCounter) ParseFileNameWithCounter(string fileNameWithoutExtension)
        {
            // 匹配模式：文件名 (数字)
            var pattern = @"^(.*?)\s+\((\d+)\)$";
            var match = System.Text.RegularExpressions.Regex.Match(fileNameWithoutExtension, pattern);

            if (match.Success && match.Groups.Count == 3)
            {
                var baseName = match.Groups[1].Value.Trim();
                if (int.TryParse(match.Groups[2].Value, out int existingCounter))
                {
                    return (baseName, existingCounter + 1);
                }
            }

            // 没有匹配到序号，返回原文件名，从1开始
            return (fileNameWithoutExtension, 1);
        }

        /// <summary>
        /// 批量处理文件名冲突
        /// </summary>
        public async Task<List<ConflictResolutionInfo>> BatchResolveConflictsAsync(
            string directory, List<string> fileNames)
        {
            var results = new List<ConflictResolutionInfo>();

            foreach (var fileName in fileNames)
            {
                var info = await GetConflictResolutionInfoAsync(directory, fileName);
                results.Add(info);
            }

            return results;
        }

        /// <summary>
        /// 检查文件名是否会导致冲突
        /// </summary>
        public bool WillCauseConflict(string directory, string fileName)
        {
            var fullPath = Path.Combine(directory, fileName);
            return File.Exists(fullPath);
        }
    }
}