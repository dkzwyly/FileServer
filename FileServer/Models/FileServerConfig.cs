namespace FileServer.Models
{
    public class FileServerConfig
    {
        public string RootPath { get; set; } = @"E:\FileServer";
        public int HttpPort { get; set; } = 8080;
        public int HttpsPort { get; set; } = 8081;
        public int QuicPort { get; set; } = 8082;
        public long MaxFileSize { get; set; } = 10L * 1024 * 1024 * 1024; // 10GB
        public bool EnableQuic { get; set; } = true;
        public string CertificatePath { get; set; } = "";
        public string CertificatePassword { get; set; } = "";
    }
}