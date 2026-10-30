using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MultiDeployOS.Master.Services
{
    public class TftpTransferCompletedArgs
    {
        public string ClientIp { get; set; } = "";
        public string FileName { get; set; } = "";
        public long TotalBytes { get; set; }
    }

    public class TftpServerService : IDisposable
    {
        public const int Port = 69;

        private class TftpSession
        {
            public IPEndPoint RemoteEndPoint { get; init; } = null!;
            public string FileName { get; init; } = "";
            public byte[] Data { get; init; } = Array.Empty<byte>();
            public int BlockSize { get; init; } = 512;
            public int WindowSize { get; init; } = 1;
            public long TotalBlocks { get; init; }
            public long LastAckedBlock { get; set; } = 0; // 0 = OACK 대기 또는 미전송
            public bool WaitingForOackAck { get; set; }
            public byte[]? LastOackPacket { get; set; }
            public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;
            public int RetryCount { get; set; } = 0;
            public object Sync { get; } = new();
        }

        private readonly PxeBootAssetManager _assetManager;
        private Socket? _socket;
        private CancellationTokenSource? _cts;
        private readonly ConcurrentDictionary<IPEndPoint, TftpSession> _sessions = new();

        public event Action<TftpTransferCompletedArgs>? FileTransferCompleted;
        public event Action<string>? LogAdded;

        public bool IsRunning => _socket != null;

        public TftpServerService(PxeBootAssetManager assetManager)
        {
            _assetManager = assetManager;
        }

        public void Start(IPAddress bindIp)
        {
            Stop();

            _cts = new CancellationTokenSource();
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _socket.ReceiveBufferSize = 512 * 1024;
            _socket.SendBufferSize = 1024 * 1024;
            _socket.Bind(new IPEndPoint(bindIp, Port));

            Task.Run(() => ReceiveLoopAsync(_socket, _cts.Token));
            Task.Run(() => RetransmitWatchdogLoopAsync(_socket, _cts.Token));

            Log($"[TFTP] 단일 포트 고속 TFTP 서버 가동 ({bindIp}:{Port})");
        }

        private async Task ReceiveLoopAsync(Socket socket, CancellationToken token)
        {
            byte[] buf = new byte[2048];
            EndPoint anyEp = new IPEndPoint(IPAddress.Any, 0);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var res = await socket.ReceiveFromAsync(buf, SocketFlags.None, anyEp, token);
                    if (res.ReceivedBytes >= 4 && res.RemoteEndPoint is IPEndPoint remoteEp)
                    {
                        ushort opcode = (ushort)((buf[0] << 8) | buf[1]);
                        switch (opcode)
                        {
                            case 1: // RRQ (Read Request)
                                byte[] rrqCopy = new byte[res.ReceivedBytes];
                                Buffer.BlockCopy(buf, 0, rrqCopy, 0, res.ReceivedBytes);
                                HandleReadRequest(socket, rrqCopy, remoteEp);
                                break;

                            case 4: // ACK
                                ushort ackBlock = (ushort)((buf[2] << 8) | buf[3]);
                                HandleAck(socket, remoteEp, ackBlock);
                                break;

                            case 5: // ERROR (UEFI tsize 프로브 중단 포함)
                                _sessions.TryRemove(remoteEp, out _);
                                break;
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    // Windows UDP ICMP Port Unreachable 무시 후 계속 수신
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        Log($"[TFTP 오류] {ex.Message}");
                    }
                }
            }
        }

        private void HandleReadRequest(Socket socket, byte[] pkt, IPEndPoint remoteEp)
        {
            var tokens = ExtractNullTerminatedStrings(pkt, 2);
            if (tokens.Count < 2) return;

            string rawPath = tokens[0];
            string cleanName = Path.GetFileName(rawPath.Replace('\\', '/').Trim('/'));
            if (string.IsNullOrEmpty(cleanName))
            {
                SendError(socket, remoteEp, 1, "Invalid filename");
                return;
            }

            if (!_assetManager.TryGetSmallAssetBytes(cleanName, out byte[] fileBytes))
            {
                Log($"[TFTP] 파일 없음 요청 거부: '{rawPath}' ({remoteEp.Address})");
                SendError(socket, remoteEp, 1, "File not found");
                return;
            }

            int blockSize = 512;
            int windowSize = 1;
            var negotiatedOptions = new List<(string Key, string Value)>();

            for (int i = 2; i + 1 < tokens.Count; i += 2)
            {
                string optKey = tokens[i];
                string optVal = tokens[i + 1];

                if (optKey.Equals("blksize", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int reqBlk))
                {
                    blockSize = Math.Clamp(reqBlk, 512, 16384);
                    negotiatedOptions.Add(("blksize", blockSize.ToString()));
                }
                else if (optKey.Equals("tsize", StringComparison.OrdinalIgnoreCase))
                {
                    negotiatedOptions.Add(("tsize", fileBytes.Length.ToString()));
                }
                else if (optKey.Equals("windowsize", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int reqWin))
                {
                    windowSize = Math.Clamp(reqWin, 1, 32);
                    negotiatedOptions.Add(("windowsize", windowSize.ToString()));
                }
                else if (optKey.Equals("timeout", StringComparison.OrdinalIgnoreCase) && int.TryParse(optVal, out int reqTo))
                {
                    int to = Math.Clamp(reqTo, 1, 30);
                    negotiatedOptions.Add(("timeout", to.ToString()));
                }
            }

            // 마지막 블록이 정확히 blockSize로 나누어 떨어지면 0바이트 종료 블록 1개 추가 (RFC 1350)
            long totalBlocks = (fileBytes.Length / blockSize) + 1;

            var session = new TftpSession
            {
                RemoteEndPoint = remoteEp,
                FileName = cleanName,
                Data = fileBytes,
                BlockSize = blockSize,
                WindowSize = windowSize,
                TotalBlocks = totalBlocks,
                LastActivityUtc = DateTime.UtcNow
            };

            _sessions[remoteEp] = session;

            lock (session.Sync)
            {
                if (negotiatedOptions.Count > 0)
                {
                    byte[] oack = BuildOackPacket(negotiatedOptions);
                    session.WaitingForOackAck = true;
                    session.LastOackPacket = oack;
                    socket.SendTo(oack, remoteEp);
                }
                else
                {
                    session.WaitingForOackAck = false;
                    SendNextWindow(socket, session);
                }
            }
        }

        private void HandleAck(Socket socket, IPEndPoint remoteEp, ushort ackBlock16)
        {
            if (!_sessions.TryGetValue(remoteEp, out var session)) return;

            bool completed = false;
            lock (session.Sync)
            {
                session.LastActivityUtc = DateTime.UtcNow;
                session.RetryCount = 0;

                if (session.WaitingForOackAck)
                {
                    if (ackBlock16 == 0)
                    {
                        session.WaitingForOackAck = false;
                        session.LastAckedBlock = 0;
                        SendNextWindow(socket, session);
                    }
                    return;
                }

                // 16-bit 블록 번호 롤오버 보정
                long baseBlock = session.LastAckedBlock & ~0xFFFFL;
                long candidate = baseBlock + ackBlock16;
                if (candidate < session.LastAckedBlock)
                {
                    candidate += 65536;
                }

                if (candidate < session.LastAckedBlock || candidate > session.TotalBlocks)
                {
                    return;
                }

                session.LastAckedBlock = candidate;

                if (session.LastAckedBlock >= session.TotalBlocks)
                {
                    completed = true;
                }
                else
                {
                    SendNextWindow(socket, session);
                }
            }

            if (completed)
            {
                _sessions.TryRemove(remoteEp, out _);
                Log($"[TFTP] 전송 완료: {session.FileName} ({session.Data.Length / 1024.0:F1} KB -> {remoteEp.Address})");
                FileTransferCompleted?.Invoke(new TftpTransferCompletedArgs
                {
                    ClientIp = remoteEp.Address.ToString(),
                    FileName = session.FileName,
                    TotalBytes = session.Data.Length
                });
            }
        }

        private static void SendNextWindow(Socket socket, TftpSession session)
        {
            long startBlock = session.LastAckedBlock + 1;
            long endBlock = Math.Min(session.TotalBlocks, session.LastAckedBlock + session.WindowSize);

            for (long blk = startBlock; blk <= endBlock; blk++)
            {
                long offset = (blk - 1) * session.BlockSize;
                int payloadLen = 0;
                if (offset < session.Data.Length)
                {
                    payloadLen = (int)Math.Min(session.BlockSize, session.Data.Length - offset);
                }

                byte[] pkt = new byte[4 + payloadLen];
                pkt[0] = 0;
                pkt[1] = 3; // DATA Opcode
                ushort blk16 = (ushort)(blk & 0xFFFF);
                pkt[2] = (byte)((blk16 >> 8) & 0xFF);
                pkt[3] = (byte)(blk16 & 0xFF);

                if (payloadLen > 0)
                {
                    Buffer.BlockCopy(session.Data, (int)offset, pkt, 4, payloadLen);
                }

                socket.SendTo(pkt, session.RemoteEndPoint);
            }
        }

        private async Task RetransmitWatchdogLoopAsync(Socket socket, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(350, token);
                    DateTime now = DateTime.UtcNow;

                    foreach (var kv in _sessions)
                    {
                        var s = kv.Value;
                        bool shouldRemove = false;

                        lock (s.Sync)
                        {
                            if ((now - s.LastActivityUtc).TotalMilliseconds >= 1200)
                            {
                                s.RetryCount++;
                                if (s.RetryCount > 5)
                                {
                                    shouldRemove = true;
                                }
                                else
                                {
                                    s.LastActivityUtc = now;
                                    try
                                    {
                                        if (s.WaitingForOackAck && s.LastOackPacket != null)
                                        {
                                            socket.SendTo(s.LastOackPacket, s.RemoteEndPoint);
                                        }
                                        else
                                        {
                                            SendNextWindow(socket, s);
                                        }
                                    }
                                    catch { shouldRemove = true; }
                                }
                            }
                        }

                        if (shouldRemove)
                        {
                            _sessions.TryRemove(kv.Key, out _);
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }

        private static byte[] BuildOackPacket(List<(string Key, string Value)> options)
        {
            using var ms = new MemoryStream();
            ms.WriteByte(0);
            ms.WriteByte(6); // OACK Opcode = 6

            foreach (var (k, v) in options)
            {
                byte[] kb = Encoding.ASCII.GetBytes(k);
                ms.Write(kb, 0, kb.Length);
                ms.WriteByte(0);

                byte[] vb = Encoding.ASCII.GetBytes(v);
                ms.Write(vb, 0, vb.Length);
                ms.WriteByte(0);
            }
            return ms.ToArray();
        }

        private static void SendError(Socket socket, IPEndPoint remoteEp, ushort errorCode, string message)
        {
            try
            {
                byte[] msgBytes = Encoding.ASCII.GetBytes(message);
                byte[] pkt = new byte[4 + msgBytes.Length + 1];
                pkt[0] = 0;
                pkt[1] = 5; // ERROR
                pkt[2] = (byte)((errorCode >> 8) & 0xFF);
                pkt[3] = (byte)(errorCode & 0xFF);
                Buffer.BlockCopy(msgBytes, 0, pkt, 4, msgBytes.Length);
                pkt[^1] = 0;
                socket.SendTo(pkt, remoteEp);
            }
            catch { }
        }

        private static List<string> ExtractNullTerminatedStrings(byte[] buf, int startOffset)
        {
            var list = new List<string>();
            int start = startOffset;
            for (int i = startOffset; i < buf.Length; i++)
            {
                if (buf[i] == 0)
                {
                    list.Add(Encoding.ASCII.GetString(buf, start, i - start));
                    start = i + 1;
                }
            }
            return list;
        }

        private void Log(string msg) => LogAdded?.Invoke(msg);

        public void Stop()
        {
            _cts?.Cancel();
            try { _socket?.Close(); } catch { }
            _socket = null;
            _sessions.Clear();
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }
    }
}

