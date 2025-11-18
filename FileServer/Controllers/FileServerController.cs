using FileServer.Models;
using FileServer.Services;
using Microsoft.AspNetCore.Mvc;
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

        public FileServerController(IFileService fileService,
                                  IServerStatusService statusService,
                                  ILogger<FileServerController> logger,
                                  IConfiguration configuration)
        {
            _fileService = fileService;
            _statusService = statusService;
            _logger = logger;
            _configuration = configuration;
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
                Uptime = status.Uptime
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
                serverUptime = status.Uptime,
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

                // 解码路径
                path = WebUtility.UrlDecode(path);

                var (stream, contentType, fileName) = await _fileService.DownloadFileAsync(path);

                // 设置响应头
                Response.Headers.Append("Accept-Ranges", "bytes");
                Response.Headers.Append("Cache-Control", "public, max-age=3600");

                // 处理范围请求
                var rangeHeader = Request.Headers["Range"].ToString();
                if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
                {
                    return await HandleRangeRequest(stream, contentType, fileName, rangeHeader);
                }

                // 对于非范围请求，直接返回文件
                var fileResult = File(stream, contentType, fileName, enableRangeProcessing: true);

                _logger.LogInformation("文件下载: {FileName} (完整文件)", fileName);
                return fileResult;
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

                // 解码路径
                path = WebUtility.UrlDecode(path);

                var (stream, contentType, fileName) = await _fileService.DownloadFileAsync(path);
                var extension = Path.GetExtension(fileName).ToLowerInvariant();

                // 设置预览相关的响应头
                Response.Headers.Append("Accept-Ranges", "bytes");
                Response.Headers.Append("Cache-Control", "public, max-age=3600");
                Response.Headers.Append("Content-Disposition", $"inline; filename=\"{WebUtility.UrlEncode(fileName)}\"");

                // 特别处理 MKV 文件 - 强制设置正确的 MIME 类型
                if (extension == ".mkv")
                {
                    contentType = "video/x-matroska";
                    _logger.LogInformation("处理 MKV 文件: {FileName}, MIME类型: {ContentType}", fileName, contentType);
                }

                // 特别处理 WMV 文件
                if (extension == ".wmv")
                {
                    contentType = "video/x-ms-wmv";
                }

                // 检查范围请求头
                var rangeHeader = Request.Headers["Range"].ToString();
                if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
                {
                    _logger.LogInformation("处理范围请求: {FileName} (范围: {RangeHeader})", fileName, rangeHeader);
                    return await HandleRangeRequest(stream, contentType, fileName, rangeHeader, true);
                }

                // 对于文本文件，返回JSON内容（不进行流式传输）
                if (IsTextFile(extension))
                {
                    _logger.LogInformation("文本文件预览: {FileName}", fileName);
                    return await HandleTextFilePreview(stream, contentType, fileName);
                }

                // 对于媒体文件（图片、视频、音频），启用范围请求处理
                _logger.LogInformation("媒体文件预览: {FileName} (类型: {ContentType}, 扩展名: {Extension})",
                    fileName, contentType, extension);
                return File(stream, contentType, enableRangeProcessing: true);
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

        [HttpGet("stream/{*path}")]
        public async Task<IActionResult> StreamFile(string path)
        {
            try
            {
                _statusService.IncrementRequests();
                _statusService.IncrementConnections();

                // 解码路径
                path = WebUtility.UrlDecode(path);

                var (stream, contentType, fileName) = await _fileService.DownloadFileAsync(path);

                // 专门为流媒体优化的设置
                Response.Headers.Append("Accept-Ranges", "bytes");
                Response.Headers.Append("Cache-Control", "no-cache");
                Response.Headers.Append("Content-Disposition", "inline");

                var rangeHeader = Request.Headers["Range"].ToString();
                if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
                {
                    return await HandleRangeRequest(stream, contentType, fileName, rangeHeader, true);
                }

                _logger.LogInformation("文件流式传输: {FileName}", fileName);
                return File(stream, contentType, enableRangeProcessing: true);
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
                    Response.StatusCode = 416; // Range Not Satisfiable
                    Response.Headers.Append("Content-Range", $"bytes */{fileSize}");
                    _logger.LogWarning("范围请求无效: {Start}-{End} (文件大小: {FileSize})", start, end, fileSize);
                    return new EmptyResult();
                }

                long contentLength = end - start + 1;
                Response.StatusCode = 206; // Partial Content
                Response.Headers.Append("Content-Range", $"bytes {start}-{end}/{fileSize}");
                Response.ContentType = contentType;
                Response.Headers.Append("Content-Length", contentLength.ToString());

                // 对于预览模式，设置内联显示
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

                // 使用更高效的流式传输
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

        private async Task<IActionResult> HandleTextFilePreview(Stream stream, string contentType, string fileName)
        {
            try
            {
                // 限制文本文件大小（1MB）
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "文本文件预览失败: {FileName}", fileName);
                return StatusCode(500, new { error = "文本文件预览失败", message = ex.Message });
            }
        }

        private async Task StreamDataWithRetry(Stream stream, long totalLength)
        {
            const int bufferSize = 81920; // 80KB 缓冲区
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
}