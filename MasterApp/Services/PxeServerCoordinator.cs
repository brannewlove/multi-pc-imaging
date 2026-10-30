using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using MultiDeployOS.Master.Models;

namespace MultiDeployOS.Master.Services
{
    public class PxeClientBootUpdate
    {
        public string MacAddress { get; set; } = "";
        public string IpAddress { get; set; } = "";
        public string Manufacturer { get; set; } = "";
        public string ModelName { get; set; } = "";
        public string SerialNumber { get; set; } = "";
        public string Uuid { get; set; } = "";
        public double Percent { get; set; }
        public double SpeedMbps { get; set; }
        public int ElapsedSeconds { get; set; }
        public string StepMessage { get; set; } = "";
    }

    public class PxeServerCoordinator : IDisposable
    {
        private readonly ConfigManager _configManager;
        public PxeBootAssetManager AssetManager { get; }
        public DhcpPxeService DhcpService { get; }
        public TftpServerService TftpService { get; }
        public HttpBootServerService HttpBootService { get; }

        private readonly ConcurrentDictionary<string, DateTime> _completedMacTimestamps = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _recentLogs = new();
        private readonly object _logLock = new();

        public bool IsRunning => DhcpService.IsRunning && TftpService.IsRunning && HttpBootService.IsRunning;
        public string ActiveBindIp { get; private set; } = "";
        public string LastErrorMessage { get; private set; } = "";

        public event Action<PxeClientBootUpdate>? ClientBootUpdated;
        public event Action<string>? LogAdded;
        public event Action<bool>? RunningStateChanged;

        public PxeServerCoordinator(ConfigManager configManager)
        {
            _configManager = configManager;
            AssetManager = new PxeBootAssetManager();
            DhcpService = new DhcpPxeService();
            TftpService = new TftpServerService(AssetManager);
            HttpBootService = new HttpBootServerService(
                AssetManager,
                macByIpResolver: ip => DhcpService.ResolveMacByIp(ip),
                shouldBypassBootLoop: IsMacInBootLoopGuard,
                getBootWimPath: () => _configManager.ServerConfig.Pxe.BootWimPath,
                getMasterImageDir: () => _configManager.ServerConfig.MasterImageDir);

            // 하위 서비스 이벤트 연결
            DhcpService.LogAdded += AppendLog;
            TftpService.LogAdded += AppendLog;
            HttpBootService.LogAdded += AppendLog;

            DhcpService.ClientBootStageChanged += OnDhcpBootStageChanged;
            TftpService.FileTransferCompleted += OnTftpFileCompleted;
            HttpBootService.ClientIpxeDiscovered += OnClientIpxeDiscovered;
            HttpBootService.BootWimProgressUpdated += OnBootWimProgressUpdated;
        }

        public (bool Success, string Message) Start()
        {
            Stop();
            LastErrorMessage = "";

            var pxeCfg = _configManager.ServerConfig.Pxe;

            // 1. 바인딩할 NIC IP 결정
            IPAddress? bindIp = ResolveTargetBindIp(pxeCfg.BindIpAddress);
            if (bindIp == null)
            {
                LastErrorMessage = "바인딩할 유효한 네트워크 어댑터(IPv4)를 찾을 수 없습니다. PXE 서버 설정에서 어댑터를 선택해주세요.";
                AppendLog($"[오류] {LastErrorMessage}");
                return (false, LastErrorMessage);
            }

            // 2. 포트 충돌 사전 진단
            bool isProxy = string.Equals(pxeCfg.DhcpMode, "PROXY", StringComparison.OrdinalIgnoreCase);
            int httpPort = pxeCfg.HttpBootPort > 0 ? pxeCfg.HttpBootPort : 39202;
            var conflicts = NetworkUtility.CheckPortConflicts(bindIp, isProxy, httpPort);
            if (conflicts.Count > 0)
            {
                LastErrorMessage = string.Join("\n", conflicts);
                AppendLog($"[포트 충돌] {LastErrorMessage}");
                return (false, LastErrorMessage);
            }

            // 3. UEFI 64-bit iPXE + wimboot 에셋 자동 준비 및 boot.wim RAM 캐싱
            var assetStatus = AssetManager.EnsureAssetsAndLoad(pxeCfg.BootWimPath, pxeCfg.CacheBootWimInRam, AppendLog);
            if (!assetStatus.AllCoreBootloadersReady)
            {
                LastErrorMessage = "필수 UEFI 부트로더 에셋 준비 실패:\n" + string.Join("\n", assetStatus.Warnings);
                AppendLog($"[에셋 오류] {LastErrorMessage}");
                return (false, LastErrorMessage);
            }

            if (!assetStatus.BootWimExists)
            {
                AppendLog("[알림] 현재 boot.wim 파일 경로가 지정되지 않았거나 파일이 없습니다. 서버는 가동되며, 나중에 boot.wim을 지정하면 즉시 스트리밍됩니다.");
            }

            // 4. DHCP + TFTP + HTTP 서버 순차 가동
            try
            {
                HttpBootService.Start(bindIp, httpPort);
                TftpService.Start(bindIp);
                DhcpService.Start(pxeCfg, bindIp);

                ActiveBindIp = bindIp.ToString();
                AppendLog($"[통합 가동] UEFI 64-bit iPXE 초고속 부팅 서버 가동 완료! (NIC: {ActiveBindIp}, 모드: {pxeCfg.DhcpMode})");
                RunningStateChanged?.Invoke(true);
                return (true, $"PXE & DHCP 부팅 서버가 정상 가동되었습니다 ({ActiveBindIp}).");
            }
            catch (Exception ex)
            {
                Stop();
                LastErrorMessage = $"PXE 서버 시작 중 오류 발생: {ex.Message}";
                AppendLog($"[시작 실패] {LastErrorMessage}");
                return (false, LastErrorMessage);
            }
        }

        public void Stop()
        {
            bool wasRunning = DhcpService.IsRunning || TftpService.IsRunning || HttpBootService.IsRunning;
            DhcpService.Stop();
            TftpService.Stop();
            HttpBootService.Stop();
            ActiveBindIp = "";

            if (wasRunning)
            {
                AppendLog("[중지] PXE / DHCP / TFTP / HTTP 부팅 서버가 중지되었습니다.");
                RunningStateChanged?.Invoke(false);
            }
        }

        public void MarkMacDeployCompleted(string? macAddress)
        {
            string norm = NetworkUtility.NormalizeMac(macAddress);
            if (!string.IsNullOrEmpty(norm))
            {
                _completedMacTimestamps[norm] = DateTime.UtcNow;
                AppendLog($"[재부팅 보호] 배포 완료 PC 등록 ({norm}) — 재부팅 시 PXE를 우회하여 내장 SSD로 부팅합니다.");
            }
        }

        public void ClearMacDeployCompleted(string? macAddress)
        {
            string norm = NetworkUtility.NormalizeMac(macAddress);
            if (!string.IsNullOrEmpty(norm))
            {
                _completedMacTimestamps.TryRemove(norm, out _);
            }
        }

        public void ClearAllBootLoopGuards()
        {
            _completedMacTimestamps.Clear();
            AppendLog("[재부팅 보호] 배포 완료 우회 목록이 모두 초기화되었습니다.");
        }

        public bool IsMacInBootLoopGuard(string? macAddress)
        {
            var cfg = _configManager.ServerConfig.Pxe;
            if (!cfg.PreventBootLoop) return false;

            string norm = NetworkUtility.NormalizeMac(macAddress);
            if (string.IsNullOrEmpty(norm)) return false;

            if (_completedMacTimestamps.TryGetValue(norm, out DateTime completedUtc))
            {
                int guardMins = Math.Max(1, cfg.BootLoopGuardMinutes);
                if ((DateTime.UtcNow - completedUtc).TotalMinutes <= guardMins)
                {
                    return true;
                }
                _completedMacTimestamps.TryRemove(norm, out _);
            }
            return false;
        }

        public List<string> GetRecentLogsSnapshot()
        {
            lock (_logLock)
            {
                return _recentLogs.ToList();
            }
        }

        private static IPAddress? ResolveTargetBindIp(string? configuredIp)
        {
            if (!string.IsNullOrWhiteSpace(configuredIp) &&
                IPAddress.TryParse(configuredIp.Trim(), out var parsed) &&
                parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                !parsed.Equals(IPAddress.Any))
            {
                return parsed;
            }

            var adapters = NetworkUtility.GetCandidateAdapters();
            var first = adapters.FirstOrDefault();
            if (first != null && IPAddress.TryParse(first.IpAddress, out var autoIp))
            {
                return autoIp;
            }
            return null;
        }

        private void OnDhcpBootStageChanged(DhcpBootEventArgs e)
        {
            if (IsMacInBootLoopGuard(e.MacAddress)) return;

            double pct = e.Stage == "IPXE" ? 15.0 : 5.0;
            ClientBootUpdated?.Invoke(new PxeClientBootUpdate
            {
                MacAddress = e.MacAddress,
                IpAddress = e.AssignedIp,
                Percent = pct,
                StepMessage = e.Detail
            });
        }

        private void OnTftpFileCompleted(TftpTransferCompletedArgs e)
        {
            string? mac = DhcpService.ResolveMacByIp(e.ClientIp);
            if (string.IsNullOrEmpty(mac) || IsMacInBootLoopGuard(mac)) return;

            ClientBootUpdated?.Invoke(new PxeClientBootUpdate
            {
                MacAddress = mac,
                IpAddress = e.ClientIp,
                Percent = 10.0,
                StepMessage = $"UEFI 부트로더({e.FileName}) 전송 완료 -> iPXE 초기화 중"
            });
        }

        private void OnClientIpxeDiscovered(IpxeClientBootInfo info)
        {
            ClientBootUpdated?.Invoke(new PxeClientBootUpdate
            {
                MacAddress = info.MacAddress,
                IpAddress = info.IpAddress,
                Manufacturer = info.Manufacturer,
                ModelName = info.ModelName,
                SerialNumber = info.SerialNumber,
                Uuid = info.Uuid,
                Percent = 20.0,
                StepMessage = "SMBIOS 식별 완료 -> WinPE(boot.wim) HTTP 초고속 전송 시작"
            });
        }

        private void OnBootWimProgressUpdated(BootWimProgressArgs e)
        {
            // 전체 PXE 부팅 진행률 중 boot.wim 전송을 20% ~ 95% 구간에 매핑, 완료 시 98% (WinPE 커널 초기화 대기)
            double mappedPct = e.IsCompleted ? 98.0 : (20.0 + (e.Percent * 0.75));
            string stepMsg = e.IsCompleted
                ? "boot.wim 전송 완료! WinPE 램디스크 부팅 및 에이전트 실행 중..."
                : $"WinPE 부트 이미지(boot.wim) 초고속 전송 중 ({(int)e.Percent}%, {e.SpeedMbps:F1} MB/s)";

            ClientBootUpdated?.Invoke(new PxeClientBootUpdate
            {
                MacAddress = e.MacAddress,
                IpAddress = e.IpAddress,
                Percent = mappedPct,
                SpeedMbps = e.SpeedMbps,
                ElapsedSeconds = e.ElapsedSeconds,
                StepMessage = stepMsg
            });
        }

        private void AppendLog(string msg)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            lock (_logLock)
            {
                _recentLogs.Add(line);
                if (_recentLogs.Count > 500)
                {
                    _recentLogs.RemoveAt(0);
                }
            }
            Console.WriteLine(line);
            LogAdded?.Invoke(line);
        }

        public void Dispose()
        {
            Stop();
            DhcpService.Dispose();
            TftpService.Dispose();
            HttpBootService.Dispose();
        }
    }
}

