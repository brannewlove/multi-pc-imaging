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

    // 1. DISM /Apply-Image 명령 구성
    // 예: dism.exe /Apply-Image /ImageFile:"Z:\images\win11.wim" /Index:1 /ApplyDir:W:
    std::wstring cmdLine = L"dism.exe /Apply-Image /ImageFile:\"" + 
        std::wstring(params.imagePath.begin(), params.imagePath.end()) + 
        L"\" /Index:" + std::to_wstring(params.imageIndex) + 
        L" /ApplyDir:\"" + std::wstring(params.targetDrive.begin(), params.targetDrive.end()) + L"\\\"";

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
            extraErr = " (WIM 파일 경로를 찾을 수 없음: " + params.imagePath + ")";
        } else if (exitCode == 87) {
            extraErr = " (DISM 매개변수 형식 오류)";
        }
        result.errorMessage = "DISM 실패(코드 " + std::to_string(exitCode) + ")" + extraErr;
        return result;
    }

    // 2. 부트로더 생성 (BCDBoot)
    if (params.runBcdBoot && !params.bootDrive.empty()) {
        if (onProgress) {
            DeployProgress prog;
            prog.step = "UEFI 부트로더 생성 (BCDBoot)";
            prog.percent = 99.0;
            prog.elapsedSeconds = result.durationSeconds;
            prog.detail = "Configuring UEFI EFI System Partition...";
            onProgress(prog);
        }

        // bcdboot W:\Windows /s S: /f UEFI
        std::wstring bcdCmd = L"bcdboot.exe \"" + 
            std::wstring(params.targetDrive.begin(), params.targetDrive.end()) + 
            L"\\Windows\" /s \"" + 
            std::wstring(params.bootDrive.begin(), params.bootDrive.end()) + 
            L"\" /f UEFI";

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
