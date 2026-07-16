using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MinioCommon;

namespace Minio2MinioSync
{
    /// <summary>
    /// Minio2MinioSync (m2ms.exe) — 一次性 MinIO 到 MinIO 全量同步工具。
    /// **进程内多线程执行**：直接调用 MinioCommon.MinioReader (源) 和 MinioUploader (目标),
    /// 通过 SemaphoreSlim 限并发, 不 spawn 子进程.
    ///
    /// 用法:
    ///   m2ms.exe --source &lt;源配置ID&gt; --target &lt;目标配置ID&gt;
    ///             [--config &lt;路径&gt;]
    ///             [--concurrency|-c &lt;n&gt;]
    ///             [--logs-dir &lt;路径&gt;]
    ///
    /// 源/目标配置复用同一份 config.json 中的现有 Config 条目,
    /// --source/--target 各自指定 config-id, 取该配置中的 MinIOEndpoint/BucketName/AccessKey/SecretKey/PathPrefix.
    ///
    /// 工作流:
    ///   1. 用 MinioReader 列出源 bucket 下所有对象 (受源 PathPrefix 过滤)
    ///   2. 对每个对象: 下载 (源) → 上传 (目标, 加目标 PathPrefix)
    ///   3. 上传失败的对象路径写入错误日志 (logs/error-...-{target-config-id}-{pid}.txt)
    /// </summary>
    class Program
    {
        static int Main(string[] args)
        {
            var exeDir = AppDomain.CurrentDomain.BaseDirectory;
            var configPath = Path.Combine(exeDir, "config.json");
            var logsDir = Path.Combine(exeDir, "logs");
            string sourceId = null;
            string targetId = null;
            var cliConcurrency = 0; // 0 = use config.MaxConcurrentUploads

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--config" && i + 1 < args.Length)
                    configPath = args[++i];
                else if (args[i] == "--logs-dir" && i + 1 < args.Length)
                    logsDir = args[++i];
                else if (args[i] == "--source" && i + 1 < args.Length)
                    sourceId = args[++i];
                else if (args[i] == "--target" && i + 1 < args.Length)
                    targetId = args[++i];
                else if ((args[i] == "--concurrency" || args[i] == "-c") && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], out cliConcurrency);
                }
            }

            Logger.Initialize(logsDir, "m2ms");
            ErrorLog.Initialize(logsDir);

            if (string.IsNullOrEmpty(sourceId) || string.IsNullOrEmpty(targetId))
            {
                Logger.Error("缺少必要参数: --source 和 --target");
                Console.Error.WriteLine("用法: m2ms.exe --source <源配置ID> --target <目标配置ID> [--config <路径>] [--concurrency|-c <并发数>] [--logs-dir <路径>]");
                return 1;
            }

            if (string.Equals(sourceId, targetId, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Error("--source 和 --target 不能是同一个配置 ID");
                return 1;
            }

            var configManager = new ConfigManager(configPath);
            var configs = configManager.LoadAllConfigs();
            SyncConfig sourceConfig = null;
            SyncConfig targetConfig = null;
            foreach (var c in configs)
            {
                if (string.Equals(c.Id, sourceId, StringComparison.OrdinalIgnoreCase))
                    sourceConfig = c;
                else if (string.Equals(c.Id, targetId, StringComparison.OrdinalIgnoreCase))
                    targetConfig = c;
            }

            if (sourceConfig == null)
            {
                Logger.Error($"未找到源配置 ID: {sourceId}");
                LogAvailableIds(configs);
                return 1;
            }
            if (targetConfig == null)
            {
                Logger.Error($"未找到目标配置 ID: {targetId}");
                LogAvailableIds(configs);
                return 1;
            }

            var sourceError = ValidateMinIOConfig(sourceConfig, "源");
            if (sourceError != null) { Logger.Error(sourceError); return 1; }
            var targetError = ValidateMinIOConfig(targetConfig, "目标");
            if (targetError != null) { Logger.Error(targetError); return 1; }

            var effectiveConcurrency = cliConcurrency > 0
                ? cliConcurrency
                : (targetConfig.MaxConcurrentUploads > 0 ? targetConfig.MaxConcurrentUploads : 10);
            var concurrencyInfo = effectiveConcurrency > 0 ? effectiveConcurrency.ToString() : "不限";

            var sourcePrefix = sourceConfig.PathPrefix ?? "";
            var targetPrefix = targetConfig.PathPrefix ?? "";

            Logger.Info("============================================");
            Logger.Info($"Minio2MinioSync 启动");
            Logger.Info($"  源配置 ID:  {sourceConfig.Id}");
            Logger.Info($"    MinIO:    {sourceConfig.MinIOEndpoint}");
            Logger.Info($"    存储桶:   {sourceConfig.BucketName}");
            Logger.Info($"    路径前缀: {(string.IsNullOrEmpty(sourcePrefix) ? "(无)" : sourcePrefix)}");
            Logger.Info($"  目标配置 ID: {targetConfig.Id}");
            Logger.Info($"    MinIO:    {targetConfig.MinIOEndpoint}");
            Logger.Info($"    存储桶:   {targetConfig.BucketName}");
            Logger.Info($"    路径前缀: {(string.IsNullOrEmpty(targetPrefix) ? "(无)" : targetPrefix)}");
            Logger.Info($"  并发数:     {concurrencyInfo}");
            Logger.Info("============================================");

            // List all source objects (already includes source PathPrefix)
            var reader = new MinioReader(
                endpoint: sourceConfig.MinIOEndpoint,
                bucket: sourceConfig.BucketName,
                accessKey: sourceConfig.AccessKey,
                secretKey: sourceConfig.SecretKey,
                pathPrefix: sourcePrefix);

            Logger.Info($"列出源 bucket '{sourceConfig.BucketName}' 的对象...");
            var sourceKeys = ListAllObjectsWithRetry(reader, sourcePrefix, maxAttempts: 3).GetAwaiter().GetResult();
            Logger.Info($"源对象数: {sourceKeys.Count}");

            if (sourceKeys.Count == 0)
            {
                Logger.Info("源 bucket 中没有匹配前缀的对象，退出。");
                return 0;
            }

            // Transform source keys → target keys: strip source prefix, prepend target prefix
            var sourceStrip = sourcePrefix.Replace('\\', '/').TrimEnd('/');
            if (!string.IsNullOrEmpty(sourceStrip)) sourceStrip += "/";
            var targetStrip = targetPrefix.Replace('\\', '/').TrimEnd('/');
            if (!string.IsNullOrEmpty(targetStrip)) targetStrip += "/";

            // Build work items: (full source key → target key)
            var items = new List<(string SourceKey, string TargetKey)>(sourceKeys.Count);
            foreach (var sourceKey in sourceKeys)
            {
                string logical = sourceKey;
                if (!string.IsNullOrEmpty(sourceStrip) && logical.StartsWith(sourceStrip, StringComparison.OrdinalIgnoreCase))
                    logical = logical.Substring(sourceStrip.Length);
                var targetKey = string.IsNullOrEmpty(targetStrip) ? logical : targetStrip + logical;
                items.Add((sourceKey, targetKey));
            }

            // Build target uploader (reused across all uploads, thread-safe)
            var uploader = new MinioUploader(
                endpoint: targetConfig.MinIOEndpoint,
                bucket: targetConfig.BucketName,
                accessKey: targetConfig.AccessKey,
                secretKey: targetConfig.SecretKey,
                pathPrefix: targetPrefix);

            var semaphoreMax = effectiveConcurrency > 0 ? effectiveConcurrency : int.MaxValue;
            using (var semaphore = new SemaphoreSlim(semaphoreMax, semaphoreMax))
            {
                int completed = 0, failed = 0, exceptions = 0;
                int total = items.Count;
                int doneCount = 0;
                var allDone = new ManualResetEvent(false);
                var ct = CancellationToken.None;

                foreach (var item in items)
                {
                    var captured = item;
                    var logicalKey = !string.IsNullOrEmpty(sourceStrip) && captured.SourceKey.StartsWith(sourceStrip, StringComparison.OrdinalIgnoreCase)
                        ? captured.SourceKey.Substring(sourceStrip.Length)
                        : captured.SourceKey;
                    var relativeKey = logicalKey.Replace('/', '\\');

                    ThreadPool.QueueUserWorkItem(async _ =>
                    {
                        await semaphore.WaitAsync().ConfigureAwait(false);
                        try
                        {
                            using var data = await reader.GetObjectAsync(captured.SourceKey, ct).ConfigureAwait(false);
                            if (data == null)
                            {
                                Interlocked.Increment(ref failed);
                                ErrorLog.Record(targetConfig.Id, relativeKey);
                            }
                            else
                            {
                                data.Position = 0;
                                var ok = await UploadStreamAsync(uploader, captured.TargetKey, data, ct).ConfigureAwait(false);
                                if (ok) Interlocked.Increment(ref completed);
                                else
                                {
                                    Interlocked.Increment(ref failed);
                                    ErrorLog.Record(targetConfig.Id, relativeKey);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref exceptions);
                            ErrorLog.Record(targetConfig.Id, relativeKey);
                            Logger.Error($"复制异常: {captured.SourceKey} → {captured.TargetKey}", ex);
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
                Logger.Info($"Minio2MinioSync 完成");
                Logger.Info($"  总计:  {total}");
                Logger.Info($"  成功:  {completed}");
                Logger.Info($"  失败:  {failed}");
                Logger.Info($"  异常:  {exceptions}");
                Logger.Info("============================================");

                if (ErrorLog.RecordedCount > 0)
                {
                    Logger.Info($"失败对象路径已记录到: {ErrorLog.GetErrorFilePath(targetConfig.Id)}");
                    Logger.Info($"错误日志文件名包含目标配置 ID '{targetConfig.Id}'，便于识别是哪个复制任务失败。");
                }

                return failed > 0 || exceptions > 0 ? 1 : 0;
            }
        }

        /// <summary>
        /// Lists all source objects, retrying with exponential backoff on failure
        /// (network blips are common during cross-region MinIO sync).
        /// </summary>
        private static async Task<List<string>> ListAllObjectsWithRetry(MinioReader reader, string prefix, int maxAttempts)
        {
            var delayMs = 1000;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var keys = await reader.ListAllObjectsAsync(prefix, CancellationToken.None).ConfigureAwait(false);
                if (keys.Count > 0 || attempt == maxAttempts)
                    return keys;
                Logger.Warn($"源对象列表为空或失败, {delayMs}ms 后重试 ({attempt}/{maxAttempts})");
                await Task.Delay(delayMs).ConfigureAwait(false);
                delayMs *= 2;
            }
            return new List<string>();
        }

        /// <summary>
        /// Uploads an in-memory stream to the target MinIO using MinioUploader.
        /// The uploader's UploadFileAsync takes a file path, so we wrap the stream
        /// in a temp file path. Since MinioUploader doesn't expose a stream API,
        /// this helper materializes the memory stream to a temp file.
        /// </summary>
        private static async Task<bool> UploadStreamAsync(MinioUploader uploader, string objectKey, MemoryStream data, CancellationToken ct)
        {
            // Write the memory stream to a temp file, upload, then delete.
            // This avoids extending MinioUploader's API surface.
            var tempPath = Path.Combine(Path.GetTempPath(), $"m2ms_{Guid.NewGuid():N}.tmp");
            try
            {
                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    data.Position = 0;
                    await data.CopyToAsync(fs, ct).ConfigureAwait(false);
                }
                return await uploader.UploadFileAsync(tempPath, objectKey, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Error($"上传流到 '{objectKey}' 失败: {ex.Message}", ex);
                return false;
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* ignore */ }
            }
        }

        private static string ValidateMinIOConfig(SyncConfig cfg, string label)
        {
            if (string.IsNullOrWhiteSpace(cfg.MinIOEndpoint))
                return $"{label}配置 '{cfg.Id}' 缺少 MinIOEndpoint";
            if (string.IsNullOrWhiteSpace(cfg.BucketName))
                return $"{label}配置 '{cfg.Id}' 缺少 BucketName";
            if (string.IsNullOrWhiteSpace(cfg.AccessKey))
                return $"{label}配置 '{cfg.Id}' 缺少 AccessKey";
            if (string.IsNullOrWhiteSpace(cfg.SecretKey))
                return $"{label}配置 '{cfg.Id}' 缺少 SecretKey";
            return null;
        }

        private static void LogAvailableIds(List<SyncConfig> configs)
        {
            Logger.Error("可用配置 ID:");
            foreach (var c in configs)
                Logger.Error($"  - {c.Id} (MinIO: {c.MinIOEndpoint}, Bucket: {c.BucketName})");
            if (configs.Count == 0)
                Logger.Error("  (无有效配置)");
        }
    }
}