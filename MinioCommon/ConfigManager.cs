using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MinioCommon;

namespace MinioCommon
{
    /// <summary>
    /// Loads and validates JSON config from a single configuration file
    /// containing an array of sync task definitions.
    ///
    /// Migrated from Newtonsoft.Json to System.Text.Json.
    /// Deserialization goes through <see cref="AppJsonContext"/> which is
    /// source-generated at build time (no runtime reflection).
    /// </summary>
    public class ConfigManager
    {
        private readonly string _configFilePath;
        private EmailSettings _emailSettings;

        /// <summary>
        /// SMTP email settings loaded from the config file (root.Email).
        /// Null when not configured.
        /// </summary>
        public EmailSettings EmailSettings => _emailSettings;

        public ConfigManager(string configFilePath)
        {
            _configFilePath = configFilePath;
        }

        /// <summary>
        /// Loads all valid configs from the single configuration file.
        /// The file must contain a JSON object with Version + Configs (see ConfigFile).
        /// Invalid configs are logged and skipped.
        /// MinIOProfile references are resolved before returning.
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

                var options = AppJsonContext.Default.Options;
                var configFile = JsonSerializer.Deserialize<ConfigFile>(json, options);

                if (configFile?.Configs == null || configFile.Configs.Count == 0)
                {
                    Logger.Warn($"配置文件中没有有效的配置项: {_configFilePath}");
                    return configs;
                }

                // Store email settings so callers can access them
                _emailSettings = configFile.Email;

                Logger.Info($"配置文件版本: {configFile.Version}");

                var configDir = Path.GetDirectoryName(Path.GetFullPath(_configFilePath));

                foreach (var config in configFile.Configs)
                {
                    if (config == null) continue;

                    // ---- Resolve MinIOProfile reference ----
                    if (!string.IsNullOrEmpty(config.MinIOProfile))
                    {
                        if (configFile.MinIOProfiles != null &&
                            configFile.MinIOProfiles.TryGetValue(config.MinIOProfile, out var profile))
                        {
                            if (string.IsNullOrEmpty(config.MinIOEndpoint))
                                config.MinIOEndpoint = profile.Endpoint;
                            if (string.IsNullOrEmpty(config.BucketName))
                                config.BucketName = profile.BucketName;
                            if (string.IsNullOrEmpty(config.AccessKey))
                                config.AccessKey = profile.AccessKey;
                            if (string.IsNullOrEmpty(config.SecretKey))
                                config.SecretKey = profile.SecretKey;
                            Logger.Info($"配置 '{config.Id}': 已从 MinIOProfile '{config.MinIOProfile}' 解析连接信息");
                        }
                        else
                        {
                            Logger.Warn($"配置 '{config.Id}': 引用的 MinIOProfile '{config.MinIOProfile}' 不存在，使用内联字段");
                        }
                    }

                    // Resolve relative LocalFolderPath to absolute
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

                if (_emailSettings != null)
                {
                    Logger.Info($"邮件通知已配置 (SMTP: {_emailSettings.SmtpServer}:{_emailSettings.SmtpPort})");
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
