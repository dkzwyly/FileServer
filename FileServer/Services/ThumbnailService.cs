// ThumbnailService.cs
using FileServer.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace FileServer.Services
{
    public class ThumbnailService : IThumbnailService
    {
        private readonly string _thumbnailsRoot;
        private readonly ILogger<ThumbnailService> _logger;

        public ThumbnailService(IConfiguration configuration, ILogger<ThumbnailService> logger)
        {
            var fileServerRoot = configuration["FileServer:RootPath"] ?? @"D:\FileServer";
            // 使用隐藏文件夹，避免递归生成缩略图
            _thumbnailsRoot = Path.Combine(fileServerRoot, ".thumbnails");
            _logger = logger;

            EnsureThumbnailDirectory();
        }

        private void EnsureThumbnailDirectory()
        {
            if (!Directory.Exists(_thumbnailsRoot))
            {
                Directory.CreateDirectory(_thumbnailsRoot);

                // 在Windows系统上设置目录为隐藏
                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        var directoryInfo = new DirectoryInfo(_thumbnailsRoot);
                        directoryInfo.Attributes |= FileAttributes.Hidden;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "无法设置缩略图目录为隐藏属性");
                    }
                }

                _logger.LogInformation("创建缩略图目录: {ThumbnailsRoot}", _thumbnailsRoot);
            }
        }

        public async Task<bool> GenerateThumbnailAsync(string imagePath, int width = 200, int height = 200)
        {
            try
            {
                // 检查是否为缩略图路径，避免递归
                if (IsThumbnailPath(imagePath))
                {
                    _logger.LogWarning("跳过缩略图生成，原图路径为缩略图: {ImagePath}", imagePath);
                    return false;
                }

                var fullImagePath = Path.GetFullPath(Path.Combine(_thumbnailsRoot, "..", imagePath));
                if (!File.Exists(fullImagePath))
                {
                    _logger.LogWarning("原图片不存在: {ImagePath}", fullImagePath);
                    return false;
                }

                // 检查文件是否为支持的图片格式
                if (!IsSupportedImageFormat(fullImagePath))
                {
                    _logger.LogWarning("不支持的图片格式: {ImagePath}", fullImagePath);
                    return false;
                }

                var thumbnailPath = await GetThumbnailPathAsync(imagePath);
                var thumbnailDir = Path.GetDirectoryName(thumbnailPath);

                if (!Directory.Exists(thumbnailDir))
                {
                    Directory.CreateDirectory(thumbnailDir);
                }

                using (var image = await Image.LoadAsync(fullImagePath))
                {
                    image.Mutate(x => x.Resize(new ResizeOptions
                    {
                        Size = new Size(width, height),
                        Mode = ResizeMode.Max
                    }));

                    var encoder = new JpegEncoder { Quality = 80 };
                    await image.SaveAsync(thumbnailPath, encoder);
                }

                _logger.LogInformation("生成缩略图成功: {ImagePath} -> {ThumbnailPath}", imagePath, thumbnailPath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "生成缩略图失败: {ImagePath}", imagePath);
                return false;
            }
        }

        public async Task<bool> DeleteThumbnailAsync(string imagePath)
        {
            try
            {
                // 检查是否为缩略图路径
                if (IsThumbnailPath(imagePath))
                {
                    _logger.LogWarning("不允许删除缩略图文件: {ImagePath}", imagePath);
                    return false;
                }

                var thumbnailPath = await GetThumbnailPathAsync(imagePath);
                if (File.Exists(thumbnailPath))
                {
                    File.Delete(thumbnailPath);
                    _logger.LogInformation("删除缩略图: {ThumbnailPath}", thumbnailPath);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除缩略图失败: {ImagePath}", imagePath);
                return false;
            }
        }

        public Task<string> GetThumbnailPathAsync(string imagePath)
        {
            // 使用相对路径的MD5哈希作为缩略图文件名，避免路径问题
            var normalizedPath = imagePath.Replace('\\', '/').TrimStart('/');
            var hash = ComputeMD5Hash(normalizedPath);

            // 创建两级子目录来避免单个目录文件过多
            var subDir1 = hash.Substring(0, 2);
            var subDir2 = hash.Substring(2, 2);
            var thumbnailDir = Path.Combine(_thumbnailsRoot, subDir1, subDir2);

            if (!Directory.Exists(thumbnailDir))
            {
                Directory.CreateDirectory(thumbnailDir);
            }

            var thumbnailName = $"{hash}.jpg"; // 统一保存为JPG格式
            var thumbnailPath = Path.Combine(thumbnailDir, thumbnailName);

            return Task.FromResult(thumbnailPath);
        }

        public Task<bool> ThumbnailExistsAsync(string imagePath)
        {
            var thumbnailPath = GetThumbnailPathAsync(imagePath).Result;
            return Task.FromResult(File.Exists(thumbnailPath));
        }

        public async Task<Stream> GetThumbnailStreamAsync(string imagePath)
        {
            var thumbnailPath = await GetThumbnailPathAsync(imagePath);
            if (File.Exists(thumbnailPath))
            {
                return new FileStream(thumbnailPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            throw new FileNotFoundException("缩略图不存在");
        }

        /// <summary>
        /// 检查路径是否为缩略图路径
        /// </summary>
        private bool IsThumbnailPath(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var thumbnailsFullPath = Path.GetFullPath(_thumbnailsRoot);

            return fullPath.StartsWith(thumbnailsFullPath, StringComparison.OrdinalIgnoreCase) ||
                   path.Contains(".thumbnails", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains("_thumbnails", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 检查是否为支持的图片格式
        /// </summary>
        private bool IsSupportedImageFormat(string filePath)
        {
            var supportedExtensions = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            return supportedExtensions.Contains(extension);
        }

        private string ComputeMD5Hash(string input)
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            var inputBytes = System.Text.Encoding.UTF8.GetBytes(input);
            var hashBytes = md5.ComputeHash(inputBytes);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }
    }
}