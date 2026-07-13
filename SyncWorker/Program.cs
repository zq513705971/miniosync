using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MinioCommon;

namespace SyncWorker
{
    /// <summary>
    /// SyncWorker — single-file CLI tool that performs ONE MinIO operation, then exits.
    ///
    /// 用途：供外部脚本/工具手工调用，一次处理一个文件（或一次目录级删除）。
    /// 守护进程 MinioSync.exe 和全量同步 FullSync.exe 都不再调用本程序，
    /// 它们在自己的进程内通过 MinioCommon.MinioUploader 多线程处理。
    ///
    /// 用法：
    ///   SyncWorker.exe --endpoint URL --bucket NAME --access-key K --secret-key K
    ///                    --file PATH --relative REL [--action upload|delete|delete-prefix]
    ///                    [--path-prefix PREFIX] [--task-id ID]
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

                // Validate required args
                if (string.IsNullOrEmpty(endpoint))   { Logger.Error($"{tag}缺少必要参数: --endpoint");   return 1; }
                if (string.IsNullOrEmpty(bucket))     { Logger.Error($"{tag}缺少必要参数: --bucket");     return 1; }
                if (string.IsNullOrEmpty(accessKey))  { Logger.Error($"{tag}缺少必要参数: --access-key");return 1; }
                if (string.IsNullOrEmpty(secretKey))  { Logger.Error($"{tag}缺少必要参数: --secret-key");return 1; }
                if (action != "delete-prefix" && string.IsNullOrEmpty(filePath))
                                                    { Logger.Error($"{tag}缺少必要参数: --file");       return 1; }
                if (string.IsNullOrEmpty(relativePath))
                                                    { Logger.Error($"{tag}缺少必要参数: --relative");   return 1; }

                if (action != "upload" && action != "delete" && action != "delete-prefix")
                {
                    Logger.Error($"{tag}无效操作 '{action}'，必须为 upload、delete 或 delete-prefix。");
                    return 1;
                }

                endpoint = endpoint.TrimEnd('/');
                Logger.Info($"{tag}SyncWorker: action={action}, file={relativePath}");

                var uploader = new MinioUploader(endpoint, bucket, accessKey, secretKey,
                    pathPrefix: pathPrefix, tag: tag);

                var ct = CancellationToken.None;
                bool success;

                if (action == "delete-prefix")
                {
                    var prefix = relativePath.Replace('\\', '/');
                    success = await uploader.DeleteObjectsByPrefixAsync(prefix, ct).ConfigureAwait(false);
                }
                else if (action == "delete")
                {
                    var objectKey = relativePath.Replace('\\', '/');
                    success = await uploader.DeleteObjectAsync(objectKey, ct).ConfigureAwait(false);
                }
                else // upload
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