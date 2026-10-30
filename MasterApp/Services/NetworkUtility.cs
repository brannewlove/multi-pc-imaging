using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace MultiDeployOS.Master.Services
{
    public class NetworkAdapterInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string IpAddress { get; set; } = "";
        public string SubnetMask { get; set; } = "255.255.255.0";
        public string GatewayIp { get; set; } = "";
        public string MacAddress { get; set; } = "";
        public long SpeedMbps { get; set; }
        public bool IsUp { get; set; }
        public bool IsWiredEthernet { get; set; }

        public override string ToString()
        {
            string stateTag = IsUp ? "연결됨" : "대기/미연결";
            string typeTag = IsWiredEthernet ? "유선LAN" : "기타NIC";
            string speedText = SpeedMbps > 0 && SpeedMbps < 1_000_000 ? $", {SpeedMbps}Mbps" : "";
            return $"[{typeTag}/{stateTag}] {IpAddress} - {Name} ({Description}{speedText})";
        }
    }

    public static class NetworkUtility
    {
        public static List<NetworkAdapterInfo> GetCandidateAdapters()
        {
            var result = new List<NetworkAdapterInfo>();

            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                    {
                        continue;
                    }

                    var ipProps = nic.GetIPProperties();
                    var gateway = ipProps.GatewayAddresses
                        .Select(g => g.Address)
                        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(a))
                        ?.ToString() ?? "";

                    byte[] macBytes = nic.GetPhysicalAddress().GetAddressBytes();
                    string macStr = macBytes.Length >= 6 ? NormalizeMac(macBytes, 0, 6) : "";

                    long speedMbps = nic.Speed > 0 ? nic.Speed / 1_000_000 : 0;
                    bool isUp = nic.OperationalStatus == OperationalStatus.Up;
                    bool isWired = nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                                   nic.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet ||
                                   nic.NetworkInterfaceType == NetworkInterfaceType.FastEthernetT;

                    foreach (var ua in ipProps.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (IPAddress.IsLoopback(ua.Address)) continue;

                        string ipStr = ua.Address.ToString();
                        string maskStr = ua.IPv4Mask?.ToString() ?? "255.255.255.0";
                        if (maskStr == "0.0.0.0") maskStr = "255.255.255.0";

                        result.Add(new NetworkAdapterInfo
                        {
                            Id = nic.Id,
                            Name = nic.Name,
                            Description = nic.Description,
                            IpAddress = ipStr,
                            SubnetMask = maskStr,
                            GatewayIp = gateway,
                            MacAddress = macStr,
                            SpeedMbps = speedMbps,
                            IsUp = isUp,
                            IsWiredEthernet = isWired
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[-] NIC 목록 조회 오류: {ex.Message}");
            }

            // 정렬 우선순위: 1) UP 상태 유선 이더넷(APIPA 169.254 제외) -> 2) UP 상태 기타 -> 3) 고정 IP가 있는 대기중 유선 이더넷
            return result
                .OrderByDescending(a => a.IsUp && !a.IpAddress.StartsWith("169.254."))
                .ThenByDescending(a => a.IsWiredEthernet)
                .ThenByDescending(a => !a.IpAddress.StartsWith("169.254."))
                .ThenByDescending(a => a.SpeedMbps)
                .ToList();
        }

        public static string NormalizeMac(byte[] buffer, int offset, int length = 6)
        {
            if (buffer == null || buffer.Length < offset + length || length < 6) return "";
            return $"{buffer[offset]:X2}:{buffer[offset + 1]:X2}:{buffer[offset + 2]:X2}:{buffer[offset + 3]:X2}:{buffer[offset + 4]:X2}:{buffer[offset + 5]:X2}";
        }

        public static string NormalizeMac(string? rawMac)
        {
            if (string.IsNullOrWhiteSpace(rawMac)) return "";
            var hexChars = rawMac.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray();
            if (hexChars.Length < 12) return rawMac.Trim().ToUpperInvariant();

            return $"{hexChars[0]}{hexChars[1]}:{hexChars[2]}{hexChars[3]}:{hexChars[4]}{hexChars[5]}:{hexChars[6]}{hexChars[7]}:{hexChars[8]}{hexChars[9]}:{hexChars[10]}{hexChars[11]}";
        }

        public static bool TryIpToUint(string? ipStr, out uint value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(ipStr)) return false;
            if (!IPAddress.TryParse(ipStr.Trim(), out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                return false;

            byte[] b = ip.GetAddressBytes();
            value = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
            return true;
        }

        public static string UintToIp(uint value)
        {
            return $"{(value >> 24) & 0xFF}.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}";
        }

        public static IPAddress UintToIpAddress(uint value)
        {
            return new IPAddress(new byte[]
            {
                (byte)((value >> 24) & 0xFF),
                (byte)((value >> 16) & 0xFF),
                (byte)((value >> 8) & 0xFF),
                (byte)(value & 0xFF)
            });
        }

        public static bool IsInSameSubnet(uint ipA, uint ipB, uint subnetMask)
        {
            return (ipA & subnetMask) == (ipB & subnetMask);
        }

        public static (string StartIp, string EndIp) SuggestPoolRange(string bindIp, string subnetMask)
        {
            if (!TryIpToUint(bindIp, out uint ipVal) || !TryIpToUint(subnetMask, out uint maskVal) || maskVal == 0xFFFFFFFF)
            {
                return ("192.168.77.100", "192.168.77.250");
            }

            uint network = ipVal & maskVal;
            uint broadcast = network | ~maskVal;
            uint totalHosts = broadcast - network;

            if (totalHosts >= 255)
            {
                // 일반적인 /24 이상 대역: .100 ~ .250 제안 (만약 마스터 IP가 그 안에 있으면 충돌 없도록 임대기에서 자동 회피)
                uint start = network + 100;
                uint end = network + 250;
                if (end >= broadcast) end = broadcast - 2;
                if (start >= end) start = network + 10;
                return (UintToIp(start), UintToIp(end));
            }
            else if (totalHosts > 6)
            {
                uint start = network + 2;
                uint end = broadcast - 2;
                if (ipVal == start && start + 1 < end) start++;
                return (UintToIp(start), UintToIp(end));
            }

            return ("192.168.77.100", "192.168.77.250");
        }

        public static List<string> CheckPortConflicts(IPAddress bindIp, bool checkProxy4011, int httpBootPort)
        {
            var issues = new List<string>();

            TestUdpBind(bindIp, 67, "DHCP(UDP 67)", "Windows 인터넷 연결 공유(ICS/SharedAccess), WDS, VMware DHCP 또는 TFTPD64", issues);
            if (checkProxy4011)
            {
                TestUdpBind(bindIp, 4011, "ProxyDHCP(UDP 4011)", "WDS 또는 타 PXE 서버", issues);
            }
            TestUdpBind(bindIp, 69, "TFTP(UDP 69)", "TFTPD64, Serva 또는 WDS TFTP 서비스", issues);
            TestTcpBind(bindIp, httpBootPort, $"HTTP 부트 서버(TCP {httpBootPort})", "동일 포트를 사용 중인 다른 프로세스", issues);

            return issues;
        }

        private static void TestUdpBind(IPAddress bindIp, int port, string label, string hint, List<string> issues)
        {
            try
            {
                using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                // ExclusiveAddressUse로 실제 점유 여부를 엄격 테스트하지 않고 실제 서버와 동일 조건 확인
                sock.Bind(new IPEndPoint(bindIp, port));
            }
            catch (SocketException ex)
            {
                string owner = FindPortOwner("UDP", port);
                issues.Add($"{label} 바인딩 불가 ({ex.SocketErrorCode}){owner} — {hint} 종료 여부를 확인하세요.");
            }
        }

        private static void TestTcpBind(IPAddress bindIp, int port, string label, string hint, List<string> issues)
        {
            try
            {
                using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                sock.Bind(new IPEndPoint(bindIp, port));
            }
            catch (SocketException ex)
            {
                string owner = FindPortOwner("TCP", port);
                issues.Add($"{label} 바인딩 불가 ({ex.SocketErrorCode}){owner} — {hint}");
            }
        }

        private static string FindPortOwner(string proto, int port)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netstat.exe",
                    Arguments = "-ano",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return "";
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(1500);

                string portToken = $":{port} ";
                foreach (var line in output.Split('\n'))
                {
                    if (!line.Contains(proto, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!line.Contains(portToken)) continue;

                    var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 4 && int.TryParse(parts[^1], out int pid) && pid > 0)
                    {
                        try
                        {
                            using var p = Process.GetProcessById(pid);
                            return $" [점유 프로세스: {p.ProcessName}.exe (PID:{pid})]";
                        }
                        catch
                        {
                            return $" [점유 PID:{pid}]";
                        }
                    }
                }
            }
            catch { }
            return "";
        }

        public static bool IsRunningAsAdministrator()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        public static async Task<(bool Success, string Message)> RegisterFirewallRulesAsync(int httpBootPort)
        {
            string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? ApplicationExeFallback();
            string scriptPath = Path.Combine(Path.GetTempPath(), $"multideploy_fw_{DateTime.Now.Ticks}.cmd");

            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("netsh advfirewall firewall delete rule name=\"MultiDeployOS Master PXE\" >nul 2>&1");
            sb.AppendLine($"netsh advfirewall firewall add rule name=\"MultiDeployOS Master PXE\" dir=in action=allow program=\"{exePath}\" enable=yes profile=any");
            sb.AppendLine("netsh advfirewall firewall add rule name=\"MultiDeployOS Master PXE\" dir=in action=allow protocol=UDP localport=67,68,69,4011,39200 profile=any");
            sb.AppendLine($"netsh advfirewall firewall add rule name=\"MultiDeployOS Master PXE\" dir=in action=allow protocol=TCP localport=39201,{httpBootPort} profile=any");

            await File.WriteAllTextAsync(scriptPath, sb.ToString(), Encoding.Default);

            try
            {
                bool isAdmin = IsRunningAsAdministrator();
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{scriptPath}\"",
                    UseShellExecute = !isAdmin,
                    Verb = isAdmin ? "" : "runas",
                    CreateNoWindow = isAdmin,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using var proc = Process.Start(psi);
                if (proc == null) return (false, "방화벽 설정 프로세스를 시작하지 못했습니다.");
                await proc.WaitForExitAsync();

                return proc.ExitCode == 0
                    ? (true, $"방화벽 인바운드 허용 규칙이 등록되었습니다 (DHCP:67, TFTP:69, HTTP:{httpBootPort}, Agent:39200~39201).")
                    : (false, $"방화벽 명령 종료 코드: {proc.ExitCode}");
            }
            catch (Exception ex)
            {
                return (false, $"방화벽 규칙 등록 취소 또는 실패: {ex.Message}");
            }
            finally
            {
                try { if (File.Exists(scriptPath)) File.Delete(scriptPath); } catch { }
            }
        }

        private static string ApplicationExeFallback()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MultiDeployMaster.exe");
        }
    }
}

