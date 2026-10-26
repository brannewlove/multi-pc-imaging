using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MultiDeployOS.Master.Models
{
    public class ServerConfig
    {
        [JsonPropertyName("masterImageDir")]
        public string MasterImageDir { get; set; } = "C:\\images";

        [JsonPropertyName("networkSharePath")]
        public string NetworkSharePath { get; set; } = "\\\\127.0.0.1\\images";

        [JsonPropertyName("clientAccessMethod")]
        public string ClientAccessMethod { get; set; } = "LOCAL_OR_SHARE";

        [JsonPropertyName("lastScannedAt")]
        public string LastScannedAt { get; set; } = DateTime.UtcNow.ToString("o");
    }

    public class TableSortConfig
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = "";

        [JsonPropertyName("dir")]
        public string Dir { get; set; } = "ASC"; // ASC, DESC
    }

    public class TableConfig
    {
        [JsonPropertyName("order")]
        public List<string> Order { get; set; } = new()
        {
            "status", "ip", "model", "serial", "mac", "disks", "policy", "image", "progress", "duration", "action"
        };

        [JsonPropertyName("hidden")]
        public List<string> Hidden { get; set; } = new();

        [JsonPropertyName("widths")]
        public Dictionary<string, int> Widths { get; set; } = new()
        {
            ["status"] = 110,
            ["ip"] = 120,
            ["model"] = 180,
            ["serial"] = 140,
            ["mac"] = 140,
            ["disks"] = 180,
            ["policy"] = 180,
            ["image"] = 200,
            ["progress"] = 220,
            ["duration"] = 100,
            ["action"] = 130
        };

        [JsonPropertyName("sort")]
        public TableSortConfig? Sort { get; set; }
    }

    public class UiSettings
    {
        [JsonPropertyName("viewMode")]
        public string ViewMode { get; set; } = "TABLE";

        [JsonPropertyName("tableConfig")]
        public TableConfig TableConfig { get; set; } = new();
    }

    public class DeployHistoryItem
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = "";

        [JsonPropertyName("modelName")]
        public string ModelName { get; set; } = "";

        [JsonPropertyName("serialNumber")]
        public string SerialNumber { get; set; } = "";

        [JsonPropertyName("macAddress")]
        public string MacAddress { get; set; } = "";

        [JsonPropertyName("policyName")]
        public string PolicyName { get; set; } = "";

        [JsonPropertyName("imageName")]
        public string ImageName { get; set; } = "";

        [JsonPropertyName("status")]
        public string Status { get; set; } = "";

        [JsonPropertyName("durationSeconds")]
        public int DurationSeconds { get; set; }

        [JsonPropertyName("errorMessage")]
        public string ErrorMessage { get; set; } = "";

        [JsonPropertyName("completedAt")]
        public string CompletedAt { get; set; } = "";
    }

    public class FullSettingsBackup
    {
        [JsonPropertyName("exportVersion")]
        public string ExportVersion { get; set; } = "1.0";

        [JsonPropertyName("exportedAt")]
        public string ExportedAt { get; set; } = DateTime.UtcNow.ToString("o");

        [JsonPropertyName("serverConfig")]
        public ServerConfig ServerConfig { get; set; } = new();

        [JsonPropertyName("uiSettings")]
        public UiSettings UiSettings { get; set; } = new();

        [JsonPropertyName("partitionPolicies")]
        public List<PartitionPolicy> PartitionPolicies { get; set; } = new();

        [JsonPropertyName("imageCatalog")]
        public List<ImageRule> ImageCatalog { get; set; } = new();
    }
}
