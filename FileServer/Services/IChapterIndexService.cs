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

            _logger.LogInformation("开始构建章节索引: {FilePath}, 总行数: {LineCount}", filePath, lines.Length);

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

            // 记录检测统计
            var chapterTitles = string.Join(", ", chapters.Take(10).Select(c => $"\"{c.Title}\""));
            _logger.LogInformation("章节索引构建完成: {FilePath}, 章节数: {ChapterCount}", filePath, chapters.Count);
            if (chapters.Count > 10)
            {
                _logger.LogDebug("前10个章节: {ChapterTitles}...", chapterTitles);
            }
            else
            {
                _logger.LogDebug("检测到的章节: {ChapterTitles}", chapterTitles);
            }

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

                // 使用更严格的章节检测
                if (IsChapterLine(line, chapterPatterns, i, lines))
                {
                    var chapter = new ChapterInfo
                    {
                        Title = ExtractCleanChapterTitle(line),
                        Page = CalculatePageFromLineNumber(i),
                        LineNumber = i,
                        Preview = GetPreviewText(lines, i, 2)
                    };

                    // 更严格的重复检测：至少相隔20行
                    if (!chapters.Any() || i - chapters.Last().LineNumber > 20)
                    {
                        chapters.Add(chapter);
                        _logger.LogDebug("检测到章节: {Title}, 行号: {LineNumber}", chapter.Title, i);
                    }
                    else
                    {
                        _logger.LogDebug("跳过密集章节: {Title}, 行号: {LineNumber}", chapter.Title, i);
                    }
                }
            }

            // 后处理：过滤掉明显不是章节的条目
            return FilterFalsePositives(chapters, lines);
        }

        private bool IsChapterLine(string line, List<Regex> patterns, int lineNumber, string[] allLines)
        {
            // 1. 首先检查正则匹配
            if (patterns.Any(p => p.IsMatch(line)))
            {
                // 如果是正则匹配的，再做一些额外检查
                if (!IsFalsePositive(line, lineNumber, allLines))
                {
                    return true;
                }
            }

            // 2. 检查符号包裹的章节
            if (HasSymbolWrapping(line))
            {
                var cleanContent = ExtractContentFromWrapping(line);
                if (patterns.Any(p => p.IsMatch(cleanContent)) || ContainsChapterKeywords(cleanContent))
                {
                    if (!IsFalsePositive(cleanContent, lineNumber, allLines))
                    {
                        return true;
                    }
                }
            }

            // 3. 更严格的复杂章节检测
            if (IsComplexChapterLine(line, lineNumber, allLines))
            {
                return true;
            }

            return false;
        }

        private bool IsComplexChapterLine(string line, int lineNumber, string[] allLines)
        {
            var cleanLine = ExtractContentFromWrapping(line);

            // 条件1: 必须包含章节关键词
            if (!ContainsChapterKeywords(cleanLine))
                return false;

            // 条件2: 长度限制（章节标题通常不会太长）
            if (cleanLine.Length > 30 && !HasSymbolWrapping(line))
                return false;

            // 条件3: 排除包含对话特征的行
            if (ContainsDialogueFeatures(cleanLine))
                return false;

            // 条件4: 排除包含常见非章节词汇的行
            if (ContainsExcludedWords(cleanLine))
                return false;

            // 条件5: 检查章节数字模式
            if (HasChapterNumberPattern(cleanLine))
                return true;

            // 条件6: 检查章节结构模式
            if (HasChapterStructurePattern(cleanLine))
                return true;

            return false;
        }

        private bool IsFalsePositive(string line, int lineNumber, string[] allLines)
        {
            // 检查是否是误判的章节行

            // 1. 检查是否包含明显的正文特征
            if (ContainsDialogueFeatures(line) || ContainsExcludedWords(line))
                return true;

            // 2. 检查上下文（章节标题通常不会在连续的行中出现）
            if (lineNumber > 0)
            {
                var prevLine = allLines[lineNumber - 1].Trim();
                if (IsPotentialChapterLine(prevLine) && lineNumber - 1 > 0)
                {
                    var prevPrevLine = allLines[lineNumber - 2].Trim();
                    if (IsPotentialChapterLine(prevPrevLine))
                    {
                        // 连续三行都像章节，很可能是误判
                        return true;
                    }
                }
            }

            // 3. 检查行长度和内容特征
            if (line.Length > 50 && !HasSymbolWrapping(line))
            {
                // 长行且没有符号包裹，很可能是正文
                return true;
            }

            return false;
        }

        private bool IsPotentialChapterLine(string line)
        {
            // 快速检查是否可能是章节行（不进行完整检测）
            if (string.IsNullOrEmpty(line) || line.Length < 2) return false;
            if (line.Contains('。')) return false;

            var cleanLine = ExtractContentFromWrapping(line);
            return ContainsChapterKeywords(cleanLine) &&
                   !ContainsDialogueFeatures(cleanLine) &&
                   !ContainsExcludedWords(cleanLine);
        }

        private bool ContainsDialogueFeatures(string content)
        {
            // 对话特征：包含冒号、问号、引号等
            var dialogueIndicators = new[] {
                "：", ":", "问道", "说道", "回答", "心想", "看着", "说道", "问", "答",
                "说", "喊", "叫", "嘟囔", "嘀咕", "笑道", "冷笑道", "怒道", "喝道",
                "解释说", "回答说", "追问道", "反问道", "开口道", "接着说", "继续说",
                "突然说", "轻声说", "大声说", "小声说", "喃喃", "自言自语", "抱怨"
            };

            // 如果包含对话特征，很可能是正文
            if (dialogueIndicators.Any(indicator => content.Contains(indicator)))
                return true;

            // 检查引号模式（对话通常有引号）
            if (content.Contains("「") || content.Contains("」") ||
                content.Contains("『") || content.Contains("』") ||
                content.Contains("\"") || content.Contains("'"))
                return true;

            return false;
        }

        private bool ContainsExcludedWords(string content)
        {
            // 明显不是章节的词汇
            var excludeWords = new[] {
                "不知道怎么", "犹豫", "一下", "然后", "但是", "因为", "所以",
                "突然", "不过", "而且", "然而", "接着", "然后", "之后",
                "之前", "这时", "那时", "此刻", "顿时", "瞬间", "转眼",
                "只见", "看来", "似乎", "好像", "仿佛", "大概", "可能",
                "应该", "当然", "其实", "确实", "果然", "当然", "不过",
                "只是", "可是", "但是", "然而", "尽管", "虽然", "如果",
                "假如", "除非", "无论", "不管", "因为", "所以", "因此",
                "于是", "然后", "接着", "之后", "同时", "另外", "此外",
                "李梦舟", "不知道怎么回答", "犹豫了一下", "问道", "回答道",
                "解释说", "心想", "看着", "说道", "笑着说", "冷笑道"
            };

            return excludeWords.Any(word => content.Contains(word));
        }

        private bool HasChapterNumberPattern(string content)
        {
            // 检查是否包含明确的章节数字模式
            var numberPatterns = new[]
            {
                new Regex(@"第[零一二三四五六七八九十百千]+[章节回部卷集]"),
                new Regex(@"第\d+[章节回部卷集]"),
                new Regex(@"^[零一二三四五六七八九十百千]+[、\.]"),
                new Regex(@"^\d+[、\.]"),
                new Regex(@"[零一二三四五六七八九十百千]+[章节回部卷集]"),
                new Regex(@"\d+[章节回部卷集]")
            };

            return numberPatterns.Any(pattern => pattern.IsMatch(content));
        }

        private bool HasChapterStructurePattern(string content)
        {
            // 检查章节结构模式
            var structurePatterns = new[]
            {
                // 卷+章 结构
                new Regex(@".*卷.*章.*"),
                // 部+章 结构
                new Regex(@".*部.*章.*"),
                // 集+章 结构
                new Regex(@".*集.*章.*"),
                // 篇+章 结构
                new Regex(@".*篇.*章.*")
            };

            return structurePatterns.Any(pattern => pattern.IsMatch(content));
        }

        private bool HasSymbolWrapping(string line)
        {
            var wrappingPatterns = new[] { "###", "***", "---", "===", "《", "【", "[", "#", "〖", "〗", "〈", "〉" };
            return wrappingPatterns.Any(pattern =>
                line.StartsWith(pattern) || line.EndsWith(pattern));
        }

        private string ExtractContentFromWrapping(string line)
        {
            var content = line.Trim();
            var wrappingChars = new[] { '#', '*', '-', '=', '《', '》', '【', '】', '[', ']', '〖', '〗', '〈', '〉' };

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
            var keywords = new[] {
                "章", "节", "回", "卷", "集", "部", "篇",
                "Chapter", "Section", "Part", "Book", "Volume",
                "序章", "前言", "后记", "楔子", "尾声", "结局"
            };
            return keywords.Any(keyword => content.Contains(keyword));
        }

        private List<ChapterInfo> FilterFalsePositives(List<ChapterInfo> chapters, string[] lines)
        {
            if (chapters.Count < 2)
                return chapters;

            var filtered = new List<ChapterInfo>();
            filtered.Add(chapters[0]);

            for (int i = 1; i < chapters.Count; i++)
            {
                var current = chapters[i];
                var previous = filtered.Last();

                // 检查章节标题的质量
                if (IsHighQualityChapter(current, lines))
                {
                    // 检查行间距（避免密集检测）
                    if (current.LineNumber - previous.LineNumber > 15)
                    {
                        filtered.Add(current);
                    }
                    else
                    {
                        _logger.LogDebug("过滤密集章节: {Title} (行号: {LineNumber})", current.Title, current.LineNumber);
                    }
                }
                else
                {
                    _logger.LogDebug("过滤低质量章节: {Title} (行号: {LineNumber})", current.Title, current.LineNumber);
                }
            }

            _logger.LogInformation("后处理过滤完成: 原始 {OriginalCount} -> 过滤后 {FilteredCount}", chapters.Count, filtered.Count);
            return filtered;
        }

        private bool IsHighQualityChapter(ChapterInfo chapter, string[] lines)
        {
            var title = chapter.Title;

            // 1. 检查标题长度
            if (title.Length > 50)
                return false;

            // 2. 检查是否包含明确的章节标识
            var strongIndicators = new[] { "第", "章", "节", "回", "卷", "集", "部", "篇", "Chapter", "Section" };
            var indicatorCount = strongIndicators.Count(indicator => title.Contains(indicator));
            if (indicatorCount == 0)
                return false;

            // 3. 检查上下文（章节标题通常后面跟着正文，而不是另一个标题）
            if (chapter.LineNumber + 1 < lines.Length)
            {
                var nextLine = lines[chapter.LineNumber + 1].Trim();
                if (IsPotentialChapterLine(nextLine))
                    return false; // 连续两行都是章节，可能是误判
            }

            // 4. 检查是否包含明显的正文特征
            if (ContainsDialogueFeatures(title) || ContainsExcludedWords(title))
                return false;

            // 5. 检查章节标题的格式（应该比较规整）
            if (HasIrregularFormat(title))
                return false;

            return true;
        }

        private bool HasIrregularFormat(string title)
        {
            // 检查标题格式是否不规则（可能是正文）

            // 包含太多标点符号
            var punctuationCount = title.Count(c => c == '，' || c == '。' || c == '！' || c == '？' || c == '；' || c == '：');
            if (punctuationCount > 2)
                return true;

            // 以某些词汇结尾（章节标题通常不会这样）
            var badEndings = new[] { "了", "的", "着", "过", "地", "得", "吗", "呢", "吧", "啊" };
            if (badEndings.Any(ending => title.EndsWith(ending)))
                return true;

            return false;
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

        private List<Regex> GetChapterPatterns()
        {
            return new List<Regex>
            {
                // 基础章节模式 - 更严格
                new Regex(@"^第[零一二三四五六七八九十百千]+章[^。]*$"),
                new Regex(@"^第[零一二三四五六七八九十百千]+节[^。]*$"),
                new Regex(@"^第[零一二三四五六七八九十百千]+回[^。]*$"),
                new Regex(@"^第\d+章[^。]*$"),
                new Regex(@"^第\d+节[^。]*$"),
                new Regex(@"^第\d+回[^。]*$"),
                
                // 卷+章组合模式 - 更严格
                new Regex(@"^第[零一二三四五六七八九十百千]+卷第[零一二三四五六七八九十百千]+章[^。]*$"),
                new Regex(@"^第[零一二三四五六七八九十百千]+卷第\d+章[^。]*$"),
                new Regex(@"^第\d+卷第[零一二三四五六七八九十百千]+章[^。]*$"),
                new Regex(@"^第\d+卷第\d+章[^。]*$"),
                
                // 其他组合模式
                new Regex(@"^第[零一二三四五六七八九十百千]+部第[零一二三四五六七八九十百千]+章[^。]*$"),
                new Regex(@"^第[零一二三四五六七八九十百千]+部第\d+章[^。]*$"),
                new Regex(@"^第\d+部第[零一二三四五六七八九十百千]+章[^。]*$"),
                new Regex(@"^第\d+部第\d+章[^。]*$"),
                
                // 更严格的数字序号
                new Regex(@"^\d+\.[^。]*$"),
                new Regex(@"^[零一二三四五六七八九十百千]+、[^。]*$"),
                
                // 英文章节
                new Regex(@"^[Cc]hapter\s+\d+[^。]*$"),
                new Regex(@"^[Ss]ection\s+\d+[^。]*$"),
                
                // 符号包裹的章节（更严格）
                new Regex(@"^###[^#。]{1,30}###$"),
                new Regex(@"^\*{3,}[^*。]{1,30}\*{3,}$"),
                new Regex(@"^-{3,}[^-。]{1,30}-{3,}$"),
                
                // 特殊章节标记
                new Regex(@"^序章[^。]*$"),
                new Regex(@"^前言[^。]*$"),
                new Regex(@"^后记[^。]*$"),
                new Regex(@"^楔子[^。]*$"),
                new Regex(@"^尾声[^。]*$")
            };
        }

        private int CalculatePageFromLineNumber(int lineNumber)
        {
            // 基于服务端固定pageSize=1000计算页码
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
            // 使用文件路径的MD5作为索引文件名，避免路径过长问题
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
            // 先删除现有索引
            DeleteChapterIndex(filePath);

            // 重新构建
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