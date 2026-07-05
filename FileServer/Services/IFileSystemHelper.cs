namespace FileServer.Services
{
    public interface IFileSystemHelper
    {
        string GetRootPath();
        string FormatFileSize(long bytes);
        string GetMimeType(string extension);
    }
}