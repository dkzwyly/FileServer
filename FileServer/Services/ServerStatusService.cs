using FileServer.Models;

namespace FileServer.Services
{
    public interface IServerStatusService
    {
        ServerStatus GetStatus();
        void IncrementConnections();
        void DecrementConnections();
        void IncrementRequests();
        void SetStartTime();
        void UpdateConfig(FileServerConfig config);
        long GetUptime(); // 添加这个方法
    }

    public class ServerStatusService : IServerStatusService
    {
        private ServerStatus _status = new();
        private readonly object _lock = new();

        public ServerStatusService(IConfiguration configuration)
        {
            var config = configuration.GetSection("FileServer").Get<FileServerConfig>();
            if (config != null)
            {
                _status.RootPath = config.RootPath;
                _status.HttpPort = config.HttpPort;
                _status.HttpsPort = config.HttpsPort;
                _status.QuicPort = config.QuicPort;
                _status.QuicEnabled = config.EnableQuic;
            }
            _status.IsRunning = true;
            _status.StartTime = DateTime.UtcNow;
        }

        public ServerStatus GetStatus()
        {
            lock (_lock)
            {
                _status.Uptime = GetUptime(); // 计算运行时间
                return _status;
            }
        }

        public long GetUptime()
        {
            return (long)(DateTime.UtcNow - _status.StartTime).TotalSeconds;
        }

        public void IncrementConnections()
        {
            lock (_lock)
            {
                _status.ActiveConnections++;
            }
        }

        public void DecrementConnections()
        {
            lock (_lock)
            {
                if (_status.ActiveConnections > 0)
                    _status.ActiveConnections--;
            }
        }

        public void IncrementRequests()
        {
            lock (_lock)
            {
                _status.TotalRequests++;
            }
        }

        public void SetStartTime()
        {
            lock (_lock)
            {
                _status.StartTime = DateTime.UtcNow;
            }
        }

        public void UpdateConfig(FileServerConfig config)
        {
            lock (_lock)
            {
                _status.RootPath = config.RootPath;
                _status.HttpPort = config.HttpPort;
                _status.HttpsPort = config.HttpsPort;
                _status.QuicPort = config.QuicPort;
                _status.QuicEnabled = config.EnableQuic;
            }
        }
    }
}