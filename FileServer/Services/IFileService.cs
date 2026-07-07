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
        /// <summary>
        /// 递归删除指定路径的文件夹及其所有内容（包括子文件夹和文件）
        /// </summary>
        /// <param name="relativePath">相对于根目录的路径</param>
        /// <returns>是否删除成功</returns>
        Task<bool> DeleteDirectoryAsync(string relativePath);
        /// <summary>
        /// 重命名文件或文件夹（仅更改名称，不改变路径）
        /// </summary>
        Task<bool> RenameAsync(string oldPath, string newName);

        /// <summary>
        /// 移动文件或文件夹到新路径（可跨目录）
        /// </summary>
        Task<bool> MoveAsync(string sourcePath, string destPath);

        /// <summary>
        /// 复制文件或文件夹到新路径
        /// </summary>
        Task<bool> CopyAsync(string sourcePath, string destPath);
    }
}