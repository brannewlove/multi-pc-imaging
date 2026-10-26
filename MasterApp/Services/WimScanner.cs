using System;
using System.Collections.Generic;
using System.IO;
using MultiDeployOS.Master.Models;

namespace MultiDeployOS.Master.Services
{
    public static class WimScanner
    {
        public static ScannedImageResult ScanFolder(string targetDir)
        {
            var res = new ScannedImageResult
            {
                FolderPath = targetDir,
                Exists = false,
                Accessible = false
            };

            if (string.IsNullOrWhiteSpace(targetDir))
            {
                res.Error = "이미지 폴더 경로가 지정되지 않았습니다.";
                return res;
            }

            if (!Directory.Exists(targetDir))
            {
                res.Error = $"지정된 폴더({targetDir})가 존재하지 않습니다.";
                return res;
            }

            res.Exists = true;
            try
            {
                var dirInfo = new DirectoryInfo(targetDir);
                long totalBytes = 0;
                var files = new List<ScannedImageFile>();

                foreach (var file in dirInfo.GetFiles("*.*", SearchOption.TopDirectoryOnly))
                {
                    string ext = file.Extension.ToLower();
                    if (ext == ".wim" || ext == ".esd" || ext == ".swm")
                    {
                        totalBytes += file.Length;
                        files.Add(new ScannedImageFile
                        {
                            FileName = file.Name,
                            FullPath = file.FullName,
                            SizeBytes = file.Length,
                            SizeGb = (file.Length / (1024.0 * 1024 * 1024)).ToString("F2"),
                            ModifiedAt = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                            LastWriteTime = file.LastWriteTime,
                            Extension = ext.TrimStart('.').ToUpper()
                        });
                    }
                }

                res.Accessible = true;
                res.Files = files;
                res.TotalFiles = files.Count;
                res.TotalSizeGb = (totalBytes / (1024.0 * 1024 * 1024)).ToString("F2");
                return res;
            }
            catch (Exception ex)
            {
                res.Error = $"폴더 접근 권한 오류: {ex.Message}";
                return res;
            }
        }

        public static ScannedImageFile? ResolveLatestImageFile(string targetDir, string patternOrFileName, string? clientModel = null)
        {
            if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir)) return null;

            var scan = ScanFolder(targetDir);
            if (!scan.Accessible || scan.Files == null || scan.Files.Count == 0) return null;

            string pattern = (patternOrFileName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(pattern)) pattern = "*.wim";

            // 1. 와일드카드(*, ?)가 없는 정확한 파일명인 경우
            if (!pattern.Contains('*') && !pattern.Contains('?'))
            {
                var exact = scan.Files.FirstOrDefault(f => f.FileName.Equals(pattern, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return exact;

                var exactWim = scan.Files.FirstOrDefault(f => f.FileName.Equals(pattern + ".wim", StringComparison.OrdinalIgnoreCase));
                if (exactWim != null) return exactWim;
            }

            // 2. 와일드카드 패턴 매칭 후보군 추출
            var candidates = scan.Files
                .Where(f => ImageMatcher.FlexibleMatch(f.FileName, pattern))
                .ToList();

            if (candidates.Count == 0)
            {
                // 확장자가 누락된 경우 패턴 뒤에 *.wim을 붙여서 재검색
                if (!pattern.EndsWith(".wim", StringComparison.OrdinalIgnoreCase) &&
                    !pattern.EndsWith(".esd", StringComparison.OrdinalIgnoreCase))
                {
                    string altPattern = pattern.EndsWith("*") ? pattern + ".wim" : pattern + "*.wim";
                    candidates = scan.Files
                        .Where(f => ImageMatcher.FlexibleMatch(f.FileName, altPattern))
                        .ToList();
                }
            }

            if (candidates.Count == 0) return null;

            // 3. 모델명이 전달된 경우: 동일 모델명 키워드를 포함하는 파일 우선 필터링
            if (!string.IsNullOrWhiteSpace(clientModel))
            {
                string cleanModel = clientModel.Trim();
                var modelMatches = candidates
                    .Where(f => f.FileName.Contains(cleanModel, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (modelMatches.Count > 0)
                {
                    candidates = modelMatches;
                }
                else
                {
                    // 하이픈/공백 분리 키워드 매칭 (예: NT950XDC-X71A -> NT950XDC 또는 NT950)
                    var parts = cleanModel.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var part in parts)
                    {
                        if (part.Length >= 4)
                        {
                            var subMatches = candidates
                                .Where(f => f.FileName.Contains(part, StringComparison.OrdinalIgnoreCase))
                                .ToList();
                            if (subMatches.Count > 0)
                            {
                                candidates = subMatches;
                                break;
                            }
                        }
                    }
                }
            }

            // 4. 동일 모델/패턴 후보들 중 최신 파일(LastWriteTime 내림차순) 자동 선택
            return candidates.OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
        }

        public static string ResolveLatestFileName(string targetDir, string patternOrFileName, string? clientModel = null)
        {
            var resolved = ResolveLatestImageFile(targetDir, patternOrFileName, clientModel);
            return resolved?.FileName ?? patternOrFileName;
        }
    }
}
