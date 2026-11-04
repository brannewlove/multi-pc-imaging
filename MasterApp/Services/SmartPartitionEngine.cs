using System;
using System.Collections.Generic;
using System.Linq;
using MultiDeployOS.Master.Models;

namespace MultiDeployOS.Master.Services
{
    public static class SmartPartitionEngine
    {
        /// <summary>
        /// 정책 및 하드웨어 구성에 맞춰 실제 OS(W:)가 설치될 타깃 디스크 번호(DiskIndex)를 결정합니다.
        /// [규칙]
        /// 1. NVMe가 1개뿐인 경우 -> 고속 NVMe에 OS 설치 (기존 정책)
        /// 2. 디스크 간 용량 차이가 있는 경우 -> 작은 용량 디스크에 OS 설치 (기존 정책)
        /// 3. 동일 용량의 NVMe가 2개인 경우 -> Disk 1번에 OS, Disk 0번에 Data 설치 (사용자 지정 특수 환경)
        /// 4. 둘 다 SATA이거나 기타 동일 용량인 경우 -> Disk 0번에 OS 설치 (기존 표준)
        /// </summary>
        public static int DetermineTargetOsDiskIndex(PartitionPolicy? policy, List<DiskInfo>? clientDisks, int defaultIndex = 0)
        {
            var internalDisks = (clientDisks ?? new List<DiskInfo>()).Where(d => !IsUsbOrRemovable(d)).ToList();
            if (internalDisks.Count == 0) internalDisks = clientDisks ?? new List<DiskInfo>();

            if (internalDisks.Count == 0)
            {
                return policy?.Conditions?.TargetDiskIndex ?? defaultIndex;
            }

            if (internalDisks.Count == 1)
            {
                return internalDisks[0].DiskIndex;
            }

            string criteria = policy?.Conditions?.TargetDiskCriteria?.Trim().ToUpperInvariant() ?? "SMART_AUTO";
            int preferredIndex = policy?.Conditions?.TargetDiskIndex ?? defaultIndex;

            // 정책이 명시적으로 Disk 1번을 요구하는 경우 (예: policy-dual-nvme-disk1-os)
            if (policy?.Id == "policy-dual-nvme-disk1-os" || criteria.Contains("DISK_1") || preferredIndex == 1)
            {
                var d1 = internalDisks.FirstOrDefault(d => d.DiskIndex == 1);
                if (d1 != null) return d1.DiskIndex;
            }

            // 디스크 하드웨어 특성 분석 (내부 고정 디스크만 대상)
            var nvmeList = internalDisks.Where(IsNvmeDisk).ToList();
            var sortedBySize = internalDisks.OrderBy(GetDiskGb).ToList();
            bool hasSignificantSizeDiff = !AreSameCapacity(GetDiskGb(sortedBySize.Last()), GetDiskGb(sortedBySize.First()));

            // [규칙 1] NVMe가 정확히 1개인 경우 (나머지는 SATA SSD / HDD):
            // -> 고속 NVMe 디스크에 무조건 Windows 11(OS) 설치 (기존 정책)
            if (nvmeList.Count == 1)
            {
                return nvmeList[0].DiskIndex;
            }

            // [규칙 2] 디스크 간 용량 차이가 유의미하게 있는 경우:
            // -> 작은 용량 디스크(OS용 SSD)에 Windows 11 설치, 큰 디스크는 Data (기존 정책)
            if (hasSignificantSizeDiff)
            {
                if (nvmeList.Count >= 2)
                {
                    return nvmeList.OrderBy(GetDiskGb).First().DiskIndex;
                }
                return sortedBySize.First().DiskIndex;
            }

            // [규칙 3] 두 디스크가 동일한 용량급(10% 또는 35GB 이내)이면서 둘 다 NVMe인 경우:
            // -> 사용자의 핵심 요청 환경! Disk 1번에 Windows 11, Disk 0번에 Data 설치!
            if (nvmeList.Count >= 2 && !hasSignificantSizeDiff)
            {
                var d1 = internalDisks.FirstOrDefault(d => d.DiskIndex == 1);
                if (d1 != null) return d1.DiskIndex;
                if (internalDisks.Count > 1) return internalDisks[1].DiskIndex;
                return internalDisks[0].DiskIndex;
            }

            // [규칙 4] 둘 다 NVMe가 아니거나(SATA 2개 등) 동일 용량인 경우:
            // -> 기존 표준 방식: Disk 0번 우선 (또는 명시적 DISK_1_FIRST 지정 시 반영)
            if (criteria.Contains("DISK_1") || preferredIndex == 1)
            {
                var d1 = internalDisks.FirstOrDefault(d => d.DiskIndex == 1);
                if (d1 != null) return d1.DiskIndex;
            }

            return internalDisks.FirstOrDefault(d => d.DiskIndex == 0)?.DiskIndex ?? internalDisks[0].DiskIndex;
        }

        public static bool IsNvmeDisk(DiskInfo? d)
        {
            if (d == null) return false;
            string bus = (d.BusType ?? "").ToUpperInvariant();
            string name = (d.FriendlyName ?? "").ToUpperInvariant();
            string model = (d.Model ?? "").ToUpperInvariant();

            if (bus.Contains("NVME")) return true;
            if (name.Contains("NVME") || name.Contains("PCIE") || name.Contains("M.2")) return true;
            if (model.Contains("NVME") || model.Contains("PCIE") || model.Contains("M.2")) return true;
            return false;
        }

        public static bool IsUsbOrRemovable(DiskInfo? d)
        {
            if (d == null) return false;
            string bus = (d.BusType ?? "").ToUpperInvariant();
            string media = (d.MediaType ?? "").ToUpperInvariant();
            if (bus.Contains("USB") || media.Contains("REMOVABLE")) return true;
            return false;
        }

        public static bool AreSameCapacity(double cap1, double cap2)
        {
            double diff = Math.Abs(cap1 - cap2);
            if (diff <= 35.0) return true; // 512GB vs 476GB (11~35GB 표기 오차 허용)
            double maxCap = Math.Max(cap1, cap2);
            if (maxCap > 0 && (diff / maxCap) <= 0.10) return true; // 10% 이내 차이 허용
            return false;
        }

        public static double GetDiskGb(DiskInfo disk)
        {
            if (disk.SizeGb > 0) return disk.SizeGb;
            if (disk.SizeBytes > 0) return disk.SizeBytes / (1024.0 * 1024 * 1024);
            return 0;
        }

        public static List<string> GenerateCommands(PartitionPolicy policy, List<DiskInfo>? clientDisks, int targetDiskIndex = 0)
        {
            int osDiskIndex = DetermineTargetOsDiskIndex(policy, clientDisks, targetDiskIndex);

            // 1. 스마트 동적 정책이거나, 지능형 모드인 경우
            if (policy.IsSmartDynamic || policy.Id == "policy-smart-auto-gpt" ||
                policy.Id.StartsWith("policy-dual-") ||
                ((policy.DiskpartCommands == null || policy.DiskpartCommands.Count == 0) &&
                 (policy.Layout == null || policy.Layout.Count == 0)))
            {
                return GenerateDynamicSmartPartitionCommands(clientDisks, policy, osDiskIndex);
            }

            // 2. 관리자가 직접 지정한 Diskpart 명령이 있는 경우
            if (policy.DiskpartCommands != null && policy.DiskpartCommands.Count > 0)
            {
                int dataDiskIndex = 0;
                if (clientDisks != null && clientDisks.Count > 1)
                {
                    var dataDisk = clientDisks.FirstOrDefault(d => d.DiskIndex != osDiskIndex);
                    if (dataDisk != null) dataDiskIndex = dataDisk.DiskIndex;
                }

                var resolvedCmds = new List<string>();
                foreach (var cmd in policy.DiskpartCommands)
                {
                    // 템플릿 변수 치환 지원
                    string c = cmd
                        .Replace("${TARGET_DISK}", osDiskIndex.ToString())
                        .Replace("${OS_DISK}", osDiskIndex.ToString())
                        .Replace("${DATA_DISK}", dataDiskIndex.ToString());
                    resolvedCmds.Add(c);
                }
                return resolvedCmds;
            }

            // 3. 기존 레이아웃 배열이 있는 경우
            if (policy.Layout != null && policy.Layout.Count > 0)
            {
                var cmds = new List<string>
                {
                    $"select disk {osDiskIndex}",
                    "clean",
                    policy.PartitionScheme == "GPT" ? "convert gpt" : "convert mbr"
                };

                foreach (var item in policy.Layout)
                {
                    string fs = string.IsNullOrWhiteSpace(item.Fs) ? "ntfs" : item.Fs.ToLower();
                    string label = (item.Label ?? (item.Type == "EFI" ? "System" : (item.Type == "OS" ? "Windows11" : "Data"))).Replace("\"", "").Trim();
                    if (item.Type == "EFI")
                    {
                        cmds.Add($"create partition efi size={item.SizeMb ?? 300}");
                        cmds.Add($"format quick fs={fs} label={label}");
                        if (!string.IsNullOrWhiteSpace(item.Letter)) cmds.Add($"assign letter={item.Letter.Trim('"', '\'', ':', ' ')}");
                    }
                    else if (item.Type == "MSR")
                    {
                        cmds.Add($"create partition msr size={item.SizeMb ?? 16}");
                    }
                    else if (item.Type == "OS" || item.Type == "DATA")
                    {
                        string sizeStr = (item.SizeMb == null || item.SizeMb.ToString() == "REMAINDER") ? "" : $" size={item.SizeMb}";
                        cmds.Add($"create partition primary{sizeStr}");
                        cmds.Add($"format quick fs={fs} label={label}");
                        if (!string.IsNullOrWhiteSpace(item.Letter)) cmds.Add($"assign letter={item.Letter.Trim('"', '\'', ':', ' ')}");
                    }
                }
                return cmds;
            }

            return GenerateDynamicSmartPartitionCommands(clientDisks, policy, osDiskIndex);
        }

        // 지능형 자동 및 다중 디스크(OS/Data 교차 배치 포함) 동적 스크립트 생성
        public static List<string> GenerateDynamicSmartPartitionCommands(List<DiskInfo>? clientDisks, PartitionPolicy? policy = null, int defaultTargetDiskIndex = 0)
        {
            var cmds = new List<string>();

            // USB/이동식 미디어를 제외한 내부 고정 디스크만 파티셔닝 대상으로 삼음 (USB 부팅 미디어 데이터 손실 방지)
            var internalDisks = (clientDisks ?? new List<DiskInfo>()).Where(d => !IsUsbOrRemovable(d)).ToList();
            if (internalDisks.Count == 0) internalDisks = clientDisks ?? new List<DiskInfo>();

            if (internalDisks.Count == 0)
            {
                int targetDisk = policy?.Conditions?.TargetDiskIndex ?? defaultTargetDiskIndex;
                return new List<string>
                {
                    $"select disk {targetDisk}", "clean", "convert gpt",
                    "create partition efi size=100", "format quick fs=fat32 label=System", "assign letter=S",
                    "create partition msr size=16",
                    "create partition primary", "format quick fs=ntfs label=Windows11", "assign letter=W"
                };
            }

            if (internalDisks.Count == 1)
            {
                // [조건 1] 내부 고정 디스크 1개 인식
                var disk = internalDisks[0];
                int diskIndex = disk.DiskIndex;
                double sizeGb = GetDiskGb(disk);
                long sizeMb = disk.SizeBytes > 0 ? (disk.SizeBytes / (1024 * 1024)) : (long)(sizeGb * 1024);

                cmds.Add($"select disk {diskIndex}");
                cmds.Add("clean");
                cmds.Add("convert gpt");
                cmds.Add("create partition efi size=100");
                cmds.Add("format quick fs=fat32 label=System");
                cmds.Add("assign letter=S");
                cmds.Add("create partition msr size=16");

                if (sizeGb > 512)
                {
                    // (1-A) 크기 512GB 초과: 300GB "Windows11" 라벨 후 설치, 나머지 "Data" 라벨
                    cmds.Add("create partition primary size=307200"); // 300GB = 307,200 MB
                    cmds.Add("format quick fs=ntfs label=Windows11");
                    cmds.Add("assign letter=W");
                    cmds.Add("create partition primary");
                    cmds.Add("format quick fs=ntfs label=Data");
                    cmds.Add("assign letter=D");
                }
                else
                {
                    // (1-B) 크기 512GB 이하: 50%로 나눈 뒤 "Windows11" 라벨 후 설치, 나머지 "Data" 라벨
                    long usableMb = Math.Max(1024, sizeMb - 120); // EFI(100MB)+MSR(16MB) 제외 잔여 공간
                    long halfMb = usableMb / 2;
                    cmds.Add($"create partition primary size={halfMb}");
                    cmds.Add("format quick fs=ntfs label=Windows11");
                    cmds.Add("assign letter=W");
                    cmds.Add("create partition primary");
                    cmds.Add("format quick fs=ntfs label=Data");
                    cmds.Add("assign letter=D");
                }
            }
            else
            {
                // [조건 2] 내부 고정 디스크 2개 이상 인식
                int osDiskIndex = DetermineTargetOsDiskIndex(policy, internalDisks, defaultTargetDiskIndex);

                var osDisk = internalDisks.FirstOrDefault(d => d.DiskIndex == osDiskIndex) ?? internalDisks[0];
                var dataDisks = internalDisks.Where(d => d.DiskIndex != osDisk.DiskIndex).OrderBy(d => d.DiskIndex).ToList();

                // 1. OS 디스크: 전체 GPT 포맷, EFI(S:), MSR 생성 후 전체를 "Windows11" 라벨로 포맷 (W:)
                cmds.Add($"select disk {osDisk.DiskIndex}");
                cmds.Add("clean");
                cmds.Add("convert gpt");
                cmds.Add("create partition efi size=100");
                cmds.Add("format quick fs=fat32 label=System");
                cmds.Add("assign letter=S");
                cmds.Add("create partition msr size=16");
                cmds.Add("create partition primary");
                cmds.Add("format quick fs=ntfs label=Windows11");
                cmds.Add("assign letter=W");

                // 2. 나머지 디스크: 전부 GPT 포맷, 1개면 "Data(D:)", 2개 이상이면 "Data1(D:)", "Data2" 형식
                for (int i = 0; i < dataDisks.Count; i++)
                {
                    var dDisk = dataDisks[i];
                    string label = dataDisks.Count == 1 ? "Data" : $"Data{i + 1}";
                    cmds.Add($"select disk {dDisk.DiskIndex}");
                    cmds.Add("clean");
                    cmds.Add("convert gpt");
                    cmds.Add("create partition primary");
                    cmds.Add($"format quick fs=ntfs label={label}");
                    if (i == 0)
                    {
                        cmds.Add("assign letter=D");
                    }
                    else
                    {
                        cmds.Add("assign");
                    }
                }
            }

            return cmds;
        }
    }
}
