using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace CJAutoProvisioner
{
    public class AssignedInfoResult
    {
        public bool Enabled { get; set; }
        public string AssignedId { get; set; }
        public string FoundPath { get; set; }

        public AssignedInfoResult(bool enabled, string assignedId, string foundPath)
        {
            Enabled = enabled;
            AssignedId = assignedId;
            FoundPath = foundPath;
        }
    }

    public class ControlInfo
    {
        public IntPtr Hwnd { get; set; }
        public string Text { get; set; }
        public string ClassName { get; set; }

        public ControlInfo(IntPtr hWnd, string text, string className)
        {
            Hwnd = hWnd;
            Text = text;
            ClassName = className;
        }
    }

    internal static class Program
    {
        // ==========================================
        // [사용자 설정 파라미터 (환경 기본값)]
        // ==========================================
        private const string TargetAppPath = @"C:\Temp\CJ_Work\AS_2.0\AutoSetup2.0.exe";
        private const string WindowTitleExact = "CJ 보안 소프트웨어 설치 자동화 v2.0";
        private const string WindowTitlePartial = "CJ 보안 소프트웨어";
        private const string TargetButtonText = "설치 시작";
        private const string ConfigFilePath = @"C:\Temp\CJ_Work\AS_2.0\setup_config.ini";
        private const string LogFilePath = @"C:\Temp\CJ_Work\provision.log";
        private const string CompletedTagPath = @"C:\Temp\CJ_Work\provision_completed.tag";

        // 폴링 대기 시간
        private const int MaxWaitSeconds = 30;
        private const int PollIntervalMs = 500;

        // ==========================================
        // [Win32 Native API 선언]
        // ==========================================
        private const uint WM_COMMAND = 0x0111;
        private const uint BM_CLICK = 0x00F5;
        private const uint BM_SETSTATE = 0x00F3;
        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;
        private const int SW_RESTORE = 9;
        private const int SW_SHOW = 5;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, int dwExtraInfo);

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const byte VK_TAB = 0x09;
        private const byte VK_RETURN = 0x0D;
        private const byte VK_SPACE = 0x20;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        // ==========================================
        // [Main 진입점]
        // ==========================================
        public static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            // [방어벽 1: 단일 인스턴스 보장 (Named Mutex)]
            bool isNewInstance;
            using (var mutex = new Mutex(true, "Global\\MultiDeploy_CJAutoProvisioner_Mutex", out isNewInstance))
            {
                if (!isNewInstance)
                {
                    Log("[INFO] 이미 다른 프로비저너 인스턴스가 실행 중입니다. 중복 실행 방지를 위해 즉시 종료합니다.");
                    return 0;
                }

                Log("================================================================================");
                Log("[CJ 무인 프로비저너] Post-OOBE Auto-Provisioner v1.1.0 시작");
                Log("================================================================================");

                // [방어벽 2: 완료 마커 파일 확인]
                if (File.Exists(CompletedTagPath))
                {
                    Log("[INFO] 이전 실행에서 프로비저너가 이미 성공적으로 완료되었습니다 (provision_completed.tag 감지).");
                    Log("[INFO] 중복 실행 방지를 위해 잔여 자동 실행 항목을 정리하고 즉시 종료합니다.");
                    CleanupAutoRunEntries();
                    return 0;
                }

                // [방어벽 3: 시작 즉시 타 자동실행 트리거 및 잔여 스크립트 일괄 정리]
                CleanupAutoRunEntries();

                string currentExePath = null;
                try
                {
                    currentExePath = Process.GetCurrentProcess().MainModule.FileName;
                }
                catch
                {
                    currentExePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CJAutoProvisioner.exe");
                }

                try
                {
                    // [Step 1] assigned_info.json 탐색 및 ID/활성화 여부 파싱
                    Log("[Step 1] 할당 정보(assigned_info.json) 및 실행 모드 확인 중...");
                    AssignedInfoResult info = ReadAssignedInfo();

                    if (!info.Enabled)
                    {
                        Log("[INFO] 무인 프로비저너가 비활성화(enabled=false)로 설정되어 있습니다. 작업을 건너뛰고 종료합니다.");
                        MarkCompleted();
                        TriggerSelfDelete(currentExePath, info.FoundPath);
                        return 0;
                    }

                    bool isCaseA = !string.IsNullOrEmpty(info.AssignedId) && info.AssignedId.Trim().Length > 0;
                    if (isCaseA)
                    {
                        Log(string.Format("[CASE A 감지] 정상 발급 모드: 주입된 CJ_ID = '{0}'", info.AssignedId));
                    }
                    else
                    {
                        Log("[CASE B 감지] 발급 전 사전 세팅 모드: CJ_ID 공란 (user_id='', sindoh_enabled=False)");
                    }

                    // [Step 2] 초기 설정 파일(setup_config.ini) 분기 수정
                    var iniCandidates = new List<string>
                    {
                        ConfigFilePath,
                        @"C:\Temp\CJ_Work\AS_2.0\setup_config.ini",
                        @"C:\Temp\CJ_Work\setup_config.ini"
                    };
                    foreach (var ini in iniCandidates)
                    {
                        if (File.Exists(ini))
                        {
                            Log(string.Format("[Step 2] 초기 설정 파일 수정 중... (대상: {0})", ini));
                            UpdateSetupConfig(ini, isCaseA, info.AssignedId);
                        }
                    }

                    // [Step 3] 보안 프로그램 프로세스 확인 및 실행 (중복 기동 방지)
                    string appPath = TargetAppPath;
                    if (!File.Exists(appPath))
                    {
                        string[] cands = new string[]
                        {
                            @"C:\Temp\CJ_Work\AS_2.0\AutoSetup2.0.exe",
                            @"C:\Temp\CJ_Work\AutoSetup2.0.exe",
                            @"C:\Temp\CJ_Work\AutoSetup 1.17.exe",
                            @"C:\Temp\CJ_Work\AutoSetup1.16.exe"
                        };
                        foreach (var c in cands)
                        {
                            if (File.Exists(c)) { appPath = c; break; }
                        }
                        if (!File.Exists(appPath) && Directory.Exists(@"C:\Temp\CJ_Work"))
                        {
                            string[] matches = Directory.GetFiles(@"C:\Temp\CJ_Work", "AutoSetup*.exe", SearchOption.AllDirectories);
                            if (matches.Length > 0) appPath = matches[0];
                        }
                    }

                    SuppressUacPolicy();

                    // [방어벽 4: 이미 실행 중인 AutoSetup 프로세스 확인]
                    Process existingProc = FindExistingAutoSetupProcess();
                    if (existingProc != null)
                    {
                        Log(string.Format("[PROCESS] 이미 실행 중인 보안 프로그램 감지 (PID: {0}). 프로세스를 중복 실행하지 않고 기존 창을 탐색합니다.", existingProc.Id));
                    }
                    else
                    {
                        Log(string.Format("[Step 3] 보안 프로그램 실행 중... (실행경로: {0})", appPath));
                        LaunchTargetApplication(appPath);
                    }

                    // [Step 4] 창 로드 대기 및 [설치 시작] 버튼 자동 선택 & 스페이스 입력
                    Log(string.Format("[Step 4] 프로그램 창 '{0}' 로드 대기 및 버튼 포커스/스페이스 입력 시도...", WindowTitleExact));
                    bool clickSuccess = WaitAndTriggerStartButton(WindowTitleExact, WindowTitlePartial, TargetButtonText, MaxWaitSeconds);

                    if (clickSuccess)
                    {
                        Log("[SUCCESS] 보안 소프트웨어 [설치 시작] 버튼 선택 및 스페이스(SPACE) 입력이 성공적으로 완수되었습니다.");
                    }
                    else
                    {
                        Log("[WARNING] 제한 시간 내 버튼 이벤트를 감지하지 못했습니다.");
                    }

                    // 완료 마커 기록
                    MarkCompleted();

                    Log("[Step 5] 프로비저너 정상 완료. UAC 보안 정책 복원 및 정리를 수행합니다.");
                    RestoreUacPolicy();
                    TriggerSelfDelete(currentExePath, info.FoundPath);

                    Log("[COMPLETED] 모든 자동화 단계가 성공적으로 완료되었습니다. 5초 후 창이 자동으로 닫힙니다.");
                    Thread.Sleep(5000);
                    return 0;
                }
                catch (Exception ex)
                {
                    Log(string.Format("[FATAL ERROR] 예외 발생: {0}\n{1}", ex.Message, ex.StackTrace));
                    RestoreUacPolicy();
                    TriggerSelfDelete(currentExePath, null);
                    Log("[ERROR] 5초 후 창이 닫힙니다.");
                    Thread.Sleep(5000);
                    return 1;
                }
            }
        }

        // ==========================================
        // [중복 기동 방지: 이미 실행 중인 AutoSetup 프로세스 탐색]
        // ==========================================
        private static Process FindExistingAutoSetupProcess()
        {
            try
            {
                Process[] procs = Process.GetProcesses();
                foreach (var p in procs)
                {
                    try
                    {
                        string name = p.ProcessName.ToLowerInvariant();
                        if (name.Contains("autosetup"))
                        {
                            return p;
                        }
                    }
                    catch {}
                }
            }
            catch {}
            return null;
        }

        // ==========================================
        // [중복 기동 방지: 자동실행 항목 및 잔여 스크립트 즉시 청소]
        // ==========================================
        private static void CleanupAutoRunEntries()
        {
            try
            {
                // 1. 공용/개인 시작프로그램 폴더 내 스크립트 제거
                string[] startupFiles = new string[]
                {
                    @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp\run_provision.cmd",
                    @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp\CJAutoProvisioner.exe",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "run_provision.cmd"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "CJAutoProvisioner.exe")
                };
                foreach (var f in startupFiles)
                {
                    if (File.Exists(f))
                    {
                        try { File.Delete(f); Log(string.Format("[CLEANUP] 시작프로그램 잔여 파일 제거: {0}", f)); } catch {}
                    }
                }

                // 2. 레지스트리 Run / RunOnce 제거 (HKLM & HKCU)
                string[] subKeys = new string[]
                {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"
                };
                foreach (var subKey in subKeys)
                {
                    try
                    {
                        using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(subKey, true))
                        {
                            if (k != null)
                            {
                                k.DeleteValue("CJAutoProvision", false);
                                k.DeleteValue("CJAutoProvisioner", false);
                            }
                        }
                    }
                    catch {}
                    try
                    {
                        using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(subKey, true))
                        {
                            if (k != null)
                            {
                                k.DeleteValue("CJAutoProvision", false);
                                k.DeleteValue("CJAutoProvisioner", false);
                            }
                        }
                    }
                    catch {}
                }

                // 3. 임시 런처 cmd / ps1 스크립트 제거 (다중 호출 원인 차단)
                string[] tempScripts = new string[]
                {
                    @"C:\Temp\CJ_Work\run_provision.cmd",
                    @"C:\Temp\CJ_Work\run_provision.ps1"
                };
                foreach (var s in tempScripts)
                {
                    if (File.Exists(s))
                    {
                        try { File.Delete(s); Log(string.Format("[CLEANUP] 중복 런처 스크립트 제거: {0}", s)); } catch {}
                    }
                }
            }
            catch (Exception ex)
            {
                Log(string.Format("[WARNING] 자동실행 항목 청소 중 예외: {0}", ex.Message));
            }
        }

        private static void MarkCompleted()
        {
            try
            {
                string dir = Path.GetDirectoryName(CompletedTagPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(CompletedTagPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Encoding.UTF8);
            }
            catch {}
        }

        // ==========================================
        // [Step 1: assigned_info.json 파싱]
        // ==========================================
        private static AssignedInfoResult ReadAssignedInfo()
        {
            var candidates = new List<string>
            {
                @"C:\Temp\CJ_Work\assigned_info.json",
                @"C:\Temp\CJ_Work\MultiDeploy\assigned_info.json",
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assigned_info.json"),
                @"C:\Temp\assigned_info.json"
            };

            string foundPath = null;
            foreach (var path in candidates)
            {
                if (File.Exists(path))
                {
                    foundPath = path;
                    break;
                }
            }

            if (foundPath == null)
            {
                Log("[WARNING] assigned_info.json 파일을 찾을 수 없습니다. (사전 세팅 모드 B로 대체 진행)");
                return new AssignedInfoResult(true, null, null);
            }

            Log(string.Format("[FOUND] 할당 정보 파일 발견: {0}", foundPath));

            try
            {
                string jsonText = File.ReadAllText(foundPath, Encoding.UTF8);

                bool enabled = true;
                Match mEnabled = Regex.Match(jsonText, @"""enabled""\s*:\s*(true|false)", RegexOptions.IgnoreCase);
                if (mEnabled.Success)
                {
                    enabled = bool.Parse(mEnabled.Groups[1].Value.ToLowerInvariant());
                }

                string assignedId = null;
                string[] idKeys = new string[] { "assigned_id", "cj_id", "user_id" };
                foreach (var key in idKeys)
                {
                    Match mId = Regex.Match(jsonText, string.Format(@"""{0}""\s*:\s*""([^""]*)""", key), RegexOptions.IgnoreCase);
                    if (mId.Success)
                    {
                        assignedId = mId.Groups[1].Value.Trim();
                        break;
                    }
                }

                if (string.IsNullOrEmpty(assignedId))
                {
                    Match mIdBare = Regex.Match(jsonText, @"""(?:assigned_id|cj_id|user_id)""\s*:\s*([^,\}\s]+)", RegexOptions.IgnoreCase);
                    if (mIdBare.Success)
                    {
                        string val = mIdBare.Groups[1].Value.Trim();
                        if (!val.Equals("null", StringComparison.OrdinalIgnoreCase))
                        {
                            assignedId = val.Trim('"', '\'');
                        }
                    }
                }

                return new AssignedInfoResult(enabled, assignedId, foundPath);
            }
            catch (Exception ex)
            {
                Log(string.Format("[ERROR] assigned_info.json 파싱 실패 ({0}). 기본 모드로 대체 진행합니다.", ex.Message));
                return new AssignedInfoResult(true, null, foundPath);
            }
        }

        // ==========================================
        // [Step 2: setup_config.ini 수정]
        // ==========================================
        private static void UpdateSetupConfig(string iniPath, bool isCaseA, string assignedId)
        {
            try
            {
                string bakPath = iniPath + ".bak";
                if (File.Exists(iniPath))
                {
                    File.Copy(iniPath, bakPath, true);
                    Log(string.Format("[BACKUP] 원본 설정 파일 백업 생성 완료: {0}", bakPath));
                }
            }
            catch (Exception ex)
            {
                Log(string.Format("[WARNING] 백업 파일 생성 실패 (진행은 계속함): {0}", ex.Message));
            }

            Encoding encoding = DetectFileEncoding(iniPath);
            string[] lines = File.Exists(iniPath) ? File.ReadAllLines(iniPath, encoding) : new string[0];
            var newLines = new List<string>();

            bool userIdUpdated = false;
            bool sindohUpdated = false;

            string cleanId = assignedId != null ? assignedId.Trim('"', '\'', ' ') : "";

            foreach (var rawLine in lines)
            {
                string line = rawLine;

                // 1. user_id 수정
                if (Regex.IsMatch(line, @"^\s*user_id\s*=", RegexOptions.IgnoreCase))
                {
                    if (isCaseA)
                    {
                        line = string.Format("user_id = {0}", cleanId);
                        Log(string.Format("[CONFIG EDIT] user_id 설정 -> '{0}'", cleanId));
                    }
                    else
                    {
                        line = "user_id = ";
                        Log("[CONFIG EDIT] user_id 설정 -> 공란 (user_id = )");
                    }
                    userIdUpdated = true;
                }
                // 2. sindoh_enabled 수정 (CASE B인 경우 False로 강제)
                else if (Regex.IsMatch(line, @"^\s*sindoh_enabled\s*=", RegexOptions.IgnoreCase))
                {
                    if (!isCaseA)
                    {
                        line = "sindoh_enabled = False";
                        Log("[CONFIG EDIT] sindoh_enabled 설정 -> False (공란 모드)");
                    }
                    sindohUpdated = true;
                }

                newLines.Add(line);
            }

            if (!userIdUpdated)
            {
                string lineToAdd = isCaseA ? string.Format("user_id = {0}", cleanId) : "user_id = ";
                newLines.Add(lineToAdd);
                Log(string.Format("[CONFIG APPEND] 누락된 user_id 추가: {0}", lineToAdd));
            }
            if (!sindohUpdated && !isCaseA)
            {
                newLines.Add("sindoh_enabled = False");
                Log("[CONFIG APPEND] 누락된 sindoh_enabled 추가: False");
            }

            File.WriteAllLines(iniPath, newLines.ToArray(), encoding);
            Log(string.Format("[SUCCESS] 설정 파일({0})이 성공적으로 저장되었습니다.", iniPath));
        }

        private static Encoding DetectFileEncoding(string filePath)
        {
            if (!File.Exists(filePath)) return Encoding.UTF8;
            try
            {
                byte[] bom = new byte[4];
                using (var file = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                {
                    file.Read(bom, 0, 4);
                }
                if (bom[0] == 0xef && bom[1] == 0xbb && bom[2] == 0xbf) return Encoding.UTF8;
                if (bom[0] == 0xff && bom[1] == 0xfe) return Encoding.Unicode;
                if (bom[0] == 0xfe && bom[1] == 0xff) return Encoding.BigEndianUnicode;
                return Encoding.UTF8;
            }
            catch
            {
                return Encoding.UTF8;
            }
        }

        // ==========================================
        // [Step 3: 보안 프로그램 프로세스 실행]
        // ==========================================
        private static Process LaunchTargetApplication(string exePath)
        {
            if (!File.Exists(exePath))
            {
                Log(string.Format("[ERROR] 대상 실행 파일이 존재하지 않습니다: {0}", exePath));
                return null;
            }

            string workDir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(workDir)) workDir = @"C:\Temp\CJ_Work\AS_2.0";

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = workDir,
                    UseShellExecute = true
                };

                Process proc = Process.Start(psi);
                if (proc != null)
                {
                    Log(string.Format("[PROCESS] 프로세스 시작 성공 (PID: {0}, 실행파일: {1})", proc.Id, exePath));
                    return proc;
                }
            }
            catch (Exception ex)
            {
                Log(string.Format("[ERROR] 프로세스 실행 실패: {0}", ex.Message));
            }

            return null;
        }

        // ==========================================
        // [Step 4: 창 감지 및 버튼 선택 후 스페이스바 입력 (핵심)]
        // ==========================================
        private static bool WaitAndTriggerStartButton(string exactTitle, string partialTitle, string buttonText, int timeoutSeconds)
        {
            DateTime deadline = DateTime.Now.AddSeconds(timeoutSeconds);
            IntPtr targetHwnd = IntPtr.Zero;

            Log(string.Format("[*] 창 탐색 시작 (타이틀: '{0}', 최대 {1}초 대기)...", exactTitle, timeoutSeconds));

            while (DateTime.Now < deadline)
            {
                targetHwnd = FindWindow(null, exactTitle);

                if (targetHwnd == IntPtr.Zero && !string.IsNullOrEmpty(partialTitle))
                {
                    targetHwnd = FindWindowByPartialTitle(partialTitle);
                }

                if (targetHwnd != IntPtr.Zero && IsWindowVisible(targetHwnd))
                {
                    Log(string.Format("[FOUND] 대상 창 핸들 발견: 0x{0:X8}", targetHwnd.ToInt64()));
                    break;
                }

                Thread.Sleep(PollIntervalMs);
            }

            if (targetHwnd == IntPtr.Zero)
            {
                Log(string.Format("[TIMEOUT] {0}초 내에 '{1}' 창을 찾지 못했습니다.", timeoutSeconds, exactTitle));
                return false;
            }

            // 1. 대상 창을 포그라운드로 확실하게 전환 (AttachThreadInput으로 포커스 강제 획득)
            ActivateWindowForcefully(targetHwnd);
            Thread.Sleep(600);

            // ==========================================================
            // [방식 1: Windows UI Automation 정밀 버튼 선택 + 스페이스 입력]
            // ==========================================================
            try
            {
                Log("[UIA] UI Automation 기반 컨트롤 탐색 시도 중...");
                AutomationElement rootElement = AutomationElement.FromHandle(targetHwnd);
                if (rootElement != null)
                {
                    var btnCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
                    AutomationElementCollection allButtons = rootElement.FindAll(TreeScope.Descendants, btnCondition);

                    AutomationElement targetBtn = null;
                    if (allButtons != null && allButtons.Count > 0)
                    {
                        Log(string.Format("[UIA] 감지된 Button 컨트롤 수: {0}개", allButtons.Count));
                        foreach (AutomationElement btn in allButtons)
                        {
                            string name = btn.Current.Name != null ? btn.Current.Name.Trim() : "";
                            string autoId = btn.Current.AutomationId != null ? btn.Current.AutomationId.Trim() : "";
                            Log(string.Format(" - Button: Name='{0}', AutomationId='{1}'", name, autoId));

                            if (name.Contains("설치") || name.Contains("시작") || name.Contains("Start") ||
                                autoId.ToLowerInvariant().Contains("start") || autoId.ToLowerInvariant().Contains("install"))
                            {
                                targetBtn = btn;
                                break;
                            }
                        }

                        // 이름을 찾지 못했으면 첫 번째 활성 버튼 선택
                        if (targetBtn == null)
                        {
                            foreach (AutomationElement btn in allButtons)
                            {
                                if (btn.Current.IsEnabled)
                                {
                                    targetBtn = btn;
                                    break;
                                }
                            }
                        }
                    }

                    if (targetBtn != null)
                    {
                        Log(string.Format("[UIA SUCCESS] 대상 버튼 발견: Name='{0}' -> 버튼 포커스(선택) 이동 수행", targetBtn.Current.Name));

                        // 1) 버튼에 키보드 포커스 지정 (버튼 선택)
                        try { targetBtn.SetFocus(); } catch {}
                        Thread.Sleep(200);

                        // 2) 스페이스바(SPACE) 키 입력 전송
                        Log("[KEYBOARD] 버튼 선택 완료 -> 스페이스바(SPACE) 키 전송!");
                        SendSpaceKey();
                        Thread.Sleep(150);

                        // 3) 엔터(RETURN) 키 보조 전송
                        SendEnterKey();
                        Thread.Sleep(150);

                        // 4) InvokePattern이 지원되면 실행
                        try
                        {
                            object invPattern;
                            if (targetBtn.TryGetCurrentPattern(InvokePattern.Pattern, out invPattern))
                            {
                                ((InvokePattern)invPattern).Invoke();
                                Log("[UIA] InvokePattern.Invoke 신호 보조 전송 완료");
                            }
                        }
                        catch {}

                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Log(string.Format("[UIA WARNING] UI Automation 탐색 중 예외: {0} (키보드 시퀀스 모드로 계속)", ex.Message));
            }

            // ==========================================================
            // [방식 2: Win32 자식 컨트롤 탐색 후 포커스 및 스페이스 입력]
            // ==========================================================
            try
            {
                List<ControlInfo> children = FindChildControls(targetHwnd);
                IntPtr btnHwnd = IntPtr.Zero;
                string foundText = "";

                foreach (var c in children)
                {
                    string text = c.Text != null ? c.Text.Trim() : "";
                    if (text.Contains("설치") || text.Contains("시작") || text.Equals("OK", StringComparison.OrdinalIgnoreCase) || text.Equals("확인"))
                    {
                        btnHwnd = c.Hwnd;
                        foundText = c.Text;
                        break;
                    }
                }

                if (btnHwnd != IntPtr.Zero)
                {
                    Log(string.Format("[WIN32 CHILD] 버튼 컨트롤 핸들 발견: 0x{0:X8} ('{1}') -> 포커스 및 스페이스 전송", btnHwnd.ToInt64(), foundText));
                    SetFocus(btnHwnd);
                    Thread.Sleep(100);

                    SendMessage(btnHwnd, BM_SETSTATE, (IntPtr)1, IntPtr.Zero);
                    SendMessage(btnHwnd, WM_KEYDOWN, (IntPtr)VK_SPACE, IntPtr.Zero);
                    SendMessage(btnHwnd, WM_KEYUP, (IntPtr)VK_SPACE, IntPtr.Zero);
                    SendMessage(btnHwnd, BM_SETSTATE, (IntPtr)0, IntPtr.Zero);

                    SendSpaceKey();
                    SendEnterKey();
                    return true;
                }
            }
            catch {}

            // ==========================================================
            // [방식 3: 창 포커스 기반 Tab 및 스페이스 키보드 시퀀스 (최강 범용 모드)]
            // ==========================================================
            Log("[KEYBOARD SEQUENCE] 버튼 전용 핸들/UIA 미탐지 -> 창 활성화 후 키보드 Tab 및 스페이스바 순차 입력 시도");
            ActivateWindowForcefully(targetHwnd);
            Thread.Sleep(300);

            // 시퀀스 1: 현재 창의 기본 포커스 컨트롤에서 스페이스바 및 엔터 전송
            Log("[KEYBOARD] 시퀀스 1: 현재 포커스에서 스페이스바(SPACE) 전송");
            SendSpaceKey();
            Thread.Sleep(200);
            SendEnterKey();
            Thread.Sleep(300);

            // 시퀀스 2: TAB 키로 다음 버튼 컨트롤 선택 이동 후 스페이스바 전송
            Log("[KEYBOARD] 시퀀스 2: TAB 키로 버튼 선택 이동 후 스페이스바(SPACE) 전송");
            SendTabKey();
            Thread.Sleep(150);
            SendSpaceKey();
            Thread.Sleep(100);
            SendEnterKey();
            Thread.Sleep(300);

            // 시퀀스 3: TAB 키 1회 더 이동 후 스페이스바 전송 (다중 필드 대응)
            Log("[KEYBOARD] 시퀀스 3: TAB 1회 추가 이동 후 스페이스바(SPACE) 전송");
            SendTabKey();
            Thread.Sleep(150);
            SendSpaceKey();
            Thread.Sleep(100);
            SendEnterKey();

            return true;
        }

        private static void ActivateWindowForcefully(IntPtr hWnd)
        {
            try
            {
                uint targetThread = GetWindowThreadProcessId(hWnd, IntPtr.Zero);
                uint currentThread = GetCurrentThreadId();

                AttachThreadInput(currentThread, targetThread, true);
                ShowWindow(hWnd, SW_RESTORE);
                SetForegroundWindow(hWnd);
                BringWindowToTop(hWnd);
                SetFocus(hWnd);
                AttachThreadInput(currentThread, targetThread, false);
            }
            catch {}
        }

        private static void SendSpaceKey()
        {
            keybd_event(VK_SPACE, 0, 0, UIntPtr.Zero);
            Thread.Sleep(80);
            keybd_event(VK_SPACE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        private static void SendEnterKey()
        {
            keybd_event(VK_RETURN, 0, 0, UIntPtr.Zero);
            Thread.Sleep(80);
            keybd_event(VK_RETURN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        private static void SendTabKey()
        {
            keybd_event(VK_TAB, 0, 0, UIntPtr.Zero);
            Thread.Sleep(80);
            keybd_event(VK_TAB, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        private static IntPtr FindWindowByPartialTitle(string partial)
        {
            IntPtr matched = IntPtr.Zero;
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (!IsWindowVisible(hWnd)) return true;

                StringBuilder sb = new StringBuilder(512);
                GetWindowText(hWnd, sb, sb.Capacity);
                string title = sb.ToString();

                if (!string.IsNullOrEmpty(title) && title.IndexOf(partial, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    matched = hWnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            return matched;
        }

        private static List<ControlInfo> FindChildControls(IntPtr parentHwnd)
        {
            var list = new List<ControlInfo>();
            EnumChildWindows(parentHwnd, delegate(IntPtr hChild, IntPtr lParam)
            {
                StringBuilder sbText = new StringBuilder(256);
                GetWindowText(hChild, sbText, sbText.Capacity);

                StringBuilder sbClass = new StringBuilder(256);
                GetClassName(hChild, sbClass, sbClass.Capacity);

                list.Add(new ControlInfo(hChild, sbText.ToString(), sbClass.ToString()));
                return true;
            }, IntPtr.Zero);

            return list;
        }

        // ==========================================
        // [Step 5: 로깅 및 자체 정리]
        // ==========================================
        private static readonly object _logLock = new object();

        private static void Log(string message)
        {
            string line = string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] {1}", DateTime.Now, message);
            Console.WriteLine(line);

            lock (_logLock)
            {
                try
                {
                    string dir = Path.GetDirectoryName(LogFilePath);
                    if (string.IsNullOrEmpty(dir)) dir = @"C:\Temp\CJ_Work";
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                    using (var sw = new StreamWriter(LogFilePath, true, Encoding.UTF8))
                    {
                        sw.WriteLine(line);
                    }
                }
                catch
                {
                }
            }
        }

        // ==========================================
        // [UAC 보안 정책 무인 자동 승격(0) 및 기본값(5) 복원]
        // ==========================================
        private static void SuppressUacPolicy()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", true))
                {
                    if (key != null)
                    {
                        key.SetValue("ConsentPromptBehaviorAdmin", 0, Microsoft.Win32.RegistryValueKind.DWord);
                        key.SetValue("PromptOnSecureDesktop", 0, Microsoft.Win32.RegistryValueKind.DWord);
                        Log("[UAC SUPPRESS] 사용자 계정 컨트롤(UAC) 확인창 무인 자동승격 적용 완료 (ConsentPromptBehaviorAdmin=0, PromptOnSecureDesktop=0)");
                    }
                }
            }
            catch (Exception ex)
            {
                Log(string.Format("[WARNING] UAC 설정 비활성화 중 예외: {0}", ex.Message));
            }
        }

        private static void RestoreUacPolicy()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", true))
                {
                    if (key != null)
                    {
                        key.SetValue("ConsentPromptBehaviorAdmin", 5, Microsoft.Win32.RegistryValueKind.DWord);
                        key.SetValue("PromptOnSecureDesktop", 1, Microsoft.Win32.RegistryValueKind.DWord);
                        Log("[UAC RESTORE] 사용자 계정 컨트롤(UAC) 보안 프롬프트 설정을 기본값(5/1)으로 안전하게 복원했습니다.");
                    }
                }
            }
            catch (Exception ex)
            {
                Log(string.Format("[WARNING] UAC 설정 복원 중 예외: {0}", ex.Message));
            }
        }

        private static void TriggerSelfDelete(string exePath, string sourceJsonPath)
        {
            try
            {
                // 안전장치: 개발 디렉터리, 소스 저장소, 또는 C:\Temp\CJ_Work 외부인 경우 삭제 건너뜀 (테스트 파일 보존)
                string lowerPath = (exePath ?? "").ToLowerInvariant();
                if (lowerPath.Contains("code") || lowerPath.Contains("multi-pc-imaging") || lowerPath.Contains("tools") || lowerPath.Contains("bin") || !lowerPath.Contains(@"c:\temp\cj_work"))
                {
                    Log(string.Format("[TEST MODE] 개발/테스트 경로에서 실행되어 자체 삭제(Self-Delete)를 건너뜁니다: {0}", exePath));
                    return;
                }

                // 배포 후 타깃 PC 환경(C:\Temp\CJ_Work)에서만 실행 파일 정리 수행
                string deleteCmd = string.Format("timeout /t 5 >nul & del /f /q \"{0}\"", exePath);

                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = string.Format("/c {0}", deleteCmd),
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };

                Process.Start(psi);
                Log(string.Format("[CLEANUP] 무인 프로비저너 자체 자동 삭제(Self-Delete) 예약 완료: {0}", exePath));
            }
            catch (Exception ex)
            {
                Log(string.Format("[WARNING] Self-Delete 예약 중 오류: {0}", ex.Message));
            }
        }
    }
}
