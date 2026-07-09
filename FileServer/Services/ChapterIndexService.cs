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

        // ---------- 核心指纹方法 ----------
        private string GetIndexFilePath(string fingerprint)
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            var hash = BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(fingerprint)))
                                   .Replace("-", "").ToLower();
            return Path.Combine(_indexBasePath, $"{hash}.json");
        }

        private async Task<ChapterIndex?> LoadChapterIndexByFingerprintAsync(string fingerprint)
        {
            var indexFile = GetIndexFilePath(fingerprint);
            if (!System.IO.File.Exists(indexFile))
                return null;
            try
            {
                var json = await System.IO.File.ReadAllTextAsync(indexFile);
                return System.Text.Json.JsonSerializer.Deserialize<ChapterIndex>(json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "加载章节索引失败: {IndexFile}", indexFile);
                return null;
            }
        }

        public async Task<ChapterIndex?> GetChapterIndexByFingerprintAsync(string fingerprint)
        {
            return await LoadChapterIndexByFingerprintAsync(fingerprint);
        }

        public async Task<ChapterIndex?> GetChapterIndexByPathAsync(string filePath)
        {
            // 通过文件信息计算指纹（与元数据服务保持一致）
            var fullPath = Path.Combine(_fileService.GetRootPath(), filePath);
            if (!File.Exists(fullPath))
                return null;
            var fi = new FileInfo(fullPath);
            var fingerprint = $"{fi.Length}_{fi.LastWriteTimeUtc.Ticks}";
            return await GetChapterIndexByFingerprintAsync(fingerprint);
        }

        public async Task<ChapterIndex> BuildChapterIndexAsync(string filePath, string content, string fingerprint)
        {
            var fileInfo = await _fileService.GetFileInfoAsync(filePath);

            // 步骤1：顺序解析，记录所有行内容和每行的起始字符偏移
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
                if (pos < contentLength && content[pos] == '\r') pos++;
                if (pos < contentLength && content[pos] == '\n') pos++;
            }

            var linesArray = lines.ToArray();
            var offsetsArray = lineOffsets.ToArray();
            var patterns = GetChapterPatterns();

            // 步骤2：并行检测章节行
            var chapterBag = new System.Collections.Concurrent.ConcurrentBag<(int LineIndex, ChapterInfo Chapter)>();

            Parallel.For(0, linesArray.Length, i =>
            {
                string line = linesArray[i];
                if (IsChapterLine(line, patterns, i, linesArray))
                {
                    var chapter = new ChapterInfo
                    {
                        Title = line.Trim(),
                        StartCharOffset = offsetsArray[i],
                        Preview = GetPreviewText(linesArray, i, 2)
                    };
                    chapterBag.Add((i, chapter));
                }
            });

            // 步骤3：按原始行号排序
            var chapters = chapterBag.OrderBy(x => x.LineIndex).Select(x => x.Chapter).ToList();

            var index = new ChapterIndex
            {
                Fingerprint = fingerprint,
                FilePath = filePath,
                FileSize = fileInfo.Size,
                FileLastModified = fileInfo.LastModified,
                IndexTime = DateTime.Now,
                TotalChapters = chapters.Count,
                Chapters = chapters
            };

            await SaveChapterIndexAsync(index);
            return index;
        }

        public async Task SaveChapterIndexAsync(ChapterIndex index)
        {
            if (string.IsNullOrEmpty(index.Fingerprint))
                throw new InvalidOperationException("索引对象缺少 Fingerprint");
            var indexFile = GetIndexFilePath(index.Fingerprint);
            var json = System.Text.Json.JsonSerializer.Serialize(index, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await System.IO.File.WriteAllTextAsync(indexFile, json);
            _logger.LogDebug("章节索引保存成功: {IndexFile}", indexFile);
        }

        public async Task<bool> DeleteChapterIndexByFingerprintAsync(string fingerprint)
        {
            var indexFile = GetIndexFilePath(fingerprint);
            if (System.IO.File.Exists(indexFile))
            {
                System.IO.File.Delete(indexFile);
                _logger.LogInformation("删除章节索引文件: {IndexFile}", indexFile);
                return true;
            }
            return false;
        }

        // ---------- 兼容旧接口 ----------
        public bool DeleteChapterIndex(string filePath)
        {
            // 通过文件信息计算指纹并删除（同步）
            var fullPath = Path.Combine(_fileService.GetRootPath(), filePath);
            if (!File.Exists(fullPath))
                return false;
            var fi = new FileInfo(fullPath);
            var fingerprint = $"{fi.Length}_{fi.LastWriteTimeUtc.Ticks}";
            var indexFile = GetIndexFilePath(fingerprint);
            if (System.IO.File.Exists(indexFile))
            {
                System.IO.File.Delete(indexFile);
                return true;
            }
            return false;
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
                                 $"创建: {fileInfo.CreationTime}\n---");
                    }
                    catch
                    {
                        infos.Add($"无效索引文件: {file}");
                    }
                }
            }
            return infos;
        }

        public async Task<ChapterIndex> ForceRebuildChapterIndexAsync(string filePath, string content)
        {
            var fullPath = Path.Combine(_fileService.GetRootPath(), filePath);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"文件不存在: {filePath}");
            var fi = new FileInfo(fullPath);
            var fingerprint = $"{fi.Length}_{fi.LastWriteTimeUtc.Ticks}";
            await DeleteChapterIndexByFingerprintAsync(fingerprint);
            return await BuildChapterIndexAsync(filePath, content, fingerprint);
        }

        public string GetIndexFileInfo(string filePath)
        {
            var fullPath = Path.Combine(_fileService.GetRootPath(), filePath);
            if (!File.Exists(fullPath))
                return "文件不存在";
            var fi = new FileInfo(fullPath);
            var fingerprint = $"{fi.Length}_{fi.LastWriteTimeUtc.Ticks}";
            var indexFile = GetIndexFilePath(fingerprint);
            if (!System.IO.File.Exists(indexFile))
                return $"索引文件不存在: {indexFile}";
            try
            {
                var info = new System.IO.FileInfo(indexFile);
                var content = System.IO.File.ReadAllText(indexFile);
                var index = System.Text.Json.JsonSerializer.Deserialize<ChapterIndex>(content);
                return $"索引文件: {indexFile}\n" +
                       $"文件大小: {info.Length} bytes\n" +
                       $"创建时间: {info.CreationTime}\n" +
                       $"章节数量: {index?.TotalChapters ?? 0}\n" +
                       $"索引时间: {index?.IndexTime}";
            }
            catch (Exception ex)
            {
                return $"读取索引文件失败: {indexFile} - {ex.Message}";
            }
        }

        public async Task<ChapterIndex?> GetCachedChapterIndexAsync(string filePath)
        {
            try
            {
                var index = await GetChapterIndexByPathAsync(filePath);
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

        // ---------- 以下为原有的所有私有方法，未做任何改动 ----------
        private bool IsChapterLine(string line, List<Regex> patterns, int lineNumber, string[] allLines)
        {
            // 1. 必须匹配章节模式
            if (!patterns.Any(p => p.IsMatch(line)))
                return false;

            // 2. 长度限制
            if (line.Length > 100)
                return false;

            // 3. 排除明显的对话特征
            if (ContainsStrongDialogueFeatures(line))
                return false;

            // 4. 检查上下文
            if (!IsAtParagraphStart(lineNumber, allLines))
                return false;

            // 5. 检查格式
            if (!HasReasonableChapterFormat(line))
                return false;

            // 6. 额外的验证
            if (IsNormalNarrative(line, lineNumber, allLines))
                return false;

            return true;
        }

        private bool ContainsStrongDialogueFeatures(string content)
        {
            var trimmed = content.Trim();

            if ((trimmed.Contains("「") && trimmed.Contains("」")) ||
                (trimmed.Contains("『") && trimmed.Contains("』")) ||
                (trimmed.Contains("\"") && trimmed.Count(c => c == '"') >= 2))
            {
                if (trimmed.Length > 30)
                {
                    _logger.LogDebug("排除长对话: {Content}", content);
                    return true;
                }
            }

            var strongDialogueIndicators = new[] {
                "问道", "说道", "回答", "心想", "问", "答",
                "说", "喊", "叫", "笑道", "冷笑道", "怒道", "喝道",
                "大声", "小声", "轻声", "喃喃", "嘀咕"
            };

            foreach (var indicator in strongDialogueIndicators)
            {
                if (content.Contains(indicator))
                {
                    if (content.Trim().StartsWith(indicator))
                        continue;

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

            if (trimmed.StartsWith("一回"))
            {
                var after = trimmed.Substring(2).Trim();
                var narrativeIndicators = new[] {
                    "到", "在", "就", "看到", "遇到", "发现", "开始",
                    "来到", "进入", "走到", "见到", "想起", "发现"
                };

                if (narrativeIndicators.Any(indicator => after.StartsWith(indicator)))
                {
                    _logger.LogDebug("排除叙述性文字: {Line}", line);
                    return true;
                }

                if (lineNumber + 1 < allLines.Length)
                {
                    var nextLine = allLines[lineNumber + 1].Trim();
                    if (!string.IsNullOrEmpty(nextLine) && nextLine.Length > 10)
                    {
                        if (!IsSectionSeparator(nextLine) && !nextLine.StartsWith("第") && nextLine.Length > 20)
                        {
                            _logger.LogDebug("排除连续叙述文字: {Line}", line);
                            return true;
                        }
                    }
                }
            }

            var timeIndicators = new[] { "第二天", "过了一会儿", "片刻之后", "突然", "这时", "然后", "接着", "随后" };
            if (timeIndicators.Any(indicator => trimmed.Contains(indicator)))
            {
                if (timeIndicators.Any(indicator => trimmed.StartsWith(indicator) || trimmed.IndexOf(indicator) < 5))
                {
                    _logger.LogDebug("排除时间描述: {Line}", line);
                    return true;
                }
            }

            if (trimmed.Contains("，") && trimmed.Contains("。"))
            {
                _logger.LogDebug("排除普通句子: {Line}", line);
                return true;
            }

            return false;
        }

        private bool IsAtParagraphStart(int lineNumber, string[] allLines)
        {
            if (lineNumber == 0)
                return true;

            if (lineNumber >= 1)
            {
                var prevLine = allLines[lineNumber - 1].Trim();
                if (string.IsNullOrEmpty(prevLine))
                    return true;
            }

            if (lineNumber >= 2)
            {
                var prevLine1 = allLines[lineNumber - 1].Trim();
                var prevLine2 = allLines[lineNumber - 2].Trim();
                if (string.IsNullOrEmpty(prevLine1) && string.IsNullOrEmpty(prevLine2))
                    return true;
            }

            if (lineNumber >= 1)
            {
                var prevLine = allLines[lineNumber - 1].Trim();
                if (IsSectionSeparator(prevLine))
                    return true;
            }

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

            var separators = new[] { "***", "---", "###", "===", "※※※", "○○○", "§§§" };
            if (separators.Any(sep => line.StartsWith(sep) && line.EndsWith(sep)))
                return true;

            if (line.All(c => "*-=#○●※§~".Contains(c)) && line.Length >= 3)
                return true;

            return false;
        }

        private bool HasReasonableChapterFormat(string line)
        {
            var trimmed = line.Trim();

            var invalidStartChars = new[] { '，', '。', '！', '？', '；', '、' };
            if (invalidStartChars.Contains(trimmed[0]))
                return false;

            if (Regex.IsMatch(trimmed, @"[.\-\\\*#]{3,}"))
                return false;

            return true;
        }

        private List<Regex> GetChapterPatterns()
        {
            var chineseNum = @"[零一二三四五六七八九十百千]+";
            var arabicNum = @"\d+";
            var numberPattern = $"(?:{chineseNum}|{arabicNum})";
            var keywordPattern = @"[章节回卷幕]";
            var anyWhitespace = @"[\s\u3000]*";
            var optionalColon = $@"{anyWhitespace}[:：]?{anyWhitespace}";
            var optionalTitle = @".*?";

            var patterns = new List<Regex>
            {
                new Regex($@"^第{numberPattern}{keywordPattern}{anyWhitespace}{optionalTitle}$", RegexOptions.Compiled),
                new Regex(@"^(序章|前言|后记|楔子|尾声|结局|完结|终章|附录|外传)$", RegexOptions.Compiled),
                new Regex($@"^(序章|前言|后记|楔子|尾声|结局|完结|终章|附录|外传){anyWhitespace}{optionalTitle}$", RegexOptions.Compiled),
                new Regex($@"^正文{anyWhitespace}第{numberPattern}{keywordPattern}{anyWhitespace}{optionalTitle}$", RegexOptions.Compiled),
                new Regex($@"^#+{anyWhitespace}第{numberPattern}{keywordPattern}{anyWhitespace}{optionalTitle}{anyWhitespace}#+$", RegexOptions.Compiled),
                new Regex($@"^\*+{anyWhitespace}第{numberPattern}{keywordPattern}{anyWhitespace}{optionalTitle}{anyWhitespace}\*+$", RegexOptions.Compiled),
                new Regex($@"^-+{anyWhitespace}第{numberPattern}{keywordPattern}{anyWhitespace}{optionalTitle}{anyWhitespace}-+$", RegexOptions.Compiled),
                new Regex($@"^=+{anyWhitespace}第{numberPattern}{keywordPattern}{anyWhitespace}{optionalTitle}{anyWhitespace}=+$", RegexOptions.Compiled),
                new Regex($@"^#+{anyWhitespace}{keywordPattern}{anyWhitespace}{optionalTitle}{anyWhitespace}#+$", RegexOptions.Compiled),
                new Regex($@"^\*+{anyWhitespace}{keywordPattern}{anyWhitespace}{optionalTitle}{anyWhitespace}\*+$", RegexOptions.Compiled),
                new Regex($@"^-+{anyWhitespace}{keywordPattern}{anyWhitespace}{optionalTitle}{anyWhitespace}-+$", RegexOptions.Compiled),
                new Regex($@"^=+{anyWhitespace}{keywordPattern}{anyWhitespace}{optionalTitle}{anyWhitespace}=+$", RegexOptions.Compiled),
                new Regex($@"^Chapter{anyWhitespace}{arabicNum}{anyWhitespace}{optionalTitle}$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                new Regex($@"^Part{anyWhitespace}{arabicNum}{anyWhitespace}{optionalTitle}$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                new Regex($@"^Section{anyWhitespace}{arabicNum}{anyWhitespace}{optionalTitle}$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                new Regex($@"^{chineseNum}{keywordPattern}{anyWhitespace}{optionalTitle}$", RegexOptions.Compiled),
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

        private async Task<bool> IsIndexValidAsync(string filePath, ChapterIndex index)
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
    }
}