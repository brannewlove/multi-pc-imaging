using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MultiDeployOS.Master.Models;

namespace MultiDeployOS.Master.Services
{
    public class TcpServerService : IDisposable
    {
        public const int Port = 39201;
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;

        public ConcurrentDictionary<string, ClientSession> ConnectedClients { get; } = new();

        public event Action<ClientSession>? ClientRegistered;
        public event Action<ClientSession>? ClientProgressUpdated;
        public event Action<ClientSession, ResultInfo>? ClientCompleted;
        public event Action<ClientSession>? ClientDisconnected;

        public void Start()
        {
            if (_listener != null) return;
            _cts = new CancellationTokenSource();

            try
            {
                _listener = new TcpListener(IPAddress.Any, Port);
                _listener.Start();
                Task.Run(() => AcceptClientsLoop(_cts.Token));
                Console.WriteLine($"[+] WinPE Agent TCP 서버 구동: 0.0.0.0:{Port}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[-] TCP 서버 시작 오류: {ex.Message}");
            }
        }

        private async Task AcceptClientsLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener != null)
            {
                try
                {
                    var tcpClient = await _listener.AcceptTcpClientAsync(token);
                    string sessionId = "PC-" + Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper();
                    string remoteIp = ((IPEndPoint?)tcpClient.Client.RemoteEndPoint)?.Address.ToString() ?? "";

                    var session = new ClientSession
                    {
                        SessionId = sessionId,
                        IpAddress = remoteIp,
                        ConnectedAt = DateTime.Now,
                        Status = "CONNECTED",
                        SocketClient = tcpClient
                    };

                    ConnectedClients[sessionId] = session;
                    _ = Task.Run(() => HandleClientCommunication(session, token));
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine($"[-] TCP Accept 루프 에러: {ex.Message}");
                }
            }
        }

        private async Task HandleClientCommunication(ClientSession session, CancellationToken token)
        {
            var client = session.SocketClient;
            if (client == null) return;

            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);

                while (!token.IsCancellationRequested && client.Connected)
                {
                    string? line = await reader.ReadLineAsync(token);
                    if (line == null) break;

                    line = line.Trim();
                    if (string.IsNullOrEmpty(line)) continue;

                    ProcessIncomingMessage(session, line);
                }
            }
            catch (Exception) { }
            finally
            {
                ConnectedClients.TryRemove(session.SessionId, out _);
                ClientDisconnected?.Invoke(session);
                client.Close();
            }
        }

        private void ProcessIncomingMessage(ClientSession session, string jsonString)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonString);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp)) return;

                string type = typeProp.GetString() ?? "";

                if (type == "CLIENT_REGISTER")
                {
                    if (root.TryGetProperty("payload", out var payload))
                    {
                        if (payload.TryGetProperty("system", out var sysProp))
                            session.System = JsonSerializer.Deserialize<SystemInfo>(sysProp.GetRawText()) ?? new();

                        if (payload.TryGetProperty("network", out var netProp))
                            session.Network = JsonSerializer.Deserialize<NetworkInfo>(netProp.GetRawText()) ?? new();

                        if (payload.TryGetProperty("disks", out var disksProp))
                            session.Disks = JsonSerializer.Deserialize<List<DiskInfo>>(disksProp.GetRawText()) ?? new();
                    }

                    session.Status = "READY";
                    ClientRegistered?.Invoke(session);
                }
                else if (type == "CLIENT_PROGRESS")
                {
                    session.Status = "IMAGING";
                    session.Progress = JsonSerializer.Deserialize<ProgressInfo>(jsonString) ?? new();
                    ClientProgressUpdated?.Invoke(session);
                }
                else if (type == "CLIENT_RESULT")
                {
                    var result = JsonSerializer.Deserialize<ResultInfo>(jsonString) ?? new();
                    session.Status = (result.Status == "SUCCESS") ? "SUCCESS" : "FAILED";
                    session.Result = result;
                    ClientCompleted?.Invoke(session, result);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[-] 클라이언트 메시지 파싱 오류 [{session.SessionId}]: {ex.Message}");
            }
        }

        public bool ApproveClient(ClientSession session, PartitionPolicy policy, ImageRule imageRule, ServerConfig serverConfig)
        {
            if (session.SocketClient == null || !session.SocketClient.Connected) return false;
            if (session.Status == "IMAGING") return false;

            try
            {
                var cmds = SmartPartitionEngine.GenerateCommands(policy, session.Disks, 0);

                string targetFileName = imageRule.TargetImage?.ImageFileName ?? "Win11_Universal.wim";
                int targetIndex = imageRule.TargetImage?.ImageIndex ?? 1;

                string masterDir = (serverConfig.MasterImageDir ?? @"C:\images").Trim().Trim('"', '\'');
                string sharePath = (serverConfig.NetworkSharePath ?? @"\\127.0.0.1\images").Trim().Trim('"', '\'');

                // 슬래시 통일
                masterDir = masterDir.Replace('/', '\\');
                sharePath = sharePath.Replace('/', '\\');

                // 127.0.0.1 또는 localhost 자동 치환 (클라이언트와 연결된 실제 서버 LAN IP 사용)
                string serverLanIp = "127.0.0.1";
                if (session.SocketClient?.Client?.LocalEndPoint is IPEndPoint ep && ep.Address != null)
                {
                    serverLanIp = ep.Address.ToString();
                }
                if (sharePath.Contains("127.0.0.1")) sharePath = sharePath.Replace("127.0.0.1", serverLanIp);
                if (sharePath.Contains("localhost")) sharePath = sharePath.Replace("localhost", serverLanIp);

                // localPath 계산 (이미 .wim 파일명이 포함되어 있다면 중복 결합 방지)
                string localPath = masterDir.EndsWith(".wim", StringComparison.OrdinalIgnoreCase) || masterDir.EndsWith(".esd", StringComparison.OrdinalIgnoreCase)
                    ? masterDir
                    : Path.Combine(masterDir.TrimEnd('\\'), targetFileName);

                // networkPath 계산 (이미 .wim 파일명이 포함되어 있다면 중복 결합 방지)
                string networkPath = sharePath.EndsWith(".wim", StringComparison.OrdinalIgnoreCase) || sharePath.EndsWith(".esd", StringComparison.OrdinalIgnoreCase)
                    ? sharePath
                    : Path.Combine(sharePath.TrimEnd('\\'), targetFileName);

                var deployPayload = new
                {
                    type = "MASTER_APPROVE_DEPLOY",
                    payload = new
                    {
                        sessionId = session.SessionId,
                        targetDiskIndex = 0,
                        partitionPolicyId = policy.Id,
                        partitionCommands = cmds,
                        imageInfo = new
                        {
                            ruleId = imageRule.Id,
                            ruleName = imageRule.Name,
                            imageName = targetFileName,
                            imageIndex = targetIndex,
                            imageLocalPath = localPath,
                            imageNetworkPath = networkPath,
                            sourceType = serverConfig.ClientAccessMethod ?? "LOCAL_OR_SHARE",
                            sourceUrl = $"http://{serverLanIp}:39202/images/{Uri.EscapeDataString(targetFileName)}",
                            targetDriveLetter = "W:",
                            bootDriveLetter = "S:"
                        },
                        postAction = "REBOOT"
                    }
                };

                string json = JsonSerializer.Serialize(deployPayload) + "\n";
                byte[] bytes = Encoding.UTF8.GetBytes(json);

                var stream = session.SocketClient.GetStream();
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();

                session.Status = "IMAGING";
                session.Progress = new ProgressInfo
                {
                    Percent = 0,
                    Step = "배포 명령 승인됨 (작업 준비 중)",
                    SpeedMbps = 0,
                    ElapsedSeconds = 0
                };
                session.MatchedPolicy = policy;
                session.MatchedImage = imageRule;

                ClientProgressUpdated?.Invoke(session);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[-] 승인 메시지 전송 오류 [{session.SessionId}]: {ex.Message}");
                return false;
            }
        }

        public void Stop()
        {
            _cts?.Cancel();
            _listener?.Stop();
            _listener = null;
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }
    }
}
