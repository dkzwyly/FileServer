using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TagLib;
using FileServer.Models;

namespace FileServer.Services
{
    public class AudioMetadataService : IAudioMetadataService
    {
        private readonly ILogger<AudioMetadataService> _logger;
        private readonly string _mappingFilePath;
        private readonly string _coversDirectory;

        public AudioMetadataService(ILogger<AudioMetadataService> logger, IConfiguration configuration)
        {
            _logger = logger;
            var rootPath = configuration.GetValue<string>("FileServer:RootPath") ?? @"E:\FileServer";
            _mappingFilePath = Path.Combine(rootPath, "song-metadata-mappings.json");
            _coversDirectory = Path.Combine(rootPath, "covers");

            if (!Directory.Exists(_coversDirectory))
                Directory.CreateDirectory(_coversDirectory);
        }

        public async Task<SongMetadata> GetMetadataAsync(string filePath)
        {
            try
            {
                // 1. 检查手动映射
                var mapping = await GetMetadataMappingAsync(filePath);
                if (mapping != null)
                {
                    _logger.LogInformation("使用手动映射的元数据: {Path}", filePath);
                    return mapping;
                }

                // 2. 自动从文件读取标签
                using var file = TagLib.File.Create(filePath);
                var metadata = new SongMetadata
                {
                    Title = string.IsNullOrEmpty(file.Tag.Title)
                        ? Path.GetFileNameWithoutExtension(filePath)
                        : file.Tag.Title,
                    Artist = string.IsNullOrEmpty(file.Tag.FirstPerformer)
                        ? "未知艺术家"
                        : file.Tag.FirstPerformer,
                    Album = string.IsNullOrEmpty(file.Tag.Album)
                        ? "未知专辑"
                        : file.Tag.Album,
                    HasCover = file.Tag.Pictures != null && file.Tag.Pictures.Length > 0,
                    CustomCoverPath = null
                };

                _logger.LogInformation("自动解析元数据: {Path} -> 标题:{Title}, 艺术家:{Artist}, 专辑:{Album}",
                    filePath, metadata.Title, metadata.Artist, metadata.Album);
                return metadata;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "读取音频元数据失败: {Path}", filePath);
                return new SongMetadata
                {
                    Title = Path.GetFileNameWithoutExtension(filePath),
                    Artist = "未知艺术家",
                    Album = "未知专辑",
                    HasCover = false,
                    CustomCoverPath = null
                };
            }
        }

        public async Task<Stream?> GetAlbumCoverAsync(string filePath)
        {
            try
            {
                // 1. 检查是否有自定义封面映射
                var mapping = await GetMetadataMappingAsync(filePath);
                if (mapping != null && !string.IsNullOrEmpty(mapping.CustomCoverPath))
                {
                    var coverFullPath = Path.Combine(_coversDirectory, mapping.CustomCoverPath);
                    if (System.IO.File.Exists(coverFullPath))
                    {
                        _logger.LogInformation("返回自定义封面: {Path}", coverFullPath);
                        return new FileStream(coverFullPath, FileMode.Open, FileAccess.Read);
                    }
                    _logger.LogWarning("自定义封面文件丢失: {Path}", coverFullPath);
                }

                // 2. 返回内嵌封面
                using var file = TagLib.File.Create(filePath);
                var picture = file.Tag.Pictures?.FirstOrDefault();
                if (picture != null)
                {
                    _logger.LogInformation("返回内嵌封面: {Path}", filePath);
                    return new MemoryStream(picture.Data.Data);
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取专辑封面失败: {Path}", filePath);
                return null;
            }
        }

        public async Task<string?> SaveCustomCoverAsync(string songPath, Stream coverStream, string fileName)
        {
            try
            {
                var ext = Path.GetExtension(fileName);
                var safeFileName = $"{Guid.NewGuid():N}{ext}";
                var coverFullPath = Path.Combine(_coversDirectory, safeFileName);

                await using (var fileStream = new FileStream(coverFullPath, FileMode.Create))
                {
                    await coverStream.CopyToAsync(fileStream);
                }

                var mappings = await LoadMappingsAsync();
                if (mappings.TryGetValue(songPath, out var existing))
                {
                    existing.CustomCoverPath = safeFileName;
                    existing.HasCover = true;
                }
                else
                {
                    mappings[songPath] = new SongMetadata
                    {
                        Title = "",
                        Artist = "",
                        Album = "",
                        HasCover = true,
                        CustomCoverPath = safeFileName
                    };
                }
                await SaveMappingsAsync(mappings);

                _logger.LogInformation("保存自定义封面成功: {SongPath} -> {CoverPath}", songPath, safeFileName);
                return safeFileName;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存自定义封面失败: {SongPath}", songPath);
                return null;
            }
        }

        public async Task<bool> DeleteCustomCoverAsync(string songPath)
        {
            try
            {
                var mappings = await LoadMappingsAsync();
                if (mappings.TryGetValue(songPath, out var metadata) && !string.IsNullOrEmpty(metadata.CustomCoverPath))
                {
                    var coverFullPath = Path.Combine(_coversDirectory, metadata.CustomCoverPath);
                    if (System.IO.File.Exists(coverFullPath))
                        System.IO.File.Delete(coverFullPath);

                    metadata.CustomCoverPath = null;
                    metadata.HasCover = false;
                    await SaveMappingsAsync(mappings);

                    _logger.LogInformation("删除自定义封面成功: {SongPath}", songPath);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除自定义封面失败: {SongPath}", songPath);
                return false;
            }
        }

        public async Task<bool> SaveMetadataMappingAsync(string songPath, SongMetadata metadata)
        {
            try
            {
                var mappings = await LoadMappingsAsync();
                // 保留原有的自定义封面信息
                if (mappings.TryGetValue(songPath, out var existing))
                {
                    metadata.CustomCoverPath = existing.CustomCoverPath;
                    metadata.HasCover = existing.HasCover;
                }
                mappings[songPath] = metadata;
                await SaveMappingsAsync(mappings);
                _logger.LogInformation("保存元数据映射成功: {SongPath}", songPath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存元数据映射失败: {SongPath}", songPath);
                return false;
            }
        }

        public async Task<SongMetadata?> GetMetadataMappingAsync(string songPath)
        {
            try
            {
                var mappings = await LoadMappingsAsync();
                return mappings.TryGetValue(songPath, out var metadata) ? metadata : null;
            }
            catch
            {
                return null;
            }
        }

        public async Task<bool> DeleteMetadataMappingAsync(string songPath)
        {
            try
            {
                var mappings = await LoadMappingsAsync();
                if (mappings.Remove(songPath))
                {
                    // 同时删除关联的封面文件
                    if (mappings.TryGetValue(songPath, out var metadata) && !string.IsNullOrEmpty(metadata.CustomCoverPath))
                    {
                        var coverFullPath = Path.Combine(_coversDirectory, metadata.CustomCoverPath);
                        if (System.IO.File.Exists(coverFullPath))
                            System.IO.File.Delete(coverFullPath);
                    }
                    await SaveMappingsAsync(mappings);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除元数据映射失败: {SongPath}", songPath);
                return false;
            }
        }

        private async Task<Dictionary<string, SongMetadata>> LoadMappingsAsync()
        {
            if (!System.IO.File.Exists(_mappingFilePath))
                return new Dictionary<string, SongMetadata>();

            var json = await System.IO.File.ReadAllTextAsync(_mappingFilePath);
            return JsonSerializer.Deserialize<Dictionary<string, SongMetadata>>(json)
                   ?? new Dictionary<string, SongMetadata>();
        }

        private async Task SaveMappingsAsync(Dictionary<string, SongMetadata> mappings)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(mappings, options);
            await System.IO.File.WriteAllTextAsync(_mappingFilePath, json);
        }
    }
}