#include "partition_engine.h"
#include <windows.h>
#include <fstream>
#include <sstream>
#include <iostream>

namespace PartitionEngine {

// diskpart 스크립트 명령 자동 정제 유틸리티
// 따옴표가 들어간 label="System" 또는 assign letter="S"는 VDS 에러(0x80042429 / 2147755049)를 유발하므로 따옴표를 정제
static std::string SanitizeDiskpartCommand(std::string cmd) {
    while (!cmd.empty() && (cmd.front() == ' ' || cmd.front() == '\t')) cmd.erase(cmd.begin());
    while (!cmd.empty() && (cmd.back() == ' ' || cmd.back() == '\t' || cmd.back() == '\r' || cmd.back() == '\n')) cmd.pop_back();

    // 1. assign letter="S" 또는 assign letter=S: -> assign letter=S (따옴표 및 콜론 제거)
    size_t assignPos = cmd.find("assign letter=");
    if (assignPos != std::string::npos) {
        std::string prefix = cmd.substr(0, assignPos + 14);
        std::string letterVal = cmd.substr(assignPos + 14);
        std::string cleaned;
        for (char c : letterVal) {
            if (c != '"' && c != '\'' && c != ':' && c != ' ') {
                cleaned.push_back(c);
            }
        }
        return prefix + cleaned;
    }

    // 2. format quick fs=fat32 label="System" -> 따옴표 제거 (0x80042429 VDS_E_BAD_LABEL 원천 차단)
    size_t labelPos = cmd.find("label=");
    if (labelPos != std::string::npos) {
        std::string prefix = cmd.substr(0, labelPos + 6);
        std::string rest = cmd.substr(labelPos + 6);
        std::string cleanedRest;
        char qChar = 0;
        size_t idx = 0;
        if (!rest.empty() && (rest[0] == '"' || rest[0] == '\'')) {
            qChar = rest[0];
            idx = 1;
        }
        while (idx < rest.length()) {
            if (qChar != 0 && rest[idx] == qChar) {
                idx++;
                break;
            } else if (qChar == 0 && (rest[idx] == ' ' || rest[idx] == '\t')) {
                break;
            } else {
                if (rest[idx] != '"' && rest[idx] != '\'') {
                    cleanedRest.push_back(rest[idx]);
                }
                idx++;
            }
        }
        cleanedRest += rest.substr(idx);
        return prefix + cleanedRest;
    }

    return cmd;
}

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
    for (const auto& rawCmd : commands) {
        std::string cleanCmd = SanitizeDiskpartCommand(rawCmd);
        if (!cleanCmd.empty()) {
            ofs << cleanCmd << "\n";
        }
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
        std::string errDetail;
        for (const auto& l : res.outputLogs) {
            if (l.find("오류") != std::string::npos || l.find("error") != std::string::npos ||
                l.find("Error") != std::string::npos || l.find("실패") != std::string::npos ||
                l.find("VDS") != std::string::npos || l.find("Virtual Disk Service") != std::string::npos) {
                if (!errDetail.empty()) errDetail += " | ";
                errDetail += l;
            }
        }
        if (exitCode == 2147755049 || exitCode == 0x80042429) {
            errDetail += " (VDS_E_BAD_LABEL: 볼륨 레이블 형식/문자 오류)";
        }
        res.errorMessage = "Diskpart 오류 (코드 " + std::to_string(exitCode) + "): " + 
            (errDetail.empty() ? "디스크 파티셔닝 작업 실패" : errDetail);
    }

    return res;
}

} // namespace PartitionEngine
