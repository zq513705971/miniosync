using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using MinioCommon;

namespace MinioSync
{
    class Program
    {
        private static readonly List<FolderMonitor> _monitors = new List<FolderMonitor>();
        private static bool _running = true;

        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Logger.Error("未处理的异常", e.ExceptionObject as Exception);
            };

            var exeDir = AppDomain.CurrentDomain.BaseDirectory;
            var configPath = Path.Combine(exeDir, "config.json");
            var logsDir = Path.Combine(exeDir, "logs");

            // Parse optional CLI args
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--config" && i + 1 < args.Length)
                    configPath = args[++i];
                else if (args[i] == "--logs-dir" && i + 1 < args.Length)
                    logsDir = args[++i];
            }

            Logger.Initialize(logsDir, "mms");
            ErrorLog.Initialize(logsDir);

            Logger.Info("============================================");
            Logger.Info("MinioSync 守护进程已启动");
            Logger.Info($"配置文件: {configPath}");
            Logger.Info($"日志目录: {logsDir}");
            Logger.Info("============================================");

            // Load configs
            var configManager = new ConfigManager(configPath);
            var configs = configManager.LoadAllConfigs();
            var emailSettings = configManager.EmailSettings;

            if (configs.Count == 0)
            {
                Logger.Warn("未加载有效配置。请创建 config.json 文件。");
                Logger.Warn("格式示例:");
                Logger.Warn("  {\"Version\":1,\"Configs\":[{\"Id\":\"my-project\",\"LocalFolderPath\":\"C:\\Data\",\"MinIOEndpoint\":\"http://localhost:9000\",\"BucketName\":\"bucket\",\"AccessKey\":\"key\",\"SecretKey\":\"secret\",\"SyncIntervalSeconds\":60,\"Enable\":true}]}");
            }

            // Start monitors (skip disabled configs and remote-only configs)
            var started = 0;
            foreach (var config in configs)
            {
                if (!config.Enable)
                {
                    Logger.Info($"  [{config.Id}] 已禁用，跳过");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(config.LocalFolderPath))
                {
                    Logger.Info($"  [{config.Id}] 远程配置(无本地文件夹)，跳过监控");
                    continue;
                }

                try
                {
                    var monitor = new FolderMonitor(config, emailSettings);
                    _monitors.Add(monitor);
                    started++;
                }
                catch (Exception ex)
                {
                    Logger.Error($"  [{config.Id}] 启动失败", ex);
                }
            }

            if (started > 0)
                Logger.Info($"共 {started} 个监控已启动，按 Ctrl+C 停止。");
            else
                Logger.Warn("没有启动任何监控。请检查配置。");

            // Handle Ctrl+C graceful shutdown
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                Logger.Info("已请求关闭...");
                _running = false;
            };

            while (_running)
            {
                Thread.Sleep(1000);
            }

            Shutdown();
        }

        private static void Shutdown()
        {
            Logger.Info("正在关闭监控器...");
            foreach (var monitor in _monitors)
            {
                monitor.Dispose();
            }
            _monitors.Clear();
            Logger.Info("MinioSync 守护进程已停止。");
        }
    }
}