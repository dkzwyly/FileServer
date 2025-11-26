// 在 Services 文件夹下添加 IChapterIndexService.cs
using FileServer.Models;

namespace FileServer.Services
{
    public interface IChapterIndexService
    {
        Task<ChapterIndex?> GetOrBuildChapterIndexAsync(string filePath, string content);
        Task<ChapterIndex> BuildChapterIndexAsync(string filePath, string content);
        Task SaveChapterIndexAsync(string filePath, ChapterIndex index);
        Task<bool> IsIndexValidAsync(string filePath, ChapterIndex index);
        string GetIndexFilePath(string filePath);
    }

    public class ChapterIndexService : IChapterIndexService
    {
        private readonly IFileService _fileService;
        private readonly ILogger<ChapterIndexService> _logger;
        private readonly string _indexBasePath;

        public ChapterIndexService(IFileService fileService, ILogger<ChapterIndexService> logger, IConfiguration configuration)
        {
            _fileService = fileService;
            _logger = logger;
            _indexBasePath = Path.Combine(Environment.CurrentDirectory, "ChapterIndexes");

            if (!Directory.Exists(_indexBasePath))
            {
                Directory.CreateDirectory(_indexBasePath);
                _logger.LogInformation("创建章节索引目录: {Path}", _indexBasePath);
            }
        }

        public async Task<ChapterIndex?> GetOrBuildChapterIndexAsync(string filePath, string content)
        {
            try
            {
                // 先尝试加载现有索引
                var index = await LoadChapterIndexAsync(filePath);
                if (index != null && await IsIndexValidAsync(filePath, index))
                {
                    _logger.LogDebug("使用现有章节索引: {FilePath}, 章节数: {ChapterCount}", filePath, index.TotalChapters);
                    return index;
                }

                // 索引不存在或已过期，构建新索引
                _logger.LogInformation("构建新章节索引: {FilePath}", filePath);
                return await BuildChapterIndexAsync(filePath, content);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取或构建章节索引失败: {FilePath}", filePath);
                return null;
            }
        }

        public async Task<ChapterIndex> BuildChapterIndexAsync(string filePath, string content)
        {
            var fileInfo = await _fileService.GetFileInfoAsync(filePath);
            var lines = content.Split('\n');
            var chapters = DetectChapters(lines);

            var index = new ChapterIndex
            {
                FilePath = filePath,
                FileSize = fileInfo.Size,
                FileLastModified = fileInfo.LastModified,
                IndexTime = DateTime.Now,
                TotalChapters = chapters.Count,
                Chapters = chapters
            };

            await SaveChapterIndexAsync(filePath, index);
            _logger.LogInformation("章节索引构建完成: {FilePath}, 章节数: {ChapterCount}", filePath, chapters.Count);

            return index;
        }

        private List<ChapterInfo> DetectChapters(string[] lines)
        {
            var chapters = new List<ChapterInfo>();
            var chapterPatterns = GetChapterPatterns();

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrEmpty(line) || line.Length < 2) continue;

                // 跳过包含句号的行（可能是正文）
                if (line.Contains('。')) continue;

                if (IsChapterLine(line, chapterPatterns))
                {
                    var chapter = new ChapterInfo
                    {
                        Title = ExtractCleanChapterTitle(line),
                        Page = CalculatePageFromLineNumber(i), // 基于固定pageSize=1000
                        LineNumber = i,
                        Preview = GetPreviewText(lines, i, 2) // 取2行预览
                    };

                    // 避免重复章节（相邻行）
                    if (!chapters.Any() || i - chapters.Last().LineNumber > 5)
                    {
                        chapters.Add(chapter);
                    }
                }
            }

            return chapters;
        }

        private int CalculatePageFromLineNumber(int lineNumber)
        {
            // 基于服务端固定pageSize=1000计算页码
            return (lineNumber / 1000) + 1;
        }

        private bool IsChapterLine(string line, List<System.Text.RegularExpressions.Regex> patterns)
        {
            // 检查正则匹配
            if (patterns.Any(p => p.IsMatch(line)))
                return true;

            // 检查符号包裹的章节
            if (HasSymbolWrapping(line))
            {
                var cleanContent = ExtractContentFromWrapping(line);
                return patterns.Any(p => p.IsMatch(cleanContent)) ||
                       ContainsChapterKeywords(cleanContent);
            }

            return false;
        }

        private bool HasSymbolWrapping(string line)
        {
            var wrappingPatterns = new[] { "###", "***", "---", "===", "《", "【", "[", "#" };
            return wrappingPatterns.Any(pattern =>
                line.StartsWith(pattern) || line.EndsWith(pattern));
        }

        private string ExtractContentFromWrapping(string line)
        {
            var content = line.Trim();
            var wrappingChars = new[] { '#', '*', '-', '=', '《', '》', '【', '】', '[', ']' };

            foreach (var ch in wrappingChars)
            {
                content = content.Trim(ch);
            }

            return content.Trim();
        }

        private string ExtractCleanChapterTitle(string line)
        {
            var title = ExtractContentFromWrapping(line);
            return title.Trim();
        }

        private bool ContainsChapterKeywords(string content)
        {
            var keywords = new[] { "章", "节", "回", "卷", "集", "部", "篇", "Chapter", "Section", "Part" };
            return keywords.Any(keyword => content.Contains(keyword));
        }

        private string GetPreviewText(string[] lines, int currentIndex, int linesCount)
        {
            var previewLines = new List<string>();
            for (int i = currentIndex + 1; i <= currentIndex + linesCount && i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (!string.IsNullOrEmpty(line) && line.Length > 10) // 只取有内容的行
                {
                    previewLines.Add(line.Length > 50 ? line.Substring(0, 50) + "..." : line);
                }
            }
            return string.Join(" ", previewLines);
        }

        private List<System.Text.RegularExpressions.Regex> GetChapterPatterns()
        {
            return new List<System.Text.RegularExpressions.Regex>
            {
                new System.Text.RegularExpressions.Regex("^第[零一二三四五六七八九十百千]+章[^。]*$"),
                new System.Text.RegularExpressions.Regex("^第[零一二三四五六七八九十百千]+节[^。]*$"),
                new System.Text.RegularExpressions.Regex("^第[零一二三四五六七八九十百千]+回[^。]*$"),
                new System.Text.RegularExpressions.Regex("^第\\d+章[^。]*$"),
                new System.Text.RegularExpressions.Regex("^第\\d+节[^。]*$"),
                new System.Text.RegularExpressions.Regex("^第\\d+回[^。]*$"),
                new System.Text.RegularExpressions.Regex("^\\d+\\.[^。]*$"),
                new System.Text.RegularExpressions.Regex("^[一二三四五六七八九十百千]+、[^。]*$"),
                new System.Text.RegularExpressions.Regex("^[(（][一二三四五六七八九十百千]+[)）][^。]*$"),
                new System.Text.RegularExpressions.Regex("^[(（]\\d+[)）][^。]*$"),
                new System.Text.RegularExpressions.Regex("^[Cc]hapter\\s+\\d+[^。]*$"),
                new System.Text.RegularExpressions.Regex("^[Ss]ection\\s+\\d+[^。]*$")
            };
        }

        private async Task<ChapterIndex?> LoadChapterIndexAsync(string filePath)
        {
            var indexFile = GetIndexFilePath(filePath);
            if (!System.IO.File.Exists(indexFile))
                return null;

            try
            {
                var json = await System.IO.File.ReadAllTextAsync(indexFile);
                return System.Text.Json.JsonSerializer.Deserialize<ChapterIndex>(json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "加载章节索引文件失败: {IndexFile}", indexFile);
                return null;
            }
        }

        public async Task SaveChapterIndexAsync(string filePath, ChapterIndex index)
        {
            try
            {
                var indexFile = GetIndexFilePath(filePath);
                var json = System.Text.Json.JsonSerializer.Serialize(index, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                });
                await System.IO.File.WriteAllTextAsync(indexFile, json);

                _logger.LogDebug("章节索引保存成功: {IndexFile}", indexFile);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存章节索引失败: {FilePath}", filePath);
            }
        }

        public async Task<bool> IsIndexValidAsync(string filePath, ChapterIndex index)
        {
            try
            {
                var fileInfo = await _fileService.GetFileInfoAsync(filePath);
                return fileInfo.Size == index.FileSize &&
                       fileInfo.LastModified <= index.IndexTime;
            }
            catch
            {
                return false;
            }
        }

        public string GetIndexFilePath(string filePath)
        {
            // 使用文件路径的MD5作为索引文件名，避免路径过长问题
            using var md5 = System.Security.Cryptography.MD5.Create();
            var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(filePath));
            var hashString = BitConverter.ToString(hash).Replace("-", "").ToLower();

            return Path.Combine(_indexBasePath, $"{hashString}.json");
        }
    }
}