#include "partition_engine.h"
#include <windows.h>
#include <fstream>
#include <sstream>
#include <iostream>

namespace PartitionEngine {

PartitionResult ExecutePartitionPlan(int targetDiskIndex, const std::vector<std::string>& commands) {
    PartitionResult res;

    // 1. 임시 diskpart 스크립트 파일 생성 (WinPE X:\Windows\Temp 또는 현재 디렉터리)
    wchar_t tempPath[MAX_PATH];
    GetTempPathW(MAX_PATH, tempPath);
    std::wstring scriptPath = std::wstring(tempPath) + L"deploy_diskpart.txt";

    std::ofstream ofs(scriptPath.c_str());
    if (!ofs.is_open()) {
        res.success = false;
        res.errorMessage = "Failed to create temporary diskpart script file.";
        return res;
    }

    ofs << "rescan\n";
    for (const auto& cmd : commands) {
        ofs << cmd << "\n";
    }
    ofs.close();

    // 2. diskpart.exe /s <scriptPath> 실행 및 출력 파이프 캡처
    std::wstring cmdLine = L"diskpart.exe /s \"" + scriptPath + L"\"";

    SECURITY_ATTRIBUTES sa = {};
    sa.nLength = sizeof(SECURITY_ATTRIBUTES);
    sa.bInheritHandle = TRUE;

    HANDLE hReadPipe, hWritePipe;
    if (!CreatePipe(&hReadPipe, &hWritePipe, &sa, 0)) {
        res.success = false;
        res.errorMessage = "Failed to create process pipe for diskpart.";
        return res;
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
        res.success = false;
        res.errorMessage = "Failed to launch diskpart.exe process.";
        return res;
    }

    // 출력 읽기
    char buffer[512];
    DWORD bytesRead = 0;
    std::string fullOutput;
    while (ReadFile(hReadPipe, buffer, sizeof(buffer) - 1, &bytesRead, nullptr) && bytesRead > 0) {
        buffer[bytesRead] = 0;
        fullOutput += buffer;
    }

    WaitForSingleObject(pi.hProcess, 180000); // 최대 3분 대기
    DWORD exitCode = 0;
    GetExitCodeProcess(pi.hProcess, &exitCode);

    CloseHandle(pi.hProcess);
    CloseHandle(pi.hThread);
    CloseHandle(hReadPipe);
    DeleteFileW(scriptPath.c_str());

    std::istringstream stream(fullOutput);
    std::string line;
    while (std::getline(stream, line)) {
        if (!line.empty() && line.back() == '\r') line.pop_back();
        if (!line.empty()) {
            res.outputLogs.push_back(line);
        }
    }

    if (exitCode == 0) {
        res.success = true;
    } else {
        res.success = false;
        res.errorMessage = "Diskpart terminated with non-zero exit code: " + std::to_string(exitCode);
    }

    return res;
}

} // namespace PartitionEngine
