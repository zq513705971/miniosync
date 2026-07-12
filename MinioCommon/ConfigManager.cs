using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace MinioCommon
{
    /// <summary>
    /// Loads and validates JSON config from a single configuration file
    /// containing an array of sync task definitions.
    /// </summary>
    public class ConfigManager
    {
        private readonly string _configFilePath;

        public ConfigManager(string configFilePath)
        {
            _configFilePath = configFilePath;
        }

        /// <summary>
        /// Loads all valid configs from the single configuration file.
        /// The file must contain a JSON array of SyncConfig objects.
        /// Invalid configs are logged and skipped.
        /// </summary>
        public List<SyncConfig> LoadAllConfigs()
        {
            var configs = new List<SyncConfig>();

            if (!File.Exists(_configFilePath))
            {
                Logger.Warn($"配置文件不存在: {_configFilePath}");
                return configs;
            }

            Logger.Info($"加载配置文件: {_configFilePath}");

            try
            {
                var json = File.ReadAllText(_configFilePath);
                var configFile = JsonConvert.DeserializeObject<ConfigFile>(json);

                if (configFile?.Configs == null || configFile.Configs.Count == 0)
                {
                    Logger.Warn($"配置文件中没有有效的配置项: {_configFilePath}");
                    return configs;
                }

                Logger.Info($"配置文件版本: {configFile.Version}");

                var configDir = Path.GetDirectoryName(Path.GetFullPath(_configFilePath));

                foreach (var config in configFile.Configs)
                {
                    if (config == null) continue;

                    // Resolve relative LocalFolderPath to absolute (relative to config file location)
                    if (!string.IsNullOrEmpty(config.LocalFolderPath) && !Path.IsPathRooted(config.LocalFolderPath))
                    {
                        config.LocalFolderPath = Path.GetFullPath(Path.Combine(
                            configDir, config.LocalFolderPath));
                    }

                    var validationError = config.Validate();
                    if (validationError != null)
                    {
                        Logger.Warn($"配置 '{config.Id ?? "(unnamed)"}' 无效: {validationError}");
                        continue;
                    }

                    configs.Add(config);
                    Logger.Info($"已加载配置: {config.Id} -> {config.LocalFolderPath} -> {config.MinIOEndpoint}/{config.BucketName}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"加载配置文件出错 '{_configFilePath}': {ex.Message}");
            }

            return configs;
        }
    }
}
