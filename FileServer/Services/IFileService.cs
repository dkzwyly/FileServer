using FileServer.Models;
using Microsoft.AspNetCore.Http;

namespace FileServer.Services
{
    public interface IFileService
    {
        Task<FileListResponse> GetFileListAsync(string relativePath);
        Task<(Stream Stream, string ContentType, string FileName)> DownloadFileAsync(string filePath);
        Task<FileInfoModel> GetFileInfoAsync(string filePath);
        Task<UploadResponse> UploadFilesAsync(string targetPath, IFormFileCollection files);
        Task<bool> FileExistsAsync(string filePath);
        Task<bool> DirectoryExistsAsync(string directoryPath);
        string FormatFileSize(long bytes);
        string GetMimeType(string extension);
        string GetRootPath();
        Task<bool> DeleteFileAsync(string filePath);
        Task<(Stream Stream, string ContentType, string FileName)> GetThumbnailAsync(string filePath);
        Task CreateDirectoryAsync(string relativePath);  // 新增
    }
}