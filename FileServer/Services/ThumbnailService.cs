using FileServer.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Gif;
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
            _thumbnailsRoot = Path.Combine(fileServerRoot, ".thumbnails");
            _logger = logger;

            EnsureThumbnailDirectory();
        }

        private void EnsureThumbnailDirectory()
        {
            if (!Directory.Exists(_thumbnailsRoot))
            {
                Directory.CreateDirectory(_thumbnailsRoot);

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

                var extension = Path.GetExtension(fullImagePath).ToLowerInvariant();

                _logger.LogInformation("生成缩略图: {ImagePath} -> {ThumbnailPath}", imagePath, thumbnailPath);

                // 根据文件类型使用不同的编码器
                IImageEncoder encoder;
                if (extension == ".gif")
                {
                    await ProcessGifFile(fullImagePath, thumbnailPath, width, height);
                    return true;
                }
                else if (extension == ".png")
                {
                    encoder = new PngEncoder();
                }
                else if (extension == ".webp")
                {
                    // WebP保持原格式
                    encoder = new SixLabors.ImageSharp.Formats.Webp.WebpEncoder();
                }
                else
                {
                    // 其他格式（jpg, jpeg, bmp）使用JPG
                    encoder = new JpegEncoder { Quality = 80 };
                }

                using (var image = await Image.LoadAsync(fullImagePath))
                {
                    image.Mutate(x => x.Resize(new ResizeOptions
                    {
                        Size = new Size(width, height),
                        Mode = ResizeMode.Max
                    }));

                    await image.SaveAsync(thumbnailPath, encoder);
                }

                _logger.LogInformation("生成缩略图成功: {ImagePath}", imagePath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "生成缩略图失败: {ImagePath}", imagePath);
                return false;
            }
        }

        private async Task ProcessGifFile(string gifPath, string thumbnailPath, int width, int height)
        {
            try
            {
                _logger.LogInformation("处理GIF文件: {GifPath}", gifPath);

                // 对于GIF文件，我们只提取第一帧作为缩略图
                using (var image = await Image.LoadAsync(gifPath))
                {
                    // 如果是多帧GIF，只取第一帧
                    if (image.Frames.Count > 1)
                    {
                        _logger.LogInformation("GIF包含 {FrameCount} 帧，使用第一帧", image.Frames.Count);
                    }

                    image.Mutate(x => x.Resize(new ResizeOptions
                    {
                        Size = new Size(width, height),
                        Mode = ResizeMode.Max
                    }));

                    // GIF缩略图保存为PNG格式以保持透明度
                    var encoder = new PngEncoder();
                    await image.SaveAsync(thumbnailPath.Replace(".jpg", ".png"), encoder);

                    _logger.LogInformation("GIF缩略图生成成功: {ThumbnailPath}",
                        Path.GetFileName(thumbnailPath));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理GIF文件失败: {GifPath}", gifPath);
                throw;
            }
        }

        public async Task<bool> DeleteThumbnailAsync(string imagePath)
        {
            try
            {
                if (IsThumbnailPath(imagePath))
                {
                    _logger.LogWarning("不允许删除缩略图文件: {ImagePath}", imagePath);
                    return false;
                }

                var thumbnailPath = await GetThumbnailPathAsync(imagePath);
                var extension = Path.GetExtension(imagePath).ToLowerInvariant();

                // 对于GIF文件，缩略图可能是.png格式
                if (extension == ".gif")
                {
                    var pngThumbnailPath = thumbnailPath.Replace(".jpg", ".png");
                    if (File.Exists(pngThumbnailPath))
                    {
                        File.Delete(pngThumbnailPath);
                        _logger.LogInformation("删除GIF缩略图: {ThumbnailPath}", pngThumbnailPath);
                        return true;
                    }
                }

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
            var normalizedPath = imagePath.Replace('\\', '/').TrimStart('/');
            var hash = ComputeMD5Hash(normalizedPath);

            var subDir1 = hash.Substring(0, 2);
            var subDir2 = hash.Substring(2, 2);
            var thumbnailDir = Path.Combine(_thumbnailsRoot, subDir1, subDir2);

            if (!Directory.Exists(thumbnailDir))
            {
                Directory.CreateDirectory(thumbnailDir);
            }

            var thumbnailName = $"{hash}.jpg";
            var thumbnailPath = Path.Combine(thumbnailDir, thumbnailName);

            return Task.FromResult(thumbnailPath);
        }

        public async Task<bool> ThumbnailExistsAsync(string imagePath)
        {
            try
            {
                var thumbnailPath = await GetThumbnailPathAsync(imagePath);
                var extension = Path.GetExtension(imagePath).ToLowerInvariant();

                // 对于GIF文件，检查.png缩略图
                if (extension == ".gif")
                {
                    var pngThumbnailPath = thumbnailPath.Replace(".jpg", ".png");
                    return File.Exists(pngThumbnailPath);
                }

                return File.Exists(thumbnailPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检查缩略图存在性失败: {ImagePath}", imagePath);
                return false;
            }
        }

        public async Task<Stream> GetThumbnailStreamAsync(string imagePath)
        {
            try
            {
                var thumbnailPath = await GetThumbnailPathAsync(imagePath);
                var extension = Path.GetExtension(imagePath).ToLowerInvariant();

                // 对于GIF文件，使用.png缩略图
                if (extension == ".gif")
                {
                    var pngThumbnailPath = thumbnailPath.Replace(".jpg", ".png");
                    if (File.Exists(pngThumbnailPath))
                    {
                        return new FileStream(pngThumbnailPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    }
                }

                if (File.Exists(thumbnailPath))
                {
                    return new FileStream(thumbnailPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
                throw new FileNotFoundException("缩略图不存在");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取缩略图流失败: {ImagePath}", imagePath);
                throw;
            }
        }

        private bool IsThumbnailPath(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var thumbnailsFullPath = Path.GetFullPath(_thumbnailsRoot);

            return fullPath.StartsWith(thumbnailsFullPath, StringComparison.OrdinalIgnoreCase) ||
                   path.Contains(".thumbnails", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains("_thumbnails", StringComparison.OrdinalIgnoreCase);
        }

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