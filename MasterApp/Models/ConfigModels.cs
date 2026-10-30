using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MultiDeployOS.Master.Models
{
    public class PxeServerConfig
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// STANDALONE: 독립 작업대용 자체 DHCP + PXE 서버 (기본값)
        /// PROXY: 기존 공유기/DHCP 서버와 병행하는 ProxyDHCP 모드
        /// </summary>
        [JsonPropertyName("dhcpMode")]
        public string DhcpMode { get; set; } = "STANDALONE";

        [JsonPropertyName("bindIpAddress")]
        public string BindIpAddress { get; set; } = "";

        [JsonPropertyName("subnetMask")]
        public string SubnetMask { get; set; } = "255.255.255.0";

        [JsonPropertyName("gatewayIp")]
        public string GatewayIp { get; set; } = "";

        [JsonPropertyName("dnsIp")]
        public string DnsIp { get; set; } = "";

        [JsonPropertyName("poolStartIp")]
        public string PoolStartIp { get; set; } = "192.168.77.100";

        [JsonPropertyName("poolEndIp")]
        public string PoolEndIp { get; set; } = "192.168.77.250";

        [JsonPropertyName("leaseTimeSeconds")]
        public int LeaseTimeSeconds { get; set; } = 28800;

        [JsonPropertyName("httpBootPort")]
        public int HttpBootPort { get; set; } = 39202;

        [JsonPropertyName("bootWimPath")]
        public string BootWimPath { get; set; } = "";

        [JsonPropertyName("cacheBootWimInRam")]
        public bool CacheBootWimInRam { get; set; } = true;

        [JsonPropertyName("preventBootLoop")]
        public bool PreventBootLoop { get; set; } = true;

        [JsonPropertyName("bootLoopGuardMinutes")]
        public int BootLoopGuardMinutes { get; set; } = 30;
    }

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

        [JsonPropertyName("pxe")]
        public PxeServerConfig Pxe { get; set; } = new();
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
