using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using MinioCommon;

namespace FullSync
{
    /// <summary>
    /// FullSync — standalone tool that performs a full sync of all files
    /// in a configured folder to MinIO, by spawning SyncWorker.exe for each file.
    ///
    /// Usage:
    ///   FullSync.exe --config-id <id> [--configs-dir <path>] [--concurrency|-c <n>] [--worker-path <path>] [--logs-dir <path>]
    /// </summary>
    class Program
    {
        static int Main(string[] args)
        {
            var exeDir = AppDomain.CurrentDomain.BaseDirectory;
            var configPath = Path.Combine(exeDir, "config.json");
            var logsDir = Path.Combine(exeDir, "logs");
            var workerPath = Path.Combine(exeDir, "SyncWorker.exe");
            string configId = null;
            var concurrency = 10;

            // Parse arguments
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--config" && i + 1 < args.Length)
                    configPath = args[++i];
                else if (args[i] == "--logs-dir" && i + 1 < args.Length)
                    logsDir = args[++i];
                else if (args[i] == "--worker-path" && i + 1 < args.Length)
                    workerPath = args[++i];
                else if (args[i] == "--config-id" && i + 1 < args.Length)
                    configId = args[++i];
                else if ((args[i] == "--concurrency" || args[i] == "-c") && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], out concurrency);
                }
            }

            Logger.Initialize(logsDir, "fullsync");

            // Validate
            if (string.IsNullOrEmpty(configId))
            {
                Logger.Error("缺少必要参数: --config-id");
                Console.Error.WriteLine("用法: FullSync.exe --config-id <id> [--config <路径>] [--concurrency|-c <并发数>] [--worker-path <路径>] [--logs-dir <路径>]");
                return 1;
            }

            if (!File.Exists(workerPath))
            {
                Logger.Error($"未找到 SyncWorker.exe: {workerPath}");
                return 1;
            }

            // Load configs and find by ID
            var configManager = new ConfigManager(configPath);
            var configs = configManager.LoadAllConfigs();
            SyncConfig targetConfig = null;
            foreach (var c in configs)
            {
                if (string.Equals(c.Id, configId, StringComparison.OrdinalIgnoreCase))
                {
                    targetConfig = c;
                    break;
                }
            }

            if (targetConfig == null)
            {
                Logger.Error($"未找到配置 ID: {configId}");
                Logger.Error("可用配置 ID:");
                foreach (var c in configs)
                    Logger.Error($"  - {c.Id} ({c.LocalFolderPath})");
                if (configs.Count == 0)
                    Logger.Error("  (无有效配置)");
                return 1;
            }

            if (!Directory.Exists(targetConfig.LocalFolderPath))
            {
                Logger.Error($"目标文件夹不存在: {targetConfig.LocalFolderPath}");
                return 1;
            }

            Logger.Info($"============================================");
            Logger.Info($"FullSync 启动");
            Logger.Info($"  配置 ID:     {targetConfig.Id}");
            Logger.Info($"  目标文件夹:  {targetConfig.LocalFolderPath}");
            Logger.Info($"  存储桶:      {targetConfig.BucketName}");
            Logger.Info($"  MinIO 端点:  {targetConfig.MinIOEndpoint}");
            Logger.Info($"  并发数:      {(concurrency > 0 ? concurrency.ToString() : "不限")}");
            Logger.Info($"  Worker:      {workerPath}");
            Logger.Info($"============================================");

            // Collect all files
            var files = SyncHelper.CollectFiles(targetConfig.LocalFolderPath, targetConfig.FileExtensions, targetConfig.ExcludeSuffixes);

            Logger.Info($"共发现 {files.Count} 个文件");

            if (files.Count == 0)
            {
                Logger.Info("没有需要同步的文件，退出。");
                return 0;
            }

            // Spawn workers with concurrency limit
            var effectiveConcurrency = concurrency > 0 ? concurrency : int.MaxValue;
            using (var semaphore = new Semaphore(effectiveConcurrency, effectiveConcurrency))
            {
                var completed = 0;
                var failed = 0;
                var exceptions = 0;
                var total = files.Count;
                var allDone = new ManualResetEvent(false);

                foreach (var fullPath in files)
                {
                    semaphore.WaitOne();

                    var relativePath = SyncHelper.GetRelativePath(targetConfig.LocalFolderPath, fullPath);
                    var capturedPath = fullPath;
                    var capturedRelative = relativePath;

                    ThreadPool.QueueUserWorkItem(state =>
                    {
                        try
                        {
                            var taskId = Guid.NewGuid().ToString("N").Substring(0, 8);
                            Logger.Info($"[{taskId}] 开始上传: {capturedRelative}");

                            var exitCode = SyncHelper.SpawnWorkerAndWait(
                                workerPath, targetConfig, capturedPath, capturedRelative, "upload", taskId);

                            if (exitCode == 0)
                            {
                                Interlocked.Increment(ref completed);
                                Logger.Info($"[{taskId}] 上传成功: {capturedRelative}");
                            }
                            else
                            {
                                Interlocked.Increment(ref failed);
                                Logger.Warn($"[{taskId}] 上传失败: {capturedRelative} (exit={exitCode})");
                            }
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref exceptions);
                            Logger.Error($"上传异常: {capturedRelative}", ex);
                        }
                        finally
                        {
                            var done = completed + failed + exceptions;
                            if (done % Math.Max(1, total / 20) == 0 || done == total)
                            {
                                Logger.Info($"进度: {done}/{total} (成功: {completed}, 失败: {failed}, 异常: {exceptions})");
                            }

                            semaphore.Release();
                            if (done >= total)
                                allDone.Set();
                        }
                    });
                }

                allDone.WaitOne();

                Logger.Info($"============================================");
                Logger.Info($"FullSync 完成");
                Logger.Info($"  总计:  {total}");
                Logger.Info($"  成功:  {completed}");
                Logger.Info($"  失败:  {failed}");
                Logger.Info($"  异常:  {exceptions}");
                Logger.Info($"============================================");

                return failed > 0 || exceptions > 0 ? 1 : 0;
            }
        }

    }
}
