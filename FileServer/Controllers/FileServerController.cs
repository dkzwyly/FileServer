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
        private readonly IChapterIndexService _chapterIndexService;
        private readonly IVideoThumbnailService _videoThumbnailService;
        private readonly IAudioMetadataService _audioMetadataService;
        private readonly IPhotoMetadataService _photoMetadataService;

        public FileServerController(IFileService fileService,
                                  IServerStatusService statusService,
                                  ILogger<FileServerController> logger,
                                  IConfiguration configuration,
                                  IMemoryMappedFileService memoryMappedService,
                                  IChapterIndexService chapterIndexService,
                                  IVideoThumbnailService videoThumbnailService,
                                  IAudioMetadataService audioMetadataService,
                                  IPhotoMetadataService photoMetadataService)
        {
            _fileService = fileService;
            _statusService = statusService;
            _logger = logger;
            _configuration = configuration;
            _memoryMappedService = memoryMappedService;
            _chapterIndexService = chapterIndexService;
            _videoThumbnailService = videoThumbnailService;
            _audioMetadataService = audioMetadataService;
            _photoMetadataService = photoMetadataService;
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
                serverUptime = FormatUptime(status.Uptime),
                activeConnections = status.ActiveConnections
            };

            return Ok(quicInfo);
        }

        [HttpGet("list/{*path}")]
        public async Task<IActionResult> GetFileList(string path = "", [FromQuery] string sortBy = "name", [FromQuery] string sortOrder = "asc")
        {
            try
            {
                _statusService.IncrementRequests();
                var result = await _fileService.GetFileListAsync(path);

                // 按拍摄时间排序（仅对图片文件有效）
                if (sortBy.Equals("dateTaken", StringComparison.OrdinalIgnoreCase))
                {
                    // 仅对图片文件附加元数据
                    var imageFiles = result.Files.Where(f => IsImageFile(Path.GetExtension(f.Name))).ToList();
                    if (imageFiles.Any())
                    {
                        var paths = imageFiles.Select(f => f.Path);
                        var metadataDict = await _photoMetadataService.GetBatchMetadataAsync(paths);

                        // 将元数据直接赋值给 FileInfoModel 对象
                        foreach (var file in result.Files)
                        {
                            if (metadataDict.TryGetValue(file.Path, out var meta))
                                file.Metadata = meta;
                        }

                        // 按 DateTaken 排序（null 值视作 DateTime.MaxValue 排在最后）
                        result.Files = sortOrder.ToLower() == "asc"
                            ? result.Files.OrderBy(f => f.Metadata?.DateTaken ?? DateTime.MaxValue).ToList()
                            : result.Files.OrderByDescending(f => f.Metadata?.DateTaken ?? DateTime.MinValue).ToList();

                        return Ok(result);   // 直接返回标准的 FileListResponse
                    }
                }

                // 其他排序方式（名称、大小、修改时间）
                switch (sortBy.ToLower())
                {
                    case "size":
                        result.Files = sortOrder.ToLower() == "asc"
                            ? result.Files.OrderBy(f => f.Size).ToList()
                            : result.Files.OrderByDescending(f => f.Size).ToList();
                        break;
                    case "modified":
                        result.Files = sortOrder.ToLower() == "asc"
                            ? result.Files.OrderBy(f => f.LastModified).ToList()
                            : result.Files.OrderByDescending(f => f.LastModified).ToList();
                        break;
                    default:
                        result.Files = sortOrder.ToLower() == "asc"
                            ? result.Files.OrderBy(f => f.Name).ToList()
                            : result.Files.OrderByDescending(f => f.Name).ToList();
                        break;
                }

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
        // 搜索照片（支持按日期、GPS、分页、排序）
        [HttpGet("photo-metadata/search")]
        public async Task<IActionResult> SearchPhotos(
            [FromQuery] string? directory = null,
            [FromQuery] string? sortBy = "dateTaken",
            [FromQuery] bool sortAscending = true,
            [FromQuery] DateTime? startDate = null,
            [FromQuery] DateTime? endDate = null,
            [FromQuery] double? minLat = null,
            [FromQuery] double? maxLat = null,
            [FromQuery] double? minLng = null,
            [FromQuery] double? maxLng = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 100)
        {
            try
            {
                _statusService.IncrementRequests();

                var options = new PhotoSearchOptions
                {
                    DirectoryPath = string.IsNullOrEmpty(directory) ? null : WebUtility.UrlDecode(directory),
                    SortBy = sortBy,
                    SortAscending = sortAscending,
                    StartDate = startDate,
                    EndDate = endDate,
                    MinLatitude = minLat,
                    MaxLatitude = maxLat,
                    MinLongitude = minLng,
                    MaxLongitude = maxLng,
                    Skip = (page - 1) * pageSize,
                    Take = pageSize
                };

                var (items, totalCount) = await _photoMetadataService.SearchPhotosAsync(options);

                return Ok(new
                {
                    page,
                    pageSize,
                    totalCount,
                    totalPages = (int)Math.Ceiling((double)totalCount / pageSize),
                    items
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "搜索照片失败");
                return StatusCode(500, new { error = "搜索失败", message = ex.Message });
            }
        }

        // 强制刷新单张图片的元数据
        [HttpPost("photo-metadata/refresh/{*path}")]
        public async Task<IActionResult> RefreshPhotoMetadata(string path)
        {
            try
            {
                _statusService.IncrementRequests();
                path = WebUtility.UrlDecode(path);
                await _photoMetadataService.RefreshMetadataAsync(path);
                return Ok(new { success = true, message = "元数据已刷新" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "刷新元数据失败: {Path}", path);
                return StatusCode(500, new { error = "刷新失败", message = ex.Message });
            }
        }

        // 全量重建所有图片的元数据索引（后台异步任务）
        [HttpPost("photo-metadata/reindex")]
        public IActionResult ReindexAllPhotos()
        {
            try
            {
                _statusService.IncrementRequests();
                // 后台执行，不阻塞请求
                _ = Task.Run(async () =>
                {
                    var progress = new Progress<string>(msg => _logger.LogInformation(msg));
                    await _photoMetadataService.ScanAndIndexAllPhotosAsync(progress);
                });
                return Accepted(new { message = "全量索引已启动，请查看日志" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "启动全量索引失败");
                return StatusCode(500, new { error = "启动失败", message = ex.Message });
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

                if (fileInfo.Size > 10 * 1024 * 1024)
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
        public async Task<IActionResult> PreviewFile(string path, [FromQuery] int page = 1, [FromQuery] int pageSize = 1000)
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

                // 对于文本文件，返回JSON内容（支持分页）
                if (IsTextFile(extension))
                {
                    _logger.LogInformation("文本文件预览: {FileName} (页码: {Page}, 页大小: {PageSize})", fileInfo.Name, page, pageSize);
                    return await HandleTextFilePreview(path, page, pageSize);
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
                    // ========== 新增：增量更新照片元数据 ==========
                    if (result.UploadedFiles != null)
                    {
                        foreach (var uploadedFile in result.UploadedFiles.Where(f => f.Success))
                        {
                            var extension = Path.GetExtension(uploadedFile.Path).ToLowerInvariant();
                            if (IsImageFile(extension))
                            {
                                _ = _photoMetadataService.GetOrExtractMetadataAsync(uploadedFile.Path);
                            }
                        }
                    }
                    // ===============================================
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

        [HttpGet("thumbnail/{*path}")]
        public async Task<IActionResult> GetThumbnail(string path)
        {
            try
            {
                _statusService.IncrementRequests();

                path = WebUtility.UrlDecode(path);
                var extension = Path.GetExtension(path).ToLowerInvariant();

                _logger.LogInformation("缩略图请求 - 路径: {Path}, 扩展名: {Extension}", path, extension);

                if (!IsImageFile(extension))
                {
                    _logger.LogWarning("非图片文件请求缩略图: {Path}", path);
                    return BadRequest(new { error = "非图片文件不支持缩略图" });
                }

                var fileExists = await _fileService.FileExistsAsync(path);
                if (!fileExists)
                {
                    _logger.LogWarning("原文件不存在: {Path}", path);
                    return NotFound(new { error = "原文件不存在" });
                }

                _logger.LogInformation("开始获取缩略图: {Path}", path);
                var (stream, contentType, fileName) = await _fileService.GetThumbnailAsync(path);

                Response.Headers.Append("Cache-Control", "public, max-age=3600");
                Response.Headers.Append("Content-Disposition", $"inline; filename=\"{WebUtility.UrlEncode(fileName)}\"");

                _logger.LogInformation("成功返回缩略图: {Path}", path);
                return File(stream, contentType);
            }
            catch (FileNotFoundException ex)
            {
                _logger.LogWarning("缩略图文件不存在: {Path}, 错误: {Message}", path, ex.Message);

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

        [HttpGet("stream/{*path}")]
        public async Task<IActionResult> StreamFile(string path)
        {
            try
            {
                _statusService.IncrementRequests();
                _statusService.IncrementConnections();

                path = WebUtility.UrlDecode(path);
                var fileInfo = await _fileService.GetFileInfoAsync(path);

                Response.Headers.Append("Accept-Ranges", "bytes");
                Response.Headers.Append("Cache-Control", "no-cache");
                Response.Headers.Append("Content-Disposition", "inline");

                var rangeHeader = Request.Headers["Range"].ToString();

                if (fileInfo.Size > 5 * 1024 * 1024)
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

        #region 章节相关端点

        [HttpGet("chapters/{*path}")]
        public async Task<IActionResult> GetFileChapters(string path)
        {
            try
            {
                _statusService.IncrementRequests();

                // 添加 URL 解码
                path = WebUtility.UrlDecode(path);
                _logger.LogInformation("处理章节请求 - 解码前: {OriginalPath}, 解码后: {DecodedPath}",
                    Request.Path.Value, path);

                var (stream, contentType, fileName) = await _fileService.DownloadFileAsync(path);

                using (stream)
                {
                    byte[] fileBytes;
                    using (var memoryStream = new MemoryStream())
                    {
                        await stream.CopyToAsync(memoryStream);
                        fileBytes = memoryStream.ToArray();
                    }

                    string content;
                    if (fileBytes.Length >= 3 && fileBytes[0] == 0xEF && fileBytes[1] == 0xBB && fileBytes[2] == 0xBF)
                    {
                        content = Encoding.UTF8.GetString(fileBytes, 3, fileBytes.Length - 3);
                    }
                    else
                    {
                        content = Encoding.UTF8.GetString(fileBytes);
                    }

                    var chapterIndex = await _chapterIndexService.GetOrBuildChapterIndexAsync(path, content);

                    if (chapterIndex == null)
                    {
                        return NotFound(new { error = "无法构建章节索引" });
                    }

                    return Ok(new
                    {
                        fileName,
                        totalChapters = chapterIndex.TotalChapters,
                        chapters = chapterIndex.Chapters.Select(c => new
                        {
                            title = c.Title,
                            page = c.Page,
                            lineNumber = c.LineNumber,
                            preview = c.Preview
                        })
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取章节列表失败: {Path}", path);
                return StatusCode(500, new { error = "获取章节列表失败", message = ex.Message });
            }
        }

        [HttpPost("chapters/rebuild/{*path}")]
        public async Task<IActionResult> RebuildChapterIndex(string path)
        {
            try
            {
                _statusService.IncrementRequests();

                // 添加 URL 解码
                path = WebUtility.UrlDecode(path);
                _logger.LogInformation("重建章节索引 - 解码前: {OriginalPath}, 解码后: {DecodedPath}",
                    Request.Path.Value, path);

                var (stream, contentType, fileName) = await _fileService.DownloadFileAsync(path);

                using (stream)
                {
                    byte[] fileBytes;
                    using (var memoryStream = new MemoryStream())
                    {
                        await stream.CopyToAsync(memoryStream);
                        fileBytes = memoryStream.ToArray();
                    }

                    string content;
                    if (fileBytes.Length >= 3 && fileBytes[0] == 0xEF && fileBytes[1] == 0xBB && fileBytes[2] == 0xBF)
                    {
                        content = Encoding.UTF8.GetString(fileBytes, 3, fileBytes.Length - 3);
                    }
                    else
                    {
                        content = Encoding.UTF8.GetString(fileBytes);
                    }

                    var chapterIndex = await _chapterIndexService.ForceRebuildChapterIndexAsync(path, content);

                    return Ok(new
                    {
                        success = true,
                        message = "章节索引重建完成",
                        fileName,
                        totalChapters = chapterIndex.TotalChapters,
                        chapters = chapterIndex.Chapters.Select(c => new
                        {
                            title = c.Title,
                            page = c.Page,
                            lineNumber = c.LineNumber,
                            preview = c.Preview
                        })
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "重建章节索引失败: {Path}", path);
                return StatusCode(500, new { error = "重建章节索引失败", message = ex.Message });
            }
        }

        [HttpGet("chapters/info/{*path}")]
        public IActionResult GetChapterIndexInfo(string path)
        {
            try
            {
                _statusService.IncrementRequests();

                // 添加 URL 解码
                path = WebUtility.UrlDecode(path);
                _logger.LogInformation("获取章节索引信息 - 解码前: {OriginalPath}, 解码后: {DecodedPath}",
                    Request.Path.Value, path);

                var info = _chapterIndexService.GetIndexFileInfo(path);
                return Ok(new { info });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取章节索引信息失败: {Path}", path);
                return StatusCode(500, new { error = "获取章节索引信息失败", message = ex.Message });
            }
        }

        [HttpDelete("chapters/{*path}")]
        public IActionResult DeleteChapterIndex(string path)
        {
            try
            {
                _statusService.IncrementRequests();

                // 添加 URL 解码
                path = WebUtility.UrlDecode(path);
                _logger.LogInformation("删除章节索引 - 解码前: {OriginalPath}, 解码后: {DecodedPath}",
                    Request.Path.Value, path);

                var result = _chapterIndexService.DeleteChapterIndex(path);

                if (result)
                {
                    return Ok(new { success = true, message = "章节索引删除成功" });
                }
                else
                {
                    return NotFound(new { error = "章节索引文件不存在" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除章节索引失败: {Path}", path);
                return StatusCode(500, new { error = "删除章节索引失败", message = ex.Message });
            }
        }

        [HttpGet("chapters/admin/all")]
        public IActionResult GetAllChapterIndexes()
        {
            try
            {
                _statusService.IncrementRequests();

                var infos = _chapterIndexService.GetAllIndexFilesInfo();
                return Ok(new { indexFiles = infos });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取所有章节索引信息失败");
                return StatusCode(500, new { error = "获取所有章节索引信息失败", message = ex.Message });
            }
        }

        #endregion

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
            const int bufferSize = 131072;
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
            const int bufferSize = 131072;
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
                    retryCount = 0;

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

        private async Task<IActionResult> HandleTextFilePreview(string path, int page = 1, int pageSize = 1000)
        {
            try
            {
                var (stream, contentType, fileName) = await _fileService.DownloadFileAsync(path);

                using (stream)
                {
                    // 读取整个文件到内存（性能优先）
                    byte[] fileBytes;
                    using (var memoryStream = new MemoryStream())
                    {
                        await stream.CopyToAsync(memoryStream);
                        fileBytes = memoryStream.ToArray();
                    }

                    // 检测并处理 UTF-8 BOM
                    string content;
                    if (fileBytes.Length >= 3 && fileBytes[0] == 0xEF && fileBytes[1] == 0xBB && fileBytes[2] == 0xBF)
                    {
                        content = Encoding.UTF8.GetString(fileBytes, 3, fileBytes.Length - 3);
                    }
                    else
                    {
                        content = Encoding.UTF8.GetString(fileBytes);
                    }

                    // 按行分割
                    var lines = content.Split('\n');
                    var totalLines = lines.Length;

                    // 计算分页
                    if (page < 1) page = 1;
                    if (pageSize < 1) pageSize = 1000;
                    if (pageSize > 10000) pageSize = 10000;

                    var totalPages = (int)Math.Ceiling((double)totalLines / pageSize);
                    var startIndex = (page - 1) * pageSize;
                    var endIndex = Math.Min(startIndex + pageSize, totalLines);

                    // 获取当前页的内容
                    var pageLines = new List<string>();
                    for (int i = startIndex; i < endIndex; i++)
                    {
                        pageLines.Add(lines[i].TrimEnd('\r'));
                    }

                    var pageContent = string.Join("\n", pageLines);

                    // 获取或构建章节索引（只在第一页请求时构建，避免重复工作）
                    ChapterIndex chapterIndex = null;
                    if (page == 1)
                    {
                        chapterIndex = await _chapterIndexService.GetOrBuildChapterIndexAsync(path, content);
                        _logger.LogInformation("章节索引处理完成: {FileName}, 章节数: {ChapterCount}",
                            fileName, chapterIndex?.TotalChapters ?? 0);
                    }

                    _logger.LogInformation("文本文件分页预览 - 文件: {FileName}, 页码: {Page}, 页大小: {PageSize}, 总行数: {TotalLines}, 总页数: {TotalPages}",
                        fileName, page, pageSize, totalLines, totalPages);

                    // 构建响应对象
                    var response = new
                    {
                        type = "text",
                        fileName,
                        content = pageContent,
                        encoding = "utf-8",
                        pagination = new
                        {
                            currentPage = page,
                            pageSize = pageSize,
                            totalLines = totalLines,
                            totalPages = totalPages,
                            hasPrevious = page > 1,
                            hasNext = page < totalPages,
                            startLine = startIndex + 1,
                            endLine = endIndex
                        },
                        fileInfo = new
                        {
                            size = fileBytes.Length,
                            lines = totalLines,
                            hasBom = fileBytes.Length >= 3 && fileBytes[0] == 0xEF && fileBytes[1] == 0xBB && fileBytes[2] == 0xBF
                        }
                    };

                    // 如果有章节信息，添加到响应中
                    if (chapterIndex != null)
                    {
                        var responseWithChapters = new
                        {
                            response.type,
                            response.fileName,
                            response.content,
                            response.encoding,
                            response.pagination,
                            response.fileInfo,
                            chapters = new
                            {
                                total = chapterIndex.TotalChapters,
                                list = chapterIndex.Chapters.Select(c => new
                                {
                                    title = c.Title,
                                    page = c.Page,
                                    line = c.LineNumber,
                                    preview = c.Preview
                                }).ToArray()
                            }
                        };

                        return Ok(responseWithChapters);
                    }

                    return Ok(response);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "文本文件预览失败");
                return StatusCode(500, new { error = "文本文件预览失败", message = ex.Message });
            }
        }
        // 添加视频缩略图端点
        [HttpPost("video-thumbnail/generate")]
        public async Task<IActionResult> GenerateVideoThumbnail([FromBody] VideoThumbnailRequest request)
        {
            try
            {
                _statusService.IncrementRequests();

                if (string.IsNullOrEmpty(request.VideoPath))
                {
                    return BadRequest(new { error = "视频路径不能为空" });
                }

                // URL 解码路径
                request.VideoPath = WebUtility.UrlDecode(request.VideoPath);

                _logger.LogInformation("生成视频缩略图请求: {VideoPath}, 位置: {Position}%, 尺寸: {Width}x{Height}",
                    request.VideoPath, request.PositionPercentage, request.Width, request.Height);

                var result = await _videoThumbnailService.GenerateThumbnailAsync(request);

                if (result.Success)
                {
                    return Ok(new
                    {
                        success = true,
                        thumbnailPath = result.ThumbnailPath,
                        positionPercentage = result.PositionPercentage,
                        // 使用格式化属性或手动处理可空类型
                        videoDuration = result.VideoDuration.HasValue ?
                            result.VideoDuration.Value.ToString(@"hh\:mm\:ss") : "未知",
                        thumbnailTime = result.ThumbnailTime.HasValue ?
                            result.ThumbnailTime.Value.ToString(@"hh\:mm\:ss") : "未知",
                        // 或者直接使用模型中的格式化属性
                        videoDurationFormatted = result.VideoDurationFormatted,
                        thumbnailTimeFormatted = result.ThumbnailTimeFormatted,
                        message = result.Message
                    });
                }
                else
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = result.Message
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "生成视频缩略图失败: {VideoPath}", request?.VideoPath);
                return StatusCode(500, new { error = "生成视频缩略图失败", message = ex.Message });
            }
        }

        [HttpGet("video-thumbnail/{*path}")]
        public async Task<IActionResult> GetVideoThumbnail(string path, [FromQuery] int width = 320, [FromQuery] int height = 180)
        {
            try
            {
                _statusService.IncrementRequests();

                path = WebUtility.UrlDecode(path);

                // 添加路径验证
                if (string.IsNullOrEmpty(path))
                {
                    _logger.LogWarning("视频缩略图请求路径为空");
                    return await GetPlaceholderThumbnail(width, height);
                }

                var extension = Path.GetExtension(path).ToLowerInvariant();

                _logger.LogInformation("视频缩略图请求 - 路径: {Path}, 扩展名: {Extension}", path, extension);

                // 检查是否为视频文件
                if (!_videoThumbnailService.IsVideoFile(extension))
                {
                    _logger.LogWarning("非视频文件请求缩略图: {Path}", path);
                    return await GetPlaceholderThumbnail(width, height);
                }

                // 检查视频文件是否存在
                if (!await _fileService.FileExistsAsync(path))
                {
                    _logger.LogWarning("视频文件不存在: {Path}", path);
                    return await GetPlaceholderThumbnail(width, height);
                }

                // 检查缩略图是否已生成
                var thumbnailPath = _videoThumbnailService.GetThumbnailPath(path, width, height);

                if (string.IsNullOrEmpty(thumbnailPath) || !System.IO.File.Exists(thumbnailPath))
                {
                    // 缩略图不存在，加入生成队列
                    _videoThumbnailService.QueueVideoForGeneration(path);

                    _logger.LogInformation("缩略图不存在，已加入生成队列: {Path}", path);

                    // 返回占位图而不是404
                    return await GetPlaceholderThumbnail(width, height);
                }

                // 返回缩略图文件
                var stream = await _videoThumbnailService.GetThumbnailStreamAsync(thumbnailPath);

                if (stream == null)
                {
                    _logger.LogWarning("缩略图文件读取失败，返回占位图: {Path}", path);
                    return await GetPlaceholderThumbnail(width, height);
                }

                Response.Headers.Append("Cache-Control", "public, max-age=86400"); // 缓存1天
                Response.Headers.Append("Content-Disposition", $"inline; filename=\"{WebUtility.UrlEncode(Path.GetFileName(thumbnailPath))}\"");

                _logger.LogInformation("成功返回视频缩略图: {Path}", path);

                return File(stream, "image/jpeg");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取视频缩略图失败: {Path}", path);
                // 发生异常时返回占位图
                return await GetPlaceholderThumbnail(width, height);
            }
        }

        // 添加占位图生成方法
        private async Task<IActionResult> GetPlaceholderThumbnail(int width, int height)
        {
            try
            {
                // 生成简单的占位图
                var svgContent = $@"<svg width='{width}' height='{height}' xmlns='http://www.w3.org/2000/svg'>
                    <rect width='100%' height='100%' fill='#2F4F4F'/>
                    <text x='50%' y='50%' font-family='Arial' font-size='{Math.Min(width, height) / 10}' 
                          fill='white' text-anchor='middle' dominant-baseline='middle'>Video Thumbnail</text>
                    <polygon points='{width/2-20},{height/2-20} {width/2+20},{height/2} {width/2-20},{height/2+20}' 
                          fill='white'/>
                </svg>";

                var svgBytes = System.Text.Encoding.UTF8.GetBytes(svgContent);
                var stream = new MemoryStream(svgBytes);

                Response.Headers.Append("Cache-Control", "no-cache, no-store");
                return File(stream, "image/svg+xml");
            }
            catch
            {
                // 如果SVG生成失败，返回空的图片
                return NotFound();
            }
        }

        // 添加状态查询端点
        [HttpGet("video-thumbnail/status")]
        public IActionResult GetThumbnailGenerationStatus()
        {
            try
            {
                var status = _videoThumbnailService.GetGenerationStatus();

                return Ok(new
                {
                    queueLength = status.QueueLength,
                    generatedCount = status.GeneratedCount,
                    isRunning = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取缩略图生成状态失败");
                return StatusCode(500, new { error = "获取状态失败", message = ex.Message });
            }
        }

        [HttpGet("video-info/{*path}")]
        public async Task<IActionResult> GetVideoInfo(string path)
        {
            try
            {
                _statusService.IncrementRequests();

                path = WebUtility.UrlDecode(path);
                var extension = Path.GetExtension(path).ToLowerInvariant();

                if (!_videoThumbnailService.IsVideoFile(extension))
                {
                    return BadRequest(new { error = "非视频文件" });
                }

                if (!await _videoThumbnailService.VideoFileExistsAsync(path))
                {
                    return NotFound(new { error = "视频文件不存在" });
                }

                var duration = await _videoThumbnailService.GetVideoDurationAsync(path);
                var fileInfo = await _fileService.GetFileInfoAsync(path);

                return Ok(new
                {
                    fileName = fileInfo.Name,
                    fileSize = fileInfo.Size,
                    fileSizeFormatted = fileInfo.SizeFormatted,
                    duration = duration?.ToString(@"hh\:mm\:ss"),
                    durationSeconds = duration?.TotalSeconds,
                    supportedForThumbnail = duration != null
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取视频信息失败: {Path}", path);
                return StatusCode(500, new { error = "获取视频信息失败", message = ex.Message });
            }
        }
        #region 歌词相关端点
        


        [HttpPost("lyrics/mapping")]
        public async Task<IActionResult> SaveLyricsMapping([FromBody] LyricsMappingRequest request)
        {
            try
            {
                _statusService.IncrementRequests();

                if (string.IsNullOrEmpty(request.SongPath))
                {
                    return BadRequest(new { error = "歌曲路径不能为空" });
                }

                // URL 解码路径
                request.SongPath = WebUtility.UrlDecode(request.SongPath);
                request.LyricsPath = WebUtility.UrlDecode(request.LyricsPath);

                _logger.LogInformation("保存歌词映射 - 歌曲: {SongPath}, 歌词: {LyricsPath}",
                    request.SongPath, request.LyricsPath);

                // 检查歌曲文件是否存在
                if (!await _fileService.FileExistsAsync(request.SongPath))
                {
                    return NotFound(new { error = "歌曲文件不存在" });
                }

                // 特殊处理：标记为无歌词
                if (request.LyricsPath == "NO_LYRICS")
                {
                    // 保存无歌词标记
                    var result = await SaveLyricsMappingToFile(request);

                    if (result)
                    {
                        _logger.LogInformation("标记歌曲为无歌词: {SongPath}", request.SongPath);
                        return Ok(new { success = true, message = "歌曲已标记为无歌词" });
                    }
                    else
                    {
                        return StatusCode(500, new { error = "标记无歌词失败" });
                    }
                }

                // 正常歌词文件映射
                if (string.IsNullOrEmpty(request.LyricsPath))
                {
                    return BadRequest(new { error = "歌词路径不能为空" });
                }

                // 检查歌词文件是否存在
                if (!await _fileService.FileExistsAsync(request.LyricsPath))
                {
                    return NotFound(new { error = "歌词文件不存在" });
                }

                // 保存映射到数据库或文件
                var saveResult = await SaveLyricsMappingToFile(request);

                if (saveResult)
                {
                    return Ok(new { success = true, message = "歌词映射保存成功" });
                }
                else
                {
                    return StatusCode(500, new { error = "保存歌词映射失败" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存歌词映射失败");
                return StatusCode(500, new { error = "保存歌词映射失败", message = ex.Message });
            }
        }

        [HttpGet("lyrics/{*songPath}")]
        public async Task<IActionResult> GetLyrics(string songPath)
        {
            try
            {
                _statusService.IncrementRequests();

                songPath = WebUtility.UrlDecode(songPath);
                _logger.LogInformation("获取歌词 - 歌曲路径: {SongPath}", songPath);

                // 1. 首先查找映射的歌词文件
                var mappedLyricsPath = await GetMappedLyricsPath(songPath);

                if (!string.IsNullOrEmpty(mappedLyricsPath))
                {
                    // 检查是否为无歌词标记
                    if (mappedLyricsPath == "NO_LYRICS")
                    {
                        _logger.LogInformation("歌曲标记为无歌词: {SongPath}", songPath);
                        return Ok(new
                        {
                            type = "no_lyrics",
                            message = "此歌曲已标记为无歌词"
                        });
                    }

                    _logger.LogInformation("找到映射的歌词文件: {LyricsPath}", mappedLyricsPath);
                    return await GetLyricsContent(mappedLyricsPath);
                }

                // 2. 如果没有映射，尝试智能匹配
                var directory = Path.GetDirectoryName(songPath);
                var fileName = Path.GetFileNameWithoutExtension(songPath);

                // 智能匹配查找最佳匹配（无论置信度如何，只要有匹配就返回）
                var bestLyricsMatch = await FindBestLyricsMatch(songPath, fileName, directory);

                if (bestLyricsMatch != null && bestLyricsMatch.LyricsFile != null)
                {
                    // 找到匹配，自动保存映射
                    await SaveLyricsMappingToFile(new LyricsMappingRequest
                    {
                        SongPath = songPath,
                        LyricsPath = bestLyricsMatch.LyricsFile.Path
                    });

                    _logger.LogInformation("智能匹配成功并建立映射: {SongPath} -> {LyricsPath}, 分数: {Score}",
                        songPath, bestLyricsMatch.LyricsFile.Path, bestLyricsMatch.MatchScore);

                    return await GetLyricsContent(bestLyricsMatch.LyricsFile.Path);
                }

                // 3. 如果智能匹配失败（没有找到任何匹配），尝试在同目录下查找同名的.lrc文件
                var possibleLyricsPath = Path.Combine(directory, fileName + ".lrc");
                if (await _fileService.FileExistsAsync(possibleLyricsPath))
                {
                    _logger.LogInformation("找到同名歌词文件: {LyricsPath}", possibleLyricsPath);
                    return await GetLyricsContent(possibleLyricsPath);
                }

                // 4. 查找同目录下其他歌词文件
                var lyricsFiles = await FindLyricsFilesInDirectory(directory);
                if (lyricsFiles.Any())
                {
                    _logger.LogInformation("在目录中找到 {Count} 个歌词文件", lyricsFiles.Count);
                    return Ok(new
                    {
                        type = "available_files",
                        files = lyricsFiles,
                        message = "请选择歌词文件"
                    });
                }

                _logger.LogWarning("未找到歌词文件: {SongPath}", songPath);
                return NotFound(new { error = "未找到歌词文件" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取歌词失败: {SongPath}", songPath);
                return StatusCode(500, new { error = "获取歌词失败", message = ex.Message });
            }
        }


        #region 智能匹配方法

        /// <summary>
        /// 在同级目录中查找最佳歌词匹配
        /// </summary>
        /// <summary>
        /// 在同级目录中查找最佳歌词匹配
        /// </summary>
        private async Task<BestLyricsMatch> FindBestLyricsMatch(string songPath, string songFileName, string directory)
        {
            try
            {
                // 获取同级目录中的所有歌词文件
                var lyricsFiles = await FindLyricsFilesInDirectory(directory);

                if (!lyricsFiles.Any())
                {
                    return null;
                }

                // 从歌曲文件名提取信息
                var songInfo = ExtractSongInfoFromFileName(songFileName);
                _logger.LogDebug("提取歌曲信息: {FileName} -> 歌名: {SongName}, 歌手: {Artist}",
                    songFileName, songInfo.SongName, songInfo.Artist);

                BestLyricsMatch bestMatch = null;
                double bestScore = 0;

                // 对每个歌词文件计算匹配分数
                foreach (var lyricsFile in lyricsFiles)
                {
                    var matchResult = CalculateMatchResult(songInfo, lyricsFile);

                    if (matchResult != null && (bestMatch == null || matchResult.MatchScore > bestScore))
                    {
                        bestScore = matchResult.MatchScore;
                        bestMatch = new BestLyricsMatch
                        {
                            LyricsFile = lyricsFile,
                            MatchScore = matchResult.MatchScore,
                            MatchedChars = matchResult.MatchedChars,
                            MatchedMultiChars = matchResult.MatchedMultiChars,
                            HasLyricsKeyword = matchResult.HasLyricsKeyword
                        };

                        _logger.LogDebug("更新最佳匹配: {LyricsFile} -> 分数: {Score}",
                            lyricsFile.Name, matchResult.MatchScore);
                    }
                }

                // 只要有匹配就返回（无论分数高低）
                if (bestMatch != null)
                {
                    _logger.LogInformation("找到最佳歌词匹配: {LyricsPath}, 分数: {Score}",
                        bestMatch.LyricsFile.Path, bestMatch.MatchScore);
                    return bestMatch;
                }

                _logger.LogDebug("未找到任何匹配");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查找最佳歌词匹配失败");
                return null;
            }
        }

        /// <summary>
        /// 计算匹配结果，返回详细信息
        /// </summary>
        private MatchResult CalculateMatchResult(SongInfo songInfo, LyricsFileInfo lyricsFile)
        {
            var lyricsFileName = Path.GetFileNameWithoutExtension(lyricsFile.Name);
            var cleanedLyricsName = CleanText(lyricsFileName);

            // 检查是否包含"歌词"关键词（非必须，但加分）
            bool hasLyricsKeyword = cleanedLyricsName.Contains("歌词") || lyricsFileName.Contains("歌词");

            // 开始匹配
            double matchScore = hasLyricsKeyword ? 0.1 : 0.0;
            var matchedChars = new List<string>();
            var matchedMultiChars = new List<string>();

            // 1. 匹配歌名字符
            if (!string.IsNullOrEmpty(songInfo.CleanedSongName))
            {
                int charMatches = 0;
                foreach (var c in songInfo.CleanedSongName)
                {
                    var charStr = c.ToString();
                    if (cleanedLyricsName.Contains(charStr))
                    {
                        charMatches++;
                        matchedChars.Add(charStr);
                    }
                }

                if (charMatches > 0)
                {
                    double charMatchRatio = (double)charMatches / songInfo.CleanedSongName.Length;
                    matchScore += charMatchRatio * 0.6; // 占60%权重

                    // 连续匹配奖励
                    if (cleanedLyricsName.Contains(songInfo.CleanedSongName))
                    {
                        matchScore += 0.3; // 完整匹配加30%
                        matchedMultiChars.Add(songInfo.CleanedSongName);
                    }
                    else
                    {
                        // 查找最长连续匹配
                        int longestMatch = 0;
                        string longestSubstring = "";

                        for (int i = 0; i < songInfo.CleanedSongName.Length; i++)
                        {
                            for (int j = i + 2; j <= songInfo.CleanedSongName.Length; j++)
                            {
                                var substring = songInfo.CleanedSongName.Substring(i, j - i);
                                if (cleanedLyricsName.Contains(substring) && substring.Length > longestMatch)
                                {
                                    longestMatch = substring.Length;
                                    longestSubstring = substring;
                                }
                            }
                        }

                        if (longestMatch >= 2)
                        {
                            matchScore += longestMatch * 0.05; // 每个连续匹配的字加5%
                            if (!string.IsNullOrEmpty(longestSubstring))
                            {
                                matchedMultiChars.Add(longestSubstring);
                            }
                        }
                    }
                }
            }

            // 2. 匹配歌手名字符（如果有）
            if (!string.IsNullOrEmpty(songInfo.CleanedArtist))
            {
                int artistMatches = 0;
                foreach (var c in songInfo.CleanedArtist)
                {
                    var charStr = c.ToString();
                    if (cleanedLyricsName.Contains(charStr))
                    {
                        artistMatches++;
                        matchedChars.Add(charStr);
                    }
                }

                if (artistMatches > 0)
                {
                    double artistMatchRatio = (double)artistMatches / songInfo.CleanedArtist.Length;
                    matchScore += artistMatchRatio * 0.3; // 占30%权重
                }
            }

            // 至少需要匹配1个字符（非常宽松的条件）
            if (matchedChars.Count == 0 && matchedMultiChars.Count == 0)
            {
                return null;
            }

            return new MatchResult
            {
                MatchScore = Math.Min(matchScore, 1.0),
                HasLyricsKeyword = hasLyricsKeyword,
                MatchedChars = matchedChars,
                MatchedMultiChars = matchedMultiChars
            };
        }

        /// <summary>
        /// 从文件名提取歌曲信息
        /// </summary>
        private SongInfo ExtractSongInfoFromFileName(string fileName)
        {
            var info = new SongInfo
            {
                FileName = fileName,
                CleanedFileName = CleanText(fileName)
            };

            // 常见的分隔符
            var separators = new[] { "-", "–", "—", "_", "~", "·", " ", "　" };

            // 尝试用分隔符分割
            foreach (var sep in separators)
            {
                if (fileName.Contains(sep))
                {
                    var parts = fileName.Split(new[] { sep }, StringSplitOptions.RemoveEmptyEntries);

                    if (parts.Length >= 2)
                    {
                        // 假设第一个是歌名，最后一个是歌手
                        info.SongName = parts[0].Trim();
                        info.Artist = parts[^1].Trim();

                        info.CleanedSongName = CleanText(info.SongName);
                        info.CleanedArtist = CleanText(info.Artist);

                        break;
                    }
                }
            }

            // 如果没有分隔符，整个文件名作为歌名
            if (string.IsNullOrEmpty(info.SongName))
            {
                info.SongName = fileName;
                info.CleanedSongName = info.CleanedFileName;
            }

            return info;
        }

        /// <summary>
        /// 清理文本，去除所有符号只保留文字
        /// </summary>
        private string CleanText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            var result = new StringBuilder();

            foreach (char c in text)
            {
                // 保留中文字符
                if (c >= 0x4E00 && c <= 0x9FFF)
                {
                    result.Append(c);
                }
                // 保留英文字母（统一转小写）
                else if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
                {
                    result.Append(char.ToLowerInvariant(c));
                }
                // 保留数字
                else if (c >= '0' && c <= '9')
                {
                    result.Append(c);
                }
            }

            return result.ToString();
        }


        #endregion

        [HttpGet("lyrics/files/{*directory}")]
        public async Task<IActionResult> GetLyricsFiles(string directory)
        {
            try
            {
                _statusService.IncrementRequests();

                directory = WebUtility.UrlDecode(directory);
                _logger.LogInformation("获取目录中的歌词文件: {Directory}", directory);

                var lyricsFiles = await FindLyricsFilesInDirectory(directory);

                return Ok(new
                {
                    directory,
                    lyricsFiles = lyricsFiles,
                    count = lyricsFiles.Count
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取歌词文件列表失败: {Directory}", directory);
                return StatusCode(500, new { error = "获取歌词文件列表失败", message = ex.Message });
            }
        }

        [HttpGet("lyrics/mapping/{*songPath}")]
        public async Task<IActionResult> GetLyricsMapping(string songPath)
        {
            try
            {
                _statusService.IncrementRequests();

                songPath = WebUtility.UrlDecode(songPath);
                _logger.LogInformation("获取歌词映射: {SongPath}", songPath);

                var mappedLyricsPath = await GetMappedLyricsPath(songPath);

                if (!string.IsNullOrEmpty(mappedLyricsPath))
                {
                    // 处理无歌词标记
                    if (mappedLyricsPath == "NO_LYRICS")
                    {
                        return Ok(new
                        {
                            songPath,
                            lyricsPath = "NO_LYRICS",
                            lyricsFileName = "无歌词",
                            exists = false,
                            isNoLyrics = true
                        });
                    }

                    return Ok(new
                    {
                        songPath,
                        lyricsPath = mappedLyricsPath,
                        lyricsFileName = Path.GetFileName(mappedLyricsPath),
                        exists = await _fileService.FileExistsAsync(mappedLyricsPath),
                        isNoLyrics = false
                    });
                }

                return NotFound(new { error = "未找到歌词映射" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取歌词映射失败: {SongPath}", songPath);
                return StatusCode(500, new { error = "获取歌词映射失败", message = ex.Message });
            }
        }

        [HttpDelete("lyrics/mapping/{*songPath}")]
        public async Task<IActionResult> DeleteLyricsMapping(string songPath)
        {
            try
            {
                _statusService.IncrementRequests();

                songPath = WebUtility.UrlDecode(songPath);
                _logger.LogInformation("删除歌词映射: {SongPath}", songPath);

                var result = await DeleteLyricsMappingFromFile(songPath);

                if (result)
                {
                    return Ok(new { success = true, message = "歌词映射删除成功" });
                }
                else
                {
                    return NotFound(new { error = "未找到歌词映射" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除歌词映射失败: {SongPath}", songPath);
                return StatusCode(500, new { error = "删除歌词映射失败", message = ex.Message });
            }
        }

        #endregion

        #region 私有方法 - 歌词功能

        private async Task<bool> SaveLyricsMappingToFile(LyricsMappingRequest request)
        {
            try
            {
                var mappingFilePath = Path.Combine(_fileService.GetRootPath(), "lyrics-mappings.json");
                Dictionary<string, string> mappings = new Dictionary<string, string>();

                // 读取现有的映射文件
                if (System.IO.File.Exists(mappingFilePath))
                {
                    var json = await System.IO.File.ReadAllTextAsync(mappingFilePath);
                    mappings = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                              ?? new Dictionary<string, string>();
                }

                // 添加或更新映射
                mappings[request.SongPath] = request.LyricsPath;

                // 保存回文件
                var newJson = System.Text.Json.JsonSerializer.Serialize(mappings, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                });
                await System.IO.File.WriteAllTextAsync(mappingFilePath, newJson);

                _logger.LogInformation("歌词映射保存成功: {SongPath} -> {LyricsPath}", request.SongPath, request.LyricsPath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存歌词映射到文件失败");
                return false;
            }
        }

        private async Task<string> GetMappedLyricsPath(string songPath)
        {
            try
            {
                var mappingFilePath = Path.Combine(_fileService.GetRootPath(), "lyrics-mappings.json");

                if (!System.IO.File.Exists(mappingFilePath))
                {
                    return null;
                }

                var json = await System.IO.File.ReadAllTextAsync(mappingFilePath);
                var mappings = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);

                if (mappings != null && mappings.TryGetValue(songPath, out var lyricsPath))
                {
                    return lyricsPath;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取映射歌词路径失败");
                return null;
            }
        }

        private async Task<bool> DeleteLyricsMappingFromFile(string songPath)
        {
            try
            {
                var mappingFilePath = Path.Combine(_fileService.GetRootPath(), "lyrics-mappings.json");

                if (!System.IO.File.Exists(mappingFilePath))
                {
                    return false;
                }

                var json = await System.IO.File.ReadAllTextAsync(mappingFilePath);
                var mappings = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);

                if (mappings != null && mappings.Remove(songPath))
                {
                    var newJson = System.Text.Json.JsonSerializer.Serialize(mappings, new System.Text.Json.JsonSerializerOptions
                    {
                        WriteIndented = true
                    });
                    await System.IO.File.WriteAllTextAsync(mappingFilePath, newJson);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除歌词映射失败");
                return false;
            }
        }

        private async Task<IActionResult> GetLyricsContent(string lyricsPath)
        {
            try
            {
                var (stream, contentType, fileName) = await _fileService.DownloadFileAsync(lyricsPath);

                using (stream)
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    var content = await reader.ReadToEndAsync();

                    return Ok(new
                    {
                        type = "lyrics_content",
                        lyricsPath,
                        fileName,
                        content,
                        encoding = "utf-8"
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取歌词内容失败: {LyricsPath}", lyricsPath);
                return StatusCode(500, new { error = "获取歌词内容失败", message = ex.Message });
            }
        }

        private async Task<List<LyricsFileInfo>> FindLyricsFilesInDirectory(string directory)
        {
            var lyricsFiles = new List<LyricsFileInfo>();

            try
            {
                var fileListResponse = await _fileService.GetFileListAsync(directory);

                // 修复：遍历 Files 列表而不是 FileListResponse 对象
                foreach (var file in fileListResponse.Files)
                {
                    if (IsLyricsFile(file.Name))
                    {
                        lyricsFiles.Add(new LyricsFileInfo
                        {
                            Path = file.Path,
                            Name = file.Name,
                            Size = file.Size,
                            SizeFormatted = file.SizeFormatted,
                            ModifiedTime = file.LastModified
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查找歌词文件失败: {Directory}", directory);
            }

            return lyricsFiles;
        }
        #region 歌曲元数据与封面端点

        [HttpGet("song/metadata/{*path}")]
        public async Task<IActionResult> GetSongMetadata(string path)
        {
            try
            {
                _statusService.IncrementRequests();
                path = WebUtility.UrlDecode(path);

                var fullPath = Path.Combine(_fileService.GetRootPath(), path);
                if (!await _fileService.FileExistsAsync(path))
                    return NotFound(new { error = "文件不存在" });

                var metadata = await _audioMetadataService.GetMetadataAsync(fullPath);
                return Ok(new
                {
                    path,
                    fileName = Path.GetFileName(path),
                    metadata
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取歌曲元数据失败: {Path}", path);
                return StatusCode(500, new { error = "获取元数据失败", message = ex.Message });
            }
        }

        [HttpPost("song/metadata/mapping")]
        public async Task<IActionResult> SaveSongMetadataMapping([FromBody] SaveMetadataMappingRequest request)
        {
            try
            {
                _statusService.IncrementRequests();
                if (string.IsNullOrEmpty(request.SongPath))
                    return BadRequest(new { error = "歌曲路径不能为空" });

                request.SongPath = WebUtility.UrlDecode(request.SongPath);
                var fullPath = Path.Combine(_fileService.GetRootPath(), request.SongPath);
                if (!await _fileService.FileExistsAsync(request.SongPath))
                    return NotFound(new { error = "歌曲文件不存在" });

                var metadata = new SongMetadata
                {
                    Title = request.Title ?? "",
                    Artist = request.Artist ?? "",
                    Album = request.Album ?? "",
                    HasCover = false,
                    CustomCoverPath = null
                };

                var result = await _audioMetadataService.SaveMetadataMappingAsync(fullPath, metadata);
                if (result)
                    return Ok(new { success = true, message = "元数据映射保存成功" });
                else
                    return StatusCode(500, new { error = "保存失败" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存元数据映射失败");
                return StatusCode(500, new { error = "保存失败", message = ex.Message });
            }
        }

        [HttpDelete("song/metadata/mapping/{*path}")]
        public async Task<IActionResult> DeleteSongMetadataMapping(string path)
        {
            try
            {
                _statusService.IncrementRequests();
                path = WebUtility.UrlDecode(path);
                var fullPath = Path.Combine(_fileService.GetRootPath(), path);

                var result = await _audioMetadataService.DeleteMetadataMappingAsync(fullPath);
                if (result)
                    return Ok(new { success = true, message = "元数据映射已删除" });
                else
                    return NotFound(new { error = "未找到映射" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除元数据映射失败");
                return StatusCode(500, new { error = "删除失败", message = ex.Message });
            }
        }

        [HttpGet("song/cover/{*path}")]
        public async Task<IActionResult> GetAlbumCover(string path)
        {
            try
            {
                _statusService.IncrementRequests();
                path = WebUtility.UrlDecode(path);
                var fullPath = Path.Combine(_fileService.GetRootPath(), path);

                if (!await _fileService.FileExistsAsync(path))
                    return NotFound(new { error = "文件不存在" });

                var coverStream = await _audioMetadataService.GetAlbumCoverAsync(fullPath);
                if (coverStream == null)
                    return NotFound(new { error = "没有封面图片" });

                Response.Headers.Append("Cache-Control", "public, max-age=86400");
                return File(coverStream, "image/jpeg"); // 可根据实际类型调整
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取专辑封面失败");
                return StatusCode(500, new { error = "获取封面失败", message = ex.Message });
            }
        }

        [HttpPost("song/cover/upload")]
        [RequestSizeLimit(10 * 1024 * 1024)] // 10MB
        public async Task<IActionResult> UploadAlbumCover([FromForm] CoverUploadRequest request)
        {
            try
            {
                _statusService.IncrementRequests();
                if (string.IsNullOrEmpty(request.SongPath))
                    return BadRequest(new { error = "歌曲路径不能为空" });
                if (request.CoverFile == null || request.CoverFile.Length == 0)
                    return BadRequest(new { error = "请选择图片文件" });

                request.SongPath = WebUtility.UrlDecode(request.SongPath);
                var fullPath = Path.Combine(_fileService.GetRootPath(), request.SongPath);
                if (!await _fileService.FileExistsAsync(request.SongPath))
                    return NotFound(new { error = "歌曲文件不存在" });

                var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp" };
                var ext = Path.GetExtension(request.CoverFile.FileName).ToLowerInvariant();
                if (!allowedExtensions.Contains(ext))
                    return BadRequest(new { error = "不支持的图片格式" });

                await using var stream = request.CoverFile.OpenReadStream();
                var savedName = await _audioMetadataService.SaveCustomCoverAsync(fullPath, stream, request.CoverFile.FileName);
                if (savedName != null)
                    return Ok(new { success = true, message = "封面上传成功", coverPath = savedName });
                else
                    return StatusCode(500, new { error = "保存封面失败" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "上传专辑封面失败");
                return StatusCode(500, new { error = "上传失败", message = ex.Message });
            }
        }

        [HttpDelete("song/cover/{*path}")]
        public async Task<IActionResult> DeleteAlbumCover(string path)
        {
            try
            {
                _statusService.IncrementRequests();
                path = WebUtility.UrlDecode(path);
                var fullPath = Path.Combine(_fileService.GetRootPath(), path);

                if (!await _fileService.FileExistsAsync(path))
                    return NotFound(new { error = "歌曲文件不存在" });

                var result = await _audioMetadataService.DeleteCustomCoverAsync(fullPath);
                if (result)
                    return Ok(new { success = true, message = "自定义封面已删除" });
                else
                    return NotFound(new { error = "没有找到自定义封面" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除专辑封面失败");
                return StatusCode(500, new { error = "删除失败", message = ex.Message });
            }
        }

        #endregion
        private bool IsLyricsFile(string fileName)
        {
            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            var lyricsExtensions = new[] { ".lrc", ".txt", ".srt", ".ass", ".ssa" };
            return lyricsExtensions.Contains(extension);
        }

        #endregion


        private string FormatUptime(long uptimeInMilliseconds)
        {
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

        private bool IsImageFile(string extension)
        {
            var imageExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" };
            return imageExtensions.Contains(extension.ToLowerInvariant());
        }

        #endregion
    }

    public static class FileServiceExtensions
    {
        public static string GetRootPath(this IFileService fileService)
        {
            return @"E:\FileServer";
        }
    }
}