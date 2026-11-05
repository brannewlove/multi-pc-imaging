using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

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

        // 폴링 대기 시간
        private const int MaxWaitSeconds = 25;
        private const int PollIntervalMs = 500;

        // ==========================================
        // [Win32 Native API 선언]
        // ==========================================
        private const uint WM_COMMAND = 0x0111;
        private const uint BM_CLICK = 0x00F5;
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
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

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

        private const byte VK_RETURN = 0x0D;
        private const byte VK_SPACE = 0x20;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        // ==========================================
        // [Main 진입점]
        // ==========================================
        public static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Log("================================================================================");
            Log("[CJ 무인 프로비저너] Post-OOBE Auto-Provisioner v1.0.0 시작");
            Log("================================================================================");

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

                // 무인 프로비저너 활성화 여부 확인
                if (!info.Enabled)
                {
                    Log("[INFO] 무인 프로비저너가 비활성화(enabled=false)로 설정되어 있습니다. 작업을 건너뛰고 종료합니다.");
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

                // [Step 3] 보안 프로그램 프로세스 실행
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
                Log(string.Format("[Step 3] 보안 프로그램 실행 중... (실행경로: {0})", appPath));
                Process targetProcess = LaunchTargetApplication(appPath);

                // [Step 4] 창 로드 대기 및 [설치 시작] 버튼 자동 클릭
                Log(string.Format("[Step 4] 프로그램 창 '{0}' 로드 대기 및 '{1}' 버튼 클릭 시도...", WindowTitleExact, TargetButtonText));
                bool clickSuccess = WaitAndClickStartButton(WindowTitleExact, WindowTitlePartial, TargetButtonText, MaxWaitSeconds);

                if (clickSuccess)
                {
                    Log("[SUCCESS] 보안 소프트웨어 [설치 시작] 버튼 클릭 신호가 성공적으로 전송되었습니다.");
                }
                else
                {
                    Log("[WARNING] 제한 시간 내 버튼을 감지하지 못했습니다. (창이 이미 완료되었거나 이미 진행 중일 수 있습니다.)");
                }

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
        // [Step 4: 창 감지 및 버튼 클릭]
        // ==========================================
        private static bool WaitAndClickStartButton(string exactTitle, string partialTitle, string buttonText, int timeoutSeconds)
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

            ShowWindow(targetHwnd, SW_RESTORE);
            SetForegroundWindow(targetHwnd);
            Thread.Sleep(400);

            DateTime btnDeadline = DateTime.Now.AddSeconds(5);
            IntPtr btnHwnd = IntPtr.Zero;
            string foundBtnText = "";

            while (DateTime.Now < btnDeadline)
            {
                List<ControlInfo> buttons = FindChildControls(targetHwnd);
                foreach (var b in buttons)
                {
                    string text = b.Text != null ? b.Text.Trim() : "";
                    if (text.Equals(buttonText, StringComparison.OrdinalIgnoreCase) ||
                        text.IndexOf("설치 시작", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        text.IndexOf("설치시작", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        text.Equals("시작", StringComparison.OrdinalIgnoreCase))
                    {
                        btnHwnd = b.Hwnd;
                        foundBtnText = b.Text;
                        break;
                    }
                }

                if (btnHwnd != IntPtr.Zero) break;
                Thread.Sleep(300);
            }

            if (btnHwnd != IntPtr.Zero)
            {
                Log(string.Format("[BUTTON FOUND] 버튼 컨트롤 발견: '{0}' (핸들: 0x{1:X8})", foundBtnText, btnHwnd.ToInt64()));

                SendMessage(btnHwnd, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                Thread.Sleep(100);

                SendMessage(btnHwnd, WM_LBUTTONDOWN, (IntPtr)1, IntPtr.Zero);
                Thread.Sleep(50);
                SendMessage(btnHwnd, WM_LBUTTONUP, IntPtr.Zero, IntPtr.Zero);
                Thread.Sleep(100);
                keybd_event(VK_RETURN, 0, 0, UIntPtr.Zero);
                Thread.Sleep(50);
                keybd_event(VK_RETURN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

                Log("[CLICK] Win32 BM_CLICK 및 WM_LBUTTONDOWN/UP 신호 전송 완료!");
                return true;
            }

            Log("[FALLBACK] 버튼 별도 핸들을 찾지 못함 -> 창 좌표 기반 마우스 클릭 시도");
            RECT rect;
            if (GetWindowRect(targetHwnd, out rect))
            {
                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;

                int clickX = rect.Left + (width / 2);
                int clickY = rect.Top + (int)(height * 0.75);

                SetCursorPos(clickX, clickY);
                Thread.Sleep(50);
                mouse_event(MOUSEEVENTF_LEFTDOWN, (uint)clickX, (uint)clickY, 0, 0);
                Thread.Sleep(50);
                mouse_event(MOUSEEVENTF_LEFTUP, (uint)clickX, (uint)clickY, 0, 0);

                keybd_event(VK_RETURN, 0, 0, UIntPtr.Zero);
                Thread.Sleep(50);
                keybd_event(VK_RETURN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

                Log(string.Format("[CLICK FALLBACK] 창 좌표 ({0}, {1}) 마우스 클릭 전송 완료", clickX, clickY));
                return true;
            }

            return false;
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
                    if (string.IsNullOrEmpty(dir)) dir = @"C:\Temp\CJ_Work\MultiDeploy";
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
