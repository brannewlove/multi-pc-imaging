#pragma once
#include <string>
#include <vector>

namespace PartitionEngine {

struct PartitionResult {
    bool success = false;
    std::string errorMessage;
    std::vector<std::string> outputLogs;
};

// 마스터에서 수신한 diskpart 명령 목록을 기반으로 파티셔닝 및 포맷 수행
PartitionResult ExecutePartitionPlan(int targetDiskIndex, const std::vector<std::string>& commands);

} // namespace PartitionEngine
