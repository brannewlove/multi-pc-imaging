using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MultiDeployOS.Master.Models;

namespace MultiDeployOS.Master.Services
{
    public class DhcpLeaseInfo
    {
        public string MacAddress { get; set; } = "";
        public string IpAddress { get; set; } = "";
        public DateTime AllocatedAt { get; set; } = DateTime.Now;
        public DateTime ExpiresAt { get; set; } = DateTime.Now.AddHours(8);
        public string Stage { get; set; } = "DHCP"; // UEFI_PXE, IPXE, WINPE_OS
    }

    public class DhcpBootEventArgs
    {
        public string MacAddress { get; set; } = "";
        public string AssignedIp { get; set; } = "";
        public string Stage { get; set; } = "";
        public string Detail { get; set; } = "";
    }

    public class DhcpPxeService : IDisposable
    {
        public const int DhcpServerPort = 67;
        public const int DhcpClientPort = 68;
        public const int ProxyDhcpPort = 4011;

        private Socket? _dhcpSocket;
        private Socket? _proxySocket;
        private CancellationTokenSource? _cts;

        private IPAddress _bindIp = IPAddress.Loopback;
        private uint _bindIpUint;
        private uint _subnetMaskUint;
        private uint _gatewayUint;
        private uint _dnsUint;
        private uint _poolStartUint;
        private uint _poolEndUint;
        private int _leaseSeconds = 28800;
        private int _httpBootPort = 39202;
        private bool _isProxyMode = false;

        public ConcurrentDictionary<string, DhcpLeaseInfo> LeasesByMac { get; } = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<uint, string> _ipToMac = new();
        private readonly HashSet<uint> _declinedIps = new();
        private readonly object _leaseLock = new();

        public event Action<DhcpBootEventArgs>? ClientBootStageChanged;
        public event Action<string>? LogAdded;

        public bool IsRunning => _dhcpSocket != null;

        public void Start(PxeServerConfig config, IPAddress resolvedBindIp)
        {
            Stop();

            _bindIp = resolvedBindIp;
            _isProxyMode = string.Equals(config.DhcpMode, "PROXY", StringComparison.OrdinalIgnoreCase);
            _httpBootPort = config.HttpBootPort > 0 ? config.HttpBootPort : 39202;
            _leaseSeconds = Math.Max(300, config.LeaseTimeSeconds);

            NetworkUtility.TryIpToUint(_bindIp.ToString(), out _bindIpUint);
            if (!NetworkUtility.TryIpToUint(config.SubnetMask, out _subnetMaskUint) || _subnetMaskUint == 0)
            {
                NetworkUtility.TryIpToUint("255.255.255.0", out _subnetMaskUint);
            }

            if (!NetworkUtility.TryIpToUint(config.GatewayIp, out _gatewayUint) || _gatewayUint == 0)
                _gatewayUint = _bindIpUint;

            if (!NetworkUtility.TryIpToUint(config.DnsIp, out _dnsUint) || _dnsUint == 0)
                _dnsUint = _bindIpUint;

            if (!NetworkUtility.TryIpToUint(config.PoolStartIp, out _poolStartUint) ||
                !NetworkUtility.TryIpToUint(config.PoolEndIp, out _poolEndUint) ||
                _poolStartUint > _poolEndUint)
            {
                var suggested = NetworkUtility.SuggestPoolRange(_bindIp.ToString(), NetworkUtility.UintToIp(_subnetMaskUint));
                NetworkUtility.TryIpToUint(suggested.StartIp, out _poolStartUint);
                NetworkUtility.TryIpToUint(suggested.EndIp, out _poolEndUint);
            }

            _cts = new CancellationTokenSource();

            // 1. UDP 67 소켓 바인딩 (선택된 NIC 전용 바인딩으로 타 네트워크 유출 원천 차단)
            _dhcpSocket = CreateBoundUdpSocket(_bindIp, DhcpServerPort);
            Task.Run(() => ReceiveLoopAsync(_dhcpSocket, isProxyPort4011: false, _cts.Token));

            // 2. ProxyDHCP 모드일 경우 UDP 4011 추가 바인딩
            if (_isProxyMode)
            {
                _proxySocket = CreateBoundUdpSocket(_bindIp, ProxyDhcpPort);
                Task.Run(() => ReceiveLoopAsync(_proxySocket, isProxyPort4011: true, _cts.Token));
                Log($"[DHCP] ProxyDHCP 서버 가동 완료 ({_bindIp}:67 / 4011)");
            }
            else
            {
                Log($"[DHCP] 독립 작업대 Standalone DHCP 서버 가동 ({_bindIp}:67, IP 풀: {NetworkUtility.UintToIp(_poolStartUint)} ~ {NetworkUtility.UintToIp(_poolEndUint)})");
            }
        }

        private static Socket CreateBoundUdpSocket(IPAddress bindIp, int port)
        {
            var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            sock.Bind(new IPEndPoint(bindIp, port));
            return sock;
        }

        public string? ResolveMacByIp(string? ipAddress)
        {
            if (string.IsNullOrWhiteSpace(ipAddress)) return null;
            if (NetworkUtility.TryIpToUint(ipAddress, out uint ipUint) && _ipToMac.TryGetValue(ipUint, out string? mac))
            {
                return mac;
            }
            var match = LeasesByMac.Values.FirstOrDefault(l => string.Equals(l.IpAddress, ipAddress, StringComparison.OrdinalIgnoreCase));
            return match?.MacAddress;
        }

        private async Task ReceiveLoopAsync(Socket socket, bool isProxyPort4011, CancellationToken token)
        {
            byte[] buffer = new byte[1500];
            EndPoint anyRemote = new IPEndPoint(IPAddress.Any, 0);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var res = await socket.ReceiveFromAsync(buffer, SocketFlags.None, anyRemote, token);
                    if (res.ReceivedBytes >= 240 && res.RemoteEndPoint is IPEndPoint remoteEp)
                    {
                        byte[] packet = new byte[res.ReceivedBytes];
                        Buffer.BlockCopy(buffer, 0, packet, 0, res.ReceivedBytes);
                        ProcessDhcpPacket(socket, packet, remoteEp, isProxyPort4011);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        Log($"[DHCP 오류] 패킷 수신 중 예외: {ex.Message}");
                    }
                }
            }
        }

        private void ProcessDhcpPacket(Socket socket, byte[] pkt, IPEndPoint remoteEp, bool isProxyPort4011)
        {
            // BOOTP Header 검증
            if (pkt[0] != 1) return; // BOOTREQUEST(1)만 처리
            byte htype = pkt[1];
            byte hlen = pkt[2];
            if (htype != 1 || hlen != 6) return;

            // Magic Cookie (99, 130, 83, 99) 검증
            if (pkt[236] != 99 || pkt[237] != 130 || pkt[238] != 83 || pkt[239] != 99) return;

            string mac = NetworkUtility.NormalizeMac(pkt, 28, 6);
            if (string.IsNullOrEmpty(mac) || mac == "00:00:00:00:00:00") return;

            uint ciaddr = ReadUint32(pkt, 12);
            uint giaddr = ReadUint32(pkt, 24);
            ushort flags = (ushort)((pkt[10] << 8) | pkt[11]);
            bool broadcastFlag = (flags & 0x8000) != 0;

            // DHCP Options 파싱
            var options = ParseOptions(pkt, 240);
            if (!options.TryGetValue(53, out byte[]? msgTypeBytes) || msgTypeBytes.Length < 1) return;
            byte msgType = msgTypeBytes[0]; // 1:DISCOVER, 3:REQUEST, 4:DECLINE, 7:RELEASE, 8:INFORM

            string vendorClass = options.TryGetValue(60, out var opt60) ? Encoding.ASCII.GetString(opt60) : "";
            bool isIpxeUserClass = options.TryGetValue(77, out var opt77) && ContainsAscii(opt77, "iPXE");
            ushort? clientArch = null;
            if (options.TryGetValue(93, out var opt93) && opt93.Length >= 2)
            {
                clientArch = (ushort)((opt93[0] << 8) | opt93[1]);
            }

            bool isPxeClient = vendorClass.StartsWith("PXEClient", StringComparison.OrdinalIgnoreCase) ||
                               vendorClass.StartsWith("HTTPClient", StringComparison.OrdinalIgnoreCase) ||
                               isIpxeUserClass ||
                               clientArch.HasValue;

            bool isUefiHttpClient = !isIpxeUserClass &&
                (vendorClass.StartsWith("HTTPClient", StringComparison.OrdinalIgnoreCase) || clientArch == 16);

            // Legacy BIOS (0x0000) 필터링 — UEFI 64-bit 전용 안내
            if (isPxeClient && !isIpxeUserClass && clientArch.HasValue && clientArch.Value == 0)
            {
                if (msgType == 1)
                {
                    Log($"[PXE 경고] Legacy BIOS 부팅 요청 감지됨 ({mac}) — 본 서버는 UEFI 64-bit 전용입니다. 타깃 PC BIOS에서 UEFI 모드로 변경하세요.");
                }
                return;
            }

            uint? requestedIp = null;
            if (options.TryGetValue(50, out var opt50) && opt50.Length == 4)
            {
                requestedIp = ReadUint32(opt50, 0);
            }

            uint? serverId = null;
            if (options.TryGetValue(54, out var opt54) && opt54.Length == 4)
            {
                serverId = ReadUint32(opt54, 0);
            }

            // 부트 파일 결정 (UEFI 64-bit iPXE 체인로딩)
            string bootFile = "";
            string stageTag = "WINPE_OS";
            string stageDetail = "WinPE 네트워크(DHCP) IP 할당 완료";

            if (isIpxeUserClass)
            {
                bootFile = $"http://{_bindIp}:{_httpBootPort}/boot.ipxe";
                stageTag = "IPXE";
                stageDetail = "iPXE 커널 부팅됨 -> HTTP 초고속 부트 스크립트 요청 대기";
            }
            else if (isUefiHttpClient)
            {
                bootFile = $"http://{_bindIp}:{_httpBootPort}/pxe/snponly.efi";
                stageTag = "UEFI_PXE";
                stageDetail = "UEFI HTTP Boot 감지 -> iPXE(snponly.efi) 전송 준비";
            }
            else if (isPxeClient)
            {
                bootFile = "snponly.efi";
                stageTag = "UEFI_PXE";
                stageDetail = "UEFI 64-bit PXE 부팅 감지 -> TFTP 부트로더(snponly.efi) 제안";
            }

            // ------------------------------------------------------------------
            // 모드 1: ProxyDHCP 모드 (기존 공유기가 IP 할당, 마스터는 PXE 부트 정보만 응답)
            // ------------------------------------------------------------------
            if (_isProxyMode)
            {
                if (!isPxeClient) return;

                if (msgType == 1) // DISCOVER -> Proxy DHCPOFFER (yiaddr = 0.0.0.0)
                {
                    byte[] reply = BuildReplyPacket(
                        pkt,
                        replyMsgType: 2,
                        yiaddr: 0,
                        siaddr: _bindIpUint,
                        bootFile: bootFile,
                        includeSubnetAndLease: false,
                        vendorClassEcho: isUefiHttpClient ? "HTTPClient" : "PXEClient");

                    SendReply(socket, reply, remoteEp, ciaddr, giaddr, broadcastFlag, isProxyPort4011, isNak: false);
                    Log($"[ProxyDHCP] OFFER 전송 ({mac}) -> {bootFile}");
                }
                else if (msgType == 3 || msgType == 8) // REQUEST or INFORM
                {
                    if (!isProxyPort4011 && serverId.HasValue && serverId.Value != _bindIpUint && !isIpxeUserClass)
                    {
                        // 일반 공유기 DHCP 서버로의 REQUEST는 패스 (클라이언트가 직후 UDP 4011로 요청함)
                        // 단, iPXE는 ProxyDHCP Offer도 함께 읽으므로 UDP 4011 요청에 확실히 응답
                        return;
                    }

                    byte[] reply = BuildReplyPacket(
                        pkt,
                        replyMsgType: 5,
                        yiaddr: 0,
                        siaddr: _bindIpUint,
                        bootFile: bootFile,
                        includeSubnetAndLease: false,
                        vendorClassEcho: isUefiHttpClient ? "HTTPClient" : "PXEClient");

                    SendReply(socket, reply, remoteEp, ciaddr, giaddr, broadcastFlag, isProxyPort4011, isNak: false);
                    string clientIpStr = ciaddr != 0 ? NetworkUtility.UintToIp(ciaddr) : (remoteEp.Address.Equals(IPAddress.Any) ? "" : remoteEp.Address.ToString());
                    Log($"[ProxyDHCP] ACK 전송 ({mac}, IP:{clientIpStr}) -> {bootFile}");

                    ClientBootStageChanged?.Invoke(new DhcpBootEventArgs
                    {
                        MacAddress = mac,
                        AssignedIp = clientIpStr,
                        Stage = stageTag,
                        Detail = stageDetail
                    });
                }
                return;
            }

            // ------------------------------------------------------------------
            // 모드 2: 독립 작업대 Standalone DHCP + PXE 모드 (기본/주력)
            // ------------------------------------------------------------------
            if (msgType == 4 && requestedIp.HasValue) // DHCPDECLINE (IP 충돌 보고)
            {
                lock (_leaseLock)
                {
                    _declinedIps.Add(requestedIp.Value);
                    if (LeasesByMac.TryRemove(mac, out _))
                    {
                        _ipToMac.TryRemove(requestedIp.Value, out _);
                    }
                }
                Log($"[DHCP] 클라이언트 IP 충돌 보고(DECLINE): {NetworkUtility.UintToIp(requestedIp.Value)} ({mac})");
                return;
            }

            if (msgType == 7) // DHCPRELEASE
            {
                return; // Sticky IP 유지를 위해 테이블에 보존 (재부팅 시에도 동일 IP 부여)
            }

            if (msgType == 8) // DHCPINFORM
            {
                byte[] informAck = BuildReplyPacket(
                    pkt,
                    replyMsgType: 5,
                    yiaddr: 0,
                    siaddr: _bindIpUint,
                    bootFile: bootFile,
                    includeSubnetAndLease: false,
                    vendorClassEcho: isUefiHttpClient ? "HTTPClient" : null,
                    includeSubnetOnlyForInform: true);

                SendReply(socket, informAck, remoteEp, ciaddr, giaddr, broadcastFlag, false, isNak: false);
                return;
            }

            if (msgType == 1) // DHCPDISCOVER
            {
                uint? offeredIp = AllocateOrGetLeaseIp(mac, requestedIp, stageTag);
                if (!offeredIp.HasValue)
                {
                    Log($"[DHCP 오류] IP 풀이 가득 차서 {mac}에 IP를 할당할 수 없습니다!");
                    return;
                }

                // 핵심: Standalone 모드에서는 일반 UEFI PXE에 Option 60("PXEClient")을 넣지 않아야
                // UEFI 펌웨어가 Proxy 포트 4011을 기다리지 않고 즉시 TFTP로 snponly.efi를 다운로드함!
                byte[] offerPkt = BuildReplyPacket(
                    pkt,
                    replyMsgType: 2, // DHCPOFFER
                    yiaddr: offeredIp.Value,
                    siaddr: _bindIpUint,
                    bootFile: bootFile,
                    includeSubnetAndLease: true,
                    vendorClassEcho: isUefiHttpClient ? "HTTPClient" : null);

                SendReply(socket, offerPkt, remoteEp, ciaddr, giaddr, broadcastFlag, false, isNak: false);

                string ipStr = NetworkUtility.UintToIp(offeredIp.Value);
                Log($"[DHCP] DISCOVER -> OFFER ({mac} => {ipStr}{(string.IsNullOrEmpty(bootFile) ? "" : $", Boot: {bootFile}")})");

                if (isPxeClient)
                {
                    ClientBootStageChanged?.Invoke(new DhcpBootEventArgs
                    {
                        MacAddress = mac,
                        AssignedIp = ipStr,
                        Stage = stageTag,
                        Detail = stageDetail
                    });
                }
            }
            else if (msgType == 3) // DHCPREQUEST
            {
                // 다른 DHCP 서버를 명시적으로 선택한 경우 무시
                if (serverId.HasValue && serverId.Value != _bindIpUint)
                {
                    return;
                }

                uint targetReqIp = requestedIp ?? ciaddr;
                if (targetReqIp != 0)
                {
                    // 다른 공유기 대역의 이전 IP를 요청하거나 충돌 IP인 경우 즉시 NAK 전송하여 빠른 재할당 유도
                    bool validSubnet = NetworkUtility.IsInSameSubnet(targetReqIp, _bindIpUint, _subnetMaskUint);
                    bool isSelfOrBroadcast = targetReqIp == _bindIpUint ||
                                             targetReqIp == (_bindIpUint & _subnetMaskUint) ||
                                             targetReqIp == ((_bindIpUint & _subnetMaskUint) | ~_subnetMaskUint);
                    bool takenByOtherMac = _ipToMac.TryGetValue(targetReqIp, out string? ownerMac) &&
                                           !string.Equals(ownerMac, mac, StringComparison.OrdinalIgnoreCase);

                    if (!validSubnet || isSelfOrBroadcast || takenByOtherMac)
                    {
                        byte[] nakPkt = BuildNakPacket(pkt);
                        SendReply(socket, nakPkt, remoteEp, 0, giaddr, true, false, isNak: true);
                        Log($"[DHCP] 타 대역/충돌 IP 요청({NetworkUtility.UintToIp(targetReqIp)}) -> NAK 응답 ({mac}, 즉시 재협상 유도)");
                        return;
                    }
                }

                uint? assignedIp = AllocateOrGetLeaseIp(mac, targetReqIp != 0 ? targetReqIp : null, stageTag);
                if (!assignedIp.HasValue) return;

                byte[] ackPkt = BuildReplyPacket(
                    pkt,
                    replyMsgType: 5, // DHCPACK
                    yiaddr: assignedIp.Value,
                    siaddr: _bindIpUint,
                    bootFile: bootFile,
                    includeSubnetAndLease: true,
                    vendorClassEcho: isUefiHttpClient ? "HTTPClient" : null);

                SendReply(socket, ackPkt, remoteEp, ciaddr, giaddr, broadcastFlag, false, isNak: false);

                string ipStr = NetworkUtility.UintToIp(assignedIp.Value);
                Log($"[DHCP] REQUEST -> ACK ({mac} => {ipStr}{(string.IsNullOrEmpty(bootFile) ? "" : $", Boot: {bootFile}")})");

                if (isPxeClient)
                {
                    ClientBootStageChanged?.Invoke(new DhcpBootEventArgs
                    {
                        MacAddress = mac,
                        AssignedIp = ipStr,
                        Stage = stageTag,
                        Detail = stageDetail
                    });
                }
            }
        }

        private uint? AllocateOrGetLeaseIp(string mac, uint? preferredIp, string stage)
        {
            lock (_leaseLock)
            {
                // 1. 기존 Sticky Lease 확인
                if (LeasesByMac.TryGetValue(mac, out var existing) &&
                    NetworkUtility.TryIpToUint(existing.IpAddress, out uint existingUint) &&
                    NetworkUtility.IsInSameSubnet(existingUint, _bindIpUint, _subnetMaskUint) &&
                    existingUint != _bindIpUint &&
                    !_declinedIps.Contains(existingUint))
                {
                    existing.ExpiresAt = DateTime.Now.AddSeconds(_leaseSeconds);
                    existing.Stage = stage;
                    _ipToMac[existingUint] = mac;
                    return existingUint;
                }

                // 2. 클라이언트가 선호하는 IP가 풀 범위 내이고 비어있는지 확인
                if (preferredIp.HasValue)
                {
                    uint p = preferredIp.Value;
                    if (p >= _poolStartUint && p <= _poolEndUint &&
                        p != _bindIpUint && p != _gatewayUint &&
                        !_declinedIps.Contains(p) &&
                        (!_ipToMac.TryGetValue(p, out string? owner) || string.Equals(owner, mac, StringComparison.OrdinalIgnoreCase)))
                    {
                        SaveLease(mac, p, stage);
                        return p;
                    }
                }

                // 3. 풀에서 미사용 IP 순차 검색
                for (uint ip = _poolStartUint; ip <= _poolEndUint; ip++)
                {
                    if (ip == _bindIpUint || ip == _gatewayUint) continue;
                    if (_declinedIps.Contains(ip)) continue;

                    if (!_ipToMac.TryGetValue(ip, out string? owner) ||
                        string.Equals(owner, mac, StringComparison.OrdinalIgnoreCase))
                    {
                        SaveLease(mac, ip, stage);
                        return ip;
                    }
                }

                // 4. 풀이 가득 찬 경우 만료된 Lease 회수
                var expired = LeasesByMac.Values.OrderBy(l => l.ExpiresAt).FirstOrDefault(l => l.ExpiresAt < DateTime.Now);
                if (expired != null && NetworkUtility.TryIpToUint(expired.IpAddress, out uint reclaimedIp))
                {
                    LeasesByMac.TryRemove(expired.MacAddress, out _);
                    SaveLease(mac, reclaimedIp, stage);
                    return reclaimedIp;
                }

                return null;
            }
        }

        private void SaveLease(string mac, uint ipUint, string stage)
        {
            string ipStr = NetworkUtility.UintToIp(ipUint);
            _ipToMac[ipUint] = mac;
            LeasesByMac[mac] = new DhcpLeaseInfo
            {
                MacAddress = mac,
                IpAddress = ipStr,
                AllocatedAt = DateTime.Now,
                ExpiresAt = DateTime.Now.AddSeconds(_leaseSeconds),
                Stage = stage
            };
        }

        private byte[] BuildReplyPacket(
            byte[] req,
            byte replyMsgType,
            uint yiaddr,
            uint siaddr,
            string bootFile,
            bool includeSubnetAndLease,
            string? vendorClassEcho,
            bool includeSubnetOnlyForInform = false)
        {
            byte[] buf = new byte[576];
            buf[0] = 2; // BOOTREPLY
            buf[1] = 1; // Ethernet
            buf[2] = 6; // MAC len
            buf[3] = 0;

            // xid (4..7) & flags (10..11) & ciaddr (12..15) & giaddr (24..27) & chaddr (28..43)
            Buffer.BlockCopy(req, 4, buf, 4, 4);
            Buffer.BlockCopy(req, 10, buf, 10, 2);
            Buffer.BlockCopy(req, 12, buf, 12, 4);
            WriteUint32(buf, 16, yiaddr);
            WriteUint32(buf, 20, siaddr);
            Buffer.BlockCopy(req, 24, buf, 24, 4);
            Buffer.BlockCopy(req, 28, buf, 28, 16);

            // sname (44..107): 마스터 IP 문자열
            string serverName = _bindIp.ToString();
            byte[] snameBytes = Encoding.ASCII.GetBytes(serverName);
            Buffer.BlockCopy(snameBytes, 0, buf, 44, Math.Min(snameBytes.Length, 63));

            // file (108..235): 부트 파일명 (null-terminated)
            if (!string.IsNullOrEmpty(bootFile))
            {
                byte[] fileBytes = Encoding.ASCII.GetBytes(bootFile);
                Buffer.BlockCopy(fileBytes, 0, buf, 108, Math.Min(fileBytes.Length, 127));
            }

            // Magic Cookie
            buf[236] = 99;
            buf[237] = 130;
            buf[238] = 83;
            buf[239] = 99;

            int idx = 240;

            // Option 53: DHCP Message Type
            idx = WriteOption(buf, idx, 53, new[] { replyMsgType });

            // Option 54: Server Identifier
            idx = WriteOptionUint32(buf, idx, 54, _bindIpUint);

            if (includeSubnetAndLease)
            {
                // Option 51: Lease Time
                idx = WriteOptionUint32(buf, idx, 51, (uint)_leaseSeconds);
                // Option 58: Renewal Time (T1 = 50%)
                idx = WriteOptionUint32(buf, idx, 58, (uint)(_leaseSeconds / 2));
                // Option 59: Rebinding Time (T2 = 87.5%)
                idx = WriteOptionUint32(buf, idx, 59, (uint)(_leaseSeconds * 7 / 8));
                // Option 1: Subnet Mask
                idx = WriteOptionUint32(buf, idx, 1, _subnetMaskUint);
                // Option 3: Router (Gateway)
                idx = WriteOptionUint32(buf, idx, 3, _gatewayUint);
                // Option 6: DNS Server
                idx = WriteOptionUint32(buf, idx, 6, _dnsUint);
            }
            else if (includeSubnetOnlyForInform)
            {
                idx = WriteOptionUint32(buf, idx, 1, _subnetMaskUint);
                idx = WriteOptionUint32(buf, idx, 3, _gatewayUint);
                idx = WriteOptionUint32(buf, idx, 6, _dnsUint);
            }

            if (!string.IsNullOrEmpty(vendorClassEcho))
            {
                idx = WriteOption(buf, idx, 60, Encoding.ASCII.GetBytes(vendorClassEcho));
            }

            if (!string.IsNullOrEmpty(bootFile))
            {
                // Option 66: TFTP Server Name
                idx = WriteOption(buf, idx, 66, snameBytes);
                // Option 67: Bootfile Name
                idx = WriteOption(buf, idx, 67, Encoding.ASCII.GetBytes(bootFile));
            }

            // Option 255: End
            buf[idx++] = 0xFF;

            int finalLen = Math.Max(idx, 300); // BOOTP 최소 300바이트 패딩 보장
            byte[] result = new byte[finalLen];
            Buffer.BlockCopy(buf, 0, result, 0, finalLen);
            return result;
        }

        private byte[] BuildNakPacket(byte[] req)
        {
            byte[] buf = new byte[300];
            buf[0] = 2; // BOOTREPLY
            buf[1] = 1;
            buf[2] = 6;
            Buffer.BlockCopy(req, 4, buf, 4, 4);
            buf[10] = 0x80; // Broadcast NAK
            Buffer.BlockCopy(req, 24, buf, 24, 4);
            Buffer.BlockCopy(req, 28, buf, 28, 16);

            buf[236] = 99;
            buf[237] = 130;
            buf[238] = 83;
            buf[239] = 99;

            int idx = 240;
            idx = WriteOption(buf, idx, 53, new byte[] { 6 }); // DHCPNAK
            idx = WriteOptionUint32(buf, idx, 54, _bindIpUint);
            buf[idx] = 0xFF;
            return buf;
        }

        private void SendReply(
            Socket socket,
            byte[] packet,
            IPEndPoint remoteEp,
            uint ciaddr,
            uint giaddr,
            bool broadcastFlag,
            bool isProxyPort4011,
            bool isNak)
        {
            try
            {
                IPEndPoint dest;

                if (IPAddress.IsLoopback(_bindIp))
                {
                    // 로컬 루프백 시뮬레이션 테스트 호환
                    dest = remoteEp;
                }
                else if (isProxyPort4011 && !remoteEp.Address.Equals(IPAddress.Any))
                {
                    dest = remoteEp;
                }
                else if (giaddr != 0)
                {
                    dest = new IPEndPoint(NetworkUtility.UintToIpAddress(giaddr), DhcpServerPort);
                }
                else if (!isNak && !broadcastFlag && ciaddr != 0 && !remoteEp.Address.Equals(IPAddress.Any))
                {
                    dest = new IPEndPoint(NetworkUtility.UintToIpAddress(ciaddr), DhcpClientPort);
                }
                else
                {
                    // 초기 PXE / iPXE / WinPE 부팅 시 ARP 미완성 상태에서도 100% 도달하도록 바인딩된 NIC로 브로드캐스트
                    dest = new IPEndPoint(IPAddress.Broadcast, DhcpClientPort);
                }

                socket.SendTo(packet, dest);
            }
            catch (Exception ex)
            {
                Log($"[DHCP 전송 오류] {ex.Message}");
            }
        }

        private static Dictionary<byte, byte[]> ParseOptions(byte[] pkt, int startOffset)
        {
            var dict = new Dictionary<byte, byte[]>();
            int i = startOffset;
            while (i < pkt.Length)
            {
                byte code = pkt[i++];
                if (code == 0xFF) break; // End
                if (code == 0x00) continue; // Pad
                if (i >= pkt.Length) break;

                int len = pkt[i++];
                if (i + len > pkt.Length) break;

                byte[] val = new byte[len];
                Buffer.BlockCopy(pkt, i, val, 0, len);
                dict[code] = val;
                i += len;
            }
            return dict;
        }

        private static bool ContainsAscii(byte[] haystack, string needle)
        {
            if (haystack.Length < needle.Length) return false;
            string s = Encoding.ASCII.GetString(haystack);
            return s.Contains(needle, StringComparison.OrdinalIgnoreCase);
        }

        private static uint ReadUint32(byte[] buf, int offset)
        {
            return ((uint)buf[offset] << 24) |
                   ((uint)buf[offset + 1] << 16) |
                   ((uint)buf[offset + 2] << 8) |
                   buf[offset + 3];
        }

        private static void WriteUint32(byte[] buf, int offset, uint val)
        {
            buf[offset] = (byte)((val >> 24) & 0xFF);
            buf[offset + 1] = (byte)((val >> 16) & 0xFF);
            buf[offset + 2] = (byte)((val >> 8) & 0xFF);
            buf[offset + 3] = (byte)(val & 0xFF);
        }

        private static int WriteOption(byte[] buf, int idx, byte code, byte[] data)
        {
            buf[idx++] = code;
            buf[idx++] = (byte)data.Length;
            Buffer.BlockCopy(data, 0, buf, idx, data.Length);
            return idx + data.Length;
        }

        private static int WriteOptionUint32(byte[] buf, int idx, byte code, uint val)
        {
            buf[idx++] = code;
            buf[idx++] = 4;
            WriteUint32(buf, idx, val);
            return idx + 4;
        }

        private void Log(string msg) => LogAdded?.Invoke(msg);

        public void Stop()
        {
            _cts?.Cancel();
            try { _dhcpSocket?.Close(); } catch { }
            try { _proxySocket?.Close(); } catch { }
            _dhcpSocket = null;
            _proxySocket = null;
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }
    }
}

