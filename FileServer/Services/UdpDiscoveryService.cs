using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FileServer.Services
{
    public class UdpDiscoveryService : BackgroundService
    {
        private const int DiscoveryPort = 38080;
        private const string DiscoverRequest = "FILESERVER_DISCOVER";

        private readonly ILogger<UdpDiscoveryService> _logger;
        private readonly IConfiguration _configuration;
        private readonly List<UdpClient> _clients = new();
        private readonly List<Task> _receiveTasks = new();

        public UdpDiscoveryService(
            ILogger<UdpDiscoveryService> logger,
            IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("UDP 发现服务启动，端口: {Port}", DiscoveryPort);

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                    try
                    {
                        var udp = new UdpClient();
                        udp.Client.SetSocketOption(
                            SocketOptionLevel.Socket,
                            SocketOptionName.ReuseAddress, true);
                        udp.Client.Bind(new IPEndPoint(ua.Address, DiscoveryPort));
                        _clients.Add(udp);

                        _logger.LogInformation("UDP 绑定: {NicName} [{Ip}]",
                            nic.Name, ua.Address);

                        _receiveTasks.Add(ReceiveLoopAsync(udp, stoppingToken));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "UDP 绑定失败: {Ip}", ua.Address);
                    }
                }
            }

            if (_clients.Count == 0)
                _logger.LogWarning("没有可用的 UDP 绑定，发现功能不可用");

            await Task.WhenAll(_receiveTasks);
        }

        private async Task ReceiveLoopAsync(UdpClient udp, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var result = await udp.ReceiveAsync(ct);
                    var msg = Encoding.UTF8.GetString(result.Buffer).Trim();

                    if (msg == DiscoverRequest)
                    {
                        var reply = BuildReply();
                        var bytes = Encoding.UTF8.GetBytes(reply);
                        await udp.SendAsync(bytes, bytes.Length, result.RemoteEndPoint);
                        _logger.LogDebug("响应发现: {Remote}", result.RemoteEndPoint);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "UDP 接收异常");
                    await Task.Delay(1000, ct);
                }
            }
        }

        private string BuildReply()
        {
            var httpPort = _configuration.GetValue<int>("FileServerConfig:HttpPort", 8080);
            var httpsPort = _configuration.GetValue<int>("FileServerConfig:HttpsPort", 8081);

            var reply = new
            {
                type = "FILESERVER",
                name = Dns.GetHostName(),
                http = httpPort,
                https = httpsPort,
                version = "3.0.0"
            };
            return JsonSerializer.Serialize(reply);
        }

        public override void Dispose()
        {
            foreach (var udp in _clients)
            {
                try { udp.Close(); udp.Dispose(); } catch { }
            }
            base.Dispose();
        }
    }
}