#include "net_client.h"
#include <winsock2.h>
#include <ws2tcpip.h>
#include <iostream>
#include <vector>

#pragma comment(lib, "ws2_32.lib")

namespace NetClient {

static bool g_wsaInit = false;
static void EnsureWsaInit() {
    if (!g_wsaInit) {
        WSADATA wsa;
        WSAStartup(MAKEWORD(2, 2), &wsa);
        g_wsaInit = true;
    }
}

bool DiscoverMasterServer(int timeoutSeconds, MasterConnectionInfo& outInfo) {
    EnsureWsaInit();

    SOCKET udpSock = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (udpSock == INVALID_SOCKET) return false;

    BOOL broadcastEnable = TRUE;
    setsockopt(udpSock, SOL_SOCKET, SO_BROADCAST, reinterpret_cast<const char*>(&broadcastEnable), sizeof(broadcastEnable));

    DWORD timeout = timeoutSeconds * 1000;
    setsockopt(udpSock, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&timeout), sizeof(timeout));

    sockaddr_in broadcastAddr = {};
    broadcastAddr.sin_family = AF_INET;
    broadcastAddr.sin_port = htons(39200);
    broadcastAddr.sin_addr.s_addr = INADDR_BROADCAST;

    const char* discoveryMsg = "{\"type\":\"CLIENT_DISCOVERY\"}\n";
    sendto(udpSock, discoveryMsg, (int)strlen(discoveryMsg), 0,
           reinterpret_cast<sockaddr*>(&broadcastAddr), sizeof(broadcastAddr));

    char recvBuf[1024] = {0};
    sockaddr_in senderAddr = {};
    int senderLen = sizeof(senderAddr);

    int recvBytes = recvfrom(udpSock, recvBuf, sizeof(recvBuf) - 1, 0,
                             reinterpret_cast<sockaddr*>(&senderAddr), &senderLen);

    closesocket(udpSock);

    if (recvBytes > 0) {
        recvBuf[recvBytes] = 0;
        char ipStr[INET_ADDRSTRLEN] = {0};
        inet_ntop(AF_INET, &(senderAddr.sin_addr), ipStr, sizeof(ipStr));
        outInfo.ip = ipStr;
        outInfo.tcpPort = 39201;
        return true;
    }

    return false;
}

AgentSocketClient::AgentSocketClient() : m_socket(INVALID_SOCKET), m_connected(false) {
    EnsureWsaInit();
}

AgentSocketClient::~AgentSocketClient() {
    Close();
}

bool AgentSocketClient::Connect(const std::string& masterIp, int tcpPort) {
    Close();

    SOCKET s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (s == INVALID_SOCKET) return false;

    sockaddr_in serverAddr = {};
    serverAddr.sin_family = AF_INET;
    serverAddr.sin_port = htons(tcpPort);
    inet_pton(AF_INET, masterIp.c_str(), &(serverAddr.sin_addr));

    if (connect(s, reinterpret_cast<sockaddr*>(&serverAddr), sizeof(serverAddr)) == SOCKET_ERROR) {
        closesocket(s);
        return false;
    }

    m_socket = s;
    m_connected = true;
    return true;
}

void AgentSocketClient::Close() {
    if (m_socket != INVALID_SOCKET) {
        closesocket(m_socket);
        m_socket = INVALID_SOCKET;
    }
    m_connected = false;
}

bool AgentSocketClient::IsConnected() const {
    return m_connected;
}

bool AgentSocketClient::SendJsonMessage(const std::string& jsonString) {
    if (!m_connected) return false;
    std::string payload = jsonString + "\n";
    int sent = send(m_socket, payload.c_str(), (int)payload.length(), 0);
    return sent == (int)payload.length();
}

bool AgentSocketClient::ReadMessage(std::string& outJsonString, int timeoutMs) {
    if (!m_connected) return false;

    if (timeoutMs > 0) {
        fd_set readSet;
        FD_ZERO(&readSet);
        FD_SET(m_socket, &readSet);

        timeval tv;
        tv.tv_sec = timeoutMs / 1000;
        tv.tv_usec = (timeoutMs % 1000) * 1000;

        int sel = select(0, &readSet, nullptr, nullptr, &tv);
        if (sel <= 0) return false;
    }

    outJsonString.clear();
    char c = 0;
    while (true) {
        int r = recv(m_socket, &c, 1, 0);
        if (r <= 0) {
            Close();
            return false;
        }
        if (c == '\n') break;
        outJsonString += c;
    }

    return true;
}

} // namespace NetClient
