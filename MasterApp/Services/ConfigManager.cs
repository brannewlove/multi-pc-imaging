using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using MultiDeployOS.Master.Models;

namespace MultiDeployOS.Master.Services
{
    public class ConfigManager
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        };

        public string ConfigDir { get; private set; }
        public ServerConfig ServerConfig { get; private set; } = new();
        public UiSettings UiSettings { get; private set; } = new();
        public List<PartitionPolicy> PartitionPolicies { get; private set; } = new();
        public List<ImageRule> ImageCatalog { get; private set; } = new();
        public List<DeployHistoryItem> History { get; private set; } = new();

        public ConfigManager()
        {
            ConfigDir = ResolveConfigDirectory();
            Directory.CreateDirectory(ConfigDir);
            LoadAll();
        }

        private string ResolveConfigDirectory()
        {
            // 1. 현재 실행 위치 바로 아래 config
            string localConfig = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config");
            if (Directory.Exists(localConfig)) return localConfig;

            // 2. 상위 master/config 디렉터리 (개발 소스 환경)
            string parentMasterConfig = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "master", "config"));
            if (Directory.Exists(parentMasterConfig)) return parentMasterConfig;

            string repoMasterConfig = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "master", "config"));
            if (Directory.Exists(repoMasterConfig)) return repoMasterConfig;

            return localConfig;
        }

        public void LoadAll()
        {
            LoadServerConfig();
            LoadUiSettings();
            LoadPartitionPolicies();
            LoadImageCatalog();
            LoadHistory();
        }

        public void LoadServerConfig()
        {
            string path = Path.Combine(ConfigDir, "server_config.json");
            try
            {
                if (File.Exists(path))
                {
                    ServerConfig = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path), JsonOptions) ?? new();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[-] 서버 설정 로드 실패: " + ex.Message);
            }

            // 환경 독립적 폴더 경로 보정
            ServerConfig.MasterImageDir = ResolveEffectiveImageDir(ServerConfig.MasterImageDir);
        }

        private string ResolveEffectiveImageDir(string configuredDir)
        {
            if (!string.IsNullOrWhiteSpace(configuredDir) && Directory.Exists(configuredDir))
            {
                return configuredDir;
            }

            // 프로젝트 내 sample_images 폴더 탐색
            string sampleDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "sample_images"));
            if (Directory.Exists(sampleDir)) return sampleDir;

            string standardDir = "C:\\images";
            try
            {
                if (!Directory.Exists(standardDir)) Directory.CreateDirectory(standardDir);
                return standardDir;
            }
            catch
            {
                return configuredDir ?? standardDir;
            }
        }

        public void SaveServerConfig()
        {
            string path = Path.Combine(ConfigDir, "server_config.json");
            try
            {
                File.WriteAllText(path, JsonSerializer.Serialize(ServerConfig, JsonOptions));
            }
            catch (Exception ex) { Console.WriteLine("[-] server_config.json 저장 실패: " + ex.Message); }
        }

        public void LoadUiSettings()
        {
            string path = Path.Combine(ConfigDir, "ui_settings.json");
            try
            {
                if (File.Exists(path))
                {
                    UiSettings = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(path), JsonOptions) ?? new();
                }
            }
            catch (Exception ex) { Console.WriteLine("[-] ui_settings.json 로드 실패: " + ex.Message); }
        }

        public void SaveUiSettings()
        {
            string json = JsonSerializer.Serialize(UiSettings, JsonOptions);
            string path = Path.Combine(ConfigDir, "ui_settings.json");
            try
            {
                File.WriteAllText(path, json);
            }
            catch (Exception ex) { Console.WriteLine("[-] ui_settings.json 저장 실패: " + ex.Message); }

            // 개발/소스 환경인 경우 소스 디렉터리의 ui_settings.json도 함께 동기화 (재빌드 시 덮어쓰기 방지)
            try
            {
                string[] sourceCandidates = new[]
                {
                    Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "MasterApp", "config", "ui_settings.json")),
                    Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "MasterApp", "config", "ui_settings.json")),
                    Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "MasterApp", "config", "ui_settings.json")),
                    Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "master", "config", "ui_settings.json"))
                };

                foreach (var srcPath in sourceCandidates)
                {
                    if (File.Exists(srcPath) && !string.Equals(srcPath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        File.WriteAllText(srcPath, json);
                    }
                }
            }
            catch { }
        }

        public void LoadPartitionPolicies()
        {
            string path = Path.Combine(ConfigDir, "partition_policies.json");
            try
            {
                if (File.Exists(path))
                {
                    PartitionPolicies = JsonSerializer.Deserialize<List<PartitionPolicy>>(File.ReadAllText(path), JsonOptions) ?? new();
                }
            }
            catch (Exception ex) { Console.WriteLine("[-] partition_policies.json 로드 실패: " + ex.Message); }

            if (PartitionPolicies.Count == 0)
            {
                // 기본 스마트 정책 생성
                PartitionPolicies.Add(new PartitionPolicy
                {
                    Id = "policy-smart-auto-gpt",
                    Name = "지능형 자동 감지 분할 (GPT 표준 권장양식)",
                    Description = "디스크 1개 시 용량(512GB 초과: 300GB / 이하: 50% 분할) 및 디스크 2개 이상 시 최소용량 OS+Data1/Data2 자동 GPT 구성",
                    PartitionScheme = "GPT",
                    AutoApprove = true,
                    IsSmartDynamic = true,
                    Conditions = new PolicyConditions { ModelPattern = "*", TargetDiskCriteria = "SMART_AUTO" }
                });
                SavePartitionPolicies();
            }
        }

        public void SavePartitionPolicies()
        {
            string path = Path.Combine(ConfigDir, "partition_policies.json");
            try
            {
                File.WriteAllText(path, JsonSerializer.Serialize(PartitionPolicies, JsonOptions));
            }
            catch (Exception ex) { Console.WriteLine("[-] partition_policies.json 저장 실패: " + ex.Message); }
        }

        public void LoadImageCatalog()
        {
            string path = Path.Combine(ConfigDir, "image_catalog.json");
            try
            {
                if (File.Exists(path))
                {
                    ImageCatalog = JsonSerializer.Deserialize<List<ImageRule>>(File.ReadAllText(path), JsonOptions) ?? new();
                }
            }
            catch (Exception ex) { Console.WriteLine("[-] image_catalog.json 로드 실패: " + ex.Message); }

            // 만약 현재 설정 디렉터리에 규칙이 1개(기본 fallback) 이하이면 소스 디렉터리(MasterApp/config)의 전체 규칙 복사
            if (ImageCatalog.Count <= 1)
            {
                string[] candidates = new[]
                {
                    Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "MasterApp", "config", "image_catalog.json")),
                    Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "MasterApp", "config", "image_catalog.json")),
                    Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "MasterApp", "config", "image_catalog.json"))
                };
                foreach (var cand in candidates)
                {
                    if (File.Exists(cand) && cand != path)
                    {
                        try
                        {
                            var fromSrc = JsonSerializer.Deserialize<List<ImageRule>>(File.ReadAllText(cand), JsonOptions);
                            if (fromSrc != null && fromSrc.Count > ImageCatalog.Count)
                            {
                                ImageCatalog = fromSrc;
                                SaveImageCatalog();
                                break;
                            }
                        }
                        catch { }
                    }
                }
            }

            if (ImageCatalog.Count == 0)
            {
                ImageCatalog.Add(new ImageRule
                {
                    Id = "rule-generic-fallback",
                    Name = "범용 순정 Windows 11 Pro (기본 대체 규칙)",
                    Enabled = true,
                    Priority = 999,
                    IsFallback = true,
                    Conditions = new ImageRuleConditions { Manufacturer = "*", ModelPattern = "*" },
                    TargetImage = new TargetImageInfo { ImageFileName = "Win11_23H2_Universal_Clean.wim", ImageIndex = 1, Description = "순정 Windows 11 Pro 공용 이미지" }
                });
                SaveImageCatalog();
            }
        }

        public void SaveImageCatalog()
        {
            string path = Path.Combine(ConfigDir, "image_catalog.json");
            try
            {
                File.WriteAllText(path, JsonSerializer.Serialize(ImageCatalog, JsonOptions));
            }
            catch (Exception ex) { Console.WriteLine("[-] image_catalog.json 저장 실패: " + ex.Message); }
        }

        public void LoadHistory()
        {
            string path = Path.Combine(ConfigDir, "history.json");
            try
            {
                if (File.Exists(path))
                {
                    History = JsonSerializer.Deserialize<List<DeployHistoryItem>>(File.ReadAllText(path), JsonOptions) ?? new();
                }
            }
            catch { History = new(); }
        }

        public void SaveHistory()
        {
            string path = Path.Combine(ConfigDir, "history.json");
            try
            {
                File.WriteAllText(path, JsonSerializer.Serialize(History, JsonOptions));
            }
            catch (Exception ex) { Console.WriteLine("[-] history.json 저장 실패: " + ex.Message); }
        }

        public void AddHistoryItem(DeployHistoryItem item)
        {
            History.Insert(0, item);
            if (History.Count > 100) History.RemoveRange(100, History.Count - 100);
            SaveHistory();
        }

        // 전체 설정 단일 JSON 파일 내보내기 (Export)
        public void ExportFullSettings(string targetFilePath)
        {
            var backup = new FullSettingsBackup
            {
                ServerConfig = ServerConfig,
                UiSettings = UiSettings,
                PartitionPolicies = PartitionPolicies,
                ImageCatalog = ImageCatalog
            };
            File.WriteAllText(targetFilePath, JsonSerializer.Serialize(backup, JsonOptions));
        }

        // 전체 설정 단일 JSON 파일 가져오기 (Import)
        public bool ImportFullSettings(string sourceFilePath)
        {
            if (!File.Exists(sourceFilePath)) return false;
            string json = File.ReadAllText(sourceFilePath);
            var backup = JsonSerializer.Deserialize<FullSettingsBackup>(json, JsonOptions);
            if (backup == null) return false;

            if (backup.ServerConfig != null) ServerConfig = backup.ServerConfig;
            if (backup.UiSettings != null) UiSettings = backup.UiSettings;
            if (backup.PartitionPolicies != null && backup.PartitionPolicies.Count > 0) PartitionPolicies = backup.PartitionPolicies;
            if (backup.ImageCatalog != null && backup.ImageCatalog.Count > 0) ImageCatalog = backup.ImageCatalog;

            SaveServerConfig();
            SaveUiSettings();
            SavePartitionPolicies();
            SaveImageCatalog();
            return true;
        }
    }
}
