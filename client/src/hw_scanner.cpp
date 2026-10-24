#include "hw_scanner.h"
#include <windows.h>
#include <iphlpapi.h>
#include <winioctl.h>
#include <iostream>
#include <sstream>
#include <iomanip>

#pragma comment(lib, "iphlpapi.lib")
#pragma comment(lib, "ws2_32.lib")

namespace HardwareScanner {

#pragma pack(push, 1)
struct RawSMBIOSData {
    BYTE  Used20CallingMethod;
    BYTE  SMBIOSMajorVersion;
    BYTE  SMBIOSMinorVersion;
    BYTE  DmiRevision;
    DWORD Length;
    BYTE  SMBIOSTableData[1];
};

struct SMBIOSHeader {
    BYTE Type;
    BYTE Length;
    WORD Handle;
};

struct SMBIOS_Type1 { // System Information
    SMBIOSHeader Header;
    BYTE Manufacturer;
    BYTE ProductName;
    BYTE Version;
    BYTE SerialNumber;
    BYTE UUID[16];
    BYTE WakeUpType;
    BYTE SKU;
    BYTE Family;
};
#pragma pack(pop)

static const char* GetSMBIOSString(const BYTE* stringStart, BYTE stringIndex) {
    if (stringIndex == 0) return "N/A";
    const char* str = reinterpret_cast<const char*>(stringStart);
    for (BYTE i = 1; i < stringIndex; ++i) {
        while (*str != 0) ++str;
        ++str; // 다음 문자열로 이동
    }
    return str;
}

static void ScanSMBIOS(SystemInfo& outSys) {
    outSys.manufacturer = "Unknown Manufacturer";
    outSys.modelName = "Generic Model";
    outSys.serialNumber = "UNKNOWN-SN";
    outSys.uuid = "00000000-0000-0000-0000-000000000000";

    const DWORD signature = 'RSMB';
    DWORD bufSize = GetSystemFirmwareTable(signature, 0, nullptr, 0);
    if (bufSize == 0) return;

    std::vector<BYTE> buffer(bufSize);
    if (GetSystemFirmwareTable(signature, 0, buffer.data(), bufSize) != bufSize) {
        return;
    }

    const RawSMBIOSData* smbios = reinterpret_cast<const RawSMBIOSData*>(buffer.data());
    const BYTE* data = smbios->SMBIOSTableData;
    const BYTE* end = data + smbios->Length;

    while (data + sizeof(SMBIOSHeader) <= end) {
        const SMBIOSHeader* header = reinterpret_cast<const SMBIOSHeader*>(data);
        if (header->Length == 0) break;

        const BYTE* stringArea = data + header->Length;

        if (header->Type == 1 && header->Length >= sizeof(SMBIOS_Type1)) {
            const SMBIOS_Type1* type1 = reinterpret_cast<const SMBIOS_Type1*>(data);
            outSys.manufacturer = GetSMBIOSString(stringArea, type1->Manufacturer);
            outSys.modelName = GetSMBIOSString(stringArea, type1->ProductName);
            outSys.serialNumber = GetSMBIOSString(stringArea, type1->SerialNumber);

            char uuidBuf[40] = {0};
            sprintf_s(uuidBuf, "%02X%02X%02X%02X-%02X%02X-%02X%02X-%02X%02X-%02X%02X%02X%02X%02X%02X",
                type1->UUID[0], type1->UUID[1], type1->UUID[2], type1->UUID[3],
                type1->UUID[4], type1->UUID[5], type1->UUID[6], type1->UUID[7],
                type1->UUID[8], type1->UUID[9], type1->UUID[10], type1->UUID[11],
                type1->UUID[12], type1->UUID[13], type1->UUID[14], type1->UUID[15]);
            outSys.uuid = uuidBuf;
            break;
        }

        // 끝 문자(NULL 2개) 탐색
        const BYTE* next = stringArea;
        while (next < end && !(next[0] == 0 && next[1] == 0)) {
            ++next;
        }
        data = next + 2;
    }

    // UEFI 여부 확인
    FIRMWARE_TYPE fwType = FirmwareTypeUnknown;
    if (GetFirmwareType(&fwType)) {
        outSys.isUefi = (fwType == FirmwareTypeUefi);
    }
}

static void ScanNetwork(NetworkInfo& outNet) {
    outNet.primaryMac = "00:00:00:00:00:00";
    outNet.ipAddress = "127.0.0.1";
    outNet.adapterName = "Ethernet";

    ULONG outBufLen = 15000;
    std::vector<BYTE> buffer(outBufLen);
    PIP_ADAPTER_ADDRESSES pAddresses = reinterpret_cast<IP_ADAPTER_ADDRESSES*>(buffer.data());

    DWORD ret = GetAdaptersAddresses(AF_INET, GAA_FLAG_INCLUDE_PREFIX, nullptr, pAddresses, &outBufLen);
    if (ret == ERROR_BUFFER_OVERFLOW) {
        buffer.resize(outBufLen);
        pAddresses = reinterpret_cast<IP_ADAPTER_ADDRESSES*>(buffer.data());
        ret = GetAdaptersAddresses(AF_INET, GAA_FLAG_INCLUDE_PREFIX, nullptr, pAddresses, &outBufLen);
    }

    if (ret == NO_ERROR) {
        for (PIP_ADAPTER_ADDRESSES pCurr = pAddresses; pCurr != nullptr; pCurr = pCurr->Next) {
            // 이더넷 어댑터 또는 활성화된 유선 네트워크 우선 선택
            if (pCurr->IfType == MIB_IF_TYPE_ETHERNET && pCurr->OperStatus == IfOperStatusUp) {
                if (pCurr->PhysicalAddressLength == 6) {
                    char macBuf[24] = {0};
                    sprintf_s(macBuf, "%02X:%02X:%02X:%02X:%02X:%02X",
                        pCurr->PhysicalAddress[0], pCurr->PhysicalAddress[1],
                        pCurr->PhysicalAddress[2], pCurr->PhysicalAddress[3],
                        pCurr->PhysicalAddress[4], pCurr->PhysicalAddress[5]);
                    outNet.primaryMac = macBuf;
                }

                // IP 추출
                if (pCurr->FirstUnicastAddress) {
                    sockaddr_in* sa_in = reinterpret_cast<sockaddr_in*>(pCurr->FirstUnicastAddress->Address.lpSockaddr);
                    char ipBuf[32] = {0};
                    inet_ntop(AF_INET, &(sa_in->sin_addr), ipBuf, sizeof(ipBuf));
                    outNet.ipAddress = ipBuf;
                }

                if (pCurr->FriendlyName) {
                    std::wstring ws(pCurr->FriendlyName);
                    outNet.adapterName = std::string(ws.begin(), ws.end());
                }
                break;
            }
        }
    }
}

static const char* BusTypeToString(STORAGE_BUS_TYPE busType) {
    switch (busType) {
        case BusTypeNvme: return "NVMe";
        case BusTypeSata: return "SATA";
        case BusTypeUsb:  return "USB";
        case BusTypeScsi: return "SCSI";
        case BusTypeRaid: return "RAID";
        default:          return "Unknown";
    }
}

static void ScanDisks(std::vector<DiskInfo>& outDisks) {
    outDisks.clear();

    for (int driveIdx = 0; driveIdx < 16; ++driveIdx) {
        wchar_t devPath[64];
        swprintf_s(devPath, L"\\\\.\\PhysicalDrive%d", driveIdx);

        HANDLE hDisk = CreateFileW(devPath, GENERIC_READ,
                                   FILE_SHARE_READ | FILE_SHARE_WRITE,
                                   nullptr, OPEN_EXISTING, 0, nullptr);
        if (hDisk == INVALID_HANDLE_VALUE) continue;

        DiskInfo disk;
        disk.diskIndex = driveIdx;
        disk.friendlyName = "Unknown Disk";
        disk.busType = "SATA";
        disk.mediaType = "SSD";

        // 1. 디스크 프로퍼티(모델명, 버스 타입) 조회
        STORAGE_PROPERTY_QUERY query = {};
        query.PropertyId = StorageDeviceProperty;
        query.QueryType = PropertyStandardQuery;

        BYTE descBuf[1024] = {};
        DWORD bytesReturned = 0;
        if (DeviceIoControl(hDisk, IOCTL_STORAGE_QUERY_PROPERTY,
                            &query, sizeof(query),
                            descBuf, sizeof(descBuf),
                            &bytesReturned, nullptr)) {
            STORAGE_DEVICE_DESCRIPTOR* desc = reinterpret_cast<STORAGE_DEVICE_DESCRIPTOR*>(descBuf);
            disk.busType = BusTypeToString(desc->BusType);

            if (desc->ProductIdOffset != 0) {
                const char* prodId = reinterpret_cast<const char*>(descBuf + desc->ProductIdOffset);
                disk.friendlyName = prodId;
                // 트림
                while (!disk.friendlyName.empty() && disk.friendlyName.back() == ' ')
                    disk.friendlyName.pop_back();
            }
        }

        // 2. 디스크 지오메트리 및 총 용량
        GET_DISK_ATTRIBUTES diskAttrs = {};
        GET_LENGTH_INFORMATION lengthInfo = {};
        if (DeviceIoControl(hDisk, IOCTL_DISK_GET_LENGTH_INFO,
                            nullptr, 0,
                            &lengthInfo, sizeof(lengthInfo),
                            &bytesReturned, nullptr)) {
            disk.sizeBytes = lengthInfo.Length.QuadPart;
            disk.sizeGb = static_cast<double>(disk.sizeBytes) / (1024.0 * 1024.0 * 1024.0);
        }

        // 3. 파티션 테이블(GPT / MBR) 레이아웃 스캔
        BYTE layoutBuf[4096] = {};
        if (DeviceIoControl(hDisk, IOCTL_DISK_GET_DRIVE_LAYOUT_EX,
                            nullptr, 0,
                            layoutBuf, sizeof(layoutBuf),
                            &bytesReturned, nullptr)) {
            DRIVE_LAYOUT_INFORMATION_EX* layout = reinterpret_cast<DRIVE_LAYOUT_INFORMATION_EX*>(layoutBuf);
            if (layout->PartitionStyle == PARTITION_STYLE_GPT) {
                disk.partitionStyle = "GPT";
            } else if (layout->PartitionStyle == PARTITION_STYLE_MBR) {
                disk.partitionStyle = "MBR";
            } else {
                disk.partitionStyle = "RAW";
            }

            for (DWORD p = 0; p < layout->PartitionCount; ++p) {
                PARTITION_INFORMATION_EX* pInfo = &layout->PartitionEntry[p];
                if (pInfo->PartitionLength.QuadPart > 0) {
                    PartitionInfo part;
                    part.partitionNumber = pInfo->PartitionNumber;
                    part.sizeBytes = pInfo->PartitionLength.QuadPart;
                    if (pInfo->PartitionStyle == PARTITION_STYLE_GPT) {
                        part.type = "GPT-Partition";
                    } else {
                        part.type = "MBR-Partition";
                    }
                    disk.partitions.push_back(part);
                }
            }
        }

        CloseHandle(hDisk);
        outDisks.push_back(disk);
    }
}

SimpleJson::JsonValue FullInventory::ToJson() const {
    auto root = SimpleJson::JsonValue::CreateObject();

    // System
    auto sysObj = SimpleJson::JsonValue::CreateObject();
    sysObj.Set("manufacturer", system.manufacturer);
    sysObj.Set("modelName", system.modelName);
    sysObj.Set("serialNumber", system.serialNumber);
    sysObj.Set("uuid", system.uuid);
    sysObj.Set("isUefi", system.isUefi);
    root.Set("system", sysObj);

    // Network
    auto netObj = SimpleJson::JsonValue::CreateObject();
    netObj.Set("primaryMac", network.primaryMac);
    netObj.Set("ipAddress", network.ipAddress);
    netObj.Set("adapterName", network.adapterName);
    root.Set("network", netObj);

    // Disks
    auto disksArr = SimpleJson::JsonValue::CreateArray();
    for (const auto& disk : disks) {
        auto dObj = SimpleJson::JsonValue::CreateObject();
        dObj.Set("diskIndex", disk.diskIndex);
        dObj.Set("friendlyName", disk.friendlyName);
        dObj.Set("busType", disk.busType);
        dObj.Set("mediaType", disk.mediaType);
        dObj.Set("sizeBytes", disk.sizeBytes);
        dObj.Set("sizeGb", disk.sizeGb);
        dObj.Set("partitionStyle", disk.partitionStyle);
        dObj.Set("partitionCount", static_cast<int>(disk.partitions.size()));
        disksArr.Push(dObj);
    }
    root.Set("disks", disksArr);

    return root;
}

FullInventory ScanHardwareInventory() {
    FullInventory inv;
    ScanSMBIOS(inv.system);
    ScanNetwork(inv.network);
    ScanDisks(inv.disks);
    return inv;
}

} // namespace HardwareScanner
