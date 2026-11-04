using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace MultiDeployOS.Master.Services
{
    public class BootAssetStatus
    {
        public bool SnponlyReady { get; set; }
        public bool WimbootReady { get; set; }
        public bool BootmgfwReady { get; set; }
        public bool BcdReady { get; set; }
        public bool BootSdiReady { get; set; }
        public bool BootWimExists { get; set; }
        public bool BootWimCachedInRam { get; set; }
        public long BootWimSizeBytes { get; set; }
        public string PxeAssetDirectory { get; set; } = "";
        public List<string> Warnings { get; set; } = new();

        public bool AllCoreBootloadersReady => SnponlyReady && WimbootReady && BootmgfwReady && BcdReady && BootSdiReady;
        public bool FullyReadyForBoot => AllCoreBootloadersReady && BootWimExists;
    }

    public class PxeBootAssetManager
    {
        private readonly object _sync = new();
        public string PxeDir { get; }

        private readonly Dictionary<string, byte[]> _smallAssets = new(StringComparer.OrdinalIgnoreCase);

        // boot.wim 인메모리 캐시 (다중 PC 동시 부팅 시 마스터 디스크 I/O 0% 보장)
        private byte[]? _cachedBootWimBytes;
        private string _cachedBootWimPath = "";
        private DateTime _cachedBootWimWriteTimeUtc = DateTime.MinValue;
        private long _cachedBootWimLength = -1;

        public const long MaxRamCacheBytes = 1_600L * 1024 * 1024; // 1.6 GB 이하 RAM 캐싱 허용

        public PxeBootAssetManager()
        {
            PxeDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pxe");
        }

        public BootAssetStatus EnsureAssetsAndLoad(string? bootWimPath, bool cacheBootWimInRam, Action<string>? logger = null)
        {
            var status = new BootAssetStatus { PxeAssetDirectory = PxeDir };

            try
            {
                Directory.CreateDirectory(PxeDir);
            }
            catch (Exception ex)
            {
                status.Warnings.Add($"PXE 폴더 생성 오류: {ex.Message}");
            }

            // 1. snponly.efi (UEFI x64 iPXE 부트로더)
            status.SnponlyReady = EnsureEmbeddedOrDiskAsset("snponly.efi", "pxe.snponly.efi", logger, status.Warnings);

            // 2. wimboot (iPXE WinPE HTTP 부트 커널)
            status.WimbootReady = EnsureEmbeddedOrDiskAsset("wimboot", "pxe.wimboot", logger, status.Warnings);

            // 3. bootmgfw.efi (Windows 표준 UEFI Boot Manager)
            status.BootmgfwReady = EnsureWindowsSystemAsset(
                "bootmgfw.efi",
                new[]
                {
                    @"C:\Windows\Boot\EFI\bootmgfw.efi",
                    @"C:\Windows\Boot\PXE\wdsmgfw.efi"
                },
                logger,
                status.Warnings);

            // 4. BCD (UEFI RAMDisk 부팅용 표준 BCD 스토어)
            status.BcdReady = EnsureWindowsSystemAsset(
                "BCD",
                new[]
                {
                    @"C:\Windows\Boot\DVD\EFI\BCD"
                },
                logger,
                status.Warnings,
                fallbackGenerator: GenerateEfiBcdWithBcdEdit);

            // 5. boot.sdi (RAMDisk 디스크 이미지 구조체)
            status.BootSdiReady = EnsureWindowsSystemAsset(
                "boot.sdi",
                new[]
                {
                    @"C:\Windows\Boot\DVD\EFI\boot.sdi",
                    @"C:\Windows\Boot\DVD\PCAT\boot.sdi"
                },
                logger,
                status.Warnings);

            // 6. boot.wim 확인 및 선택적 RAM 캐싱
            string normalizedWim = NormalizeWimPath(bootWimPath);
            if (!string.IsNullOrEmpty(normalizedWim) && File.Exists(normalizedWim))
            {
                var fi = new FileInfo(normalizedWim);
                status.BootWimExists = true;
                status.BootWimSizeBytes = fi.Length;

                if (cacheBootWimInRam && fi.Length > 0 && fi.Length <= MaxRamCacheBytes)
                {
                    lock (_sync)
                    {
                        if (_cachedBootWimBytes == null ||
                            !string.Equals(_cachedBootWimPath, normalizedWim, StringComparison.OrdinalIgnoreCase) ||
                            _cachedBootWimWriteTimeUtc != fi.LastWriteTimeUtc ||
                            _cachedBootWimLength != fi.Length)
                        {
                            logger?.Invoke($"[PXE 캐시] boot.wim 인메모리(RAM) 적재 중... ({fi.Length / (1024.0 * 1024.0):F1} MB)");
                            _cachedBootWimBytes = File.ReadAllBytes(normalizedWim);
                            _cachedBootWimPath = normalizedWim;
                            _cachedBootWimWriteTimeUtc = fi.LastWriteTimeUtc;
                            _cachedBootWimLength = fi.Length;
                            logger?.Invoke($"[PXE 캐시] boot.wim RAM 캐싱 완료! (다중 PC 동시 부팅 시 디스크 부하 0%)");
                        }
                        status.BootWimCachedInRam = true;
                    }
                }
                else
                {
                    lock (_sync)
                    {
                        if (!cacheBootWimInRam)
                        {
                            _cachedBootWimBytes = null;
                            _cachedBootWimPath = "";
                        }
                        status.BootWimCachedInRam = _cachedBootWimBytes != null &&
                            string.Equals(_cachedBootWimPath, normalizedWim, StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
            else
            {
                status.BootWimExists = false;
                if (!string.IsNullOrWhiteSpace(bootWimPath))
                {
                    status.Warnings.Add($"지정된 boot.wim 파일을 찾을 수 없습니다: {bootWimPath}");
                }
            }

            return status;
        }

        public void InvalidateBootWimCache()
        {
            lock (_sync)
            {
                _cachedBootWimBytes = null;
                _cachedBootWimPath = "";
                _cachedBootWimLength = -1;
            }
        }

        public bool TryGetSmallAssetBytes(string fileName, out byte[] data)
        {
            string clean = Path.GetFileName(fileName.Trim().TrimStart('/', '\\'));
            lock (_sync)
            {
                if (_smallAssets.TryGetValue(clean, out var cached) && cached.Length > 0)
                {
                    data = cached;
                    return true;
                }
            }

            string diskPath = Path.Combine(PxeDir, clean);
            if (File.Exists(diskPath))
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(diskPath);
                    lock (_sync)
                    {
                        _smallAssets[clean] = bytes;
                    }
                    data = bytes;
                    return true;
                }
                catch { }
            }

            data = Array.Empty<byte>();
            return false;
        }

        public byte[]? GetCachedBootWimBytes(string? configuredWimPath)
        {
            string normalized = NormalizeWimPath(configuredWimPath);
            lock (_sync)
            {
                if (_cachedBootWimBytes != null &&
                    string.Equals(_cachedBootWimPath, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return _cachedBootWimBytes;
                }
                return null;
            }
        }

        public static string NormalizeWimPath(string? rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath)) return "";
            string trimmed = rawPath.Trim().Trim('"', '\'').Replace('/', '\\');
            if (Path.IsPathRooted(trimmed)) return trimmed;
            return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, trimmed));
        }

        private bool EnsureEmbeddedOrDiskAsset(string fileName, string resourceName, Action<string>? logger, List<string> warnings)
        {
            string targetPath = Path.Combine(PxeDir, fileName);

            try
            {
                if (!File.Exists(targetPath) || new FileInfo(targetPath).Length == 0)
                {
                    var asm = Assembly.GetExecutingAssembly();
                    using var stream = asm.GetManifestResourceStream(resourceName);
                    if (stream != null)
                    {
                        using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                        stream.CopyTo(fs);
                        logger?.Invoke($"[PXE 에셋] 내장 리소스 추출 완료: {fileName}");
                    }
                }

                if (File.Exists(targetPath))
                {
                    byte[] bytes = File.ReadAllBytes(targetPath);
                    if (bytes.Length > 0)
                    {
                        lock (_sync) { _smallAssets[fileName] = bytes; }
                        return true;
                    }
                }

                warnings.Add($"필수 PXE 부트로더 파일이 없습니다: {targetPath}");
            }
            catch (Exception ex)
            {
                warnings.Add($"{fileName} 로드 오류: {ex.Message}");
            }
            return false;
        }

        private bool EnsureWindowsSystemAsset(
            string fileName,
            string[] systemSourceCandidates,
            Action<string>? logger,
            List<string> warnings,
            Func<string, bool>? fallbackGenerator = null)
        {
            string targetPath = Path.Combine(PxeDir, fileName);

            try
            {
                if (!File.Exists(targetPath) || new FileInfo(targetPath).Length == 0)
                {
                    foreach (var src in systemSourceCandidates)
                    {
                        if (File.Exists(src))
                        {
                            File.Copy(src, targetPath, overwrite: true);
                            logger?.Invoke($"[PXE 에셋] Windows 시스템 부트 파일 자동 준비: {fileName}");
                            break;
                        }
                    }

                    if ((!File.Exists(targetPath) || new FileInfo(targetPath).Length == 0) && fallbackGenerator != null)
                    {
                        if (fallbackGenerator(targetPath))
                        {
                            logger?.Invoke($"[PXE 에셋] {fileName} 동적 생성 완료");
                        }
                    }
                }

                if (File.Exists(targetPath))
                {
                    byte[] bytes = File.ReadAllBytes(targetPath);
                    if (bytes.Length > 0)
                    {
                        lock (_sync) { _smallAssets[fileName] = bytes; }
                        return true;
                    }
                }

                warnings.Add($"Windows UEFI 부트 에셋({fileName})을 찾을 수 없습니다.");
            }
            catch (Exception ex)
            {
                warnings.Add($"{fileName} 준비 오류: {ex.Message}");
            }
            return false;
        }

        private static bool GenerateEfiBcdWithBcdEdit(string bcdPath)
        {
            try
            {
                if (File.Exists(bcdPath)) File.Delete(bcdPath);

                RunBcdEdit($"/createstore \"{bcdPath}\"");
                RunBcdEdit($"/store \"{bcdPath}\" /create {{ramdiskoptions}} /d \"Ramdisk options\"");
                RunBcdEdit($"/store \"{bcdPath}\" /set {{ramdiskoptions}} ramdisksdidevice boot");
                RunBcdEdit($"/store \"{bcdPath}\" /set {{ramdiskoptions}} ramdisksdipath \\boot\\boot.sdi");

                string outStr = RunBcdEdit($"/store \"{bcdPath}\" /create /d \"MultiDeployOS WinPE (UEFI x64)\" /application osloader");
                int openBrace = outStr.IndexOf('{');
                int closeBrace = outStr.IndexOf('}');
                if (openBrace < 0 || closeBrace <= openBrace) return false;
                string guid = outStr.Substring(openBrace, closeBrace - openBrace + 1);

                RunBcdEdit($"/store \"{bcdPath}\" /set {guid} device ramdisk=[boot]\\sources\\boot.wim,{{ramdiskoptions}}");
                RunBcdEdit($"/store \"{bcdPath}\" /set {guid} osdevice ramdisk=[boot]\\sources\\boot.wim,{{ramdiskoptions}}");
                RunBcdEdit($"/store \"{bcdPath}\" /set {guid} path \\windows\\system32\\boot\\winload.efi");
                RunBcdEdit($"/store \"{bcdPath}\" /set {guid} systemroot \\windows");
                RunBcdEdit($"/store \"{bcdPath}\" /set {guid} detecthal Yes");
                RunBcdEdit($"/store \"{bcdPath}\" /set {guid} winpe Yes");

                RunBcdEdit($"/store \"{bcdPath}\" /create {{bootmgr}} /d \"Windows Boot Manager\"");
                RunBcdEdit($"/store \"{bcdPath}\" /set {{bootmgr}} default {guid}");
                RunBcdEdit($"/store \"{bcdPath}\" /set {{bootmgr}} displayorder {guid}");
                RunBcdEdit($"/store \"{bcdPath}\" /set {{bootmgr}} timeout 0");

                return File.Exists(bcdPath) && new FileInfo(bcdPath).Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static string RunBcdEdit(string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "bcdedit.exe",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return "";
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return output;
        }

        /// <summary>
        /// 순정 WinPE boot.wim에 PeImagingAgent.exe 및 startnet.cmd를 DISM으로 원클릭 자동 주입
        /// </summary>
        public async Task<(bool Success, string Message)> InjectAgentIntoBootWimAsync(string bootWimPath, Action<string>? onProgress = null)
        {
            string wimPath = NormalizeWimPath(bootWimPath);
            if (string.IsNullOrEmpty(wimPath) || !File.Exists(wimPath))
            {
                return (false, "유효한 boot.wim 파일 경로를 먼저 선택해주세요.");
            }

            string? agentPath = ResolveAgentExecutablePath();
            if (string.IsNullOrEmpty(agentPath) || !File.Exists(agentPath))
            {
                return (false, "PeImagingAgent.exe 바이너리를 찾을 수 없습니다. client 프로젝트를 먼저 빌드하거나 bin\\agent 폴더를 확인하세요.");
            }

            string startnetPath = ResolveOrCreateStartnetScript();
            string mountBase = Path.Combine(Path.GetTempPath(), $"MD_WinPEMount_{DateTime.Now:HHmmss}");
            string logFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dism_inject.log");
            string cmdFile = Path.Combine(Path.GetTempPath(), $"MD_DismPatch_{DateTime.Now.Ticks}.cmd");

            string? provPath = ResolveProvisionerExecutablePath();
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("setlocal enabledelayedexpansion");
            sb.AppendLine("chcp 65001 >nul");
            sb.AppendLine($"echo ======================================================== > \"{logFile}\"");
            sb.AppendLine($"echo [MultiDeploy] WinPE DISM Full Auto-Injector Starting >> \"{logFile}\"");
            sb.AppendLine($"echo Date/Time  : %DATE% %TIME% >> \"{logFile}\"");
            sb.AppendLine($"echo Target WIM : \"{wimPath}\" >> \"{logFile}\"");
            sb.AppendLine($"echo Agent Src  : \"{agentPath}\" >> \"{logFile}\"");
            sb.AppendLine($"echo ======================================================== >> \"{logFile}\"");
            if (!string.IsNullOrEmpty(provPath))
            {
                sb.AppendLine($"echo Prov Src   : \"{provPath}\" >> \"{logFile}\"");
            }
            sb.AppendLine($"echo ======================================================== >> \"{logFile}\"");
            sb.AppendLine($"attrib -r -s -h \"{wimPath}\" >nul 2>&1");
            sb.AppendLine($"echo [Pre-Check] Discarding any stale mount points... >> \"{logFile}\"");
            sb.AppendLine("for /d %%D in (\"%TEMP%\\MD_WinPEMount_*\") do (");
            sb.AppendLine($"    echo  - Cleaning stale mount %%D >> \"{logFile}\"");
            sb.AppendLine($"    dism /Unmount-Image /MountDir:\"%%D\" /Discard >> \"{logFile}\" 2>&1");
            sb.AppendLine("    rd /s /q \"%%D\" >nul 2>&1");
            sb.AppendLine(")");
            sb.AppendLine($"echo [Pre-Check] Executing DISM Cleanup-Wim... >> \"{logFile}\"");
            sb.AppendLine($"dism /Cleanup-Wim >> \"{logFile}\" 2>&1");
            sb.AppendLine();
            sb.AppendLine("set PATCHED_COUNT=0");
            sb.AppendLine();
            sb.AppendLine("rem 1번 인덱스부터 최대 4번 인덱스까지 주입 처리 (인덱스 부재 시 안전하게 건너뜀)");
            sb.AppendLine("for /L %%i in (1,1,4) do (");
            sb.AppendLine("    call :ProcessIndex %%i");
            sb.AppendLine(")");
            sb.AppendLine();
            sb.AppendLine($"echo -------------------------------------------------------- >> \"{logFile}\"");
            sb.AppendLine($"echo Total committed WIM indexes: !PATCHED_COUNT! >> \"{logFile}\"");
            sb.AppendLine();
            sb.AppendLine($"for %%P in (\"{wimPath}\") do (");
            sb.AppendLine("    set \"WIM_DIR=%%~dpP\"");
            sb.AppendLine("    set \"WIM_DRV=%%~dP\"");
            sb.AppendLine(")");
            sb.AppendLine($"echo Checking external folders for: !WIM_DRV! and !WIM_DIR! >> \"{logFile}\"");
            sb.AppendLine($"if exist \"!WIM_DRV!\\agent\\\" copy /y \"{agentPath}\" \"!WIM_DRV!\\agent\\PeImagingAgent.exe\" >> \"{logFile}\" 2>&1");
            sb.AppendLine($"if exist \"!WIM_DRV!\\\" copy /y \"{agentPath}\" \"!WIM_DRV!\\PeImagingAgent.exe\" >> \"{logFile}\" 2>&1");
            sb.AppendLine($"if exist \"!WIM_DIR!\\\" (");
            sb.AppendLine($"    copy /y \"{agentPath}\" \"!WIM_DIR!\\PeImagingAgent.exe\" >> \"{logFile}\" 2>&1");
            sb.AppendLine("    if not exist \"!WIM_DIR!\\agent\" mkdir \"!WIM_DIR!\\agent\" >nul 2>&1");
            sb.AppendLine($"    copy /y \"{agentPath}\" \"!WIM_DIR!\\agent\\PeImagingAgent.exe\" >> \"{logFile}\" 2>&1");
            sb.AppendLine(")");
            if (!string.IsNullOrEmpty(provPath) && File.Exists(provPath))
            {
                sb.AppendLine($"if exist \"!WIM_DRV!\\agent\\\" copy /y \"{provPath}\" \"!WIM_DRV!\\agent\\CJAutoProvisioner.exe\" >> \"{logFile}\" 2>&1");
                sb.AppendLine($"if exist \"!WIM_DRV!\\\" copy /y \"{provPath}\" \"!WIM_DRV!\\CJAutoProvisioner.exe\" >> \"{logFile}\" 2>&1");
                sb.AppendLine($"if exist \"!WIM_DIR!\\\" (");
                sb.AppendLine($"    copy /y \"{provPath}\" \"!WIM_DIR!\\CJAutoProvisioner.exe\" >> \"{logFile}\" 2>&1");
                sb.AppendLine($"    copy /y \"{provPath}\" \"!WIM_DIR!\\agent\\CJAutoProvisioner.exe\" >> \"{logFile}\" 2>&1");
                sb.AppendLine(")");
            }
            sb.AppendLine("rem 연결된 외장 USB 드라이브(D~Z)에 에이전트가 존재하면 해당 USB도 함께 자동 최신화");
            sb.AppendLine("for %%U in (D E F G H I J K L M N) do (");
            sb.AppendLine($"    if exist \"%%U:\\agent\\PeImagingAgent.exe\" copy /y \"{agentPath}\" \"%%U:\\agent\\PeImagingAgent.exe\" >> \"{logFile}\" 2>&1");
            sb.AppendLine($"    if exist \"%%U:\\PeImagingAgent.exe\" copy /y \"{agentPath}\" \"%%U:\\PeImagingAgent.exe\" >> \"{logFile}\" 2>&1");
            sb.AppendLine(")");
            sb.AppendLine();
            sb.AppendLine("if !PATCHED_COUNT! EQU 0 (");
            sb.AppendLine($"    echo [ERROR] No WIM indexes were successfully committed! >> \"{logFile}\"");
            sb.AppendLine("    exit /b 10");
            sb.AppendLine(")");
            sb.AppendLine($"echo [SUCCESS] All target images in boot.wim successfully updated! >> \"{logFile}\"");
            sb.AppendLine("exit /b 0");
            sb.AppendLine();
            sb.AppendLine(":ProcessIndex");
            sb.AppendLine("set \"IDX=%~1\"");
            sb.AppendLine($"set \"CUR_MOUNT={mountBase}_%IDX%\"");
            sb.AppendLine("if exist \"!CUR_MOUNT!\" (");
            sb.AppendLine($"    dism /Unmount-Image /MountDir:\"!CUR_MOUNT!\" /Discard >nul 2>&1");
            sb.AppendLine("    rd /s /q \"!CUR_MOUNT!\" >nul 2>&1");
            sb.AppendLine(")");
            sb.AppendLine("mkdir \"!CUR_MOUNT!\" >nul 2>&1");
            sb.AppendLine($"echo -------------------------------------------------------- >> \"{logFile}\"");
            sb.AppendLine($"echo [Index %IDX%] Attempting to mount image... >> \"{logFile}\"");
            sb.AppendLine($"dism /Mount-Image /ImageFile:\"{wimPath}\" /Index:%IDX% /MountDir:\"!CUR_MOUNT!\" >> \"{logFile}\" 2>&1");
            sb.AppendLine("if !ERRORLEVEL! NEQ 0 (");
            sb.AppendLine($"    echo [Index %IDX%] Image not present in WIM or mount failed. Skipping. >> \"{logFile}\"");
            sb.AppendLine("    rd /s /q \"!CUR_MOUNT!\" >nul 2>&1");
            sb.AppendLine("    exit /b 0");
            sb.AppendLine(")");
            sb.AppendLine($"echo [Index %IDX%] Mount successful! Injecting binaries... >> \"{logFile}\"");
            sb.AppendLine("if not exist \"!CUR_MOUNT!\\Windows\\System32\" mkdir \"!CUR_MOUNT!\\Windows\\System32\" >nul 2>&1");
            sb.AppendLine($"copy /y \"{agentPath}\" \"!CUR_MOUNT!\\Windows\\System32\\PeImagingAgent.exe\" >> \"{logFile}\" 2>&1");
            sb.AppendLine($"copy /y \"{agentPath}\" \"!CUR_MOUNT!\\PeImagingAgent.exe\" >> \"{logFile}\" 2>&1");
            sb.AppendLine($"copy /y \"{startnetPath}\" \"!CUR_MOUNT!\\Windows\\System32\\startnet.cmd\" >> \"{logFile}\" 2>&1");
            if (!string.IsNullOrEmpty(provPath) && File.Exists(provPath))
            {
                sb.AppendLine($"copy /y \"{provPath}\" \"!CUR_MOUNT!\\Windows\\System32\\CJAutoProvisioner.exe\" >> \"{logFile}\" 2>&1");
                sb.AppendLine($"copy /y \"{provPath}\" \"!CUR_MOUNT!\\CJAutoProvisioner.exe\" >> \"{logFile}\" 2>&1");
            }
            sb.AppendLine("if exist \"!CUR_MOUNT!\\agent\\\" (");
            sb.AppendLine($"    copy /y \"{agentPath}\" \"!CUR_MOUNT!\\agent\\PeImagingAgent.exe\" >> \"{logFile}\" 2>&1");
            if (!string.IsNullOrEmpty(provPath) && File.Exists(provPath))
            {
                sb.AppendLine($"    copy /y \"{provPath}\" \"!CUR_MOUNT!\\agent\\CJAutoProvisioner.exe\" >> \"{logFile}\" 2>&1");
            }
            sb.AppendLine(")");
            sb.AppendLine("ping 127.0.0.1 -n 2 >nul 2>&1");
            sb.AppendLine($"echo [Index %IDX%] Committing changes & unmounting... >> \"{logFile}\"");
            sb.AppendLine($"dism /Unmount-Image /MountDir:\"!CUR_MOUNT!\" /Commit >> \"{logFile}\" 2>&1");
            sb.AppendLine("if !ERRORLEVEL! NEQ 0 (");
            sb.AppendLine($"    echo [Index %IDX%] Retrying unmount commit in 2 seconds... >> \"{logFile}\"");
            sb.AppendLine("    ping 127.0.0.1 -n 3 >nul 2>&1");
            sb.AppendLine($"    dism /Unmount-Image /MountDir:\"!CUR_MOUNT!\" /Commit >> \"{logFile}\" 2>&1");
            sb.AppendLine(")");
            sb.AppendLine("if !ERRORLEVEL! NEQ 0 (");
            sb.AppendLine($"    echo [Index %IDX%] ERROR: Commit failed! Discarding mount... >> \"{logFile}\"");
            sb.AppendLine($"    dism /Unmount-Image /MountDir:\"!CUR_MOUNT!\" /Discard >> \"{logFile}\" 2>&1");
            sb.AppendLine(") else (");
            sb.AppendLine($"    echo [Index %IDX%] Successfully COMMITTED and saved to WIM! >> \"{logFile}\"");
            sb.AppendLine("    set /a PATCHED_COUNT+=1");
            sb.AppendLine(")");
            sb.AppendLine("rd /s /q \"!CUR_MOUNT!\" >nul 2>&1");
            sb.AppendLine("exit /b 0");

            await File.WriteAllTextAsync(cmdFile, sb.ToString(), new UTF8Encoding(false));

            try
            {
                bool isAdmin = NetworkUtility.IsRunningAsAdministrator();
                onProgress?.Invoke("DISM 마운트 및 에이전트 자동 주입 작업을 시작합니다... (관리자 권한 필요 시 UAC 승인)");

                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{cmdFile}\"",
                    UseShellExecute = !isAdmin,
                    Verb = isAdmin ? "" : "runas",
                    CreateNoWindow = isAdmin,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using var proc = Process.Start(psi);
                if (proc == null) return (false, "DISM 패치 프로세스를 시작하지 못했습니다.");

                while (!proc.HasExited)
                {
                    await Task.Delay(800);
                    if (File.Exists(logFile))
                    {
                        try
                        {
                            using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            using var sr = new StreamReader(fs, Encoding.UTF8);
                            string text = await sr.ReadToEndAsync();
                            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                            if (lines.Length > 0)
                            {
                                onProgress?.Invoke(lines[^1].Trim());
                            }
                        }
                        catch { }
                    }
                }

                InvalidateBootWimCache();

                string logTail = "";
                if (File.Exists(logFile))
                {
                    try
                    {
                        string full = await File.ReadAllTextAsync(logFile);
                        var lines = full.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                        logTail = string.Join("\n", lines.TakeLast(15));
                    }
                    catch { }
                }

                if (proc.ExitCode == 0)
                {
                    var agentFi = new FileInfo(agentPath);
                    return (true, $"boot.wim 내부의 모든 인덱스 및 관련 폴더에 최신 에이전트 주입이 완료되었습니다!\n\n" +
                                  $"[주입된 최신 에셋]\n" +
                                  $"- 파일명: {agentFi.Name} ({agentFi.Length / 1024} KB, 수정일: {agentFi.LastWriteTime:yyyy-MM-dd HH:mm:ss})\n" +
                                  $"- 대상 WIM: {wimPath}\n\n" +
                                  $"[DISM 작업 결과 요약]\n{logTail}\n\n" +
                                  $"※ 전체 로그는 bin\\dism_inject.log 에 영구 보존됩니다.");
                }
                else
                {
                    return (false, $"DISM 패치 실패 (종료 코드: {proc.ExitCode})\n\n[실패 로그 내역]\n{logTail}\n\n상세 로그: bin\\dism_inject.log");
                }
            }
            catch (Exception ex)
            {
                return (false, $"DISM 실행 오류: {ex.Message}");
            }
            finally
            {
                try { if (File.Exists(cmdFile)) File.Delete(cmdFile); } catch { }
            }
        }

        private static string? ResolveAgentExecutablePath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDir, "agent", "PeImagingAgent.exe"),
                Path.Combine(baseDir, "PeImagingAgent.exe"),
                Path.GetFullPath(Path.Combine(baseDir, "..", "client", "build", "PeImagingAgent.exe")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "client", "build", "Release", "PeImagingAgent.exe")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "client", "build", "PeImagingAgent.exe")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "client", "build", "Release", "PeImagingAgent.exe"))
            };

            var best = candidates
                .Where(File.Exists)
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .FirstOrDefault();

            if (best != null)
            {
                string agentDirExe = Path.Combine(baseDir, "agent", "PeImagingAgent.exe");
                try
                {
                    if (!string.Equals(best, agentDirExe, StringComparison.OrdinalIgnoreCase))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(agentDirExe)!);
                        File.Copy(best, agentDirExe, true);
                    }
                }
                catch { }
            }

            return best;
        }

        private static string? ResolveProvisionerExecutablePath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDir, "agent", "CJAutoProvisioner.exe"),
                Path.Combine(baseDir, "tools", "CJAutoProvisioner", "dist", "CJAutoProvisioner.exe"),
                Path.Combine(baseDir, "CJAutoProvisioner.exe"),
                Path.GetFullPath(Path.Combine(baseDir, "..", "tools", "CJAutoProvisioner", "dist", "CJAutoProvisioner.exe")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "tools", "CJAutoProvisioner", "dist", "CJAutoProvisioner.exe")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "tools", "CJAutoProvisioner", "dist", "CJAutoProvisioner.exe"))
            };

            return candidates
                .Where(File.Exists)
                .OrderByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault();
        }

        private static string ResolveOrCreateStartnetScript()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDir, "agent", "startnet.cmd"),
                Path.GetFullPath(Path.Combine(baseDir, "..", "client", "scripts", "startnet.cmd")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "client", "scripts", "startnet.cmd"))
            };

            foreach (var c in candidates)
            {
                if (File.Exists(c)) return c;
            }

            string fallbackPath = Path.Combine(Path.GetTempPath(), "multideploy_startnet.cmd");
            File.WriteAllText(fallbackPath,
                "@echo off\r\nchcp 65001 > nul\r\nwpeinit\r\nping 127.0.0.1 -n 3 > nul\r\n:AGENT_LOOP\r\nX:\\Windows\\System32\\PeImagingAgent.exe\r\nping 127.0.0.1 -n 6 > nul\r\ngoto AGENT_LOOP\r\n",
                new UTF8Encoding(false));
            return fallbackPath;
        }
    }
}

