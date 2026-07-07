using System.Collections.Concurrent;
using FileServer.Models;
using LiteDB;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace FileServer.Services
{
    public class FileTreeCacheService : IFileTreeCacheService
    {
        private readonly ConcurrentDictionary<string, FileNode> _nodes = new();
        private readonly ConcurrentDictionary<string, HashSet<string>> _children = new();
        private readonly ConcurrentQueue<ChangeEntry> _changes = new();
        private int _changeCount = 0;
        private readonly ReaderWriterLockSlim _rwLock = new();
        private readonly string _dbPath;
        private readonly string _connectionString;
        private readonly ILogger<FileTreeCacheService> _logger;
        private readonly IFileSystemHelper _fileSystemHelper;
        private readonly BsonMapper _mapper;

        public FileTreeCacheService(
            ILogger<FileTreeCacheService> logger,
            IConfiguration configuration,
            IFileSystemHelper fileSystemHelper)
        {
            _logger = logger;
            _fileSystemHelper = fileSystemHelper;

            var rootPath = _fileSystemHelper.GetRootPath();
            var metadataDir = configuration["FileServerConfig:MetadataDirectory"] ?? "系统文件";
            var fullMetadataDir = Path.Combine(rootPath, metadataDir);
            if (!Directory.Exists(fullMetadataDir))
                Directory.CreateDirectory(fullMetadataDir);

            _dbPath = Path.Combine(fullMetadataDir, "file-tree.db");
            _connectionString = $"Filename={_dbPath};Connection=Shared";
            _logger.LogInformation($"文件树缓存数据库路径: {_dbPath}");

            // 配置 BsonMapper，明确 Path 为 Id
            _mapper = new BsonMapper();
            _mapper.Entity<FileNode>().Id(x => x.Path);

            _ = Task.Run(async () => await LoadAllFromDatabaseAsync());
        }

        // ---------- 公共接口实现 ----------

        public async Task LoadAllFromDatabaseAsync()
        {
            await Task.Run(async () =>
            {
                try
                {
                    List<FileNode> dbNodes = null;
                    using (var db = new LiteDatabase(_connectionString, _mapper))
                    {
                        var col = db.GetCollection<FileNode>("nodes");
                        dbNodes = col.FindAll().ToList();
                    }

                    _rwLock.EnterWriteLock();
                    try
                    {
                        _nodes.Clear();
                        _children.Clear();

                        foreach (var node in dbNodes)
                        {
                            _nodes[node.Path] = node;
                            if (!string.IsNullOrEmpty(node.Path))
                            {
                                var parent = node.ParentPath ?? "";
                                if (!_children.ContainsKey(parent))
                                    _children[parent] = new HashSet<string>();
                                _children[parent].Add(node.Path);
                            }
                        }
                        _changes.Clear();
                        Interlocked.Exchange(ref _changeCount, 0);
                        _logger.LogInformation($"已从数据库加载 {_nodes.Count} 个节点");
                    }
                    finally
                    {
                        _rwLock.ExitWriteLock();
                    }

                    if (_nodes.Count == 0)
                    {
                        _logger.LogInformation("数据库为空或未加载到节点，开始全量扫描构建文件树...");
                        await BuildFullTreeAsync();
                        await FullSaveToDatabaseAsync();
                        _logger.LogInformation($"全量扫描完成，共加载 {_nodes.Count} 个节点");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "从数据库加载文件树失败，将使用空缓存");
                    try
                    {
                        await BuildFullTreeAsync();
                        await FullSaveToDatabaseAsync();
                    }
                    catch (Exception inner)
                    {
                        _logger.LogError(inner, "紧急全量扫描也失败");
                    }
                }
            });
        }

        /// <summary>
        /// 全量扫描文件树，收集所有节点后一次性替换缓存（不操作数据库）
        /// </summary>
        private async Task BuildFullTreeAsync()
        {
            var rootPath = _fileSystemHelper.GetRootPath();
            if (!Directory.Exists(rootPath))
            {
                _logger.LogWarning("根目录不存在，创建: {RootPath}", rootPath);
                Directory.CreateDirectory(rootPath);
            }

            var allNodes = new List<FileNode>();
            var allChildren = new Dictionary<string, HashSet<string>>();

            var queue = new Queue<string>();
            queue.Enqueue("");

            int totalScanned = 0;
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var physicalPath = Path.Combine(rootPath, current);
                if (!Directory.Exists(physicalPath))
                    continue;

                var dirInfo = new DirectoryInfo(physicalPath);
                var diskLastWrite = dirInfo.LastWriteTimeUtc;

                string[] subDirs, files;
                try
                {
                    subDirs = Directory.GetDirectories(physicalPath);
                    files = Directory.GetFiles(physicalPath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"扫描目录 {current} 时获取子项失败，跳过");
                    continue;
                }

                if (!string.IsNullOrEmpty(current))
                {
                    var dirNode = new FileNode
                    {
                        Path = current,
                        Name = Path.GetFileName(current),
                        ParentPath = GetParentPath(current),
                        IsDirectory = true,
                        LastModified = diskLastWrite,
                        MimeType = ""
                    };
                    allNodes.Add(dirNode);

                    var parent = GetParentPath(current);
                    if (!allChildren.ContainsKey(parent))
                        allChildren[parent] = new HashSet<string>();
                    allChildren[parent].Add(current);
                }

                foreach (var dir in subDirs)
                {
                    var dirName = Path.GetFileName(dir);
                    var relativePath = string.IsNullOrEmpty(current) ? dirName : Path.Combine(current, dirName).Replace("\\", "/");
                    queue.Enqueue(relativePath);
                }

                foreach (var file in files)
                {
                    var fileNode = CreateFileNode(current, file);
                    allNodes.Add(fileNode);

                    if (!allChildren.ContainsKey(current))
                        allChildren[current] = new HashSet<string>();
                    allChildren[current].Add(fileNode.Path);
                }

                totalScanned++;
                if (totalScanned % 100 == 0)
                    _logger.LogDebug("全量扫描进度: 已扫描 {Count} 个目录", totalScanned);
            }

            _rwLock.EnterWriteLock();
            try
            {
                _nodes.Clear();
                _children.Clear();
                foreach (var node in allNodes)
                {
                    _nodes[node.Path] = node;
                }
                foreach (var kv in allChildren)
                {
                    _children[kv.Key] = kv.Value;
                }
                _changes.Clear();
                Interlocked.Exchange(ref _changeCount, 0);
                _logger.LogInformation($"全量扫描完成，共加载 {_nodes.Count} 个节点，{_children.Count} 个父目录");
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }

        public async Task<FileListResponse> GetDirectoryContentAsync(string relativePath)
        {
            var normalized = (relativePath ?? "").Replace('\\', '/').Trim('/');
            if (normalized == "/") normalized = "";

            bool needScan = false;
            _rwLock.EnterReadLock();
            try
            {
                if (_nodes.TryGetValue(normalized, out var dirNode))
                {
                    var physicalPath = Path.Combine(_fileSystemHelper.GetRootPath(), normalized);
                    if (Directory.Exists(physicalPath))
                    {
                        var diskLastWrite = Directory.GetLastWriteTimeUtc(physicalPath);
                        if (dirNode.LastModified != diskLastWrite)
                        {
                            needScan = true;
                            _logger.LogDebug($"目录 {normalized} 修改时间变化，需要重新扫描");
                        }
                        else
                        {
                            bool hasChildren = _children.TryGetValue(normalized, out var childSet) && childSet.Count > 0;
                            if (!hasChildren)
                            {
                                if (Directory.EnumerateFileSystemEntries(physicalPath).Any())
                                {
                                    needScan = true;
                                    _logger.LogDebug($"目录 {normalized} 缓存为空但磁盘非空，强制扫描");
                                }
                            }
                            if (!needScan)
                                return BuildResponseFromCache(normalized);
                        }
                    }
                    else
                    {
                        _logger.LogWarning($"目录 {normalized} 已在磁盘上删除，清除缓存");
                        needScan = true;
                    }
                }
                else
                {
                    needScan = true;
                }
            }
            finally
            {
                _rwLock.ExitReadLock();
            }

            if (needScan)
            {
                _logger.LogInformation($"扫描磁盘目录: {normalized}");
                await ScanAndUpdateDirectoryAsync(normalized);
                _rwLock.EnterReadLock();
                try
                {
                    return BuildResponseFromCache(normalized);
                }
                finally
                {
                    _rwLock.ExitReadLock();
                }
            }

            return new FileListResponse
            {
                CurrentPath = normalized,
                ParentPath = GetParentPath(normalized),
                Directories = new List<DirectoryInfoModel>(),
                Files = new List<FileInfoModel>()
            };
        }

        public async Task AddNodeAsync(FileNode node)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            if (string.IsNullOrEmpty(node.Path))
                throw new InvalidOperationException("不能添加根节点");

            var physicalPath = Path.Combine(_fileSystemHelper.GetRootPath(), node.Path);
            if (node.IsDirectory)
            {
                if (!Directory.Exists(physicalPath))
                    throw new DirectoryNotFoundException($"目录不存在: {node.Path}");
            }
            else
            {
                if (!File.Exists(physicalPath))
                    throw new FileNotFoundException($"文件不存在: {node.Path}");
            }

            _rwLock.EnterWriteLock();
            try
            {
                if (_nodes.ContainsKey(node.Path))
                {
                    _logger.LogWarning($"节点 {node.Path} 已存在，将跳过添加");
                    return;
                }

                _nodes[node.Path] = node;
                var parent = node.ParentPath ?? "";
                if (!_children.ContainsKey(parent))
                    _children[parent] = new HashSet<string>();
                _children[parent].Add(node.Path);

                _changes.Enqueue(new ChangeEntry { Type = ChangeType.Add, Node = node });
                Interlocked.Increment(ref _changeCount);
                _logger.LogDebug($"添加节点: {node.Path}");
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }

        public async Task RemoveNodeAsync(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("不能删除根节点", nameof(path));

            _rwLock.EnterWriteLock();
            try
            {
                if (!_nodes.ContainsKey(path))
                {
                    _logger.LogWarning($"节点 {path} 不存在，删除忽略");
                    return;
                }
                await RemoveNodeInternalAsync(path, enqueueChanges: true);
                _logger.LogDebug($"删除节点及其后代: {path}");
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }

        public async Task ApplyChangesToDatabaseAsync()
        {
            if (Interlocked.CompareExchange(ref _changeCount, 0, 0) == 0)
            {
                _logger.LogInformation("无变更，跳过同步");
                return;
            }

            var entries = new List<ChangeEntry>();
            while (_changes.TryDequeue(out var entry))
                entries.Add(entry);

            Interlocked.Exchange(ref _changeCount, 0);

            if (entries.Count == 0)
                return;

            try
            {
                using var db = new LiteDatabase(_connectionString, _mapper);
                var col = db.GetCollection<FileNode>("nodes");
                col.EnsureIndex(x => x.ParentPath);

                foreach (var entry in entries)
                {
                    try
                    {
                        if (entry.Type == ChangeType.Add)
                        {
                            if (string.IsNullOrEmpty(entry.Node?.Path))
                            {
                                _logger.LogWarning("跳过添加空 Path 的节点");
                                continue;
                            }
                            col.Insert(entry.Node);
                        }
                        else
                        {
                            col.Delete(entry.Path);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "处理变更失败: {Type} {Path/Node}", entry.Type, entry.Path ?? entry.Node?.Path);
                    }
                }
                _logger.LogInformation($"增量同步完成，应用了 {entries.Count} 个变更");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "增量同步失败，将变更重新入队");
                foreach (var entry in entries)
                {
                    _changes.Enqueue(entry);
                    Interlocked.Increment(ref _changeCount);
                }
                throw;
            }
        }

        public async Task FullSaveToDatabaseAsync()
        {
            List<FileNode> snapshot;
            _rwLock.EnterReadLock();
            try
            {
                snapshot = _nodes.Values.ToList();
            }
            finally
            {
                _rwLock.ExitReadLock();
            }

            const int maxRetries = 3;
            int retryCount = 0;
            while (retryCount < maxRetries)
            {
                try
                {
                    using var db = new LiteDatabase(_connectionString, _mapper);
                    var col = db.GetCollection<FileNode>("nodes");
                    col.DeleteAll();
                    var validNodes = snapshot.Where(n => !string.IsNullOrEmpty(n.Path)).ToList();
                    if (validNodes.Count > 0)
                        col.InsertBulk(validNodes);
                    _logger.LogInformation($"全量覆盖完成，共 {validNodes.Count} 个节点（过滤了 {snapshot.Count - validNodes.Count} 个无效节点）");
                    _changes.Clear();
                    Interlocked.Exchange(ref _changeCount, 0);
                    return;
                }
                catch (IOException) when (retryCount < maxRetries - 1)
                {
                    retryCount++;
                    _logger.LogWarning($"全量保存时文件被占用，等待 500ms 后重试 (第 {retryCount} 次)");
                    await Task.Delay(500);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "全量覆盖失败");
                    throw;
                }
            }
            throw new IOException($"全量保存失败，重试 {maxRetries} 次后仍被占用");
        }

        // ---------- 私有辅助方法 ----------

        private FileListResponse BuildResponseFromCache(string path)
        {
            var response = new FileListResponse
            {
                CurrentPath = path,
                ParentPath = GetParentPath(path)
            };

            if (_children.TryGetValue(path, out var childPaths))
            {
                foreach (var childPath in childPaths)
                {
                    if (_nodes.TryGetValue(childPath, out var node))
                    {
                        if (node.IsDirectory)
                        {
                            response.Directories.Add(new DirectoryInfoModel
                            {
                                Name = node.Name,
                                Path = node.Path
                            });
                        }
                        else
                        {
                            response.Files.Add(new FileInfoModel
                            {
                                Name = node.Name,
                                Path = node.Path,
                                Size = node.Size ?? 0,
                                SizeFormatted = _fileSystemHelper.FormatFileSize(node.Size ?? 0),
                                Extension = Path.GetExtension(node.Name).ToLowerInvariant(),
                                LastModified = node.LastModified,
                                IsVideo = node.IsVideo,
                                IsAudio = node.IsAudio,
                                MimeType = node.MimeType,
                                HasThumbnail = false
                            });
                        }
                    }
                }
            }

            return response;
        }

        private async Task ScanAndUpdateDirectoryAsync(string relativePath)
        {
            var rootPath = _fileSystemHelper.GetRootPath();
            var physicalPath = Path.Combine(rootPath, relativePath);
            if (!Directory.Exists(physicalPath))
            {
                if (_nodes.ContainsKey(relativePath))
                    await RemoveNodeAsync(relativePath);
                throw new DirectoryNotFoundException($"目录不存在: {relativePath}");
            }

            var dirInfo = new DirectoryInfo(physicalPath);
            var diskLastWrite = dirInfo.LastWriteTimeUtc;

            var subDirs = new List<string>();
            var files = new List<string>();
            try
            {
                subDirs.AddRange(Directory.GetDirectories(physicalPath));
                files.AddRange(Directory.GetFiles(physicalPath));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"扫描目录 {relativePath} 时获取子项失败");
            }

            _logger.LogDebug($"扫描目录 {relativePath}: 子目录 {subDirs.Count} 个，文件 {files.Count} 个");

            var newNodes = new List<FileNode>();
            foreach (var dir in subDirs)
            {
                try
                {
                    var dirNode = CreateDirectoryNode(relativePath, dir);
                    newNodes.Add(dirNode);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"处理子目录失败: {dir}");
                }
            }
            foreach (var file in files)
            {
                try
                {
                    var fileNode = CreateFileNode(relativePath, file);
                    newNodes.Add(fileNode);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"处理文件失败: {file}");
                }
            }

            _rwLock.EnterWriteLock();
            try
            {
                HashSet<string> oldChildPaths = _children.ContainsKey(relativePath)
                    ? new HashSet<string>(_children[relativePath])
                    : new HashSet<string>();

                var newChildPaths = new HashSet<string>(newNodes.Select(n => n.Path));
                var toRemove = oldChildPaths.Except(newChildPaths).ToList();
                var toAdd = newNodes.Where(n => !_nodes.ContainsKey(n.Path)).ToList();

                foreach (var childPath in toRemove)
                {
                    await RemoveNodeInternalAsync(childPath, enqueueChanges: true);
                }

                foreach (var node in toAdd)
                {
                    _nodes[node.Path] = node;
                    if (!string.IsNullOrEmpty(node.Path))
                    {
                        var parent = node.ParentPath ?? "";
                        if (!_children.ContainsKey(parent))
                            _children[parent] = new HashSet<string>();
                        _children[parent].Add(node.Path);
                    }

                    _changes.Enqueue(new ChangeEntry { Type = ChangeType.Add, Node = node });
                    Interlocked.Increment(ref _changeCount);
                }

                if (_nodes.TryGetValue(relativePath, out var dirNode))
                {
                    dirNode.LastModified = diskLastWrite;
                }
                else
                {
                    var parent = GetParentPath(relativePath);
                    var dirNodeNew = new FileNode
                    {
                        Path = relativePath,
                        Name = string.IsNullOrEmpty(relativePath) ? "" : Path.GetFileName(relativePath),
                        ParentPath = parent,
                        IsDirectory = true,
                        LastModified = diskLastWrite,
                        MimeType = ""
                    };
                    _nodes[relativePath] = dirNodeNew;
                    if (!string.IsNullOrEmpty(relativePath))
                    {
                        if (!_children.ContainsKey(parent))
                            _children[parent] = new HashSet<string>();
                        _children[parent].Add(relativePath);
                    }

                    _changes.Enqueue(new ChangeEntry { Type = ChangeType.Add, Node = dirNodeNew });
                    Interlocked.Increment(ref _changeCount);
                }

                _logger.LogInformation($"扫描更新目录 {relativePath}: 删除 {toRemove.Count} 个，添加 {toAdd.Count} 个");
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }

        private async Task RemoveNodeInternalAsync(string path, bool enqueueChanges)
        {
            var queue = new Queue<string>();
            queue.Enqueue(path);
            var allToRemove = new List<string>();

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                allToRemove.Add(current);
                if (_children.TryGetValue(current, out var childPaths))
                {
                    foreach (var child in childPaths)
                        queue.Enqueue(child);
                }
            }

            foreach (var p in allToRemove)
            {
                if (_nodes.TryGetValue(p, out var node))
                {
                    var parent = node.ParentPath ?? "";
                    if (_children.TryGetValue(parent, out var siblings))
                        siblings.Remove(p);
                }
                _nodes.TryRemove(p, out _);
                _children.TryRemove(p, out _);
            }

            if (enqueueChanges)
            {
                foreach (var p in allToRemove)
                {
                    _changes.Enqueue(new ChangeEntry { Type = ChangeType.Remove, Path = p });
                    Interlocked.Increment(ref _changeCount);
                }
            }
        }

        private FileNode CreateDirectoryNode(string parentPath, string fullPath)
        {
            var dirInfo = new DirectoryInfo(fullPath);
            var path = string.IsNullOrEmpty(parentPath) ? dirInfo.Name : Path.Combine(parentPath, dirInfo.Name).Replace("\\", "/");
            return new FileNode
            {
                Path = path,
                Name = dirInfo.Name,
                ParentPath = parentPath ?? "",
                IsDirectory = true,
                LastModified = dirInfo.LastWriteTimeUtc,
                MimeType = ""
            };
        }

        private FileNode CreateFileNode(string parentPath, string fullPath)
        {
            var fileInfo = new FileInfo(fullPath);
            var ext = fileInfo.Extension.ToLowerInvariant();
            var path = string.IsNullOrEmpty(parentPath) ? fileInfo.Name : Path.Combine(parentPath, fileInfo.Name).Replace("\\", "/");
            return new FileNode
            {
                Path = path,
                Name = fileInfo.Name,
                ParentPath = parentPath ?? "",
                IsDirectory = false,
                Size = fileInfo.Length,
                LastModified = fileInfo.LastWriteTimeUtc,
                MimeType = _fileSystemHelper.GetMimeType(ext),
                IsVideo = IsVideoFile(ext),
                IsAudio = IsAudioFile(ext),
                IsImage = IsImageFile(ext)
            };
        }

        private bool IsVideoFile(string ext) => new[] { ".mp4", ".avi", ".mov", ".mkv", ".wmv", ".flv", ".webm", ".m4v" }.Contains(ext);
        private bool IsAudioFile(string ext) => new[] { ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".wma" }.Contains(ext);
        private bool IsImageFile(string ext) => new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" }.Contains(ext);

        private string GetParentPath(string currentPath)
        {
            if (string.IsNullOrEmpty(currentPath)) return "";
            var parts = currentPath.Split('/');
            if (parts.Length <= 1) return "";
            return string.Join("/", parts, 0, parts.Length - 1);
        }
    }
}