using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MinioCommon;

namespace FullSync
{
    /// <summary>
    /// FullSync — 一次性全量同步工具。
    /// **进程内多线程执行**：直接调用 MinioCommon.MinioUploader，通过 SemaphoreSlim 限并发，
    /// 不再 spawn SyncWorker.exe 子进程。
    ///
    /// 两种文件来源模式：
    ///   1. 目录扫描（默认）：按配置中的 LocalFolderPath 全量扫描
    ///   2. 文件列表（--list）：按列表文件每行一个完整路径逐个处理
    ///
    /// 用法：
    ///   FullSync.exe --config-id &lt;id&gt;
    ///                 [--config &lt;path&gt;]
    ///                 [--list &lt;file-list-path&gt;]
    ///                 [--concurrency|-c &lt;n&gt;]
    ///                 [--logs-dir &lt;path&gt;]
    /// </summary>
    class Program
    {
        static int Main(string[] args)
        {
            var exeDir = AppDomain.CurrentDomain.BaseDirectory;
            var configPath = Path.Combine(exeDir, "config.json");
            var logsDir = Path.Combine(exeDir, "logs");
            string configId = null;
            string listPath = null;
            var cliConcurrency = 0; // 0 = use config.MaxConcurrentUploads

            // Parse arguments
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--config" && i + 1 < args.Length)
                    configPath = args[++i];
                else if (args[i] == "--logs-dir" && i + 1 < args.Length)
                    logsDir = args[++i];
                else if (args[i] == "--config-id" && i + 1 < args.Length)
                    configId = args[++i];
                else if (args[i] == "--list" && i + 1 < args.Length)
                    listPath = args[++i];
                else if ((args[i] == "--concurrency" || args[i] == "-c") && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], out cliConcurrency);
                }
            }

            Logger.Initialize(logsDir, "fullsync");
            ErrorLog.Initialize(logsDir);

            if (string.IsNullOrEmpty(configId))
            {
                Logger.Error("缺少必要参数: --config-id");
                Console.Error.WriteLine("用法: FullSync.exe --config-id <id> [--config <路径>] [--list <文件列表>] [--concurrency|-c <并发数>] [--logs-dir <路径>]");
                return 1;
            }

            // Load configs and find by ID
            var configManager = new ConfigManager(configPath);
            var configs = configManager.LoadAllConfigs();
            var emailSettings = configManager.EmailSettings;
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

            // Resolve effective concurrency: --concurrency CLI flag wins, else config.
            var effectiveConcurrency = cliConcurrency > 0
                ? cliConcurrency
                : (targetConfig.MaxConcurrentUploads > 0 ? targetConfig.MaxConcurrentUploads : 10);
            var concurrencyInfo = effectiveConcurrency > 0 ? effectiveConcurrency.ToString() : "不限";

            // Collect files: list mode OR directory scan mode.
            List<string> files;
            string sourceDescription;
            if (!string.IsNullOrEmpty(listPath))
            {
                files = LoadFileList(listPath, targetConfig);
                sourceDescription = $"文件列表: {listPath}";
            }
            else
            {
                if (!Directory.Exists(targetConfig.LocalFolderPath))
                {
                    Logger.Error($"目标文件夹不存在: {targetConfig.LocalFolderPath}");
                    return 1;
                }
                files = SyncHelper.CollectFiles(targetConfig.LocalFolderPath,
                    targetConfig.FileExtensions, targetConfig.ExcludeSuffixes);
                sourceDescription = $"目录扫描: {targetConfig.LocalFolderPath}";
            }

            Logger.Info("============================================");
            Logger.Info($"FullSync 启动");
            Logger.Info($"  配置 ID:    {targetConfig.Id}");
            Logger.Info($"  文件来源:   {sourceDescription}");
            Logger.Info($"  存储桶:     {targetConfig.BucketName}");
            Logger.Info($"  MinIO 端点: {targetConfig.MinIOEndpoint}");
            Logger.Info($"  并发数:     {concurrencyInfo}");
            Logger.Info($"  文件数:     {files.Count}");
            Logger.Info("============================================");

            if (files.Count == 0)
            {
                Logger.Info("没有需要同步的文件，退出。");
                return 0;
            }

            // Build a single MinioUploader instance, reused across all uploads
            // (the underlying IMinioClient is thread-safe).
            var uploader = new MinioUploader(
                endpoint: targetConfig.MinIOEndpoint,
                bucket: targetConfig.BucketName,
                accessKey: targetConfig.AccessKey,
                secretKey: targetConfig.SecretKey,
                pathPrefix: targetConfig.PathPrefix ?? "");

            var semaphoreMax = effectiveConcurrency > 0 ? effectiveConcurrency : int.MaxValue;
            using (var semaphore = new SemaphoreSlim(semaphoreMax, semaphoreMax))
            {
                int completed = 0, failed = 0, exceptions = 0;
                int total = files.Count;
                int doneCount = 0;
                var allDone = new ManualResetEvent(false);
                var ct = CancellationToken.None;

                foreach (var fullPath in files)
                {
                    var capturedPath = fullPath;
                    var relativePath = SyncHelper.GetRelativePath(targetConfig.LocalFolderPath, fullPath);
                    var objectKey = relativePath.Replace('\\', '/');

                    ThreadPool.QueueUserWorkItem(async _ =>
                    {
                        await semaphore.WaitAsync().ConfigureAwait(false);
                        try
                        {
                            var ok = await uploader.UploadFileAsync(capturedPath, objectKey, ct).ConfigureAwait(false);
                            if (ok) Interlocked.Increment(ref completed);
                            else
                            {
                                Interlocked.Increment(ref failed);
                                ErrorLog.Record(configId, relativePath);
                            }
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref exceptions);
                            ErrorLog.Record(configId, relativePath);
                            Logger.Error($"上传异常: {objectKey}", ex);
                        }
                        finally
                        {
                            var done = Interlocked.Increment(ref doneCount);
                            if (done % Math.Max(1, total / 20) == 0 || done == total)
                            {
                                Logger.Info($"进度: {done}/{total} (成功: {completed}, 失败: {failed}, 异常: {exceptions})");
                            }
                            semaphore.Release();
                            if (done >= total) allDone.Set();
                        }
                    });
                }

                allDone.WaitOne();

                Logger.Info("============================================");
                Logger.Info($"FullSync 完成");
                Logger.Info($"  总计:  {total}");
                Logger.Info($"  成功:  {completed}");
                Logger.Info($"  失败:  {failed}");
                Logger.Info($"  异常:  {exceptions}");
                Logger.Info("============================================");

                if (ErrorLog.RecordedCount > 0)
                {
                    Logger.Info($"失败文件路径已记录到: {ErrorLog.GetErrorFilePath(configId)}");
                    Logger.Info($"重试命令: FullSync.exe --config-id {configId} --list \"{ErrorLog.GetErrorFilePath(configId)}\"");

                    // Send summary email on failures
                    if (emailSettings != null && targetConfig.NotifyEmails != null && targetConfig.NotifyEmails.Length > 0)
                    {
                        var subject = $"MinioSync 全量同步完成 - {configId} - 失败 {failed + exceptions}/{total}";
                        var body = $"MinioSync 全量同步结果通知\n" +
                                   $"========================\n" +
                                   $"时间:     {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                                   $"配置 ID:  {configId}\n" +
                                   $"总计:     {total}\n" +
                                   $"成功:     {completed}\n" +
                                   $"失败:     {failed}\n" +
                                   $"异常:     {exceptions}\n" +
                                   $"错误日志: {ErrorLog.GetErrorFilePath(configId)}\n" +
                                   $"========================\n" +
                                   $"此邮件由 MinioSync 自动发送。";
                        _ = EmailNotifier.SendAlertAsync(emailSettings, targetConfig.NotifyEmails, subject, body);
                    }
                }

                return failed > 0 || exceptions > 0 ? 1 : 0;
            }
        }

        /// <summary>
        /// Reads a file list (one path per line) and returns the resolved absolute
        /// file paths. Supports both absolute paths and paths relative to the config's
        /// LocalFolderPath. Blank lines and lines starting with '#' are treated as
        /// comments. Missing files are logged and skipped.
        ///
        /// Relative paths are resolved against <paramref name="config.LocalFolderPath"/>
        /// so that error log files (which store relative paths) can be fed directly
        /// into --list for retry.
        /// </summary>
        private static List<string> LoadFileList(string listPath, SyncConfig config)
        {
            if (!File.Exists(listPath))
            {
                Logger.Error($"文件列表不存在: {listPath}");
                return new List<string>();
            }

            var baseDir = config.LocalFolderPath;
            var result = new List<string>();
            int skippedMissing = 0;
            int skippedIgnored = 0;
            int skippedBlank = 0;
            int skippedComment = 0;

            foreach (var rawLine in File.ReadAllLines(listPath))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line))
                {
                    skippedBlank++;
                    continue;
                }
                if (line.StartsWith("#"))
                {
                    skippedComment++;
                    continue;
                }

                // Resolve relative paths against the config's LocalFolderPath.
                var resolvedPath = Path.IsPathRooted(line)
                    ? line
                    : Path.Combine(baseDir, line);

                if (!File.Exists(resolvedPath))
                {
                    Logger.Warn($"列表中文件不存在，跳过: {line}");
                    skippedMissing++;
                    continue;
                }

                if (SyncHelper.ShouldIgnore(resolvedPath, config.FileExtensions, config.ExcludeSuffixes))
                {
                    Logger.Info($"列表中文件被排除后缀/扩展名过滤，跳过: {line}");
                    skippedIgnored++;
                    continue;
                }

                result.Add(resolvedPath);
            }

            Logger.Info($"读取文件列表 {listPath}: 有效={result.Count}, 跳过(不存在)={skippedMissing}, 跳过(过滤)={skippedIgnored}, 跳过(空行)={skippedBlank}, 跳过(注释)={skippedComment}");
            return result;
        }
    }
}