#pragma once
#include <string>
#include <vector>
#include <cstdint>
#include "simple_json.h"

namespace HardwareScanner {

struct PartitionInfo {
    int partitionNumber = 0;
    std::string type;
    uint64_t sizeBytes = 0;
};

struct DiskInfo {
    int diskIndex = 0;
    std::string friendlyName;
    std::string busType; // NVMe, SATA, USB, Unknown
    std::string mediaType; // SSD, HDD, Unknown
    uint64_t sizeBytes = 0;
    double sizeGb = 0.0;
    std::string partitionStyle; // GPT, MBR, RAW
    std::vector<PartitionInfo> partitions;
};

struct SystemInfo {
    std::string manufacturer;
    std::string modelName;
    std::string serialNumber;
    std::string uuid;
    std::string biosVersion;
    bool isUefi = true;
};

struct NetworkInfo {
    std::string primaryMac;
    std::string ipAddress;
    std::string adapterName;
};

struct FullInventory {
    SystemInfo system;
    NetworkInfo network;
    std::vector<DiskInfo> disks;

    SimpleJson::JsonValue ToJson() const;
};

// 하드웨어 스캔 실행 함수
FullInventory ScanHardwareInventory();

} // namespace HardwareScanner
