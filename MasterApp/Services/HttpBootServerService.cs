using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MultiDeployOS.Master.Services
{
    public class IpxeClientBootInfo
    {
        public string MacAddress { get; set; } = "";
        public string IpAddress { get; set; } = "";
        public string Manufacturer { get; set; } = "";
        public string ModelName { get; set; } = "";
        public string SerialNumber { get; set; } = "";
        public string Uuid { get; set; } = "";
    }

    public class BootWimProgressArgs
    {
        public string MacAddress { get; set; } = "";
        public string IpAddress { get; set; } = "";
        public double Percent { get; set; }
        public double SpeedMbps { get; set; }
        public int ElapsedSeconds { get; set; }
        public bool IsCompleted { get; set; }
    }

    public class HttpBootServerService : IDisposable
    {
        private readonly PxeBootAssetManager _assetManager;
        private readonly Func<string, string?> _macByIpResolver;
        private readonly Func<string, bool> _shouldBypassBootLoop;
        private readonly Func<string> _getBootWimPath;
        private readonly Func<string> _getMasterImageDir;

        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private IPAddress _preferredMasterIp = IPAddress.Loopback;
        private int _port = 39202;

        public event Action<IpxeClientBootInfo>? ClientIpxeDiscovered;
        public event Action<BootWimProgressArgs>? BootWimProgressUpdated;
        public event Action<string, string>? ClientBypassedToLocalBoot; // (mac, ip)
        public event Action<string>? LogAdded;

        public bool IsRunning => _listener != null;
        public int Port => _port;

        public HttpBootServerService(
            PxeBootAssetManager assetManager,
            Func<string, string?> macByIpResolver,
            Func<string, bool> shouldBypassBootLoop,
            Func<string> getBootWimPath,
            Func<string> getMasterImageDir)
        {
            _assetManager = assetManager;
            _macByIpResolver = macByIpResolver;
            _shouldBypassBootLoop = shouldBypassBootLoop;
            _getBootWimPath = getBootWimPath;
            _getMasterImageDir = getMasterImageDir;
        }

        public void Start(IPAddress bindIp, int port)
        {
            Stop();

            _preferredMasterIp = bindIp;
            _port = port > 0 ? port : 39202;
            _cts = new CancellationTokenSource();

            try
            {
                _listener = new TcpListener(IPAddress.Any, _port);
                _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listener.Start(128);
            }
            catch
            {
                // IPAddress.Any 충돌 시 지정된 어댑터 IP로 폴백 바인딩
                _listener = new TcpListener(bindIp, _port);
                _listener.Start(128);
            }

            Task.Run(() => AcceptLoopAsync(_listener, _cts.Token));
            Log($"[HTTP 부트] 초고속 iPXE HTTP 스트리밍 서버 가동 (http://{bindIp}:{_port})");
        }

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    TcpClient client = await listener.AcceptTcpClientAsync(token);
                    _ = Task.Run(() => HandleClientAsync(client, token), token);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        Log($"[HTTP 오류] 연결 수락 예외: {ex.Message}");
                    }
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken token)
        {
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    client.SendBufferSize = 1024 * 1024; // 1MB 고속 송신 버퍼

                    string remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "";
                    string localServerIp = (client.Client.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? _preferredMasterIp.ToString();
                    if (localServerIp == "0.0.0.0") localServerIp = _preferredMasterIp.ToString();

                    using NetworkStream stream = client.GetStream();

                    // HTTP 요청 헤더 읽기 (\r\n\r\n 까지)
                    string? headerBlock = await ReadHttpHeaderAsync(stream, token);
                    if (string.IsNullOrEmpty(headerBlock)) return;

                    var lines = headerBlock.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length == 0) return;

                    var reqParts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (reqParts.Length < 2) return;

                    string method = reqParts[0].ToUpperInvariant();
                    if (method != "GET" && method != "HEAD")
                    {
                        await SendSimpleResponseAsync(stream, "405 Method Not Allowed", "text/plain", "Method Not Allowed", isHead: true, token);
                        return;
                    }

                    bool isHead = method == "HEAD";
                    string rawTarget = reqParts[1];
                    string pathPart = rawTarget;
                    string queryPart = "";
                    int qIdx = rawTarget.IndexOf('?');
                    if (qIdx >= 0)
                    {
                        pathPart = rawTarget.Substring(0, qIdx);
                        queryPart = rawTarget.Substring(qIdx + 1);
                    }

                    var query = ParseQueryString(queryPart);
                    string? rangeHeader = ExtractHeaderValue(lines, "Range");

                    // 1. /boot.ipxe (iPXE 1차 진입 스크립트 — SMBIOS 하드웨어 인벤토리 수집 체인)
                    if (pathPart.Equals("/boot.ipxe", StringComparison.OrdinalIgnoreCase))
                    {
                        string script =
                            "#!ipxe\n" +
                            $"chain http://{localServerIp}:{_port}/start.ipxe?mac=${{net0/mac}}&ip=${{ip}}&mfg=${{manufacturer:uristring}}&model=${{product:uristring}}&sn=${{serial:uristring}}&uuid=${{uuid}} || " +
                            $"chain http://{localServerIp}:{_port}/start.ipxe\n";

                        await SendSimpleResponseAsync(stream, "200 OK", "text/plain; charset=utf-8", script, isHead, token);
                        return;
                    }

                    // 2. /start.ipxe (하드웨어 식별 + 무한부팅 방지 체크 + wimboot 초고속 부트 스크립트)
                    if (pathPart.Equals("/start.ipxe", StringComparison.OrdinalIgnoreCase))
                    {
                        string mac = NetworkUtility.NormalizeMac(query.GetValueOrDefault("mac"));
                        if (string.IsNullOrEmpty(mac))
                        {
                            mac = NetworkUtility.NormalizeMac(_macByIpResolver(remoteIp));
                        }

                        string clientIp = query.GetValueOrDefault("ip") ?? "";
                        if (string.IsNullOrWhiteSpace(clientIp)) clientIp = remoteIp;

                        string mfg = query.GetValueOrDefault("mfg")?.Trim() ?? "";
                        string model = query.GetValueOrDefault("model")?.Trim() ?? "";
                        string sn = query.GetValueOrDefault("sn")?.Trim() ?? "";
                        string uuid = query.GetValueOrDefault("uuid")?.Trim() ?? "";

                        // 배포 완료 PC 무한 재부팅 방지 (Boot Loop Guard)
                        if (!string.IsNullOrEmpty(mac) && _shouldBypassBootLoop(mac))
                        {
                            Log($"[PXE 우회] 이미 배포 완료된 PC({mac} / {clientIp}) 재부팅 감지 -> 내장 NVMe/SSD 부팅으로 자동 전환");
                            ClientBypassedToLocalBoot?.Invoke(mac, clientIp);

                            string exitScript =
                                "#!ipxe\n" +
                                "echo ================================================================\n" +
                                $"echo  [MultiDeployOS] Imaging Already Completed ({mac})\n" +
                                "echo  Bypassing Network Boot -> Starting Local SSD OS...\n" +
                                "echo ================================================================\n" +
                                "sleep 2\n" +
                                "exit\n";

                            await SendSimpleResponseAsync(stream, "200 OK", "text/plain; charset=utf-8", exitScript, isHead, token);
                            return;
                        }

                        // boot.wim 준비 상태 확인
                        string wimPath = PxeBootAssetManager.NormalizeWimPath(_getBootWimPath());
                        if (string.IsNullOrEmpty(wimPath) || !File.Exists(wimPath))
                        {
                            Log($"[PXE 대기] 클라이언트({mac} / {clientIp}) 부팅 대기 중 — 마스터에 boot.wim 경로가 아직 지정되지 않았습니다.");
                            string waitScript =
                                "#!ipxe\n" +
                                "echo ================================================================\n" +
                                "echo  [MultiDeployOS] Waiting for Master boot.wim configuration...\n" +
                                "echo  Please select boot.wim in Master PXE Settings.\n" +
                                "echo ================================================================\n" +
                                "sleep 5\n" +
                                $"chain http://{localServerIp}:{_port}/start.ipxe?mac={Uri.EscapeDataString(mac)}&ip={Uri.EscapeDataString(clientIp)}\n";

                            await SendSimpleResponseAsync(stream, "200 OK", "text/plain; charset=utf-8", waitScript, isHead, token);
                            return;
                        }

                        if (!string.IsNullOrEmpty(mac))
                        {
                            ClientIpxeDiscovered?.Invoke(new IpxeClientBootInfo
                            {
                                MacAddress = mac,
                                IpAddress = clientIp,
                                Manufacturer = mfg,
                                ModelName = model,
                                SerialNumber = sn,
                                Uuid = uuid
                            });
                        }

                        Log($"[HTTP 부트] UEFI 64-bit wimboot 스크립트 전송 ({mac} | {mfg} {model} | {clientIp})");

                        string baseUrl = $"http://{localServerIp}:{_port}";
                        string escapedMac = Uri.EscapeDataString(mac);
                        string bootScript =
                            "#!ipxe\n" +
                            $"set base {baseUrl}\n" +
                            "echo ================================================================\n" +
                            "echo  [MultiDeployOS] UEFI 64-bit High-Speed HTTP Boot (wimboot)\n" +
                            $"echo  Target: {SanitizeIpxeEcho(mfg)} {SanitizeIpxeEcho(model)} ({mac} / {clientIp})\n" +
                            "echo ================================================================\n" +
                            "kernel ${base}/pxe/wimboot\n" +
                            "initrd -n bootmgfw.efi ${base}/pxe/bootmgfw.efi bootmgfw.efi\n" +
                            "initrd -n BCD          ${base}/pxe/BCD          BCD\n" +
                            "initrd -n boot.sdi     ${base}/pxe/boot.sdi     boot.sdi\n" +
                            $"initrd -n boot.wim     ${{base}}/pxe/boot.wim?mac={escapedMac} boot.wim\n" +
                            "boot || goto failed\n\n" +
                            ":failed\n" +
                            "echo [ERROR] WinPE HTTP boot failed. Retrying in 5 seconds...\n" +
                            "sleep 5\n" +
                            "reboot\n";

                        await SendSimpleResponseAsync(stream, "200 OK", "text/plain; charset=utf-8", bootScript, isHead, token);
                        return;
                    }

                    // 3. /pxe/boot.wim (대용량 RAM 캐시 또는 스트리밍 + 실시간 전송률 추적)
                    if (pathPart.Equals("/pxe/boot.wim", StringComparison.OrdinalIgnoreCase) ||
                        pathPart.Equals("/sources/boot.wim", StringComparison.OrdinalIgnoreCase))
                    {
                        string mac = NetworkUtility.NormalizeMac(query.GetValueOrDefault("mac"));
                        if (string.IsNullOrEmpty(mac))
                        {
                            mac = NetworkUtility.NormalizeMac(_macByIpResolver(remoteIp));
                        }

                        await StreamBootWimAsync(stream, mac, remoteIp, isHead, rangeHeader, token);
                        return;
                    }

                    // 4. /pxe/<small_asset> (wimboot, bootmgfw.efi, BCD, boot.sdi, snponly.efi)
                    if (pathPart.StartsWith("/pxe/", StringComparison.OrdinalIgnoreCase) ||
                        pathPart.StartsWith("/boot/", StringComparison.OrdinalIgnoreCase))
                    {
                        string assetName = Path.GetFileName(pathPart);
                        if (_assetManager.TryGetSmallAssetBytes(assetName, out byte[] assetBytes))
                        {
                            await SendBytesWithRangeAsync(stream, assetBytes, "application/octet-stream", isHead, rangeHeader, token);
                            return;
                        }
                    }

                    // 5. /images/<os_wim_file> (OS 배포용 WIM HTTP 호환 엔드포인트)
                    if (pathPart.StartsWith("/images/", StringComparison.OrdinalIgnoreCase))
                    {
                        string encodedFile = pathPart.Substring("/images/".Length);
                        string fileName = Path.GetFileName(Uri.UnescapeDataString(encodedFile));
                        string masterDir = _getMasterImageDir();
                        if (!string.IsNullOrWhiteSpace(masterDir) && !string.IsNullOrWhiteSpace(fileName))
                        {
                            string fullWimPath = Path.Combine(masterDir, fileName);
                            if (File.Exists(fullWimPath))
                            {
                                await StreamDiskFileAsync(stream, fullWimPath, isHead, rangeHeader, onChunkProgress: null, token);
                                return;
                            }
                        }
                    }

                    await SendSimpleResponseAsync(stream, "404 Not Found", "text/plain", "Not Found", isHead, token);
                }
                catch (OperationCanceledException) { }
                catch (IOException) { } // 클라이언트가 HEAD/프로브 후 소켓을 먼저 닫은 경우
                catch (Exception ex)
                {
                    Log($"[HTTP 세션 예외] {ex.Message}");
                }
            }
        }

        private async Task StreamBootWimAsync(
            NetworkStream stream,
            string mac,
            string remoteIp,
            bool isHead,
            string? rangeHeader,
            CancellationToken token)
        {
            string configuredPath = PxeBootAssetManager.NormalizeWimPath(_getBootWimPath());
            byte[]? ramBytes = _assetManager.GetCachedBootWimBytes(configuredPath);

            if (ramBytes == null && (!File.Exists(configuredPath)))
            {
                await SendSimpleResponseAsync(stream, "404 Not Found", "text/plain", "boot.wim not found", isHead, token);
                return;
            }

            if (isHead)
            {
                long totalLen = ramBytes?.LongLength ?? new FileInfo(configuredPath).Length;
                string headHdr =
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: application/octet-stream\r\n" +
                    $"Content-Length: {totalLen}\r\n" +
                    "Accept-Ranges: bytes\r\n" +
                    "Connection: close\r\n\r\n";
                byte[] hb = Encoding.ASCII.GetBytes(headHdr);
                await stream.WriteAsync(hb, token);
                return;
            }

            var sw = Stopwatch.StartNew();
            long lastReportMs = 0;

            void ReportProgress(long sent, long total, bool completed)
            {
                long elapsedMs = Math.Max(1, sw.ElapsedMilliseconds);
                if (!completed && elapsedMs - lastReportMs < 200) return;
                lastReportMs = elapsedMs;

                double pct = total > 0 ? Math.Min(100.0, (sent * 100.0) / total) : 100.0;
                double mbps = (sent / (1024.0 * 1024.0)) / (elapsedMs / 1000.0);

                if (!string.IsNullOrEmpty(mac))
                {
                    BootWimProgressUpdated?.Invoke(new BootWimProgressArgs
                    {
                        MacAddress = mac,
                        IpAddress = remoteIp,
                        Percent = completed ? 100.0 : pct,
                        SpeedMbps = Math.Round(mbps, 1),
                        ElapsedSeconds = (int)(elapsedMs / 1000),
                        IsCompleted = completed
                    });
                }
            }

            if (ramBytes != null)
            {
                Log($"[HTTP 스트리밍] boot.wim RAM 초고속 전송 시작 ({ramBytes.Length / (1024.0 * 1024.0):F1} MB -> {remoteIp} [{mac}])");
                await SendBytesWithRangeAsync(stream, ramBytes, "application/octet-stream", false, rangeHeader, token, ReportProgress);
            }
            else
            {
                var fi = new FileInfo(configuredPath);
                Log($"[HTTP 스트리밍] boot.wim 디스크 전송 시작 ({fi.Length / (1024.0 * 1024.0):F1} MB -> {remoteIp} [{mac}])");
                await StreamDiskFileAsync(stream, configuredPath, false, rangeHeader, ReportProgress, token);
            }

            double sec = Math.Max(0.1, sw.Elapsed.TotalSeconds);
            long totalSize = ramBytes?.LongLength ?? new FileInfo(configuredPath).Length;
            double avgSpeed = (totalSize / (1024.0 * 1024.0)) / sec;
            Log($"[HTTP 완료] boot.wim 전송 완료! ({remoteIp} [{mac}], {sec:F1}초 소요, 평균 {avgSpeed:F1} MB/s)");
        }

        private static async Task SendBytesWithRangeAsync(
            NetworkStream stream,
            byte[] data,
            string contentType,
            bool isHead,
            string? rangeHeader,
            CancellationToken token,
            Action<long, long, bool>? onProgress = null)
        {
            long total = data.LongLength;
            long start = 0;
            long end = total - 1;
            bool isPartial = false;

            if (!string.IsNullOrEmpty(rangeHeader) && TryParseRange(rangeHeader, total, out long rStart, out long rEnd))
            {
                start = rStart;
                end = rEnd;
                isPartial = true;
            }

            long contentLen = total == 0 ? 0 : (end - start + 1);
            string statusLine = isPartial ? "HTTP/1.1 206 Partial Content" : "HTTP/1.1 200 OK";
            var sb = new StringBuilder();
            sb.Append(statusLine).Append("\r\n");
            sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(contentLen).Append("\r\n");
            sb.Append("Accept-Ranges: bytes\r\n");
            if (isPartial)
            {
                sb.Append($"Content-Range: bytes {start}-{end}/{total}\r\n");
            }
            sb.Append("Connection: close\r\n\r\n");

            byte[] hdrBytes = Encoding.ASCII.GetBytes(sb.ToString());
            await stream.WriteAsync(hdrBytes, token);

            if (isHead || contentLen <= 0) return;

            const int chunkSize = 131072; // 128 KB 청크
            long pos = start;
            long sent = 0;

            while (pos <= end)
            {
                int toWrite = (int)Math.Min(chunkSize, end - pos + 1);
                await stream.WriteAsync(data.AsMemory((int)pos, toWrite), token);
                pos += toWrite;
                sent += toWrite;
                onProgress?.Invoke(start + sent, total, pos > end);
            }
            await stream.FlushAsync(token);
        }

        private static async Task StreamDiskFileAsync(
            NetworkStream stream,
            string filePath,
            bool isHead,
            string? rangeHeader,
            Action<long, long, bool>? onChunkProgress,
            CancellationToken token)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
            long total = fs.Length;
            long start = 0;
            long end = total - 1;
            bool isPartial = false;

            if (!string.IsNullOrEmpty(rangeHeader) && TryParseRange(rangeHeader, total, out long rStart, out long rEnd))
            {
                start = rStart;
                end = rEnd;
                isPartial = true;
            }

            long contentLen = total == 0 ? 0 : (end - start + 1);
            var sb = new StringBuilder();
            sb.Append(isPartial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
            sb.Append("Content-Type: application/octet-stream\r\n");
            sb.Append($"Content-Length: {contentLen}\r\n");
            sb.Append("Accept-Ranges: bytes\r\n");
            if (isPartial)
            {
                sb.Append($"Content-Range: bytes {start}-{end}/{total}\r\n");
            }
            sb.Append("Connection: close\r\n\r\n");

            byte[] hdr = Encoding.ASCII.GetBytes(sb.ToString());
            await stream.WriteAsync(hdr, token);

            if (isHead || contentLen <= 0) return;

            fs.Seek(start, SeekOrigin.Begin);
            byte[] buf = new byte[131072];
            long remaining = contentLen;
            long sent = 0;

            while (remaining > 0)
            {
                int toRead = (int)Math.Min(buf.Length, remaining);
                int read = await fs.ReadAsync(buf.AsMemory(0, toRead), token);
                if (read <= 0) break;

                await stream.WriteAsync(buf.AsMemory(0, read), token);
                remaining -= read;
                sent += read;
                onChunkProgress?.Invoke(start + sent, total, remaining <= 0);
            }
            await stream.FlushAsync(token);
        }

        private static async Task SendSimpleResponseAsync(
            NetworkStream stream,
            string status,
            string contentType,
            string body,
            bool isHead,
            CancellationToken token)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            string header =
                $"HTTP/1.1 {status}\r\n" +
                $"Content-Type: {contentType}\r\n" +
                $"Content-Length: {bodyBytes.Length}\r\n" +
                "Cache-Control: no-cache\r\n" +
                "Connection: close\r\n\r\n";

            byte[] hdrBytes = Encoding.ASCII.GetBytes(header);
            await stream.WriteAsync(hdrBytes, token);
            if (!isHead && bodyBytes.Length > 0)
            {
                await stream.WriteAsync(bodyBytes, token);
            }
            await stream.FlushAsync(token);
        }

        private static async Task<string?> ReadHttpHeaderAsync(NetworkStream stream, CancellationToken token)
        {
            byte[] buf = new byte[8192];
            int totalRead = 0;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            while (totalRead < buf.Length)
            {
                int r = await stream.ReadAsync(buf.AsMemory(totalRead, buf.Length - totalRead), timeoutCts.Token);
                if (r <= 0) break;
                totalRead += r;

                string text = Encoding.ASCII.GetString(buf, 0, totalRead);
                int endIdx = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (endIdx >= 0)
                {
                    return text.Substring(0, endIdx);
                }
            }
            return totalRead > 0 ? Encoding.ASCII.GetString(buf, 0, totalRead) : null;
        }

        private static Dictionary<string, string> ParseQueryString(string query)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(query)) return dict;

            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0)
                {
                    string k = Uri.UnescapeDataString(pair.Substring(0, eq));
                    string v = Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
                    dict[k] = v;
                }
            }
            return dict;
        }

        private static string? ExtractHeaderValue(string[] lines, string headerName)
        {
            string prefix = headerName + ":";
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return lines[i].Substring(prefix.Length).Trim();
                }
            }
            return null;
        }

        private static bool TryParseRange(string rangeHeader, long totalLength, out long start, out long end)
        {
            start = 0;
            end = totalLength - 1;
            if (!rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;

            string spec = rangeHeader.Substring(6).Trim();
            int dash = spec.IndexOf('-');
            if (dash < 0) return false;

            string left = spec.Substring(0, dash).Trim();
            string right = spec.Substring(dash + 1).Trim();

            if (long.TryParse(left, out long s))
            {
                start = s;
                if (!string.IsNullOrEmpty(right) && long.TryParse(right, out long e))
                {
                    end = Math.Min(e, totalLength - 1);
                }
            }
            else if (long.TryParse(right, out long suffixLen) && suffixLen > 0)
            {
                start = Math.Max(0, totalLength - suffixLen);
            }
            else
            {
                return false;
            }

            return start >= 0 && start <= end && start < totalLength;
        }

        private static string SanitizeIpxeEcho(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            var sb = new StringBuilder(input.Length);
            foreach (char c in input)
            {
                if (c >= 0x20 && c <= 0x7E && c != '$' && c != ';' && c != '&' && c != '|')
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private void Log(string msg) => LogAdded?.Invoke(msg);

        public void Stop()
        {
            _cts?.Cancel();
            try { _listener?.Stop(); } catch { }
            _listener = null;
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }
    }
}

