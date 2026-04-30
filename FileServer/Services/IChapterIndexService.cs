using FileServer.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FileServer.Services
{
    public interface IChapterIndexService
    {
        Task<ChapterIndex?> GetOrBuildChapterIndexAsync(string filePath, string content);
        Task<ChapterIndex> BuildChapterIndexAsync(string filePath, string content);
        Task SaveChapterIndexAsync(string filePath, ChapterIndex index);
        Task<bool> IsIndexValidAsync(string filePath, ChapterIndex index);
        string GetIndexFilePath(string filePath);
        bool DeleteChapterIndex(string filePath);
        List<string> GetAllIndexFilesInfo();
        Task<ChapterIndex> ForceRebuildChapterIndexAsync(string filePath, string content);
        string GetIndexFileInfo(string filePath);
        /// <summary>
        /// 尝试仅从缓存加载章节索引，文件未修改时返回有效索引，否则返回 null。
        /// 此方法不下载源文件，仅检查已存在的索引。
        /// </summary>
        Task<ChapterIndex?> GetCachedChapterIndexAsync(string filePath);
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

        /// <summary>
        /// 尝试仅从缓存加载有效索引，不下载源文件。
        /// </summary>
        public async Task<ChapterIndex?> GetCachedChapterIndexAsync(string filePath)
        {
            try
            {
                var index = await LoadChapterIndexAsync(filePath);
                if (index != null && await IsIndexValidAsync(filePath, index))
                {
                    _logger.LogDebug("使用缓存的章节索引: {FilePath}, 章节数: {ChapterCount}", filePath, index.TotalChapters);
                    return index;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取缓存章节索引失败: {FilePath}", filePath);
            }
            return null;
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

            // 步骤1：顺序解析，记录所有行内容和每行的起始字符偏移（绝对正确）
            var lines = new List<string>();
            var lineOffsets = new List<int>();
            int pos = 0;
            int contentLength = content.Length;
            while (pos < contentLength)
            {
                lineOffsets.Add(pos);
                int start = pos;
                while (pos < contentLength && content[pos] != '\r' && content[pos] != '\n')
                    pos++;
                lines.Add(content.Substring(start, pos - start));
                // 跳过换行符
                if (pos < contentLength && content[pos] == '\r') pos++;
                if (pos < contentLength && content[pos] == '\n') pos++;
            }

            var linesArray = lines.ToArray();
            var offsetsArray = lineOffsets.ToArray();
            var patterns = GetChapterPatterns();

            // 步骤2：并行检测章节行（只读，线程安全）
            var chapterBag = new System.Collections.Concurrent.ConcurrentBag<(int LineIndex, ChapterInfo Chapter)>();

            Parallel.For(0, linesArray.Length, i =>
            {
                string line = linesArray[i];
                if (IsChapterLine(line, patterns, i, linesArray))
                {
                    var chapter = new ChapterInfo
                    {
                        Title = line.Trim(),
                        StartCharOffset = offsetsArray[i],   // 关键：使用预存的正确偏移
                        Preview = GetPreviewText(linesArray, i, 2)
                    };
                    chapterBag.Add((i, chapter));
                }
            });

            // 步骤3：按原始行号排序，保持章节顺序
            var chapters = chapterBag.OrderBy(x => x.LineIndex).Select(x => x.Chapter).ToList();

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
            return index;
        }

        private bool IsChapterLine(string line, List<Regex> patterns, int lineNumber, string[] allLines)
        {
            // 1. 必须匹配章节模式
            if (!patterns.Any(p => p.IsMatch(line)))
                return false;

            // 2. 长度限制：章节标题通常不会太长
            if (line.Length > 100)  // 放宽长度限制
                return false;

            // 3. 排除明显的对话特征 - 改进判断逻辑
            if (ContainsStrongDialogueFeatures(line))
                return false;

            // 4. 检查上下文：章节标题通常出现在段落开始位置
            if (!IsAtParagraphStart(lineNumber, allLines))
                return false;

            // 5. 检查格式：应该是相对干净的标题格式
            if (!HasReasonableChapterFormat(line))
                return false;

            // 6. 额外的验证：确保不是普通的叙述性文字
            if (IsNormalNarrative(line, lineNumber, allLines))
                return false;

            return true;
        }

        private bool ContainsStrongDialogueFeatures(string content)
        {
            var trimmed = content.Trim();

            // 检查是否是完整的对话模式（包含成对的引号）
            if ((trimmed.Contains("「") && trimmed.Contains("」")) ||
                (trimmed.Contains("『") && trimmed.Contains("』")) ||
                (trimmed.Contains("\"") && trimmed.Count(c => c == '"') >= 2))
            {
                // 短对话可能是章节标题，长对话更可能是正文
                if (trimmed.Length > 30)
                {
                    _logger.LogDebug("排除长对话: {Content}", content);
                    return true;
                }
            }

            // 强对话特征：对话动词等（但排除出现在行首的情况）
            var strongDialogueIndicators = new[] {
                "问道", "说道", "回答", "心想", "问", "答",
                "说", "喊", "叫", "笑道", "冷笑道", "怒道", "喝道",
                "大声", "小声", "轻声", "喃喃", "嘀咕"
            };

            foreach (var indicator in strongDialogueIndicators)
            {
                if (content.Contains(indicator))
                {
                    // 如果 indicator 出现在行首，可能是章节标题的一部分
                    if (content.Trim().StartsWith(indicator))
                        continue;

                    // 如果 indicator 后面跟着较长的文字，更可能是叙述
                    var index = content.IndexOf(indicator);
                    if (index >= 0 && index + indicator.Length < content.Length - 3)
                    {
                        _logger.LogDebug("排除对话特征: {Content} - {Indicator}", content, indicator);
                        return true;
                    }
                }
            }

            return false;
        }

        private bool IsNormalNarrative(string line, int lineNumber, string[] allLines)
        {
            var trimmed = line.Trim();

            // 检查是否是"一回"开头的叙述性文字
            if (trimmed.StartsWith("一回"))
            {
                var after = trimmed.Substring(2).Trim();

                // 如果"一回"后面跟着动词或地点描述，很可能是叙述
                var narrativeIndicators = new[] {
                    "到", "在", "就", "看到", "遇到", "发现", "开始",
                    "来到", "进入", "走到", "见到", "想起", "发现"
                };

                if (narrativeIndicators.Any(indicator => after.StartsWith(indicator)))
                {
                    _logger.LogDebug("排除叙述性文字: {Line}", line);
                    return true;
                }

                // 检查上下文：如果是叙述，通常后面会接着更多的文字
                if (lineNumber + 1 < allLines.Length)
                {
                    var nextLine = allLines[lineNumber + 1].Trim();
                    if (!string.IsNullOrEmpty(nextLine) && nextLine.Length > 10)
                    {
                        // 如果下一行是正常的叙述文字，当前行很可能是叙述
                        if (!IsSectionSeparator(nextLine) && !nextLine.StartsWith("第") && nextLine.Length > 20)
                        {
                            _logger.LogDebug("排除连续叙述文字: {Line}", line);
                            return true;
                        }
                    }
                }
            }

            // 检查是否包含时间描述（如"第二天"、"过了一会儿"）
            var timeIndicators = new[] { "第二天", "过了一会儿", "片刻之后", "突然", "这时", "然后", "接着", "随后" };
            if (timeIndicators.Any(indicator => trimmed.Contains(indicator)))
            {
                // 只有当时间描述出现在行首附近时才排除
                if (timeIndicators.Any(indicator => trimmed.StartsWith(indicator) || trimmed.IndexOf(indicator) < 5))
                {
                    _logger.LogDebug("排除时间描述: {Line}", line);
                    return true;
                }
            }

            // 如果看起来像普通的句子（包含多个标点符号）
            if (trimmed.Contains("，") && trimmed.Contains("。"))
            {
                _logger.LogDebug("排除普通句子: {Line}", line);
                return true;
            }

            return false;
        }

        private bool IsAtParagraphStart(int lineNumber, string[] allLines)
        {
            // 章节标题通常出现在：
            // 1. 文件开头
            if (lineNumber == 0)
                return true;

            // 2. 前面有空行
            if (lineNumber >= 1)
            {
                var prevLine = allLines[lineNumber - 1].Trim();
                if (string.IsNullOrEmpty(prevLine))
                    return true;
            }

            // 3. 或者前面几行都是空行
            if (lineNumber >= 2)
            {
                var prevLine1 = allLines[lineNumber - 1].Trim();
                var prevLine2 = allLines[lineNumber - 2].Trim();
                if (string.IsNullOrEmpty(prevLine1) && string.IsNullOrEmpty(prevLine2))
                    return true;
            }

            // 4. 或者前面有章节结束的标记
            if (lineNumber >= 1)
            {
                var prevLine = allLines[lineNumber - 1].Trim();
                if (IsSectionSeparator(prevLine))
                    return true;
            }

            // 5. 放宽条件：允许前面有较短的文本行
            if (lineNumber >= 1)
            {
                var prevLine = allLines[lineNumber - 1].Trim();
                if (prevLine.Length <= 60 && !prevLine.EndsWith("。") && !prevLine.EndsWith("！") && !prevLine.EndsWith("？"))
                    return true;
            }

            return false;
        }

        private bool IsSectionSeparator(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;

            // 常见章节分隔符
            var separators = new[] { "***", "---", "###", "===", "※※※", "○○○", "§§§" };
            if (separators.Any(sep => line.StartsWith(sep) && line.EndsWith(sep)))
                return true;

            // 检查是否主要是特殊字符
            if (line.All(c => "*-=#○●※§~".Contains(c)) && line.Length >= 3)
                return true;

            return false;
        }

        private bool HasReasonableChapterFormat(string line)
        {
            // 章节标题应该相对干净，但允许一些常见的格式
            var trimmed = line.Trim();

            // 不允许以句号、逗号等正文标点开头
            var invalidStartChars = new[] { '，', '。', '！', '？', '；', '、' };
            if (invalidStartChars.Contains(trimmed[0]))
                return false;

            // 不允许包含多个连续的特殊字符（除了章节标记）
            if (Regex.IsMatch(trimmed, @"[.\-\\\*#]{3,}"))
                return false;

            return true;
        }

        private List<Regex> GetChapterPatterns()
        {
            // 中文数字 + 阿拉伯数字
            var chineseNum = @"[零一二三四五六七八九十百千]+";
            var arabicNum = @"\d+";
            var numberPattern = $"(?:{chineseNum}|{arabicNum})";

            // 章节关键词
            var keywordPattern = @"[章节回卷幕]";

            // 任意空白字符（包括空格、制表符、全角空格等），0次或多次
            var anyWhitespace = @"[\s\u3000]*";

            // 冒号（全角或半角），前后可带任意空白
            var optionalColon = $@"{anyWhitespace}[:：]?{anyWhitespace}";

            // 标题部分：允许任意非换行内容（非贪婪）
            var optionalTitle = @".*?";

            var patterns = new List<Regex>
            {
                // 1. 标准格式：第X章[任意空白][冒号(可选)][任意空白][标题]
                new Regex($@"^第{numberPattern}{keywordPattern}{anyWhitespace}{optionalTitle}$", RegexOptions.Compiled),

                // 2. 特殊章节（无数字）
                new Regex(@"^(序章|前言|后记|楔子|尾声|结局|完结|终章|附录|外传)$", RegexOptions.Compiled),
                new Regex($@"^(序章|前言|后记|楔子|尾声|结局|完结|终章|附录|外传){anyWhitespace}{optionalTitle}$", RegexOptions.Compiled),

                // 3. 带"正文"前缀
                new Regex($@"^正文{anyWhitespace}第{numberPattern}{keywordPattern}{anyWhitespace}{optionalTitle}$", RegexOptions.Compiled),

                // 4. 符号包裹格式（###、***、---、===），符号内外允许任意空白
                new Regex($@"^#+{anyWhitespace}第{numberPattern}{keywordPattern}{anyWhitespace}{optionalTitle}{anyWhitespace}#+$", RegexOptions.Compiled),
                new Regex($@"^\*+{anyWhitespace}第{numberPattern}{keywordPattern}{anyWhitespace}{optionalTitle}{anyWhitespace}\*+$", RegexOptions.Compiled),
                new Regex($@"^-+{anyWhitespace}第{numberPattern}{keywordPattern}{anyWhitespace}{optionalTitle}{anyWhitespace}-+$", RegexOptions.Compiled),
                new Regex($@"^=+{anyWhitespace}第{numberPattern}{keywordPattern}{anyWhitespace}{optionalTitle}{anyWhitespace}=+$", RegexOptions.Compiled),

                // 5. 支持纯符号包裹的无"第"字章节
                new Regex($@"^#+{anyWhitespace}{keywordPattern}{anyWhitespace}{optionalTitle}{anyWhitespace}#+$", RegexOptions.Compiled),
                new Regex($@"^\*+{anyWhitespace}{keywordPattern}{anyWhitespace}{optionalTitle}{anyWhitespace}\*+$", RegexOptions.Compiled),
                new Regex($@"^-+{anyWhitespace}{keywordPattern}{anyWhitespace}{optionalTitle}{anyWhitespace}-+$", RegexOptions.Compiled),
                new Regex($@"^=+{anyWhitespace}{keywordPattern}{anyWhitespace}{optionalTitle}{anyWhitespace}=+$", RegexOptions.Compiled),

                // 6. 英文章节
                new Regex($@"^Chapter{anyWhitespace}{arabicNum}{anyWhitespace}{optionalTitle}$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                new Regex($@"^Part{anyWhitespace}{arabicNum}{anyWhitespace}{optionalTitle}$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                new Regex($@"^Section{anyWhitespace}{arabicNum}{anyWhitespace}{optionalTitle}$", RegexOptions.IgnoreCase | RegexOptions.Compiled),

                // 7. 中文数字章节（无"第"字）
                new Regex($@"^{chineseNum}{keywordPattern}{anyWhitespace}{optionalTitle}$", RegexOptions.Compiled),
            
                // 8. 纯数字章节
                new Regex($@"^{arabicNum}{anyWhitespace}{keywordPattern}{anyWhitespace}{optionalTitle}$", RegexOptions.Compiled),
                new Regex($@"^{arabicNum}{anyWhitespace}\.{anyWhitespace}{optionalTitle}$", RegexOptions.Compiled)
            };

            return patterns;
        }

        private string GetPreviewText(string[] lines, int currentIndex, int linesCount)
        {
            var previewLines = new List<string>();
            for (int i = currentIndex + 1; i <= currentIndex + linesCount && i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (!string.IsNullOrEmpty(line) && line.Length > 10)
                {
                    previewLines.Add(line.Length > 50 ? line.Substring(0, 50) + "..." : line);
                }
            }
            return string.Join(" ", previewLines);
        }

        private int CalculatePageFromLineNumber(int lineNumber)
        {
            return (lineNumber / 1000) + 1;
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
            using var md5 = System.Security.Cryptography.MD5.Create();
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(filePath));
            var hashString = BitConverter.ToString(hash).Replace("-", "").ToLower();

            return Path.Combine(_indexBasePath, $"{hashString}.json");
        }

        public bool DeleteChapterIndex(string filePath)
        {
            try
            {
                var indexFile = GetIndexFilePath(filePath);
                if (System.IO.File.Exists(indexFile))
                {
                    System.IO.File.Delete(indexFile);
                    _logger.LogInformation("删除章节索引文件: {IndexFile}", indexFile);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除章节索引文件失败: {FilePath}", filePath);
                return false;
            }
        }

        public List<string> GetAllIndexFilesInfo()
        {
            var infos = new List<string>();

            if (Directory.Exists(_indexBasePath))
            {
                var files = Directory.GetFiles(_indexBasePath, "*.json");
                foreach (var file in files)
                {
                    try
                    {
                        var content = System.IO.File.ReadAllText(file);
                        var index = System.Text.Json.JsonSerializer.Deserialize<ChapterIndex>(content);
                        var fileInfo = new System.IO.FileInfo(file);

                        infos.Add($"文件: {file}\n" +
                                 $"原文件: {index?.FilePath}\n" +
                                 $"章节数: {index?.TotalChapters}\n" +
                                 $"大小: {fileInfo.Length} bytes\n" +
                                 $"创建: {fileInfo.CreationTime}\n" +
                                 "---");
                    }
                    catch (Exception ex)
                    {
                        infos.Add($"无效索引文件: {file} - {ex.Message}");
                    }
                }
            }

            return infos;
        }

        public async Task<ChapterIndex> ForceRebuildChapterIndexAsync(string filePath, string content)
        {
            DeleteChapterIndex(filePath);
            return await BuildChapterIndexAsync(filePath, content);
        }

        public string GetIndexFileInfo(string filePath)
        {
            var indexFile = GetIndexFilePath(filePath);
            var exists = System.IO.File.Exists(indexFile);

            if (exists)
            {
                try
                {
                    var fileInfo = new System.IO.FileInfo(indexFile);
                    var content = System.IO.File.ReadAllText(indexFile);
                    var index = System.Text.Json.JsonSerializer.Deserialize<ChapterIndex>(content);

                    return $"索引文件: {indexFile}\n" +
                           $"文件大小: {fileInfo.Length} bytes\n" +
                           $"创建时间: {fileInfo.CreationTime}\n" +
                           $"章节数量: {index?.TotalChapters ?? 0}\n" +
                           $"索引时间: {index?.IndexTime}";
                }
                catch (Exception ex)
                {
                    return $"读取索引文件失败: {indexFile} - {ex.Message}";
                }
            }
            else
            {
                return $"索引文件不存在: {indexFile}";
            }
        }
    }
}