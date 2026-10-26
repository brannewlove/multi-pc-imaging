#include "deploy_engine.h"
#include <windows.h>
#include <iostream>
#include <chrono>
#include <sstream>
#include <regex>

namespace DeployEngine {

static double ParseDismPercent(const std::string& line) {
    // DISM 출력 예: "[==== 15.0% ====]" 또는 "[ 100.0% ]"
    static const std::regex rgx(R"((\d{1,3}(\.\d+)?)%)");
    std::smatch match;
    if (std::regex_search(line, match, rgx)) {
        try {
            return std::stod(match[1].str());
        } catch (...) {}
    }
    return -1.0;
}

DeployResult ApplyWimImage(const DeployParams& params, ProgressCallback onProgress) {
    DeployResult result;
    auto startTime = std::chrono::steady_clock::now();

    // 대상 드라이브 정규화 (예: "W:", "W:\", "W" -> "W:")
    std::string cleanTarget = params.targetDrive;
    while (!cleanTarget.empty() && (cleanTarget.front() == ' ' || cleanTarget.front() == '"' || cleanTarget.front() == '\'')) cleanTarget.erase(cleanTarget.begin());
    while (!cleanTarget.empty() && (cleanTarget.back() == ' ' || cleanTarget.back() == '"' || cleanTarget.back() == '\'' || cleanTarget.back() == '\\' || cleanTarget.back() == '/')) cleanTarget.pop_back();
    if (cleanTarget.length() == 1) cleanTarget += ":";
    if (cleanTarget.empty()) cleanTarget = "W:";

    // 부트 드라이브 정규화 (예: "S:", "S:\", "S" -> "S:")
    std::string cleanBoot = params.bootDrive;
    while (!cleanBoot.empty() && (cleanBoot.front() == ' ' || cleanBoot.front() == '"' || cleanBoot.front() == '\'')) cleanBoot.erase(cleanBoot.begin());
    while (!cleanBoot.empty() && (cleanBoot.back() == ' ' || cleanBoot.back() == '"' || cleanBoot.back() == '\'' || cleanBoot.back() == '\\' || cleanBoot.back() == '/')) cleanBoot.pop_back();
    if (cleanBoot.length() == 1) cleanBoot += ":";
    if (cleanBoot.empty()) cleanBoot = "S:";

    // WIM 이미지 파일 경로 정규화 (따옴표 제거, 슬래시 통일, 끝 슬래시 제거)
    std::string cleanImage = params.imagePath;
    while (!cleanImage.empty() && (cleanImage.front() == ' ' || cleanImage.front() == '"' || cleanImage.front() == '\'')) cleanImage.erase(cleanImage.begin());
    while (!cleanImage.empty() && (cleanImage.back() == ' ' || cleanImage.back() == '"' || cleanImage.back() == '\'')) cleanImage.pop_back();
    for (char& c : cleanImage) {
        if (c == '/') c = '\\';
    }
    while (cleanImage.length() > 3 && cleanImage.back() == '\\') cleanImage.pop_back();

    std::wstring wCleanImage(cleanImage.begin(), cleanImage.end());
    std::wstring wCleanTarget(cleanTarget.begin(), cleanTarget.end());
    std::wstring wCleanBoot(cleanBoot.begin(), cleanBoot.end());

    // 1. DISM /Apply-Image 명령 구성
    // 주의: /ApplyDir:"W:\" 처럼 끝에 \" 가 오면 Windows CommandLineToArgvW 파서에서
    // 따옴표가 이스케이프되어 인자 내부로 들어가 ERROR_INVALID_NAME(123)이 발생합니다.
    // 드라이브 루트(W:\)는 공백이 없으므로 따옴표 없이 /ApplyDir:W:\ 로 전달하는 것이 안전합니다.
    std::wstring cmdLine = L"dism.exe /Apply-Image /ImageFile:\"" + wCleanImage + 
        L"\" /Index:" + std::to_wstring(params.imageIndex) + 
        L" /ApplyDir:" + wCleanTarget + L"\\";

    SECURITY_ATTRIBUTES sa = {};
    sa.nLength = sizeof(SECURITY_ATTRIBUTES);
    sa.bInheritHandle = TRUE;

    HANDLE hReadPipe, hWritePipe;
    if (!CreatePipe(&hReadPipe, &hWritePipe, &sa, 0)) {
        result.success = false;
        result.errorMessage = "Failed to create pipe for DISM execution.";
        return result;
    }
    SetHandleInformation(hReadPipe, HANDLE_FLAG_INHERIT, 0);

    STARTUPINFOW si = {};
    si.cb = sizeof(STARTUPINFO);
    si.hStdOutput = hWritePipe;
    si.hStdError = hWritePipe;
    si.dwFlags |= STARTF_USESTDHANDLES;

    PROCESS_INFORMATION pi = {};
    std::vector<wchar_t> cmdLineBuf(cmdLine.begin(), cmdLine.end());
    cmdLineBuf.push_back(0);

    BOOL created = CreateProcessW(nullptr, cmdLineBuf.data(), nullptr, nullptr, TRUE,
                                  CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi);
    CloseHandle(hWritePipe);

    if (!created) {
        CloseHandle(hReadPipe);
        result.success = false;
        result.errorMessage = "Failed to launch DISM process. Ensure dism.exe exists in WinPE.";
        return result;
    }

    char buffer[1024];
    DWORD bytesRead = 0;
    std::string currentLine;
    double lastPercent = 0.0;

    while (ReadFile(hReadPipe, buffer, sizeof(buffer) - 1, &bytesRead, nullptr) && bytesRead > 0) {
        buffer[bytesRead] = 0;
        result.detailedLogs += buffer;

        for (DWORD i = 0; i < bytesRead; ++i) {
            char c = buffer[i];
            if (c == '\r' || c == '\n') {
                if (!currentLine.empty()) {
                    double pct = ParseDismPercent(currentLine);
                    if (pct >= 0.0 && onProgress) {
                        lastPercent = pct;
                        auto now = std::chrono::steady_clock::now();
                        int elapsed = (int)std::chrono::duration_cast<std::chrono::seconds>(now - startTime).count();
                        int remaining = 0;
                        if (pct > 0.0) {
                            remaining = (int)((elapsed / pct) * (100.0 - pct));
                        }

                        DeployProgress prog;
                        prog.step = "WIM 이미지 적용 (DISM Apply-Image)";
                        prog.percent = pct;
                        prog.elapsedSeconds = elapsed;
                        prog.remainingSeconds = remaining;
                        prog.detail = currentLine;
                        onProgress(prog);
                    }
                    currentLine.clear();
                }
            } else {
                currentLine += c;
            }
        }
    }

    WaitForSingleObject(pi.hProcess, INFINITE);
    DWORD exitCode = 0;
    GetExitCodeProcess(pi.hProcess, &exitCode);

    CloseHandle(pi.hProcess);
    CloseHandle(pi.hThread);
    CloseHandle(hReadPipe);

    auto endTime = std::chrono::steady_clock::now();
    result.durationSeconds = (int)std::chrono::duration_cast<std::chrono::seconds>(endTime - startTime).count();

    if (exitCode != 0) {
        result.success = false;
        std::string extraErr = "";
        if (exitCode == 2 || exitCode == 3) {
            extraErr = " (WIM 파일 경로를 찾을 수 없음: " + cleanImage + ")";
        } else if (exitCode == 5) {
            extraErr = " (공유 폴더 접근 거부됨 ERROR_ACCESS_DENIED - 권한 필요)";
        } else if (exitCode == 67) {
            extraErr = " (네트워크 공유 이름 오류 ERROR_BAD_NET_NAME - 서버 IP/공유이름 확인 필요: " + cleanImage + ")";
        } else if (exitCode == 87) {
            extraErr = " (DISM 매개변수 형식 오류 ERROR_INVALID_PARAMETER)";
        } else if (exitCode == 123) {
            extraErr = " (경로 구문 오류 ERROR_INVALID_NAME - 잘못된 파일명/공유경로 또는 특수문자: " + cleanImage + ")";
        } else if (exitCode == 1326) {
            extraErr = " (네트워크 로그인 실패 ERROR_LOGON_FAILURE - 사용자명/비밀번호 확인)";
        }
        result.errorMessage = "DISM 실패(코드 " + std::to_string(exitCode) + ")" + extraErr;
        return result;
    }

    // 2. 부트로더 생성 (BCDBoot)
    if (params.runBcdBoot && !cleanBoot.empty()) {
        if (onProgress) {
            DeployProgress prog;
            prog.step = "UEFI 부트로더 생성 (BCDBoot)";
            prog.percent = 99.0;
            prog.elapsedSeconds = result.durationSeconds;
            prog.detail = "Configuring UEFI EFI System Partition...";
            onProgress(prog);
        }

        // bcdboot W:\Windows /s S: /f UEFI (드라이브 문자는 공백이 없으므로 따옴표 불필요)
        std::wstring bcdCmd = L"bcdboot.exe " + wCleanTarget + L"\\Windows /s " + wCleanBoot + L" /f UEFI";

        STARTUPINFOW bcdSi = {};
        bcdSi.cb = sizeof(STARTUPINFO);
        PROCESS_INFORMATION bcdPi = {};
        std::vector<wchar_t> bcdBuf(bcdCmd.begin(), bcdCmd.end());
        bcdBuf.push_back(0);

        if (CreateProcessW(nullptr, bcdBuf.data(), nullptr, nullptr, FALSE,
                           CREATE_NO_WINDOW, nullptr, nullptr, &bcdSi, &bcdPi)) {
            WaitForSingleObject(bcdPi.hProcess, 30000);
            DWORD bcdExit = 0;
            GetExitCodeProcess(bcdPi.hProcess, &bcdExit);
            CloseHandle(bcdPi.hProcess);
            CloseHandle(bcdPi.hThread);

            if (bcdExit != 0) {
                result.success = false;
                result.errorMessage = "BCDBoot failed with exit code: " + std::to_string(bcdExit);
                return result;
            }
        }
    }

    result.success = true;
    return result;
}

} // namespace DeployEngine
