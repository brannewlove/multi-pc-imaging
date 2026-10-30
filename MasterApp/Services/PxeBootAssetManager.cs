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
            string mountDir = Path.Combine(Path.GetTempPath(), $"MultiDeploy_WinPEMount_{DateTime.Now:HHmmss}");
            string logFile = Path.Combine(Path.GetTempPath(), $"MultiDeploy_DismPatch_{DateTime.Now.Ticks}.log");
            string cmdFile = Path.Combine(Path.GetTempPath(), $"MultiDeploy_DismPatch_{DateTime.Now.Ticks}.cmd");

            Directory.CreateDirectory(mountDir);

            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("chcp 65001 >nul");
            sb.AppendLine($"echo [1/3] Mounting WinPE WIM: \"{wimPath}\" ... > \"{logFile}\"");
            sb.AppendLine($"dism /Mount-Image /ImageFile:\"{wimPath}\" /Index:1 /MountDir:\"{mountDir}\" >> \"{logFile}\" 2>&1");
            sb.AppendLine("if errorlevel 1 exit /b 10");
            sb.AppendLine($"echo [2/3] Injecting PeImagingAgent.exe and startnet.cmd ... >> \"{logFile}\"");
            sb.AppendLine($"copy /y \"{agentPath}\" \"{mountDir}\\Windows\\System32\\PeImagingAgent.exe\" >> \"{logFile}\" 2>&1");
            sb.AppendLine("if errorlevel 1 (");
            sb.AppendLine($"    dism /Unmount-Image /MountDir:\"{mountDir}\" /Discard >> \"{logFile}\" 2>&1");
            sb.AppendLine("    exit /b 20");
            sb.AppendLine(")");
            sb.AppendLine($"copy /y \"{startnetPath}\" \"{mountDir}\\Windows\\System32\\startnet.cmd\" >> \"{logFile}\" 2>&1");
            sb.AppendLine($"echo [3/3] Saving & Unmounting boot.wim ... >> \"{logFile}\"");
            sb.AppendLine($"dism /Unmount-Image /MountDir:\"{mountDir}\" /Commit >> \"{logFile}\" 2>&1");
            sb.AppendLine("if errorlevel 1 exit /b 30");
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

                if (proc.ExitCode == 0)
                {
                    return (true, $"boot.wim에 PeImagingAgent.exe 및 자동 실행 startnet.cmd 주입이 완료되었습니다!\n대상 파일: {wimPath}");
                }
                else
                {
                    string errTail = "";
                    if (File.Exists(logFile))
                    {
                        try { errTail = File.ReadAllText(logFile); } catch { }
                    }
                    return (false, $"DISM 패치 실패 (코드: {proc.ExitCode}).\n{errTail}");
                }
            }
            catch (Exception ex)
            {
                return (false, $"DISM 실행 취소 또는 오류: {ex.Message}");
            }
            finally
            {
                try { if (File.Exists(cmdFile)) File.Delete(cmdFile); } catch { }
                try { if (File.Exists(logFile)) File.Delete(logFile); } catch { }
                try { if (Directory.Exists(mountDir)) Directory.Delete(mountDir, false); } catch { }
            }
        }

        private static string? ResolveAgentExecutablePath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDir, "agent", "PeImagingAgent.exe"),
                Path.Combine(baseDir, "PeImagingAgent.exe"),
                Path.GetFullPath(Path.Combine(baseDir, "..", "client", "build", "Release", "PeImagingAgent.exe")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "client", "build", "PeImagingAgent.exe")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "client", "build", "Release", "PeImagingAgent.exe")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "client", "build", "PeImagingAgent.exe"))
            };

            foreach (var c in candidates)
            {
                if (File.Exists(c)) return c;
            }
            return null;
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

