// IThumbnailService.cs
using FileServer.Models;

namespace FileServer.Services
{
    public interface IThumbnailService
    {
        Task<bool> GenerateThumbnailAsync(string imagePath, int width = 200, int height = 200);
        Task<bool> DeleteThumbnailAsync(string imagePath);
        Task<string> GetThumbnailPathAsync(string imagePath);
        Task<bool> ThumbnailExistsAsync(string imagePath);
        Task<Stream> GetThumbnailStreamAsync(string imagePath);
    }
}