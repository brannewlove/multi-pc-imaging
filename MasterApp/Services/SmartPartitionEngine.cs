using System;
using System.Collections.Generic;
using System.Linq;
using MultiDeployOS.Master.Models;

namespace MultiDeployOS.Master.Services
{
    public static class SmartPartitionEngine
    {
        public static List<string> GenerateCommands(PartitionPolicy policy, List<DiskInfo>? clientDisks, int targetDiskIndex = 0)
        {
            // 1. 스마트 동적 정책이거나, 지능형 모드인 경우
            if (policy.IsSmartDynamic || policy.Id == "policy-smart-auto-gpt" ||
                (policy.DiskpartCommands == null || policy.DiskpartCommands.Count == 0) &&
                (policy.Layout == null || policy.Layout.Count == 0))
            {
                return GenerateDynamicSmartPartitionCommands(clientDisks);
            }

            // 2. 관리자가 직접 지정한 Diskpart 명령이 있는 경우
            if (policy.DiskpartCommands != null && policy.DiskpartCommands.Count > 0)
            {
                return new List<string>(policy.DiskpartCommands);
            }

            // 3. 기존 레이아웃 배열이 있는 경우
            if (policy.Layout != null && policy.Layout.Count > 0)
            {
                var cmds = new List<string>
                {
                    $"select disk {targetDiskIndex}",
                    "clean",
                    policy.PartitionScheme == "GPT" ? "convert gpt" : "convert mbr"
                };

                foreach (var item in policy.Layout)
                {
                    string fs = string.IsNullOrWhiteSpace(item.Fs) ? "ntfs" : item.Fs.ToLower();
                    if (item.Type == "EFI")
                    {
                        cmds.Add($"create partition efi size={item.SizeMb ?? 300}");
                        cmds.Add($"format quick fs={fs} label=\"{item.Label ?? "System"}\"");
                        if (!string.IsNullOrWhiteSpace(item.Letter)) cmds.Add($"assign letter={item.Letter}");
                    }
                    else if (item.Type == "MSR")
                    {
                        cmds.Add($"create partition msr size={item.SizeMb ?? 16}");
                    }
                    else if (item.Type == "OS" || item.Type == "DATA")
                    {
                        string defaultLabel = item.Type == "OS" ? "Windows" : "Data";
                        string sizeStr = (item.SizeMb == null || item.SizeMb.ToString() == "REMAINDER") ? "" : $" size={item.SizeMb}";
                        cmds.Add($"create partition primary{sizeStr}");
                        cmds.Add($"format quick fs={fs} label=\"{item.Label ?? defaultLabel}\"");
                        if (!string.IsNullOrWhiteSpace(item.Letter)) cmds.Add($"assign letter={item.Letter}");
                    }
                }
                return cmds;
            }

            return GenerateDynamicSmartPartitionCommands(clientDisks);
        }

        // 사용자의 핵심 요구사항 100% 구현
        public static List<string> GenerateDynamicSmartPartitionCommands(List<DiskInfo>? clientDisks)
        {
            var cmds = new List<string>();

            if (clientDisks == null || clientDisks.Count == 0)
            {
                return new List<string>
                {
                    "select disk 0", "clean", "convert gpt",
                    "create partition efi size=100", "format quick fs=fat32 label=\"System\"", "assign letter=\"S\"",
                    "create partition msr size=16",
                    "create partition primary", "format quick fs=ntfs label=\"Windows11\"", "assign letter=\"W\""
                };
            }

            if (clientDisks.Count == 1)
            {
                // [조건 1] 디스크 1개 인식
                var disk = clientDisks[0];
                int diskIndex = disk.DiskIndex;
                double sizeGb = disk.SizeGb > 0 ? disk.SizeGb : (disk.SizeBytes / (1024.0 * 1024 * 1024));
                long sizeMb = disk.SizeBytes > 0 ? (disk.SizeBytes / (1024 * 1024)) : (long)(sizeGb * 1024);

                cmds.Add($"select disk {diskIndex}");
                cmds.Add("clean");
                cmds.Add("convert gpt");
                cmds.Add("create partition efi size=100");
                cmds.Add("format quick fs=fat32 label=\"System\"");
                cmds.Add("assign letter=\"S\"");
                cmds.Add("create partition msr size=16");

                if (sizeGb > 512)
                {
                    // (1-A) 크기 512GB 초과: 300GB "Windows11" 라벨 후 설치, 나머지 "Data" 라벨
                    cmds.Add("create partition primary size=307200"); // 300GB = 307,200 MB
                    cmds.Add("format quick fs=ntfs label=\"Windows11\"");
                    cmds.Add("assign letter=\"W\"");
                    cmds.Add("create partition primary");
                    cmds.Add("format quick fs=ntfs label=\"Data\"");
                    cmds.Add("assign letter=\"D\"");
                }
                else
                {
                    // (1-B) 크기 512GB 이하: 50%로 나눈 뒤 "Windows11" 라벨 후 설치, 나머지 "Data" 라벨
                    long usableMb = Math.Max(1024, sizeMb - 120); // EFI(100MB)+MSR(16MB) 제외 잔여 공간
                    long halfMb = usableMb / 2;
                    cmds.Add($"create partition primary size={halfMb}");
                    cmds.Add("format quick fs=ntfs label=\"Windows11\"");
                    cmds.Add("assign letter=\"W\"");
                    cmds.Add("create partition primary");
                    cmds.Add("format quick fs=ntfs label=\"Data\"");
                    cmds.Add("assign letter=\"D\"");
                }
            }
            else
            {
                // [조건 2] 디스크 2개 이상 인식
                // 작은 용량 디스크 우선 정렬, 용량이 동일한 경우 1번 디스크(낮은 diskIndex) 우선
                var sorted = clientDisks.OrderBy(d =>
                {
                    double gb = d.SizeGb > 0 ? d.SizeGb : (d.SizeBytes / (1024.0 * 1024 * 1024));
                    return Math.Round(gb / 2.0); // 2GB 이내 차이는 동일 크기로 간주
                }).ThenBy(d => d.DiskIndex).ToList();

                var osDisk = sorted[0];
                var dataDisks = sorted.Skip(1).ToList();

                // 1. OS 디스크: 전체 GPT 포맷, EFI(S:), MSR 생성 후 전체를 "Windows11" 라벨로 포맷 (W:)
                cmds.Add($"select disk {osDisk.DiskIndex}");
                cmds.Add("clean");
                cmds.Add("convert gpt");
                cmds.Add("create partition efi size=100");
                cmds.Add("format quick fs=fat32 label=\"System\"");
                cmds.Add("assign letter=\"S\"");
                cmds.Add("create partition msr size=16");
                cmds.Add("create partition primary");
                cmds.Add("format quick fs=ntfs label=\"Windows11\"");
                cmds.Add("assign letter=\"W\"");

                // 2. 나머지 디스크: 전부 GPT 포맷, 1개면 "Data", 2개 이상이면 "Data1", "Data2" 형식
                for (int i = 0; i < dataDisks.Count; i++)
                {
                    var dDisk = dataDisks[i];
                    string label = dataDisks.Count == 1 ? "Data" : $"Data{i + 1}";
                    cmds.Add($"select disk {dDisk.DiskIndex}");
                    cmds.Add("clean");
                    cmds.Add("convert gpt");
                    cmds.Add("create partition primary");
                    cmds.Add($"format quick fs=ntfs label=\"{label}\"");
                    if (i == 0)
                    {
                        cmds.Add("assign letter=\"D\"");
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
