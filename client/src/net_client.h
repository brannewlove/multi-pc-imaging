#pragma once
#include <string>
#include <functional>
#include <vector>
#include "simple_json.h"

namespace NetClient {

struct MasterConnectionInfo {
    std::string ip;
    int tcpPort = 39201;
};

// 마스터 서버 자동 탐색 (UDP Broadcast)
bool DiscoverMasterServer(int timeoutSeconds, MasterConnectionInfo& outInfo);

// 마스터와 지속적인 통신을 담당하는 TCP 세션 클래스
class AgentSocketClient {
public:
    AgentSocketClient();
    ~AgentSocketClient();

    bool Connect(const std::string& masterIp, int tcpPort);
    void Close();
    bool IsConnected() const;

    bool SendJsonMessage(const std::string& jsonString);
    bool ReadMessage(std::string& outJsonString, int timeoutMs = 0);

private:
    uintptr_t m_socket;
    bool m_connected;
};

} // namespace NetClient
