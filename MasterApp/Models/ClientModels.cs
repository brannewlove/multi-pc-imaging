using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text.Json.Serialization;

namespace MultiDeployOS.Master.Models
{
    public class SystemInfo
    {
        [JsonPropertyName("manufacturer")]
        public string Manufacturer { get; set; } = "";

        [JsonPropertyName("modelName")]
        public string ModelName { get; set; } = "";

        [JsonPropertyName("serialNumber")]
        public string SerialNumber { get; set; } = "";

        [JsonPropertyName("uuid")]
        public string Uuid { get; set; } = "";

        [JsonPropertyName("isUefi")]
        public bool IsUefi { get; set; } = true;
    }

    public class NetworkInfo
    {
        [JsonPropertyName("primaryMac")]
        public string PrimaryMac { get; set; } = "";

        [JsonPropertyName("ipAddress")]
        public string IpAddress { get; set; } = "";
    }

    public class PartitionInfo
    {
        [JsonPropertyName("partitionNumber")]
        public int PartitionNumber { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("sizeBytes")]
        public long SizeBytes { get; set; }
    }

    public class DiskInfo
    {
        [JsonPropertyName("diskIndex")]
        public int DiskIndex { get; set; }

        [JsonPropertyName("model")]
        public string Model { get; set; } = "";

        [JsonPropertyName("friendlyName")]
        public string FriendlyName { get; set; } = "";

        [JsonPropertyName("busType")]
        public string BusType { get; set; } = "";

        [JsonPropertyName("sizeBytes")]
        public long SizeBytes { get; set; }

        [JsonPropertyName("sizeGb")]
        public double SizeGb { get; set; }

        [JsonPropertyName("partitionStyle")]
        public string PartitionStyle { get; set; } = "";

        [JsonPropertyName("partitions")]
        public List<PartitionInfo> Partitions { get; set; } = new();
    }

    public class ProgressInfo
    {
        [JsonPropertyName("percent")]
        public double Percent { get; set; }

        [JsonPropertyName("step")]
        public string Step { get; set; } = "";

        [JsonPropertyName("speedMbps")]
        public double SpeedMbps { get; set; }

        [JsonPropertyName("elapsedSeconds")]
        public int ElapsedSeconds { get; set; }

        [JsonPropertyName("remainingSeconds")]
        public int RemainingSeconds { get; set; }

        [JsonPropertyName("detailMessage")]
        public string DetailMessage { get; set; } = "";
    }

    public class ResultInfo
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = "";

        [JsonPropertyName("totalDurationSeconds")]
        public int TotalDurationSeconds { get; set; }

        [JsonPropertyName("errorMessage")]
        public string ErrorMessage { get; set; } = "";

        [JsonPropertyName("completedAt")]
        public string CompletedAt { get; set; } = "";
    }

    public class ClientSession
    {
        public string SessionId { get; set; } = "";
        public string IpAddress { get; set; } = "";
        public DateTime ConnectedAt { get; set; } = DateTime.Now;
        public string Status { get; set; } = "CONNECTED"; // CONNECTED, READY, IMAGING, SUCCESS, FAILED

        public SystemInfo System { get; set; } = new();
        public NetworkInfo Network { get; set; } = new();
        public List<DiskInfo> Disks { get; set; } = new();

        public PartitionPolicy? MatchedPolicy { get; set; }
        public ImageRule? MatchedImage { get; set; }
        public ProgressInfo? Progress { get; set; }
        public ResultInfo? Result { get; set; }

        [JsonIgnore]
        public TcpClient? SocketClient { get; set; }
    }
}
