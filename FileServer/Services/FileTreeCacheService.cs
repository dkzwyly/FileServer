using System.Collections.Concurrent;
using FileServer.Models;
using LiteDB;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.IO;

namespace FileServer.Services
{
    public class FileTreeCacheService : IFileTreeCacheService, IDisposable
    {
        private readonly ConcurrentDictionary<string, FileNode> _nodes = new();
        private readonly ConcurrentDictionary<string, HashSet<string>> _children = new();
        private readonly ConcurrentQueue<ChangeEntry> _changes = new();
        private int _changeCount = 0;
        private readonly ReaderWriterLockSlim _rwLock = new();
        private readonly string _dbPath;
        private readonly LiteDatabase _db;
        private readonly ILogger<FileTreeCacheService> _logger;
        private readonly IFileSystemHelper _fileSystemHelper;
        private readonly BsonMapper _mapper;

        // ===== FileSystemWatcher =====
        private FileSystemWatcher? _watcher;
        private readonly ConcurrentDictionary<string, DateTime> _lastEventTime = new();
        private const int EventThrottleMs = 500;
        private readonly string _rootPath;
        private readonly string _systemFilesDir;
        private bool _disposed = false;

        public FileTreeCacheService(
            ILogger<FileTreeCacheService> logger,
            IConfiguration configuration,
            IFileSystemHelper fileSystemHelper,
            ILiteDbProvider dbProvider)
        {
            _logger = logger;
            _fileSystemHelper = fileSystemHelper;

            _rootPath = _fileSystemHelper.GetRootPath();
            _systemFilesDir = configuration["FileServerConfig:MetadataDirectory"] ?? "系统文件";

            var fullMetadataDir = Path.Combine(_rootPath, _systemFilesDir);
            if (!Directory.Exists(fullMetadataDir))
                Directory.CreateDirectory(fullMetadataDir);

            _dbPath = Path.Combine(fullMetadataDir, "file-tree.db");
            _logger.LogInformation("文件树缓存数据库路径: {DbPath}", _dbPath);

            _mapper = new BsonMapper();
            _mapper.Entity<FileNode>().Id(x => x.Path);

            _db = dbProvider.GetDatabase("file-tree", customPath: null, mapper: _mapper);

            // 加载完成后启动 watcher
            _ = Task.Run(async () =>
            {
                try
                {
                    await LoadAllFromDatabaseAsync();
                    StartWatcher();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "初始化文件树缓存失败");
                }
            });
        }

        // ==================================================================
        //  FileSystemWatcher —— 主动推送变化事件
        // ==================================================================

        private void StartWatcher()
        {
            if (!Directory.Exists(_rootPath))
            {
                _logger.LogWarning("根目录不存在，无法启动 FileSystemWatcher: {RootPath}", _rootPath);
                return;
            }

            try
            {
                _watcher = new FileSystemWatcher(_rootPath)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName
                                 | NotifyFilters.DirectoryName
                                 | NotifyFilters.Size           // ← 感知大小变化的关键
                                 | NotifyFilters.LastWrite,
                    InternalBufferSize = 64 * 1024
                };

                _watcher.Created += OnCreated;
                _watcher.Changed += OnChanged;
                _watcher.Deleted += OnDeleted;
                _watcher.Renamed += OnRenamed;
                _watcher.Error += OnWatcherError;
                _watcher.EnableRaisingEvents = true;

                _logger.LogInformation("FileSystemWatcher 已启动: {RootPath}", _rootPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "启动 FileSystemWatcher 失败，将退化为按需扫描模式");
                _watcher = null;
            }
        }

        private void OnCreated(object sender, FileSystemEventArgs e)
            => HandleFileSystemEvent(e.FullPath, WatcherChangeTypes.Created);

        private void OnChanged(object sender, FileSystemEventArgs e)
            => HandleFileSystemEvent(e.FullPath, WatcherChangeTypes.Changed);

        private void OnDeleted(object sender, FileSystemEventArgs e)
            => HandleFileSystemEvent(e.FullPath, WatcherChangeTypes.Deleted);

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            try
            {
                var oldRelative = ToRelativePath(e.OldFullPath);
                var newRelative = ToRelativePath(e.FullPath);
                if (oldRelative == null || newRelative == null) return;

                OnNodeDeleted(oldRelative);
                OnNodeChangedOrCreated(newRelative, e.FullPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理重命名事件失败: {Old} -> {New}", e.OldFullPath, e.FullPath);
            }
        }

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            _logger.LogError(e.GetException(), "FileSystemWatcher 缓冲区溢出，将执行全量重建");

            _ = Task.Run(async () =>
            {
                try
                {
                    await BuildFullTreeAsync();
                    await FullSaveToDatabaseAsync();
                    _logger.LogInformation("全量重建完成，缓存已恢复一致");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "全量重建失败");
                }
            });

            try
            {
                if (_watcher != null)
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.EnableRaisingEvents = true;
                    _logger.LogInformation("FileSystemWatcher 已重启");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "重启 FileSystemWatcher 失败");
            }
        }

        private void HandleFileSystemEvent(string fullPath, WatcherChangeTypes changeType)
        {
            if (_disposed) return;

            try
            {
                var relativePath = ToRelativePath(fullPath);
                if (relativePath == null) return;

                // 跳过系统文件目录，避免监控自己的数据库
                if (IsSystemPath(relativePath)) return;

                // 节流：删除事件永远放行，其他事件同一路径 500ms 内只处理一次
                var now = DateTime.UtcNow;
                if (changeType != WatcherChangeTypes.Deleted)
                {
                    if (_lastEventTime.TryGetValue(relativePath, out var last)
                        && (now - last).TotalMilliseconds < EventThrottleMs)
                    {
                        return;
                    }
                }
                _lastEventTime[relativePath] = now;

                switch (changeType)
                {
                    case WatcherChangeTypes.Deleted:
                        OnNodeDeleted(relativePath);
                        break;
                    case WatcherChangeTypes.Created:
                    case WatcherChangeTypes.Changed:
                        OnNodeChangedOrCreated(relativePath, fullPath);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理文件系统事件失败: {Path} {Type}", fullPath, changeType);
            }
        }

        private void OnNodeChangedOrCreated(string relativePath, string fullPath)
        {
            try
            {
                if (Directory.Exists(fullPath))
                {
                    var di = new DirectoryInfo(fullPath);
                    UpdateOrAddDirectoryNode(relativePath, di);
                }
                else if (File.Exists(fullPath))
                {
                    var fi = new FileInfo(fullPath);
                    UpdateOrAddFileNode(relativePath, fi);
                }
            }
            catch (IOException)
            {
                // 文件正在被独占写入，忽略；后续 Changed 事件会再次更新
            }
            catch (UnauthorizedAccessException)
            {
                // 权限问题，忽略
            }
        }

        private void UpdateOrAddFileNode(string relativePath, FileInfo fi)
        {
            var parentPath = GetParentPath(relativePath);
            var ext = fi.Extension.ToLowerInvariant();

            _rwLock.EnterWriteLock();
            try
            {
                if (_nodes.TryGetValue(relativePath, out var existing))
                {
                    // ===== 已有节点：更新 size / mtime =====
                    bool changed = false;
                    if (existing.Size != fi.Length)
                    {
                        existing.Size = fi.Length;
                        changed = true;
                    }
                    if (existing.LastModified != fi.LastWriteTimeUtc)
                    {
                        existing.LastModified = fi.LastWriteTimeUtc;
                        changed = true;
                    }

                    if (changed)
                    {
                        _changes.Enqueue(new ChangeEntry { Type = ChangeType.Add, Node = existing });
                        Interlocked.Increment(ref _changeCount);
                    }
                }
                else
                {
                    var node = new FileNode
                    {
                        Path = relativePath,
                        Name = fi.Name,
                        ParentPath = parentPath,
                        IsDirectory = false,
                        Size = fi.Length,
                        LastModified = fi.LastWriteTimeUtc,
                        MimeType = _fileSystemHelper.GetMimeType(ext),
                        IsVideo = IsVideoFile(ext),
                        IsAudio = IsAudioFile(ext),
                        IsImage = IsImageFile(ext)
                    };
                    _nodes[relativePath] = node;

                    if (!_children.ContainsKey(parentPath))
                        _children[parentPath] = new HashSet<string>();
                    _children[parentPath].Add(relativePath);

                    _changes.Enqueue(new ChangeEntry { Type = ChangeType.Add, Node = node });
                    Interlocked.Increment(ref _changeCount);
                }
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }

        private void UpdateOrAddDirectoryNode(string relativePath, DirectoryInfo di)
        {
            var parentPath = GetParentPath(relativePath);

            _rwLock.EnterWriteLock();
            try
            {
                if (_nodes.TryGetValue(relativePath, out var existing))
                {
                    if (existing.LastModified != di.LastWriteTimeUtc)
                    {
                        existing.LastModified = di.LastWriteTimeUtc;
                        _changes.Enqueue(new ChangeEntry { Type = ChangeType.Add, Node = existing });
                        Interlocked.Increment(ref _changeCount);
                    }
                }
                else
                {
                    var node = new FileNode
                    {
                        Path = relativePath,
                        Name = di.Name,
                        ParentPath = parentPath,
                        IsDirectory = true,
                        LastModified = di.LastWriteTimeUtc,
                        MimeType = ""
                    };
                    _nodes[relativePath] = node;

                    if (!string.IsNullOrEmpty(relativePath))
                    {
                        if (!_children.ContainsKey(parentPath))
                            _children[parentPath] = new HashSet<string>();
                        _children[parentPath].Add(relativePath);
                    }

                    _changes.Enqueue(new ChangeEntry { Type = ChangeType.Add, Node = node });
                    Interlocked.Increment(ref _changeCount);
                }
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }

        private void OnNodeDeleted(string relativePath)
        {
            _rwLock.EnterWriteLock();
            try
            {
                RemoveNodeInternal(relativePath, enqueueChanges: true);
                _lastEventTime.TryRemove(relativePath, out _);
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }

        private string? ToRelativePath(string fullPath)
        {
            try
            {
                var relative = Path.GetRelativePath(_rootPath, fullPath).Replace('\\', '/');
                if (relative == "." || relative.StartsWith("..")) return null;
                return relative.TrimStart('/');
            }
            catch
            {
                return null;
            }
        }

        private bool IsSystemPath(string relativePath)
        {
            if (string.IsNullOrEmpty(_systemFilesDir)) return false;
            return relativePath == _systemFilesDir
                || relativePath.StartsWith(_systemFilesDir + "/", StringComparison.OrdinalIgnoreCase);
        }

        // ==================================================================
        //  强刷接口 —— 绕开 mtime 判断，强制重扫
        // ==================================================================

        public async Task ForceRefreshDirectoryAsync(string relativePath, bool recursive = false)
        {
            var normalized = NormalizeRelativePath(relativePath);

            await ScanAndUpdateDirectoryAsync(normalized);

            if (recursive)
            {
                List<string> subDirs;
                _rwLock.EnterReadLock();
                try
                {
                    subDirs = _children.TryGetValue(normalized, out var children)
                        ? children.Where(c => _nodes.TryGetValue(c, out var n) && n.IsDirectory).ToList()
                        : new List<string>();
                }
                finally
                {
                    _rwLock.ExitReadLock();
                }

                foreach (var sub in subDirs)
                {
                    await ForceRefreshDirectoryAsync(sub, true);
                }
            }
        }

        // ==================================================================
        //  原有接口
        // ==================================================================

        public async Task LoadAllFromDatabaseAsync()
        {
            await Task.Run(async () =>
            {
                try
                {
                    var col = _db.GetCollection<FileNode>("nodes");
                    var dbNodes = col.FindAll().ToList();

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
                        _logger.LogInformation("已从数据库加载 {Count} 个节点", _nodes.Count);
                    }
                    finally
                    {
                        _rwLock.ExitWriteLock();
                    }

                    if (_nodes.Count == 0)
                    {
                        _logger.LogInformation("数据库为空，开始全量扫描构建文件树...");
                        await BuildFullTreeAsync();
                        await FullSaveToDatabaseAsync();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "从数据库加载文件树失败，尝试全量扫描");
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

        private async Task BuildFullTreeAsync()
        {
            var rootPath = _fileSystemHelper.GetRootPath();
            if (!Directory.Exists(rootPath))
            {
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
                if (!Directory.Exists(physicalPath)) continue;

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
                    _logger.LogError(ex, "扫描目录 {Path} 时获取子项失败，跳过", current);
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
                    var relativePath = string.IsNullOrEmpty(current)
                        ? dirName
                        : Path.Combine(current, dirName).Replace("\\", "/");
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
            }

            _rwLock.EnterWriteLock();
            try
            {
                _nodes.Clear();
                _children.Clear();
                foreach (var node in allNodes)
                    _nodes[node.Path] = node;
                foreach (var kv in allChildren)
                    _children[kv.Key] = kv.Value;
                _changes.Clear();
                Interlocked.Exchange(ref _changeCount, 0);
                _logger.LogInformation("全量扫描完成，共 {NodeCount} 个节点，{ChildCount} 个父目录",
                    _nodes.Count, _children.Count);
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }

            await Task.CompletedTask;
        }

        public async Task<FileListResponse> GetDirectoryContentAsync(string relativePath)
        {
            var normalized = NormalizeRelativePath(relativePath);

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
                        }
                        else
                        {
                            bool hasChildren = _children.TryGetValue(normalized, out var childSet)
                                               && childSet.Count > 0;
                            if (!hasChildren && Directory.EnumerateFileSystemEntries(physicalPath).Any())
                            {
                                needScan = true;
                            }
                        }
                    }
                    else
                    {
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
                try
                {
                    await ScanAndUpdateDirectoryAsync(normalized);
                }
                catch (DirectoryNotFoundException)
                {
                    // 目录已删除，返回空响应
                    return new FileListResponse
                    {
                        CurrentPath = normalized,
                        ParentPath = GetParentPath(normalized),
                        Directories = new List<DirectoryInfoModel>(),
                        Files = new List<FileInfoModel>()
                    };
                }
            }

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

        public Task AddNodeAsync(FileNode node)
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
                // 与 watcher 保持一致：已存在则更新，而不是跳过
                if (_nodes.TryGetValue(node.Path, out var existing))
                {
                    existing.Size = node.Size;
                    existing.LastModified = node.LastModified;
                    existing.IsVideo = node.IsVideo;
                    existing.IsAudio = node.IsAudio;
                    existing.IsImage = node.IsImage;
                    existing.MimeType = node.MimeType;

                    _changes.Enqueue(new ChangeEntry { Type = ChangeType.Add, Node = existing });
                    Interlocked.Increment(ref _changeCount);
                }
                else
                {
                    _nodes[node.Path] = node;
                    var parent = node.ParentPath ?? "";
                    if (!_children.ContainsKey(parent))
                        _children[parent] = new HashSet<string>();
                    _children[parent].Add(node.Path);

                    _changes.Enqueue(new ChangeEntry { Type = ChangeType.Add, Node = node });
                    Interlocked.Increment(ref _changeCount);
                }
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }

            return Task.CompletedTask;
        }

        public Task RemoveNodeAsync(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("不能删除根节点", nameof(path));

            _rwLock.EnterWriteLock();
            try
            {
                if (!_nodes.ContainsKey(path))
                    return Task.CompletedTask;

                RemoveNodeInternal(path, enqueueChanges: true);
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }

            return Task.CompletedTask;
        }

        public async Task ApplyChangesToDatabaseAsync()
        {
            if (Interlocked.CompareExchange(ref _changeCount, 0, 0) == 0)
                return;

            var entries = new List<ChangeEntry>();
            while (_changes.TryDequeue(out var entry))
                entries.Add(entry);

            Interlocked.Exchange(ref _changeCount, 0);

            if (entries.Count == 0) return;

            try
            {
                var col = _db.GetCollection<FileNode>("nodes");
                col.EnsureIndex(x => x.ParentPath);

                foreach (var entry in entries)
                {
                    try
                    {
                        if (entry.Type == ChangeType.Add)
                        {
                            if (string.IsNullOrEmpty(entry.Node?.Path))
                                continue;

                            // ===== 关键：用 Upsert，避免"节点已存在"时报错 =====
                            col.Upsert(entry.Node);
                        }
                        else
                        {
                            col.Delete(entry.Path);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "处理变更失败: {Type} {Path}",
                            entry.Type, entry.Path ?? entry.Node?.Path);
                    }
                }
                _logger.LogDebug("增量同步完成，应用了 {Count} 个变更", entries.Count);
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

            await Task.CompletedTask;
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
                    var col = _db.GetCollection<FileNode>("nodes");
                    col.DeleteAll();
                    var validNodes = snapshot.Where(n => !string.IsNullOrEmpty(n.Path)).ToList();
                    if (validNodes.Count > 0)
                        col.InsertBulk(validNodes);
                    _logger.LogInformation("全量覆盖完成，共 {Count} 个节点", validNodes.Count);
                    _changes.Clear();
                    Interlocked.Exchange(ref _changeCount, 0);
                    return;
                }
                catch (IOException) when (retryCount < maxRetries - 1)
                {
                    retryCount++;
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

        public async Task<List<string>> GetAllFilePathsAsync(string relativePath)
        {
            var normalized = NormalizeRelativePath(relativePath);

            await EnsureDirectoryCachedAsync(normalized);

            var result = new List<string>();
            var queue = new Queue<string>();

            _rwLock.EnterReadLock();
            try
            {
                if (_children.TryGetValue(normalized, out var directChildren))
                {
                    foreach (var child in directChildren)
                        queue.Enqueue(child);
                }

                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    if (_nodes.TryGetValue(current, out var node))
                    {
                        if (node.IsDirectory)
                        {
                            if (_children.TryGetValue(current, out var subChildren))
                            {
                                foreach (var sub in subChildren)
                                    queue.Enqueue(sub);
                            }
                        }
                        else
                        {
                            result.Add(current);
                        }
                    }
                }
            }
            finally
            {
                _rwLock.ExitReadLock();
            }

            return result;
        }

        private async Task EnsureDirectoryCachedAsync(string relativePath)
        {
            bool needScan = false;
            _rwLock.EnterReadLock();
            try
            {
                if (_nodes.TryGetValue(relativePath, out var dirNode))
                {
                    var physicalPath = Path.Combine(_fileSystemHelper.GetRootPath(), relativePath);
                    if (Directory.Exists(physicalPath))
                    {
                        var diskLastWrite = Directory.GetLastWriteTimeUtc(physicalPath);
                        if (dirNode.LastModified != diskLastWrite)
                            needScan = true;
                        else
                        {
                            bool hasChildren = _children.TryGetValue(relativePath, out var childSet)
                                               && childSet.Count > 0;
                            if (!hasChildren && Directory.EnumerateFileSystemEntries(physicalPath).Any())
                                needScan = true;
                        }
                    }
                    else needScan = true;
                }
                else needScan = true;
            }
            finally
            {
                _rwLock.ExitReadLock();
            }

            if (needScan)
                await ScanAndUpdateDirectoryAsync(relativePath);
        }

        private string NormalizeRelativePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            var normalized = path.Replace('\\', '/');
            if (normalized.StartsWith('/')) normalized = normalized.TrimStart('/');
            if (normalized == "/") normalized = "";
            return normalized.Trim('/');
        }

        // ==================================================================
        //  核心修复：扫描时更新已存在节点
        // ==================================================================

        private async Task ScanAndUpdateDirectoryAsync(string relativePath)
        {
            var rootPath = _fileSystemHelper.GetRootPath();
            var physicalPath = Path.Combine(rootPath, relativePath);
            if (!Directory.Exists(physicalPath))
            {
                if (_nodes.ContainsKey(relativePath))
                {
                    _rwLock.EnterWriteLock();
                    try { RemoveNodeInternal(relativePath, enqueueChanges: true); }
                    finally { _rwLock.ExitWriteLock(); }
                }
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
                _logger.LogError(ex, "扫描目录 {Path} 时获取子项失败", relativePath);
            }

            var newNodes = new List<FileNode>();
            foreach (var dir in subDirs)
            {
                try { newNodes.Add(CreateDirectoryNode(relativePath, dir)); }
                catch (Exception ex) { _logger.LogError(ex, "处理子目录失败: {Dir}", dir); }
            }
            foreach (var file in files)
            {
                try { newNodes.Add(CreateFileNode(relativePath, file)); }
                catch (Exception ex) { _logger.LogError(ex, "处理文件失败: {File}", file); }
            }

            _rwLock.EnterWriteLock();
            try
            {
                var oldChildPaths = _children.ContainsKey(relativePath)
                    ? new HashSet<string>(_children[relativePath])
                    : new HashSet<string>();

                var newChildPaths = new HashSet<string>(newNodes.Select(n => n.Path));

                // ============ 核心修复：区分 add / update ============
                var toRemove = oldChildPaths.Except(newChildPaths).ToList();
                var toAdd = new List<FileNode>();
                var toUpdate = new List<FileNode>();

                foreach (var node in newNodes)
                {
                    if (_nodes.ContainsKey(node.Path))
                        toUpdate.Add(node);
                    else
                        toAdd.Add(node);
                }

                // 1) 删除不再存在的
                foreach (var childPath in toRemove)
                    RemoveNodeInternal(childPath, enqueueChanges: true);

                // 2) 新增
                foreach (var node in toAdd)
                {
                    _nodes[node.Path] = node;
                    var parent = node.ParentPath ?? "";
                    if (!_children.ContainsKey(parent))
                        _children[parent] = new HashSet<string>();
                    _children[parent].Add(node.Path);

                    _changes.Enqueue(new ChangeEntry { Type = ChangeType.Add, Node = node });
                    Interlocked.Increment(ref _changeCount);
                }

                // 3) 更新已存在节点（关键修复点）
                int updatedCount = 0;
                foreach (var fresh in toUpdate)
                {
                    if (!_nodes.TryGetValue(fresh.Path, out var existing)) continue;

                    bool changed = false;

                    if (existing.Size != fresh.Size) { existing.Size = fresh.Size; changed = true; }
                    if (existing.LastModified != fresh.LastModified) { existing.LastModified = fresh.LastModified; changed = true; }
                    if (existing.IsDirectory != fresh.IsDirectory) { existing.IsDirectory = fresh.IsDirectory; changed = true; }
                    if (existing.IsVideo != fresh.IsVideo) { existing.IsVideo = fresh.IsVideo; changed = true; }
                    if (existing.IsAudio != fresh.IsAudio) { existing.IsAudio = fresh.IsAudio; changed = true; }
                    if (existing.IsImage != fresh.IsImage) { existing.IsImage = fresh.IsImage; changed = true; }
                    if (existing.MimeType != fresh.MimeType) { existing.MimeType = fresh.MimeType; changed = true; }
                    if (existing.Name != fresh.Name) { existing.Name = fresh.Name; changed = true; }

                    if (changed)
                    {
                        _changes.Enqueue(new ChangeEntry { Type = ChangeType.Add, Node = existing });
                        Interlocked.Increment(ref _changeCount);
                        updatedCount++;
                    }
                }

                // 4) 目录节点自身的 mtime
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

                _logger.LogInformation(
                    "扫描更新目录 {Path}: 删除 {Remove} 个，新增 {Add} 个，更新 {Update} 个",
                    relativePath, toRemove.Count, toAdd.Count, updatedCount);
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }

            await Task.CompletedTask;
        }

        private void RemoveNodeInternal(string path, bool enqueueChanges)
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
            var path = string.IsNullOrEmpty(parentPath)
                ? dirInfo.Name
                : Path.Combine(parentPath, dirInfo.Name).Replace("\\", "/");
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
            var path = string.IsNullOrEmpty(parentPath)
                ? fileInfo.Name
                : Path.Combine(parentPath, fileInfo.Name).Replace("\\", "/");
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

        // ★ 新增三个静态 HashSet（放在类字段区域，和其它字段一起）
        private static readonly HashSet<string> _videoExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { ".mp4", ".avi", ".mov", ".mkv", ".wmv", ".flv", ".webm", ".m4v" };

        private static readonly HashSet<string> _audioExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".wma" };

        private static readonly HashSet<string> _imageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" };

        // ★ 三个方法也改成 static，因为它们不再依赖实例状态
        private static bool IsVideoFile(string ext) => _videoExts.Contains(ext);
        private static bool IsAudioFile(string ext) => _audioExts.Contains(ext);
        private static bool IsImageFile(string ext) => _imageExts.Contains(ext);

        private string GetParentPath(string currentPath)
        {
            if (string.IsNullOrEmpty(currentPath)) return "";
            var parts = currentPath.Split('/');
            if (parts.Length <= 1) return "";
            return string.Join("/", parts, 0, parts.Length - 1);
        }

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

        // ==================================================================
        //  释放
        // ==================================================================

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_watcher != null)
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.Created -= OnCreated;
                    _watcher.Changed -= OnChanged;
                    _watcher.Deleted -= OnDeleted;
                    _watcher.Renamed -= OnRenamed;
                    _watcher.Error -= OnWatcherError;
                    _watcher.Dispose();
                    _watcher = null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "释放 FileSystemWatcher 失败");
            }

            try { _rwLock.Dispose(); } catch { }
        }
    }
}