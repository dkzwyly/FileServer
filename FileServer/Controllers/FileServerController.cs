using FileServer.Models;
using FileServer.Services;
using Microsoft.AspNetCore.Mvc;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Text;

namespace FileServer.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class FileServerController : ControllerBase
    {
        private readonly IFileService _fileService;
        private readonly IServerStatusService _statusService;
        private readonly ILogger<FileServerController> _logger;
        private readonly IConfiguration _configuration;
        private readonly IMemoryMappedFileService _memoryMappedService;

        public FileServerController(IFileService fileService,
                                  IServerStatusService statusService,
                                  ILogger<FileServerController> logger,
                                  IConfiguration configuration,
                                  IMemoryMappedFileService memoryMappedService)
        {
            _fileService = fileService;
            _statusService = statusService;
            _logger = logger;
            _configuration = configuration;
            _memoryMappedService = memoryMappedService;
        }

        [HttpGet("status")]
        public IActionResult GetStatus()
        {
            _statusService.IncrementRequests();
            return Ok(_statusService.GetStatus());
        }

        [HttpGet("health")]
        public IActionResult HealthCheck()
        {
            var status = _statusService.GetStatus();
            var health = new HealthResponse
            {
                Status = status.IsRunning ? "healthy" : "unhealthy",
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ActiveConnections = status.ActiveConnections,
                Uptime = FormatUptime(status.Uptime) // 修正这里
            };
            return Ok(health);
        }

        [HttpGet("quic-status")]
        public IActionResult GetQuicStatus()
        {
            var config = _configuration.GetSection("FileServer").Get<FileServerConfig>();
            var status = _statusService.GetStatus();

            var quicInfo = new
            {
                quicEnabled = config?.EnableQuic ?? false,
                httpsPort = config?.HttpsPort ?? 8081,
                supportedProtocols = new[] { "HTTP/1.1", "HTTP/2", "HTTP/3" },
                recommendedClient = "支持 HTTP/3 的客户端 (Chrome/Edge 最新版)",
                serverUptime = FormatUptime(status.Uptime), // 修正这里
                activeConnections = status.ActiveConnections
            };

            return Ok(quicInfo);
        }

        [HttpGet("list/{*path}")]
        public async Task<IActionResult> GetFileList(string path = "")
        {
            try
            {
                _statusService.IncrementRequests();
                var result = await _fileService.GetFileListAsync(path);
                return Ok(result);
            }
            catch (DirectoryNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取文件列表失败: {Path}", path);
                return StatusCode(500, new { error = "服务器内部错误", message = ex.Message });
            }
        }

        [HttpGet("download/{*path}")]
        public async Task<IActionResult> DownloadFile(string path)
        {
            try
            {
                _statusService.IncrementRequests();
                _statusService.IncrementConnections();

                path = WebUtility.UrlDecode(path);
                var fileInfo = await _fileService.GetFileInfoAsync(path);

                // 对于大文件使用内存映射，小文件使用普通流
                if (fileInfo.Size > 10 * 1024 * 1024) // 10MB以上使用内存映射
                {
                    return await DownloadWithMemoryMapping(path, fileInfo, false);
                }
                else
                {
                    return await DownloadWithStream(path, fileInfo, false);
                }
            }
            catch (FileNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "文件下载失败: {Path}", path);
                return StatusCode(500, new { error = "文件下载失败", message = ex.Message });
            }
            finally
            {
                _statusService.DecrementConnections();
            }
        }

        [HttpGet("preview/{*path}")]
        public async Task<IActionResult> PreviewFile(string path)
        {
            try
            {
                _statusService.IncrementRequests();
                _statusService.IncrementConnections();

                path = WebUtility.UrlDecode(path);
                var fileInfo = await _fileService.GetFileInfoAsync(path);
                var extension = Path.GetExtension(fileInfo.Name).ToLowerInvariant();

                // 设置预览相关的响应头
                Response.Headers.Append("Accept-Ranges", "bytes");
                Response.Headers.Append("Cache-Control", "public, max-age=3600");
                Response.Headers.Append("Content-Disposition", $"inline; filename=\"{WebUtility.UrlEncode(fileInfo.Name)}\"");

                // 特别处理 MKV 和 WMV 文件
                if (extension == ".mkv")
                {
                    Response.ContentType = "video/x-matroska";
                    _logger.LogInformation("处理 MKV 文件: {FileName}, MIME类型: {ContentType}", fileInfo.Name, "video/x-matroska");
                }
                if (extension == ".wmv")
                {
                    Response.ContentType = "video/x-ms-wmv";
                }

                // 检查范围请求
                var rangeHeader = Request.Headers["Range"].ToString();
                if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
                {
                    _logger.LogInformation("处理范围请求: {FileName} (范围: {RangeHeader})", fileInfo.Name, rangeHeader);

                    if (fileInfo.Size > 10 * 1024 * 1024)
                        return await DownloadWithMemoryMapping(path, fileInfo, true, rangeHeader);
                    else
                        return await DownloadWithStream(path, fileInfo, true, rangeHeader);
                }

                // 对于文本文件，返回JSON内容
                if (IsTextFile(extension))
                {
                    _logger.LogInformation("文本文件预览: {FileName}", fileInfo.Name);
                    return await HandleTextFilePreview(path);
                }

                // 对于媒体文件，使用内存映射
                if (fileInfo.Size > 10 * 1024 * 1024)
                    return await DownloadWithMemoryMapping(path, fileInfo, true);
                else
                    return await DownloadWithStream(path, fileInfo, true);
            }
            catch (FileNotFoundException ex)
            {
                _logger.LogWarning("文件不存在: {Path}", path);
                return NotFound(new { error = $"文件不存在: {ex.Message}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "文件预览失败: {Path}", path);
                return StatusCode(500, new { error = "文件预览失败", message = ex.Message });
            }
            finally
            {
                _statusService.DecrementConnections();
            }
        }

        [HttpPost("upload/{*path}")]
        [RequestSizeLimit(10L * 1024 * 1024 * 1024)] // 10GB
        public async Task<IActionResult> UploadFiles(string path = "")
        {
            try
            {
                _statusService.IncrementRequests();

                if (Request.Form.Files.Count == 0)
                {
                    return BadRequest(new { success = false, message = "没有文件被上传" });
                }

                var result = await _fileService.UploadFilesAsync(path, Request.Form.Files);

                if (result.Success)
                {
                    return Ok(result);
                }
                else
                {
                    return BadRequest(result);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "文件上传失败");
                return StatusCode(500, new { success = false, message = $"上传失败: {ex.Message}" });
            }
        }

        [HttpPost("directory/{*path}")]
        public IActionResult CreateDirectory(string path)
        {
            try
            {
                _statusService.IncrementRequests();

                var physicalPath = Path.Combine(_fileService.GetRootPath(), path);
                if (!Directory.Exists(physicalPath))
                {
                    Directory.CreateDirectory(physicalPath);
                    _logger.LogInformation("创建目录: {Path}", physicalPath);
                    return Ok(new { success = true, message = "目录创建成功" });
                }
                else
                {
                    return BadRequest(new { success = false, message = "目录已存在" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建目录失败: {Path}", path);
                return StatusCode(500, new { success = false, message = $"创建目录失败: {ex.Message}" });
            }
        }

        // 在 FileServerController.cs 中添加
        [HttpGet("thumbnail/{*path}")]
        public async Task<IActionResult> GetThumbnail(string path)
        {
            try
            {
                _statusService.IncrementRequests();

                path = WebUtility.UrlDecode(path);
                var extension = Path.GetExtension(path).ToLowerInvariant();

                _logger.LogInformation("缩略图请求 - 路径: {Path}, 扩展名: {Extension}", path, extension);

                // 只支持图片文件的缩略图
                if (!IsImageFile(extension))
                {
                    _logger.LogWarning("非图片文件请求缩略图: {Path}", path);
                    return BadRequest(new { error = "非图片文件不支持缩略图" });
                }

                // 检查原文件是否存在
                var fileExists = await _fileService.FileExistsAsync(path);
                if (!fileExists)
                {
                    _logger.LogWarning("原文件不存在: {Path}", path);
                    return NotFound(new { error = "原文件不存在" });
                }

                _logger.LogInformation("开始获取缩略图: {Path}", path);
                var (stream, contentType, fileName) = await _fileService.GetThumbnailAsync(path);

                Response.Headers.Append("Cache-Control", "public, max-age=3600"); // 缓存1小时
                Response.Headers.Append("Content-Disposition", $"inline; filename=\"{WebUtility.UrlEncode(fileName)}\"");

                _logger.LogInformation("成功返回缩略图: {Path}", path);
                return File(stream, contentType);
            }
            catch (FileNotFoundException ex)
            {
                _logger.LogWarning("缩略图文件不存在: {Path}, 错误: {Message}", path, ex.Message);

                // 如果缩略图不存在，尝试生成
                try
                {
                    _logger.LogInformation("尝试生成缩略图: {Path}", path);
                    var thumbnailService = HttpContext.RequestServices.GetRequiredService<IThumbnailService>();
                    var generated = await thumbnailService.GenerateThumbnailAsync(path);

                    if (generated)
                    {
                        _logger.LogInformation("缩略图生成成功，重新获取: {Path}", path);
                        var (stream, contentType, fileName) = await _fileService.GetThumbnailAsync(path);
                        return File(stream, contentType);
                    }
                    else
                    {
                        _logger.LogWarning("缩略图生成失败: {Path}", path);
                        return NotFound(new { error = "缩略图不存在且生成失败" });
                    }
                }
                catch (Exception genEx)
                {
                    _logger.LogError(genEx, "缩略图生成过程出错: {Path}", path);
                    return NotFound(new { error = $"缩略图生成失败: {genEx.Message}" });
                }
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning("无效操作: {Path}, 错误: {Message}", path, ex.Message);
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取缩略图失败: {Path}", path);
                return StatusCode(500, new { error = "获取缩略图失败", message = ex.Message });
            }
        }

        [HttpDelete("delete/{*path}")]
        public async Task<IActionResult> DeleteFile(string path)
        {
            try
            {
                _statusService.IncrementRequests();

                path = WebUtility.UrlDecode(path);
                var result = await _fileService.DeleteFileAsync(path);

                if (result)
                {
                    return Ok(new { success = true, message = "文件删除成功" });
                }
                else
                {
                    return NotFound(new { error = "文件不存在" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除文件失败: {Path}", path);
                return StatusCode(500, new { error = "删除文件失败", message = ex.Message });
            }
        }

        // 添加图片文件判断方法
        private bool IsImageFile(string extension)
        {
            var imageExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" };
            return imageExtensions.Contains(extension.ToLowerInvariant());
        }

        [HttpGet("stream/{*path}")]
        public async Task<IActionResult> StreamFile(string path)
        {
            try
            {
                _statusService.IncrementRequests();
                _statusService.IncrementConnections();

                path = WebUtility.UrlDecode(path);
                var fileInfo = await _fileService.GetFileInfoAsync(path);

                // 专门为流媒体优化的设置
                Response.Headers.Append("Accept-Ranges", "bytes");
                Response.Headers.Append("Cache-Control", "no-cache");
                Response.Headers.Append("Content-Disposition", "inline");

                var rangeHeader = Request.Headers["Range"].ToString();

                // 对于流媒体文件，强制使用内存映射以获得最佳性能
                if (fileInfo.Size > 5 * 1024 * 1024) // 5MB以上视频强制使用内存映射
                {
                    return await DownloadWithMemoryMapping(path, fileInfo, true, rangeHeader);
                }
                else
                {
                    if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
                        return await DownloadWithStream(path, fileInfo, true, rangeHeader);
                    else
                        return await DownloadWithStream(path, fileInfo, true);
                }
            }
            catch (FileNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "文件流式传输失败: {Path}", path);
                return StatusCode(500, new { error = "文件流式传输失败", message = ex.Message });
            }
            finally
            {
                _statusService.DecrementConnections();
            }
        }

        #region 私有方法 - 高性能文件传输

        private async Task<IActionResult> DownloadWithMemoryMapping(string path, FileInfoModel fileInfo, bool isPreview, string rangeHeader = null)
        {
            try
            {
                _logger.LogInformation("使用内存映射传输文件: {FileName} (大小: {Size})",
                    fileInfo.Name, fileInfo.SizeFormatted);

                var (mappedFile, contentType) = await _memoryMappedService.OpenMemoryMappedFile(path);

                if (!string.IsNullOrEmpty(rangeHeader))
                {
                    return await HandleMemoryMappedRangeRequest(mappedFile, contentType, fileInfo, rangeHeader, isPreview);
                }

                Response.ContentType = contentType;
                Response.Headers.Append("Content-Length", fileInfo.Size.ToString());

                if (!isPreview)
                {
                    Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{WebUtility.UrlEncode(fileInfo.Name)}\"");
                }

                await StreamMemoryMappedData(mappedFile, 0, fileInfo.Size);
                return new EmptyResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "内存映射文件传输失败: {Path}", path);
                throw;
            }
        }

        private async Task<IActionResult> DownloadWithStream(string path, FileInfoModel fileInfo, bool isPreview, string rangeHeader = null)
        {
            var (stream, contentType, fileName) = await _fileService.DownloadFileAsync(path);

            if (!string.IsNullOrEmpty(rangeHeader))
            {
                return await HandleRangeRequest(stream, contentType, fileName, rangeHeader, isPreview);
            }

            var fileResult = File(stream, contentType, isPreview ? null : fileName, enableRangeProcessing: true);

            if (isPreview)
            {
                Response.Headers.Append("Content-Disposition", $"inline; filename=\"{WebUtility.UrlEncode(fileName)}\"");
            }

            _logger.LogInformation("流式传输文件: {FileName}", fileName);
            return fileResult;
        }

        private async Task<IActionResult> HandleMemoryMappedRangeRequest(MemoryMappedFile mmf, string contentType, FileInfoModel fileInfo, string rangeHeader, bool isPreview)
        {
            try
            {
                var fileSize = fileInfo.Size;
                var range = rangeHeader.Substring(6);
                var parts = range.Split('-');

                long start = 0, end = fileSize - 1;

                if (long.TryParse(parts[0], out long startRange))
                    start = startRange;
                if (parts.Length > 1 && long.TryParse(parts[1], out long endRange))
                    end = endRange;
                else
                    end = fileSize - 1;

                // 验证范围
                if (start < 0) start = 0;
                if (end >= fileSize) end = fileSize - 1;
                if (start > end)
                {
                    Response.StatusCode = 416;
                    Response.Headers.Append("Content-Range", $"bytes */{fileSize}");
                    _logger.LogWarning("范围请求无效: {Start}-{End} (文件大小: {FileSize})", start, end, fileSize);
                    return new EmptyResult();
                }

                long contentLength = end - start + 1;
                Response.StatusCode = 206;
                Response.Headers.Append("Content-Range", $"bytes {start}-{end}/{fileSize}");
                Response.ContentType = contentType;
                Response.Headers.Append("Content-Length", contentLength.ToString());

                if (isPreview)
                {
                    Response.Headers.Append("Content-Disposition", $"inline; filename=\"{WebUtility.UrlEncode(fileInfo.Name)}\"");
                }
                else
                {
                    Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{WebUtility.UrlEncode(fileInfo.Name)}\"");
                }

                using (var stream = mmf.CreateViewStream(start, contentLength, MemoryMappedFileAccess.Read))
                {
                    await StreamDataWithRetry(stream, contentLength);
                }

                _logger.LogInformation("内存映射范围请求完成: {FileName} ({Start}-{End}/{Total}, 长度: {ContentLength})",
                    fileInfo.Name, start, end, fileSize, contentLength);

                return new EmptyResult();
            }
            catch (Exception ex) when (IsConnectionError(ex))
            {
                _logger.LogInformation("客户端连接中断: {FileName} (内存映射范围请求)", fileInfo.Name);
                return new EmptyResult();
            }
        }

        private async Task<IActionResult> HandleRangeRequest(Stream stream, string contentType, string fileName, string rangeHeader, bool isPreview = false)
        {
            try
            {
                var fileSize = stream.Length;
                _logger.LogInformation("处理范围请求 - 文件: {FileName}, 大小: {FileSize}, 范围头: {RangeHeader}",
                    fileName, fileSize, rangeHeader);

                var range = rangeHeader.Substring(6);
                var parts = range.Split('-');

                long start = 0, end = fileSize - 1;

                if (long.TryParse(parts[0], out long startRange))
                    start = startRange;
                if (parts.Length > 1 && long.TryParse(parts[1], out long endRange))
                    end = endRange;
                else
                    end = fileSize - 1;

                // 验证范围
                if (start < 0) start = 0;
                if (end >= fileSize) end = fileSize - 1;
                if (start > end)
                {
                    Response.StatusCode = 416;
                    Response.Headers.Append("Content-Range", $"bytes */{fileSize}");
                    _logger.LogWarning("范围请求无效: {Start}-{End} (文件大小: {FileSize})", start, end, fileSize);
                    return new EmptyResult();
                }

                long contentLength = end - start + 1;
                Response.StatusCode = 206;
                Response.Headers.Append("Content-Range", $"bytes {start}-{end}/{fileSize}");
                Response.ContentType = contentType;
                Response.Headers.Append("Content-Length", contentLength.ToString());

                if (isPreview)
                {
                    Response.Headers.Append("Content-Disposition", $"inline; filename=\"{WebUtility.UrlEncode(fileName)}\"");
                }
                else
                {
                    Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{WebUtility.UrlEncode(fileName)}\"");
                }

                if (start > 0)
                {
                    stream.Seek(start, SeekOrigin.Begin);
                    _logger.LogInformation("跳转到位置: {Start}", start);
                }

                await StreamDataWithRetry(stream, contentLength);

                _logger.LogInformation("范围请求处理完成: {FileName} ({Start}-{End}/{Total}, 长度: {ContentLength})",
                    fileName, start, end, fileSize, contentLength);

                return new EmptyResult();
            }
            catch (Exception ex) when (IsConnectionError(ex))
            {
                _logger.LogInformation("客户端连接中断: {FileName} (范围请求)", fileName);
                return new EmptyResult();
            }
            finally
            {
                await stream.DisposeAsync();
            }
        }

        private async Task StreamMemoryMappedData(MemoryMappedFile mmf, long start, long length)
        {
            const int bufferSize = 131072; // 128KB 缓冲区，提升大文件传输性能
            var buffer = new byte[bufferSize];
            long bytesRemaining = length;
            int retryCount = 0;
            const int maxRetries = 3;

            _logger.LogDebug("开始内存映射流式传输，长度: {Length}", length);

            using (var stream = mmf.CreateViewStream(start, length, MemoryMappedFileAccess.Read))
            {
                while (bytesRemaining > 0 && retryCount < maxRetries)
                {
                    try
                    {
                        int bytesToRead = (int)Math.Min(buffer.Length, bytesRemaining);
                        int bytesRead = await stream.ReadAsync(buffer, 0, bytesToRead);

                        if (bytesRead == 0) break;

                        await Response.Body.WriteAsync(buffer, 0, bytesRead);
                        await Response.Body.FlushAsync();

                        bytesRemaining -= bytesRead;
                        retryCount = 0;

                        // 每传输 10MB 记录一次进度
                        if ((length - bytesRemaining) % (10 * 1024 * 1024) < bufferSize)
                        {
                            _logger.LogDebug("内存映射传输进度: {Transferred}/{Total} ({Percentage}%)",
                                length - bytesRemaining, length,
                                (int)((length - bytesRemaining) * 100 / length));
                        }
                    }
                    catch (Exception ex) when (IsConnectionError(ex) && retryCount < maxRetries - 1)
                    {
                        retryCount++;
                        _logger.LogWarning("内存映射传输中断，重试 {RetryCount}/{MaxRetries}", retryCount, maxRetries);
                        await Task.Delay(100 * retryCount);
                    }
                }
            }

            _logger.LogDebug("内存映射流式传输完成，剩余字节: {BytesRemaining}", bytesRemaining);
        }

        private async Task StreamDataWithRetry(Stream stream, long totalLength)
        {
            const int bufferSize = 131072; // 128KB 缓冲区
            var buffer = new byte[bufferSize];
            long bytesRemaining = totalLength;
            int retryCount = 0;
            const int maxRetries = 3;

            _logger.LogDebug("开始流式传输数据，总长度: {TotalLength}", totalLength);

            while (bytesRemaining > 0 && retryCount < maxRetries)
            {
                try
                {
                    int bytesToRead = (int)Math.Min(buffer.Length, bytesRemaining);
                    int bytesRead = await stream.ReadAsync(buffer, 0, bytesToRead);

                    if (bytesRead == 0)
                    {
                        _logger.LogDebug("流读取结束，剩余字节: {BytesRemaining}", bytesRemaining);
                        break;
                    }

                    await Response.Body.WriteAsync(buffer, 0, bytesRead);
                    await Response.Body.FlushAsync();

                    bytesRemaining -= bytesRead;
                    retryCount = 0; // 重置重试计数

                    // 每传输 1MB 记录一次进度（可选）
                    if ((totalLength - bytesRemaining) % (1024 * 1024) < bufferSize)
                    {
                        _logger.LogDebug("流传输进度: {Transferred}/{Total} ({Percentage}%)",
                            totalLength - bytesRemaining, totalLength,
                            (int)((totalLength - bytesRemaining) * 100 / totalLength));
                    }
                }
                catch (Exception ex) when (IsConnectionError(ex) && retryCount < maxRetries - 1)
                {
                    retryCount++;
                    _logger.LogWarning("流传输中断，重试 {RetryCount}/{MaxRetries}", retryCount, maxRetries);
                    await Task.Delay(100 * retryCount);
                }
            }

            if (bytesRemaining > 0)
            {
                _logger.LogWarning("流传输未完成，剩余字节: {BytesRemaining}", bytesRemaining);
            }
            else
            {
                _logger.LogDebug("流传输完成");
            }
        }

        private async Task<IActionResult> HandleTextFilePreview(string path)
        {
            try
            {
                var (stream, contentType, fileName) = await _fileService.DownloadFileAsync(path);

                using (stream)
                {
                    if (stream.Length > 1024 * 1024)
                    {
                        return Ok(new
                        {
                            type = "text",
                            fileName,
                            content = "文件过大，不支持预览",
                            truncated = true,
                            size = stream.Length
                        });
                    }

                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var content = await reader.ReadToEndAsync();

                    return Ok(new
                    {
                        type = "text",
                        fileName,
                        content,
                        encoding = "utf-8",
                        size = content.Length
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "文本文件预览失败");
                return StatusCode(500, new { error = "文本文件预览失败", message = ex.Message });
            }
        }

        private string FormatUptime(long uptimeInMilliseconds)
        {
            // 假设 uptime 是以毫秒为单位的时间
            var uptime = TimeSpan.FromMilliseconds(uptimeInMilliseconds);

            if (uptime.TotalDays >= 1)
            {
                return $"{(int)uptime.TotalDays}天 {uptime.Hours}小时 {uptime.Minutes}分钟";
            }
            else if (uptime.TotalHours >= 1)
            {
                return $"{(int)uptime.TotalHours}小时 {uptime.Minutes}分钟 {uptime.Seconds}秒";
            }
            else if (uptime.TotalMinutes >= 1)
            {
                return $"{(int)uptime.TotalMinutes}分钟 {uptime.Seconds}秒";
            }
            else
            {
                return $"{uptime.Seconds}秒";
            }
        }

        private bool IsConnectionError(Exception ex)
        {
            var errorMessage = ex.Message.ToLowerInvariant();
            return errorMessage.Contains("connection") ||
                   errorMessage.Contains("连接") ||
                   errorMessage.Contains("network") ||
                   errorMessage.Contains("网络") ||
                   ex is OperationCanceledException;
        }

        private bool IsTextFile(string extension)
        {
            var textExtensions = new[] {
                ".txt", ".log", ".xml", ".json", ".csv", ".html", ".htm",
                ".css", ".js", ".md", ".cs", ".java", ".cpp", ".c", ".py",
                ".php", ".rb", ".config", ".yml", ".yaml", ".ini", ".sql"
            };
            return textExtensions.Contains(extension.ToLowerInvariant());
        }

        #endregion
    }

    // 扩展方法
    public static class FileServiceExtensions
    {
        public static string GetRootPath(this IFileService fileService)
        {
            // 通过反射或其他方式获取根路径
            // 这里简化处理，实际使用时需要修改
            return @"D:\FileServer";
        }
    }

    // 健康响应模型
    public class HealthResponse
    {
        public string Status { get; set; } = string.Empty;
        public string Timestamp { get; set; } = string.Empty;
        public int ActiveConnections { get; set; }
        public string Uptime { get; set; } = string.Empty; // 改为字符串类型
    }

    // 文件服务器配置模型
    public class FileServerConfig
    {
        public bool EnableQuic { get; set; }
        public int HttpsPort { get; set; }
    }
}