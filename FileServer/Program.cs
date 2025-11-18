using FileServer.Models;
using FileServer.Services;
using FileServer.Middleware;
using System.Security.Cryptography.X509Certificates;
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// 配置服务
builder.Services.Configure<FileServerConfig>(
    builder.Configuration.GetSection("FileServer"));

// 添加服务
builder.Services.AddSingleton<IServerStatusService, ServerStatusService>();

// 添加缩略图服务
builder.Services.AddScoped<IThumbnailService, ThumbnailService>();

// 更新文件服务注册（确保传递 thumbnailService）
builder.Services.AddScoped<IFileService, FileService>();
// 添加内存映射服务
builder.Services.AddScoped<IMemoryMappedFileService, MemoryMappedFileService>();

// 添加控制器
builder.Services.AddControllers();

// 创建并信任开发者证书
var certificate = CreateAndTrustDeveloperCertificate();
if (certificate != null)
{
    Console.WriteLine($"✅ 开发者证书已创建并信任: {certificate.Subject}");
}

// 配置 HTTP3/QUIC
builder.WebHost.ConfigureKestrel((context, options) =>
{
    var config = context.Configuration.GetSection("FileServer").Get<FileServerConfig>();

    // HTTP 端点
    options.ListenAnyIP(config?.HttpPort ?? 8080);

    // HTTPS 端点 (用于 HTTP3)
    options.ListenAnyIP(config?.HttpsPort ?? 8081, listenOptions =>
    {
        if (certificate != null)
        {
            listenOptions.UseHttps(certificate);
        }
        else
        {
            listenOptions.UseHttps();
        }

        // 启用 HTTP3
        if (config?.EnableQuic == true)
        {
            listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1AndHttp2AndHttp3;
            Console.WriteLine("🚀 HTTP/3 (QUIC) 已启用");
        }
    });
});

var app = builder.Build();

// 中间件
app.UseMiddleware<RequestLoggingMiddleware>();

// 路由
app.MapControllers();

// 根路径重定向到文件浏览器
app.MapGet("/", async context =>
{
    var statusService = context.RequestServices.GetRequiredService<IServerStatusService>();
    statusService.IncrementRequests();

    var status = statusService.GetStatus();

    var html = $@"
<!DOCTYPE html>
<html>
<head>
    <title>文件服务器 - 支持 QUIC</title>
    <meta charset='utf-8'>
    <style>
        body {{ font-family: Arial, sans-serif; margin: 40px; }}
        .container {{ max-width: 800px; margin: 0 auto; }}
        .header {{ text-align: center; margin-bottom: 30px; }}
        .status {{ background: #f5f5f5; padding: 15px; border-radius: 5px; margin-bottom: 20px; }}
        .links {{ margin-top: 20px; }}
        .link {{ display: block; padding: 10px; background: #007cba; color: white; text-decoration: none; margin: 5px 0; border-radius: 3px; text-align: center; }}
        .link:hover {{ background: #005a87; }}
        .protocols {{ display: flex; gap: 10px; margin-top: 10px; justify-content: center; }}
        .protocol {{ padding: 5px 10px; border-radius: 3px; font-size: 12px; }}
        .http {{ background: #4CAF50; color: white; }}
        .https {{ background: #2196F3; color: white; }}
        .quic {{ background: #FF9800; color: white; }}
        .cert-info {{ background: #e8f5e8; padding: 10px; border-radius: 5px; margin-bottom: 15px; border-left: 4px solid #4CAF50; }}
    </style>
</head>
<body>
    <div class='container'>
        <div class='header'>
            <h1>文件服务器 v3.0</h1>
            <p>支持 HTTP/1.1, HTTP/2 和 HTTP/3 (QUIC)</p>
            <div class='protocols'>
                <span class='protocol http'>HTTP: {status.HttpPort}</span>
                <span class='protocol https'>HTTPS: {status.HttpsPort}</span>
                <span class='protocol quic'>QUIC: {status.QuicPort}</span>
            </div>
        </div>
        
        {(certificate != null ? $@"
        <div class='cert-info'>
            <strong>🔒 HTTPS 证书:</strong> {certificate.Subject}
            <br><small>有效期: {certificate.NotBefore:yyyy-MM-dd} 至 {certificate.NotAfter:yyyy-MM-dd}</small>
        </div>" : "")}
        
        <div class='status'>
            <h3>服务器状态</h3>
            <p><strong>状态:</strong> <span style='color: green;'>运行中</span></p>
            <p><strong>活动连接:</strong> <span id='connections'>{status.ActiveConnections}</span></p>
            <p><strong>总请求:</strong> <span id='requests'>{status.TotalRequests}</span></p>
            <p><strong>运行时间:</strong> <span id='uptime'>{status.Uptime}</span> 秒</p>
            <p><strong>根目录:</strong> {status.RootPath}</p>
            <p><strong>QUIC 支持:</strong> {(status.QuicEnabled ? "✅ 已启用" : "❌ 未启用")}</p>
        </div>

        <div class='links'>
            <a href='/browser' class='link'>📁 文件浏览器</a>
            <a href='/player' class='link'>🎬 媒体播放器</a> 
            <a href='/api/fileserver/health' class='link'>❤️ 健康检查</a>
            <a href='/api/fileserver/status' class='link'>📊 服务器状态</a>
            <a href='https://localhost:{status.HttpsPort}' class='link'>🔒 HTTPS 访问</a>
            <a href='http://localhost:{status.HttpPort}' class='link'>🌐 HTTP 访问</a>
        </div>

        <div style='margin-top: 30px; padding: 15px; background: #f0f8ff; border-radius: 5px;'>
            <h4>访问地址:</h4>
            <ul>
                <li>HTTP: <code>http://localhost:{status.HttpPort}</code></li>
                <li>HTTPS: <code>https://localhost:{status.HttpsPort}</code></li>
                <li>内网访问: <code>https://{GetLocalIPAddress()}:{status.HttpsPort}</code></li>
            </ul>
        </div>
    </div>

    <script>
        async function updateStatus() {{
            try {{
                const response = await fetch('/api/fileserver/status');
                const data = await response.json();
                
                document.getElementById('connections').textContent = data.activeConnections;
                document.getElementById('requests').textContent = data.totalRequests;
                document.getElementById('uptime').textContent = data.uptime;
            }} catch (error) {{
                console.error('Failed to update status:', error);
            }}
        }}
        
        // 每 5 秒更新一次状态
        setInterval(updateStatus, 5000);
    </script>
</body>
</html>";

    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.WriteAsync(html);
});

// 文件浏览器页面
app.MapGet("/browser", async context =>
{
    var statusService = context.RequestServices.GetRequiredService<IServerStatusService>();
    statusService.IncrementRequests();

    var html = @"
<!DOCTYPE html>
<html>
<head>
    <title>文件浏览器 - 文件服务器</title>
    <meta charset='utf-8'>
    <style>
        body { font-family: Arial, sans-serif; margin: 20px; }
        .container { max-width: 1200px; margin: 0 auto; }
        .header { margin-bottom: 20px; }
        .breadcrumb { background: #f5f5f5; padding: 10px; border-radius: 5px; margin-bottom: 20px; }
        .breadcrumb a { color: #007cba; text-decoration: none; }
        .file-list { border: 1px solid #ddd; border-radius: 5px; }
        .file-header { background: #f9f9f9; padding: 10px; border-bottom: 1px solid #ddd; font-weight: bold; display: flex; }
        .file-item { padding: 10px; border-bottom: 1px solid #eee; display: flex; }
        .file-item:hover { background: #f5f5f5; }
        .file-name { flex: 3; }
        .file-size { flex: 1; }
        .file-date { flex: 2; }
        .file-actions { flex: 1; }
        .dir { color: #007cba; }
        .file { color: #333; }
        .back-link { margin-bottom: 20px; }
        .loading { text-align: center; padding: 20px; }
        .error { color: red; padding: 10px; background: #ffe6e6; border-radius: 5px; }
        .upload-section { margin: 20px 0; padding: 15px; background: #f0f8ff; border-radius: 5px; }
        .upload-btn { background: #28a745; color: white; padding: 10px 15px; border: none; border-radius: 3px; cursor: pointer; }
        .upload-btn:hover { background: #218838; }
    </style>
</head>
<body>
    <div class='container'>
        <div class='header'>
            <h1>文件浏览器</h1>
            <p><a href='/'>← 返回首页</a> | <a href='/player'>🎬 媒体播放器</a></p>
        </div>
        
        <div class='upload-section'>
            <h3>📤 上传文件</h3>
            <input type='file' id='fileInput' multiple>
            <button class='upload-btn' onclick='uploadFiles()'>上传文件</button>
            <div id='uploadStatus'></div>
        </div>
        
        <div class='breadcrumb' id='breadcrumb'>
            <a href='javascript:loadPath("""")'>根目录</a>
        </div>
        
        <div class='file-list'>
            <div class='file-header'>
                <div class='file-name'>名称</div>
                <div class='file-size'>大小</div>
                <div class='file-date'>修改时间</div>
                <div class='file-actions'>操作</div>
            </div>
            <div id='file-list-content'>
                <div class='loading'>加载中...</div>
            </div>
        </div>
    </div>

    <script>
        let currentPath = '';
        
        function loadPath(path) {
            currentPath = path;
            document.getElementById('file-list-content').innerHTML = '<div class=""loading"">加载中...</div>';
            
            fetch('/api/fileserver/list/' + path)
                .then(response => {
                    if (!response.ok) {
                        throw new Error('网络响应不正常');
                    }
                    return response.json();
                })
                .then(data => {
                    displayFileList(data);
                    updateBreadcrumb(data.currentPath, data.parentPath);
                })
                .catch(error => {
                    console.error('Error:', error);
                    document.getElementById('file-list-content').innerHTML = 
                        '<div class=""error"">加载失败: ' + error.message + '</div>';
                });
        }

        function displayFileList(data) {
            const fileList = document.getElementById('file-list-content');
            
            if (data.directories.length === 0 && data.files.length === 0) {
                fileList.innerHTML = '<div class=""file-item""><div class=""file-name"">空目录</div></div>';
                return;
            }
            
            let html = '';
            
            // 添加目录
            for (let i = 0; i < data.directories.length; i++) {
                const dir = data.directories[i];
                
                html += '<div class=""file-item dir"">';
                html += '<div class=""file-name"">';
                html += '<a href=""javascript:loadPath(\'' + dir.path + '\')"">📁 ' + dir.name + '</a>';
                html += '</div>';
                html += '<div class=""file-size"">-</div>';
                html += '<div class=""file-date"">-</div>';
                html += '<div class=""file-actions"">';
                html += '<a href=""javascript:loadPath(\'' + dir.path + '\')"">打开</a>';
                html += '</div>';
                html += '</div>';
            }
            
             // 添加文件 - 修改这部分
             for (let i = 0; i < data.files.length; i++) {
                 const file = data.files[i];
                 const fileExtension = file.name.split('.').pop().toLowerCase();
        
             // 根据文件类型确定图标和预览能力
                  const fileIcon = getFileIcon(fileExtension);
                 const canPreview = canFileBePreviewed(fileExtension);
        
                  html += '<div class=""file-item file"">';
                  html += '<div class=""file-name"">';
                  html += fileIcon + ' ' + file.name;  // 使用文件图标
                  html += '</div>';
                  html += '<div class=""file-size"">' + file.sizeFormatted + '</div>';
                  html += '<div class=""file-date"">' + new Date(file.lastModified).toLocaleString() + '</div>';
                  html += '<div class=""file-actions"">';
                  html += '<a href=""/api/fileserver/download/' + file.path + '"" target=""_blank"">下载</a>';
         
             // 为可预览的文件添加预览按钮
              if (canPreview) {
                 const previewType = getPreviewType(fileExtension);
                 html += ' | <a href=""/player?path=' + encodeURIComponent(file.path) + '&type=' + previewType + '"" target=""_blank"">预览</a>';
        }
        
        html += '</div>';
        html += '</div>';
    }
    
    fileList.innerHTML = html;
        }

        function updateBreadcrumb(currentPath, parentPath) {
            const breadcrumb = document.getElementById('breadcrumb');
            
            let html = '<a href=""javascript:loadPath(\'\')"">根目录</a>';
            
            if (currentPath) {
                const parts = currentPath.split('/');
                let accumulatedPath = '';
                
                for (let i = 0; i < parts.length; i++) {
                    const part = parts[i];
                    if (part) {
                        accumulatedPath += '/' + part;
                        html += ' / <a href=""javascript:loadPath(\'' + accumulatedPath + '\')"">' + part + '</a>';
                    }
                }
            }
            
            breadcrumb.innerHTML = html;
        }

        async function uploadFiles() {
            const fileInput = document.getElementById('fileInput');
            const uploadStatus = document.getElementById('uploadStatus');
            
            if (fileInput.files.length === 0) {
                uploadStatus.innerHTML = '<div class=""error"">请选择要上传的文件</div>';
                return;
            }

            const formData = new FormData();
            for (let i = 0; i < fileInput.files.length; i++) {
                formData.append('files', fileInput.files[i]);
            }

            uploadStatus.innerHTML = '<div class=""loading"">上传中...</div>';

            try {
                const response = await fetch('/api/fileserver/upload/' + currentPath, {
                    method: 'POST',
                    body: formData
                });

                const result = await response.json();
                
                if (result.success) {
                    uploadStatus.innerHTML = '<div style=""color: green;"">✅ ' + result.message + '</div>';
                    fileInput.value = ''; // 清空文件选择
                    loadPath(currentPath); // 刷新文件列表
                } else {
                    uploadStatus.innerHTML = '<div class=""error"">❌ ' + result.message + '</div>';
                }
            } catch (error) {
                uploadStatus.innerHTML = '<div class=""error"">❌ 上传失败: ' + error.message + '</div>';
            }
        }

                // 获取文件图标
    function getFileIcon(extension) {
        const icons = {
            'mp4': '🎬', 'avi': '🎬', 'mov': '🎬', 'mkv': '🎬', 'wmv': '🎬', 'flv': '🎬', 'webm': '🎬', 'm4v': '🎬',
            'mp3': '🎵', 'wav': '🎵', 'flac': '🎵', 'aac': '🎵', 'ogg': '🎵', 'm4a': '🎵', 'wma': '🎵',
            'jpg': '🖼️', 'jpeg': '🖼️', 'png': '🖼️', 'gif': '🖼️', 'bmp': '🖼️', 'webp': '🖼️',
            'txt': '📄', 'pdf': '📄', 'doc': '📄', 'docx': '📄',
            'zip': '📦', 'rar': '📦', '7z': '📦',
            'xml': '📋', 'json': '📋', 'csv': '📋', 'html': '🌐', 'htm': '🌐', 'css': '🎨', 'js': '⚡'
        };
        return icons[extension] || '📄';
    }

    // 判断文件是否可以预览
    function canFileBePreviewed(extension) {
        const previewableExtensions = [
            // 文本文件
            'txt', 'log', 'xml', 'json', 'csv', 'html', 'htm', 'css', 'js', 
            'md', 'cs', 'java', 'cpp', 'c', 'py', 'php', 'rb', 'config', 
            'yml', 'yaml', 'ini', 'sql',
            // 图片文件
            'jpg', 'jpeg', 'png', 'gif', 'bmp', 'webp',
            // 视频文件
            'mp4', 'avi', 'mov', 'mkv', 'wmv', 'flv', 'webm', 'm4v',
            // 音频文件
            'mp3', 'wav', 'flac', 'aac', 'ogg', 'm4a', 'wma'
        ];
        return previewableExtensions.includes(extension);
    }

    // 获取预览类型
    function getPreviewType(extension) {
        const textExtensions = ['txt', 'log', 'xml', 'json', 'csv', 'html', 'htm', 'css', 'js', 'md', 'cs', 'java', 'cpp', 'c', 'py', 'php', 'rb', 'config', 'yml', 'yaml', 'ini', 'sql'];
        const imageExtensions = ['jpg', 'jpeg', 'png', 'gif', 'bmp', 'webp'];
        const videoExtensions = ['mp4', 'avi', 'mov', 'mkv', 'wmv', 'flv', 'webm', 'm4v'];
        const audioExtensions = ['mp3', 'wav', 'flac', 'aac', 'ogg', 'm4a', 'wma'];
        
        if (textExtensions.includes(extension)) return 'text';
        if (imageExtensions.includes(extension)) return 'image';
        if (videoExtensions.includes(extension)) return 'video';
        if (audioExtensions.includes(extension)) return 'audio';
        return 'text'; // 默认作为文本处理
    }

        // 初始加载根目录
        loadPath('');
    </script>
</body>
</html>";

    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.WriteAsync(html);
});


// 播放器页面
app.MapGet("/player", async context =>
{
    var statusService = context.RequestServices.GetRequiredService<IServerStatusService>();
    statusService.IncrementRequests();

    var html = @"
<!DOCTYPE html>
<html>
<head>
    <title>媒体播放器 - 文件服务器</title>
    <meta charset='utf-8'>
    <style>
        body { font-family: Arial, sans-serif; margin: 0; padding: 20px; background: #1e1e1e; color: white; }
        .container { max-width: 1200px; margin: 0 auto; }
        .header { margin-bottom: 20px; text-align: center; }
        .back-btn { background: #007cba; color: white; padding: 10px 15px; border: none; border-radius: 5px; cursor: pointer; text-decoration: none; display: inline-block; }
        .back-btn:hover { background: #005a87; }
        .player-container { background: #2d2d2d; border-radius: 10px; padding: 20px; margin-bottom: 20px; }
        .video-player { width: 100%; max-width: 800px; margin: 0 auto; display: block; background: black; border-radius: 5px; }
        .audio-player { width: 100%; max-width: 600px; margin: 20px auto; }
        .file-info { background: #3d3d3d; padding: 15px; border-radius: 5px; margin-bottom: 20px; }
        .controls { margin-top: 20px; text-align: center; }
        .control-btn { background: #007cba; color: white; border: none; padding: 10px 15px; margin: 0 5px; border-radius: 5px; cursor: pointer; }
        .control-btn:hover { background: #005a87; }
        .text-preview { background: white; color: black; padding: 20px; border-radius: 5px; font-family: 'Courier New', monospace; white-space: pre-wrap; max-height: 600px; overflow-y: auto; }
        .image-preview { max-width: 100%; max-height: 600px; display: block; margin: 0 auto; border-radius: 5px; }
        .download-btn { background: #28a745; color: white; padding: 10px 20px; text-decoration: none; border-radius: 5px; display: inline-block; margin-top: 10px; }
        .download-btn:hover { background: #218838; }
        .error { color: #ff6b6b; text-align: center; padding: 20px; }
        .loading { text-align: center; padding: 40px; }
        .format-badge { 
            background: #6f42c1; 
            color: white; 
            padding: 2px 8px; 
            border-radius: 10px; 
            font-size: 12px; 
            margin-left: 10px;
        }
    </style>
</head>
<body>
    <div class='container'>
        <div class='header'>
            <a href='/browser' class='back-btn'>← 返回文件浏览器</a>
            <h1>🎬 媒体播放器</h1>
        </div>
        
        <div id='player-content'>
            <div class='loading'>加载中...</div>
        </div>
    </div>

    <script>
        // 获取URL参数
        function getUrlParams() {
            const params = new URLSearchParams(window.location.search);
            return {
                path: params.get('path'),
                type: params.get('type')
            };
        }

        // 加载文件内容
        async function loadFile() {
            const { path, type } = getUrlParams();
            
            if (!path) {
                showError('未指定文件路径');
                return;
            }

            try {
                if (type === 'text') {
                    await loadTextFile(path);
                } else if (type === 'image') {
                    loadImageFile(path);
                } else if (type === 'video') {
                    loadVideoFile(path);
                } else if (type === 'audio') {
                    loadAudioFile(path);
                } else {
                    // 自动检测文件类型
                    await detectAndLoadFileType(path);
                }
            } catch (error) {
                console.error('加载文件失败:', error);
                showError('加载文件失败: ' + error.message);
            }
        }

        // 自动检测文件类型并加载
        async function detectAndLoadFileType(path) {
            const extension = path.split('.').pop().toLowerCase();
            const textExtensions = ['txt', 'log', 'xml', 'json', 'csv', 'html', 'htm', 'css', 'js', 'md', 'cs', 'java', 'cpp', 'c', 'py', 'php', 'rb', 'config', 'yml', 'yaml', 'ini', 'sql'];
            const imageExtensions = ['jpg', 'jpeg', 'png', 'gif', 'bmp', 'webp'];
            const videoExtensions = ['mp4', 'avi', 'mov', 'mkv', 'wmv', 'flv', 'webm', 'm4v'];
            const audioExtensions = ['mp3', 'wav', 'flac', 'aac', 'ogg', 'm4a', 'wma'];

            if (textExtensions.includes(extension)) {
                await loadTextFile(path);
            } else if (imageExtensions.includes(extension)) {
                loadImageFile(path);
            } else if (videoExtensions.includes(extension)) {
                loadVideoFile(path);
            } else if (audioExtensions.includes(extension)) {
                loadAudioFile(path);
            } else {
                // 默认尝试作为文本文件处理
                await loadTextFile(path);
            }
        }

        // 获取视频内容类型
        function getVideoContentType(extension) {
            const types = {
                'mp4': 'video/mp4',
                'avi': 'video/x-msvideo',
                'mov': 'video/quicktime',
                'mkv': 'video/x-matroska',
                'wmv': 'video/x-ms-wmv',
                'webm': 'video/webm',
                'm4v': 'video/mp4'
            };
            return types[extension] || 'video/mp4';
        }

        // 获取音频内容类型
        function getAudioContentType(extension) {
            const types = {
                'mp3': 'audio/mpeg',
                'wav': 'audio/wav',
                'flac': 'audio/flac',
                'aac': 'audio/aac',
                'ogg': 'audio/ogg',
                'm4a': 'audio/mp4',
                'wma': 'audio/x-ms-wma'
            };
            return types[extension] || 'audio/mpeg';
        }

        // 修改文本文件加载函数
        async function loadTextFile(path) {
            try {
                const response = await fetch('/api/fileserver/preview/' + encodeURIComponent(path));
                
                if (!response.ok) {
                    // 如果预览失败，尝试直接下载文件内容
                    const downloadResponse = await fetch('/api/fileserver/download/' + encodeURIComponent(path));
                    if (!downloadResponse.ok) {
                        throw new Error('文件加载失败: ' + downloadResponse.statusText);
                    }
                    
                    const blob = await downloadResponse.blob();
                    const text = await blob.text();
                    
                    const content = `
                        <div class='file-info'>
                            <h3>📄 ${path.split('/').pop()} <span class='format-badge'>文本</span></h3>
                            <p>大小: ${formatFileSize(blob.size)} | 编码: utf-8</p>
                            <a href='/api/fileserver/download/${encodeURIComponent(path)}' class='download-btn'>📥 下载文件</a>
                        </div>
                        <div class='text-preview'>${escapeHtml(text)}</div>
                    `;
                    document.getElementById('player-content').innerHTML = content;
                    return;
                }
                
                const data = await response.json();
                
                if (data.type === 'text') {
                    const content = `
                        <div class='file-info'>
                            <h3>📄 ${data.fileName} <span class='format-badge'>文本</span></h3>
                            <p>大小: ${formatFileSize(data.size)} | 编码: ${data.encoding}</p>
                            <a href='/api/fileserver/download/${encodeURIComponent(path)}' class='download-btn'>📥 下载文件</a>
                        </div>
                        <div class='text-preview'>${escapeHtml(data.content)}</div>
                    `;
                    document.getElementById('player-content').innerHTML = content;
                } else {
                    showError('不支持的文本格式');
                }
            } catch (error) {
                console.error('加载文本文件失败:', error);
                showError('加载文本文件失败: ' + error.message);
            }
        }

       // 修改图片加载函数 - 使用预览端点
function loadImageFile(path) {
    const fileName = path.split('/').pop();
    const content = `
        <div class='file-info'>
            <h3>🖼️ ${fileName} <span class='format-badge'>图片</span></h3>
            <a href='/api/fileserver/download/${encodeURIComponent(path)}' class='download-btn'>📥 下载图片</a>
        </div>
        <div class='player-container'>
            <img src='/api/fileserver/preview/${encodeURIComponent(path)}' alt='${fileName}' class='image-preview' onerror='handleImageError(this, ""${fileName}"")'>
        </div>
    `;
    document.getElementById('player-content').innerHTML = content;
}

// 修改视频加载函数 - 使用预览端点
function loadVideoFile(path) {
    const fileName = path.split('/').pop();
    const extension = fileName.split('.').pop().toLowerCase();
    const contentType = getVideoContentType(extension);
    
    // 对于 MKV 文件，提供多个 source 选项
    let videoSources = '';
    if (extension === 'mkv') {
        videoSources = `
            <source src='/api/fileserver/preview/${encodeURIComponent(path)}' type='video/x-matroska'>
            <source src='/api/fileserver/preview/${encodeURIComponent(path)}' type='video/mp4'>
        `;
    } else {
        videoSources = `<source src='/api/fileserver/preview/${encodeURIComponent(path)}' type='${contentType}'>`;
    }
    
    const content = `
        <div class='file-info'>
            <h3>🎬 ${fileName} <span class='format-badge'>视频</span></h3>
            <p>格式: ${extension.toUpperCase()} | 支持范围请求和流式传输</p>
            <a href='/api/fileserver/download/${encodeURIComponent(path)}' class='download-btn'>📥 下载视频</a>
        </div>
        <div class='player-container'>
            <video class='video-player' controls autoplay playsinline preload=""auto"">
                ${videoSources}
                您的浏览器不支持此视频格式。
                ${extension === 'mkv' ? '<br><strong>MKV 提示:</strong> 请使用最新版 Chrome/Edge 浏览器，或安装 VLC 插件。' : ''}
            </video>
            <div class='controls'>
                <button class='control-btn' onclick='toggleFullscreen()'>全屏</button>
                <button class='control-btn' onclick='togglePictureInPicture()'>画中画</button>
                <button class='control-btn' onclick='toggleMute()'>静音</button>
                ${extension === 'mkv' ? '<button class=""control-btn"" onclick=""showMKVHelp()"">MKV 帮助</button>' : ''}
            </div>
        </div>
    `;
    document.getElementById('player-content').innerHTML = content;
}

// 修改音频加载函数 - 使用预览端点
function loadAudioFile(path) {
    const fileName = path.split('/').pop();
    const extension = fileName.split('.').pop().toLowerCase();
    const contentType = getAudioContentType(extension);
    
    const content = `
        <div class='file-info'>
            <h3>🎵 ${fileName} <span class='format-badge'>音频</span></h3>
            <p>格式: ${extension.toUpperCase()} | 支持范围请求和流式传输</p>
            <a href='/api/fileserver/download/${encodeURIComponent(path)}' class='download-btn'>📥 下载音频</a>
        </div>
        <div class='player-container'>
            <audio class='audio-player' controls autoplay preload=""auto"">
                <source src='/api/fileserver/preview/${encodeURIComponent(path)}' type='${contentType}'>
                您的浏览器不支持音频播放。
            </audio>
            <div class='controls'>
                <button class='control-btn' onclick='toggleAudioMute()'>静音</button>
            </div>
        </div>
    `;
    document.getElementById('player-content').innerHTML = content;
}

        // 视频静音切换
        function toggleMute() {
            const video = document.querySelector('video');
            if (video) {
                video.muted = !video.muted;
            }
        }

        // 音频静音切换
        function toggleAudioMute() {
            const audio = document.querySelector('audio');
            if (audio) {
                audio.muted = !audio.muted;
            }
        }

        // 显示错误信息
        function showError(message) {
            document.getElementById('player-content').innerHTML = `
                <div class='error'>
                    <h3>❌ 错误</h3>
                    <p>${message}</p>
                    <a href='/browser' class='back-btn'>返回文件浏览器</a>
                </div>
            `;
        }

        // 工具函数：格式化文件大小
        function formatFileSize(bytes) {
            if (bytes === 0) return '0 B';
            const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
            const i = Math.floor(Math.log(bytes) / Math.log(1024));
            return Math.round(bytes / Math.pow(1024, i) * 100) / 100 + ' ' + sizes[i];
        }

        // 工具函数：HTML转义
        function escapeHtml(text) {
            const div = document.createElement('div');
            div.textContent = text;
            return div.innerHTML;
        }

        // 全屏切换
        function toggleFullscreen() {
            const video = document.querySelector('video');
            if (!document.fullscreenElement) {
                if (video.requestFullscreen) {
                    video.requestFullscreen();
                } else if (video.webkitRequestFullscreen) {
                    video.webkitRequestFullscreen();
                } else if (video.msRequestFullscreen) {
                    video.msRequestFullscreen();
                }
            } else {
                if (document.exitFullscreen) {
                    document.exitFullscreen();
                } else if (document.webkitExitFullscreen) {
                    document.webkitExitFullscreen();
                } else if (document.msExitFullscreen) {
                    document.msExitFullscreen();
                }
            }
        }

        // 画中画模式
        function togglePictureInPicture() {
            const video = document.querySelector('video');
            if (document.pictureInPictureElement) {
                document.exitPictureInPicture();
            } else if (video && video.requestPictureInPicture) {
                video.requestPictureInPicture();
            }
        }

        // 页面加载完成后执行
        document.addEventListener('DOMContentLoaded', loadFile);
    </script>
</body>
</html>";

    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.WriteAsync(html);
});

// 健康检查端点
app.MapGet("/health", async context =>
{
    var statusService = context.RequestServices.GetRequiredService<IServerStatusService>();
    statusService.IncrementRequests();

    var status = statusService.GetStatus();
    var health = new HealthResponse
    {
        Status = status.IsRunning ? "healthy" : "unhealthy",
        Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        ActiveConnections = status.ActiveConnections,
        Uptime = status.Uptime
    };

    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(health));
});

Console.WriteLine("🚀 文件服务器启动成功!");
Console.WriteLine($"🌐 HTTP 访问: http://localhost:{builder.Configuration.GetValue<int>("FileServer:HttpPort", 8080)}");
Console.WriteLine($"🔒 HTTPS 访问: https://localhost:{builder.Configuration.GetValue<int>("FileServer:HttpsPort", 8081)}");
Console.WriteLine($"📡 内网访问: https://{GetLocalIPAddress()}:{builder.Configuration.GetValue<int>("FileServer:HttpsPort", 8081)}");

app.Run();

// 创建并信任开发者证书的方法
static X509Certificate2? CreateAndTrustDeveloperCertificate()
{
    try
    {
        // 检查是否已经存在开发证书
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);

        var certificates = store.Certificates.Find(
            X509FindType.FindBySubjectName,
            "localhost",
            false);

        // 如果已经存在有效的开发证书，直接返回
        var validCert = certificates.Cast<X509Certificate2>()
            .FirstOrDefault(c => c.NotBefore <= DateTime.Now && c.NotAfter >= DateTime.Now.AddDays(30));

        if (validCert != null)
        {
            return validCert;
        }

        Console.WriteLine("🔐 正在创建开发者证书...");

        // 使用 dotnet dev-certs 工具创建证书
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "dev-certs https --trust",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(processStartInfo);
        if (process != null)
        {
            process.WaitForExit();

            if (process.ExitCode == 0)
            {
                // 重新查找证书
                certificates = store.Certificates.Find(
                    X509FindType.FindBySubjectName,
                    "localhost",
                    false);

                validCert = certificates.Cast<X509Certificate2>()
                    .FirstOrDefault(c => c.NotBefore <= DateTime.Now && c.NotAfter >= DateTime.Now.AddDays(30));

                return validCert;
            }
            else
            {
                Console.WriteLine("⚠️ 无法自动创建开发者证书，请手动运行: dotnet dev-certs https --trust");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"⚠️ 创建开发者证书时出错: {ex.Message}");
        Console.WriteLine("💡 请手动运行: dotnet dev-certs https --trust");
    }

    return null;
}

// 获取本地 IP 地址的方法
static string GetLocalIPAddress()
{
    try
    {
        var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
        foreach (var ip in host.AddressList)
        {
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                return ip.ToString();
            }
        }
    }
    catch
    {
        // 忽略错误
    }

    return "localhost";
}