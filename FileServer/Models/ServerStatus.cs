namespace FileServer.Models
{
    public class ServerStatus
    {
        public bool IsRunning { get; set; }
        public int ActiveConnections { get; set; }
        public string RootPath { get; set; } = string.Empty;
        public int HttpPort { get; set; }
        public int HttpsPort { get; set; }
        public int QuicPort { get; set; }
        public bool QuicEnabled { get; set; }
        public DateTime StartTime { get; set; }
        public long TotalRequests { get; set; }
        public long Uptime { get; set; }  // 添加这个属性
    }

    public class HealthResponse
    {
        public string Status { get; set; } = "healthy";
        public string Timestamp { get; set; } = string.Empty;
        public string Service { get; set; } = "File Server";
        public int ActiveConnections { get; set; }
        public long Uptime { get; set; }
    }
}