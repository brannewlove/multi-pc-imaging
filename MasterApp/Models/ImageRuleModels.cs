using System.Text.Json.Serialization;

namespace MultiDeployOS.Master.Models
{
    public class ImageRuleConditions
    {
        [JsonPropertyName("manufacturer")]
        public string Manufacturer { get; set; } = "*";

        [JsonPropertyName("modelPattern")]
        public string ModelPattern { get; set; } = "*";

        [JsonPropertyName("minDiskGb")]
        public int MinDiskGb { get; set; } = 0;

        [JsonPropertyName("maxDiskGb")]
        public int MaxDiskGb { get; set; } = 99999;

        [JsonPropertyName("targetDiskType")]
        public string TargetDiskType { get; set; } = "ANY";
    }

    public class TargetImageInfo
    {
        [JsonPropertyName("imageFileName")]
        public string ImageFileName { get; set; } = "";

        [JsonPropertyName("imageIndex")]
        public int ImageIndex { get; set; } = 1;

        [JsonPropertyName("osVersion")]
        public string OsVersion { get; set; } = "Windows 11 Pro x64";

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";
    }

    public class ImageRule
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonPropertyName("priority")]
        public int Priority { get; set; } = 1;

        [JsonPropertyName("conditions")]
        public ImageRuleConditions Conditions { get; set; } = new();

        [JsonPropertyName("targetImage")]
        public TargetImageInfo TargetImage { get; set; } = new();

        [JsonPropertyName("isFallback")]
        public bool IsFallback { get; set; } = false;
    }

    public class ScannedImageFile
    {
        public string FileName { get; set; } = "";
        public string FullPath { get; set; } = "";
        public long SizeBytes { get; set; }
        public string SizeGb { get; set; } = "0.00";
        public string ModifiedAt { get; set; } = "";
        public DateTime LastWriteTime { get; set; }
        public string Extension { get; set; } = "WIM";
    }

    public class ScannedImageResult
    {
        public string FolderPath { get; set; } = "";
        public bool Exists { get; set; }
        public bool Accessible { get; set; }
        public string? Error { get; set; }
        public int TotalFiles { get; set; }
        public string TotalSizeGb { get; set; } = "0.00";
        public List<ScannedImageFile> Files { get; set; } = new();
    }
}
