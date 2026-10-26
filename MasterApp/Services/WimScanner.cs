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
    }
}
