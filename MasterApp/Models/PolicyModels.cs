using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MultiDeployOS.Master.Models
{
    public class PolicyConditions
    {
        [JsonPropertyName("modelPattern")]
        public string ModelPattern { get; set; } = "*";

        [JsonPropertyName("diskCountMin")]
        public int DiskCountMin { get; set; } = 1;

        [JsonPropertyName("diskCountMax")]
        public int DiskCountMax { get; set; } = 99;

        [JsonPropertyName("targetDiskCriteria")]
        public string TargetDiskCriteria { get; set; } = "SMART_AUTO";

        [JsonPropertyName("targetDiskIndex")]
        public int TargetDiskIndex { get; set; } = 0;

        [JsonPropertyName("minCapacityGb")]
        public int MinCapacityGb { get; set; } = 0;

        [JsonPropertyName("maxCapacityGb")]
        public int MaxCapacityGb { get; set; } = 99999;
    }

    public class PartitionLayoutItem
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("sizeMb")]
        public object? SizeMb { get; set; }

        [JsonPropertyName("fs")]
        public string Fs { get; set; } = "NTFS";

        [JsonPropertyName("label")]
        public string Label { get; set; } = "";

        [JsonPropertyName("letter")]
        public string Letter { get; set; } = "";
    }

    public class PartitionPolicy
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [JsonPropertyName("autoApprove")]
        public bool AutoApprove { get; set; } = true;

        [JsonPropertyName("partitionScheme")]
        public string PartitionScheme { get; set; } = "GPT";

        [JsonPropertyName("isSmartDynamic")]
        public bool IsSmartDynamic { get; set; } = false;

        [JsonPropertyName("conditions")]
        public PolicyConditions Conditions { get; set; } = new();

        [JsonPropertyName("diskpartCommands")]
        public List<string>? DiskpartCommands { get; set; }

        [JsonPropertyName("layout")]
        public List<PartitionLayoutItem>? Layout { get; set; }
    }
}
