using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MultiDeployOS.Master.Services
{
    public class UdpDiscoveryService : IDisposable
    {
        public const int Port = 39200;
        private UdpClient? _udpListener;
        private CancellationTokenSource? _cts;

        public event Action<string, int>? ClientDiscovered;

        public void Start()
        {
            if (_udpListener != null) return;
            _cts = new CancellationTokenSource();

            try
            {
                _udpListener = new UdpClient();
                _udpListener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udpListener.Client.Bind(new IPEndPoint(IPAddress.Any, Port));

                Task.Run(() => ListenLoop(_cts.Token));
                Console.WriteLine($"[+] UDP 브로드캐스트 탐색 서버 구동: 0.0.0.0:{Port}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[-] UDP 서버 시작 오류: {ex.Message}");
            }
        }

        private async Task ListenLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _udpListener != null)
            {
                try
                {
                    var result = await _udpListener.ReceiveAsync(token);
                    string msg = Encoding.UTF8.GetString(result.Buffer);

                    if (msg.Contains("CLIENT_DISCOVERY"))
                    {
                        ClientDiscovered?.Invoke(result.RemoteEndPoint.Address.ToString(), result.RemoteEndPoint.Port);

                        var ack = new
                        {
                            type = "MASTER_DISCOVERY_ACK",
                            masterIp = result.RemoteEndPoint.Address.ToString(),
                            tcpPort = TcpServerService.Port,
                            httpPort = 39202
                        };
                        string ackJson = JsonSerializer.Serialize(ack) + "\n";
                        byte[] ackBytes = Encoding.UTF8.GetBytes(ackJson);

                        await _udpListener.SendAsync(ackBytes, ackBytes.Length, result.RemoteEndPoint);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[-] UDP 수신 루프 오류: {ex.Message}");
                }
            }
        }

        public void Stop()
        {
            _cts?.Cancel();
            _udpListener?.Close();
            _udpListener = null;
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }
    }
}
