using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MinioCommon;

namespace SyncWorker
{
    /// <summary>
    /// SyncWorker — processes ONE file operation, then exits.
    /// All parameters come from CLI args (no config file access).
    /// Concurrency is managed by the daemon (MinioSync).
    ///
    /// Migrated from .NET Framework 4.6.1 / sync API to .NET 8 / async Task&lt;int&gt; Main.
    /// </summary>
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var logsDir = Path.Combine(baseDir, "logs");
            Logger.Initialize(logsDir, "worker");

            var tag = "";

            try
            {
                // Parse arguments
                string endpoint = null;
                string bucket = null;
                string accessKey = null;
                string secretKey = null;
                string filePath = null;
                string relativePath = null;
                string action = "upload";
                string taskId = null;
                string pathPrefix = "";

                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--endpoint" && i + 1 < args.Length)
                        endpoint = args[++i];
                    else if (args[i] == "--bucket" && i + 1 < args.Length)
                        bucket = args[++i];
                    else if (args[i] == "--access-key" && i + 1 < args.Length)
                        accessKey = args[++i];
                    else if (args[i] == "--secret-key" && i + 1 < args.Length)
                        secretKey = args[++i];
                    else if (args[i] == "--file" && i + 1 < args.Length)
                        filePath = args[++i];
                    else if (args[i] == "--relative" && i + 1 < args.Length)
                        relativePath = args[++i];
                    else if (args[i] == "--action" && i + 1 < args.Length)
                        action = args[++i].ToLowerInvariant();
                    else if (args[i] == "--task-id" && i + 1 < args.Length)
                        taskId = args[++i];
                    else if (args[i] == "--path-prefix" && i + 1 < args.Length)
                        pathPrefix = args[++i];
                }

                tag = string.IsNullOrEmpty(taskId) ? "" : $"[{taskId}] ";

                // Validate
                if (string.IsNullOrEmpty(endpoint)) { Logger.Error($"{tag}缺少必要参数: --endpoint"); return 1; }
                if (string.IsNullOrEmpty(bucket)) { Logger.Error($"{tag}缺少必要参数: --bucket"); return 1; }
                if (string.IsNullOrEmpty(accessKey)) { Logger.Error($"{tag}缺少必要参数: --access-key"); return 1; }
                if (string.IsNullOrEmpty(secretKey)) { Logger.Error($"{tag}缺少必要参数: --secret-key"); return 1; }
                if (action != "delete-prefix" && string.IsNullOrEmpty(filePath)) { Logger.Error($"{tag}缺少必要参数: --file"); return 1; }
                if (string.IsNullOrEmpty(relativePath)) { Logger.Error($"{tag}缺少必要参数: --relative"); return 1; }

                if (action != "upload" && action != "delete" && action != "delete-prefix")
                {
                    Logger.Error($"{tag}无效操作 '{action}'，必须为 upload、delete 或 delete-prefix。");
                    return 1;
                }

                endpoint = endpoint.TrimEnd('/');
                Logger.Info($"{tag}SyncWorker: action={action}, file={relativePath}");

                var uploader = new MinioUploader(endpoint, bucket, accessKey, secretKey, pathPrefix: pathPrefix, tag: tag);

                // SyncWorker is a one-shot process: no cooperative cancellation in this entry point.
                // (Ctrl+C is irrelevant — the parent daemon has already torn us down by then.)
                var ct = CancellationToken.None;

                bool success;
                if (action == "delete-prefix")
                {
                    // prefix comes from --relative (converted to forward slashes)
                    var prefix = relativePath.Replace('\\', '/');
                    success = await uploader.DeleteObjectsByPrefixAsync(prefix, ct).ConfigureAwait(false);
                }
                else if (action == "delete")
                {
                    var objectKey = relativePath.Replace('\\', '/');
                    success = await uploader.DeleteObjectAsync(objectKey, ct).ConfigureAwait(false);
                }
                else
                {
                    var objectKey = relativePath.Replace('\\', '/');
                    if (!File.Exists(filePath))
                    {
                        Logger.Warn($"{tag}文件不存在，跳过: {relativePath}");
                        return 0;
                    }
                    success = await uploader.UploadFileAsync(filePath, objectKey, ct).ConfigureAwait(false);
                }

                Logger.Info($"{tag}SyncWorker 完成: {(success ? "成功" : "失败")}");
                return success ? 0 : 1;
            }
            catch (Exception ex)
            {
                Logger.Error($"{tag}SyncWorker 异常", ex);
                return 1;
            }
        }
    }
}
