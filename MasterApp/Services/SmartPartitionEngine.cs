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
        /// </summary>
        public static int DetermineTargetOsDiskIndex(PartitionPolicy? policy, List<DiskInfo>? clientDisks, int defaultIndex = 0)
        {
            if (clientDisks == null || clientDisks.Count == 0)
            {
                return policy?.Conditions?.TargetDiskIndex ?? defaultIndex;
            }

            if (clientDisks.Count == 1)
            {
                return clientDisks[0].DiskIndex;
            }

            string criteria = policy?.Conditions?.TargetDiskCriteria?.Trim().ToUpperInvariant() ?? "SMART_AUTO";
            int preferredIndex = policy?.Conditions?.TargetDiskIndex ?? defaultIndex;

            // 1. 디스크 1번 우선 (듀얼 NVMe 슬롯 1번 환경 등)
            if (criteria.Contains("DISK_1") || criteria.Contains("INDEX_1") || 
                (criteria == "SMART_AUTO" && preferredIndex == 1) ||
                (policy?.Id?.Contains("disk1", StringComparison.OrdinalIgnoreCase) == true))
            {
                var d1 = clientDisks.FirstOrDefault(d => d.DiskIndex == 1);
                if (d1 != null) return d1.DiskIndex;
                if (clientDisks.Count > 1) return clientDisks[1].DiskIndex;
                return clientDisks[0].DiskIndex;
            }

            // 2. 디스크 0번 우선
            if (criteria.Contains("DISK_0") || criteria.Contains("INDEX_0") ||
                (criteria == "SMART_AUTO" && preferredIndex == 0 && criteria != "SMART_AUTO"))
            {
                var d0 = clientDisks.FirstOrDefault(d => d.DiskIndex == 0);
                if (d0 != null) return d0.DiskIndex;
                return clientDisks[0].DiskIndex;
            }

            // 3. NVMe 버스 타입 우선
            if (criteria.Contains("NVME"))
            {
                var nvmeList = clientDisks.Where(d => (d.BusType ?? "").Contains("NVME", StringComparison.OrdinalIgnoreCase)).ToList();
                if (nvmeList.Count == 1) return nvmeList[0].DiskIndex;
                if (nvmeList.Count > 1)
                {
                    if (preferredIndex == 1)
                    {
                        var nvme1 = nvmeList.FirstOrDefault(d => d.DiskIndex == 1);
                        if (nvme1 != null) return nvme1.DiskIndex;
                    }
                    var smallestNvme = nvmeList.OrderBy(GetDiskGb).ThenBy(d => d.DiskIndex).First();
                    return smallestNvme.DiskIndex;
                }
            }

            // 4. 큰 용량 디스크 우선
            if (criteria.Contains("LARGER"))
            {
                return clientDisks.OrderByDescending(GetDiskGb).ThenBy(d => d.DiskIndex).First().DiskIndex;
            }

            // 5. SMART_AUTO (기본) 또는 SMALLER_FIRST
            // 용량 차이가 3GB 초과면 작은 디스크가 OS
            var sortedBySize = clientDisks.OrderBy(d => Math.Round(GetDiskGb(d) / 2.0)).ToList();
            double diff = Math.Abs(GetDiskGb(sortedBySize.First()) - GetDiskGb(sortedBySize.Last()));

            if (diff > 3.0)
            {
                return sortedBySize.First().DiskIndex;
            }

            // 용량이 동일(±3GB 이내)한 경우: 정책의 TargetDiskIndex 설정(1 또는 0) 반영
            if (preferredIndex == 1)
            {
                var d1 = clientDisks.FirstOrDefault(d => d.DiskIndex == 1);
                if (d1 != null) return d1.DiskIndex;
            }

            return clientDisks.FirstOrDefault(d => d.DiskIndex == preferredIndex)?.DiskIndex ?? clientDisks[0].DiskIndex;
        }

        private static double GetDiskGb(DiskInfo disk)
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

            if (clientDisks == null || clientDisks.Count == 0)
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

            if (clientDisks.Count == 1)
            {
                // [조건 1] 디스크 1개 인식
                var disk = clientDisks[0];
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
                // [조건 2] 디스크 2개 이상 인식
                int osDiskIndex = DetermineTargetOsDiskIndex(policy, clientDisks, defaultTargetDiskIndex);

                var osDisk = clientDisks.FirstOrDefault(d => d.DiskIndex == osDiskIndex) ?? clientDisks[0];
                var dataDisks = clientDisks.Where(d => d.DiskIndex != osDisk.DiskIndex).OrderBy(d => d.DiskIndex).ToList();

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
