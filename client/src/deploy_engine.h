#pragma once
#include <string>
#include <functional>
#include <cstdint>

namespace DeployEngine {

struct DeployProgress {
    std::string step;
    double percent = 0.0;
    double speedMbps = 0.0;
    int elapsedSeconds = 0;
    int remainingSeconds = 0;
    std::string detail;
};

using ProgressCallback = std::function<void(const DeployProgress&)>;

struct DeployParams {
    std::string imagePath;        // 로컬 경로 또는 마운트된 SMB 경로/다운로드된 파일
    int imageIndex = 1;           // WIM 이미지 인덱스 (기본 1)
    std::string targetDrive;      // OS 파티션 드라이브 (예: "W:")
    std::string bootDrive;        // EFI 파티션 드라이브 (예: "S:")
    bool runBcdBoot = true;       // 부트로더 설치 여부
};

struct DeployResult {
    bool success = false;
    int durationSeconds = 0;
    std::string errorMessage;
    std::string detailedLogs;
};

// WIM/ESD 이미지 적용 및 부트로더 생성
DeployResult ApplyWimImage(const DeployParams& params, ProgressCallback onProgress);

} // namespace DeployEngine
