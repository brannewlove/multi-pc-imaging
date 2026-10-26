using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MultiDeployOS.Master.Models;

namespace MultiDeployOS.Master.Services
{
    public static class ImageMatcher
    {
        public static ImageRule? MatchRule(ClientSession client, List<ImageRule> catalog)
        {
            if (catalog == null || catalog.Count == 0) return null;

            string mfg = client.System.Manufacturer ?? "";
            string model = client.System.ModelName ?? "";
            var targetDisk = client.Disks.FirstOrDefault();
            double diskGb = targetDisk != null ? (targetDisk.SizeGb > 0 ? targetDisk.SizeGb : (targetDisk.SizeBytes / (1024.0 * 1024 * 1024))) : 0;
            string diskBus = targetDisk?.BusType ?? "";

            var activeRules = catalog
                .Where(r => r.Enabled)
                .OrderBy(r => r.Priority)
                .ToList();

            // 1차 패스: 일반 조건 매칭 (Fallback 제외)
            foreach (var rule in activeRules)
            {
                if (rule.IsFallback) continue;
                var cond = rule.Conditions;
                if (cond == null) continue;

                // 1. 제조사 매칭
                if (!string.IsNullOrWhiteSpace(cond.Manufacturer) && cond.Manufacturer != "*")
                {
                    var parts = cond.Manufacturer.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    bool mfgMatched = parts.Any(p => FlexibleMatch(mfg, p));
                    if (!mfgMatched) continue;
                }

                // 2. 모델명 매칭
                if (!string.IsNullOrWhiteSpace(cond.ModelPattern) && cond.ModelPattern != "*")
                {
                    var parts = cond.ModelPattern.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    bool modelMatched = parts.Any(p => FlexibleMatch(model, p));
                    if (!modelMatched) continue;
                }

                // 3. 디스크 용량 조건
                if (cond.MinDiskGb > 0 && diskGb > 0 && diskGb < cond.MinDiskGb) continue;
                if (cond.MaxDiskGb > 0 && diskGb > 0 && diskGb > cond.MaxDiskGb) continue;

                // 4. 버스 타입 조건 (ANY, ANY (모든 버스), 빈값은 전체 허용)
                if (!string.IsNullOrWhiteSpace(cond.TargetDiskType) && 
                    !cond.TargetDiskType.StartsWith("ANY", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(diskBus) || !diskBus.Contains(cond.TargetDiskType, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                return rule;
            }

            // 2차 패스: 일치하는 규칙이 없으면 Fallback 규칙 리턴
            var fallback = activeRules.FirstOrDefault(r => r.IsFallback) ?? activeRules.LastOrDefault();
            return fallback;
        }

        public static PartitionPolicy? MatchPolicy(ClientSession client, List<PartitionPolicy> policies)
        {
            if (policies == null || policies.Count == 0) return null;

            int diskCount = client.Disks.Count;
            var targetDisk = client.Disks.FirstOrDefault();
            double targetCapacityGb = targetDisk != null ? (targetDisk.SizeGb > 0 ? targetDisk.SizeGb : (targetDisk.SizeBytes / (1024.0 * 1024 * 1024))) : 0;

            foreach (var policy in policies)
            {
                var cond = policy.Conditions;
                if (cond == null) continue;

                // 디스크 개수 조건
                if (cond.DiskCountMin > 0 && diskCount < cond.DiskCountMin) continue;
                if (cond.DiskCountMax > 0 && diskCount > cond.DiskCountMax) continue;

                // 용량 조건
                if (cond.MinCapacityGb > 0 && targetCapacityGb < cond.MinCapacityGb) continue;
                if (cond.MaxCapacityGb > 0 && targetCapacityGb > cond.MaxCapacityGb) continue;

                // 모델명 조건
                if (!string.IsNullOrWhiteSpace(cond.ModelPattern) && cond.ModelPattern != "*")
                {
                    var parts = cond.ModelPattern.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    bool modelMatched = parts.Any(p => FlexibleMatch(client.System.ModelName ?? "", p));
                    if (!modelMatched) continue;
                }

                return policy;
            }

            return policies[0];
        }

        public static bool FlexibleMatch(string input, string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern) || pattern == "*") return true;
            if (string.IsNullOrWhiteSpace(input)) return false;

            pattern = pattern.Trim();
            input = input.Trim();

            // 패턴에 와일드카드(*, ?)가 명시되지 않은 경우: 사용자의 직관에 맞게 대소문자 무관 '포함(Contains)' 여부 판별
            if (!pattern.Contains('*') && !pattern.Contains('?'))
            {
                return input.Contains(pattern, StringComparison.OrdinalIgnoreCase);
            }

            // 와일드카드 정규식 변환 (예: SAMSUNG*, NT950*, *Book*)
            try
            {
                string regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                return Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase);
            }
            catch
            {
                return input.Contains(pattern.Replace("*", "").Replace("?", ""), StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
