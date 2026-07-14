using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MinioCommon;

namespace MinioSync
{
    /// <summary>
    /// Monitors a local folder for file changes.
    /// When files stabilize (no new events for FileStabilitySeconds),
    /// dispatches the actions IN-PROCESS via ThreadPool + SemaphoreSlim,
    /// calling MinioCommon.MinioUploader directly. No subprocess spawning.
    /// </summary>
    internal class FolderMonitor : IDisposable
    {
        private readonly SyncConfig _config;
        private readonly FileSystemWatcher _watcher;
        private readonly Timer _batchTimer;

        /// <summary>Reused across all in-process uploads/deletes (MinioClient is thread-safe).</summary>
        private readonly MinioUploader _uploader;

        /// <summary>Limits concurrent upload/delete tasks to MaxConcurrentUploads.</summary>
        private readonly SemaphoreSlim _semaphore;

        /// <summary>SMTP email settings for failure alerts. Null = no notification.</summary>
        private readonly EmailSettings _emailSettings;

        private class PendingChange
        {
            public string Action { get; set; }
            public DateTime LastEventTime { get; set; }
        }

        private readonly ConcurrentDictionary<string, PendingChange> _pendingChanges
            = new ConcurrentDictionary<string, PendingChange>(StringComparer.OrdinalIgnoreCase);

        private int _collecting;
        private bool _disposed;

        public string ConfigId => _config.Id ?? "";
        public string LocalFolderPath => _config.LocalFolderPath;
        public string BucketName => _config.BucketName;

        public FolderMonitor(SyncConfig config, EmailSettings emailSettings = null)
        {
            _config = config;
            _emailSettings = emailSettings;

            // Build one shared MinioUploader for all in-process calls.
            _uploader = new MinioUploader(
                endpoint: config.MinIOEndpoint,
                bucket: config.BucketName,
                accessKey: config.AccessKey,
                secretKey: config.SecretKey,
                pathPrefix: config.PathPrefix ?? "");

            // Concurrency limit: 0 means unbounded.
            var max = config.MaxConcurrentUploads > 0 ? config.MaxConcurrentUploads : int.MaxValue;
            _semaphore = new SemaphoreSlim(max, max);

            // FileSystemWatcher
            _watcher = new FileSystemWatcher
            {
                Path = config.LocalFolderPath,
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                              | NotifyFilters.LastWrite
                              | NotifyFilters.DirectoryName,
                InternalBufferSize = 65536
            };

            _watcher.Created += OnFileChanged;
            _watcher.Changed += OnFileChanged;
            _watcher.Deleted += OnFileDeleted;
            _watcher.Renamed += OnFileRenamed;
            _watcher.Error += OnWatcherError;

            var intervalMs = Math.Max(1000, config.SyncIntervalSeconds * 1000);
            _batchTimer = new Timer(OnBatchTimer, null, intervalMs, intervalMs);

            _watcher.EnableRaisingEvents = true;

            var extFilter = config.FileExtensions != null && config.FileExtensions.Length > 0
                ? string.Join(", ", config.FileExtensions)
                : "(所有文件)";
            var exclFilter = config.ExcludeSuffixes != null && config.ExcludeSuffixes.Length > 0
                ? string.Join(", ", config.ExcludeSuffixes)
                : "(无)";
            var prefixInfo = string.IsNullOrEmpty(config.PathPrefix) ? "" : $", 路径前缀: {config.PathPrefix}";
            var maxInfo = config.MaxConcurrentUploads > 0 ? config.MaxConcurrentUploads.ToString() : "不限";
            Logger.Info($"开始监控 [{config.Id}]: {config.LocalFolderPath} (存储桶: {config.BucketName}, 间隔: {config.SyncIntervalSeconds}秒, 稳定等待: {config.FileStabilitySeconds}秒, 扩展名: {extFilter}, 排除后缀: {exclFilter}, 并发: {maxInfo}{prefixInfo})");
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            if (SyncHelper.ShouldIgnore(e.FullPath, _config.FileExtensions, _config.ExcludeSuffixes)
                || (e.ChangeType == WatcherChangeTypes.Changed && IsDirectory(e.FullPath)))
                return;

            var relativePath = SyncHelper.GetRelativePath(_config.LocalFolderPath, e.FullPath);
            _pendingChanges.AddOrUpdate(relativePath,
                _ => new PendingChange { Action = "upload", LastEventTime = DateTime.UtcNow },
                (_, existing) =>
                {
                    existing.Action = "upload";
                    existing.LastEventTime = DateTime.UtcNow;
                    return existing;
                });
        }

        private void OnFileDeleted(object sender, FileSystemEventArgs e)
        {
            var relativePath = SyncHelper.GetRelativePath(_config.LocalFolderPath, e.FullPath);

            // Detect directory deletion: no file extension → it's a directory.
            // Directory deletion MUST bypass the extension filter (ShouldIgnore returns
            // true for paths without extension when FileExtensions is configured).
            // FileSystemWatcher only fires ONE Deleted event for the directory itself,
            // NOT individual events for each file inside it.
            if (string.IsNullOrEmpty(Path.GetExtension(relativePath)))
            {
                _pendingChanges.AddOrUpdate(relativePath,
                    k => new PendingChange { Action = "delete-prefix", LastEventTime = DateTime.UtcNow },
                    (k, existing) =>
                    {
                        existing.Action = "delete-prefix";
                        existing.LastEventTime = DateTime.UtcNow;
                        return existing;
                    });
                return;
            }

            // Individual file deletion
            if (SyncHelper.ShouldIgnore(e.FullPath, _config.FileExtensions, _config.ExcludeSuffixes)) return;
            _pendingChanges.AddOrUpdate(relativePath,
                k => new PendingChange { Action = "delete", LastEventTime = DateTime.UtcNow },
                (k, existing) =>
                {
                    existing.Action = "delete";
                    existing.LastEventTime = DateTime.UtcNow;
                    return existing;
                });
        }

        private void OnFileRenamed(object sender, RenamedEventArgs e)
        {
            if (!SyncHelper.ShouldIgnore(e.OldFullPath, _config.FileExtensions, _config.ExcludeSuffixes))
            {
                var oldRelative = SyncHelper.GetRelativePath(_config.LocalFolderPath, e.OldFullPath);
                _pendingChanges.AddOrUpdate(oldRelative,
                    k => new PendingChange { Action = "delete", LastEventTime = DateTime.UtcNow },
                    (k, existing) =>
                    {
                        existing.Action = "delete";
                        existing.LastEventTime = DateTime.UtcNow;
                        return existing;
                    });
            }
            if (!SyncHelper.ShouldIgnore(e.FullPath, _config.FileExtensions, _config.ExcludeSuffixes))
            {
                var newRelative = SyncHelper.GetRelativePath(_config.LocalFolderPath, e.FullPath);
                _pendingChanges.AddOrUpdate(newRelative,
                    k => new PendingChange { Action = "upload", LastEventTime = DateTime.UtcNow },
                    (k, existing) =>
                    {
                        existing.Action = "upload";
                        existing.LastEventTime = DateTime.UtcNow;
                        return existing;
                    });
            }
        }

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            Logger.Error($"文件监控器错误 '{_config.LocalFolderPath}': {e.GetException()?.Message}");
        }

        /// <summary>
        /// Collects stabilized entries from _pendingChanges and enqueues them to
        /// ThreadPool for in-process execution (gated by SemaphoreSlim).
        /// </summary>
        private void OnBatchTimer(object state)
        {
            if (_disposed) return;
            if (Interlocked.CompareExchange(ref _collecting, 1, 0) != 0) return;

            try
            {
                var now = DateTime.UtcNow;
                var threshold = Math.Max(1, _config.FileStabilitySeconds);

                int count = 0;
                foreach (var kvp in _pendingChanges)
                {
                    if ((now - kvp.Value.LastEventTime).TotalSeconds < threshold)
                        continue;

                    if (!_pendingChanges.TryRemove(kvp.Key, out var change))
                        continue; // raced with another consumer

                    string action = change.Action;
                    string key = kvp.Key;

                    if (action == "delete-prefix")
                    {
                        // Normalize to "subdir/" form (trailing slash)
                        var dirPrefix = key.Replace('\\', '/').TrimEnd('/') + "/";
                        EnqueueWork(action, "", dirPrefix);
                    }
                    else if (action == "delete")
                    {
                        EnqueueWork(action, "", key);
                    }
                    else // upload
                    {
                        var fullPath = Path.Combine(_config.LocalFolderPath, key);
                        EnqueueWork(action, fullPath, key);
                    }
                    count++;
                }

                if (count > 0)
                {
                    Logger.Info($"批次: {count} 个任务已稳定，加入 ThreadPool 队列");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"处理批次失败 '{_config.LocalFolderPath}'", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _collecting, 0);
            }
        }

        /// <summary>
        /// Queues one MinIO operation to ThreadPool. Concurrency is throttled by
        /// _semaphore. The MinioUploader instance is shared (thread-safe).
        /// </summary>
        private void EnqueueWork(string action, string fullPath, string relativeKey)
        {
            var taskId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var tag = $"[{taskId}] ";
            var objectKey = relativeKey.Replace('\\', '/');

            Logger.Info($"{tag}{action} 排队: {objectKey}");

            ThreadPool.QueueUserWorkItem(async _ =>
            {
                await _semaphore.WaitAsync().ConfigureAwait(false);
                string errorMsg = null;
                try
                {
                    bool ok;
                    if (action == "delete-prefix")
                    {
                        ok = await _uploader.DeleteObjectsByPrefixAsync(objectKey, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    else if (action == "delete")
                    {
                        ok = await _uploader.DeleteObjectAsync(objectKey, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    else // upload
                    {
                        ok = await _uploader.UploadFileAsync(fullPath, objectKey, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    if (!ok)
                    {
                        errorMsg = $"{action} 操作返回失败";
                        Logger.Warn($"{tag}{action} 失败: {objectKey}");
                        if (action == "upload")
                            ErrorLog.Record(_config.Id, relativeKey);
                    }
                }
                catch (Exception ex)
                {
                    errorMsg = $"{action} 异常: {ex.Message}";
                    Logger.Error($"{tag}{action} 异常: {objectKey}", ex);
                    if (action == "upload")
                        ErrorLog.Record(_config.Id, relativeKey);
                }
                finally
                {
                    _semaphore.Release();
                }

                // Send notification email on failure if configured
                if (errorMsg != null && _emailSettings != null &&
                    _config.NotifyEmails != null && _config.NotifyEmails.Length > 0)
                {
                    var subject = $"MinioSync 同步失败 - {_config.Id} - {action}";
                    var body = EmailNotifier.BuildFailureBody(
                        _config.Id, action, objectKey, errorMsg);
                    // Fire-and-forget: don't block the upload thread on email
                    _ = EmailNotifier.SendAlertAsync(
                        _emailSettings, _config.NotifyEmails, subject, body);
                }
            });
        }

        private static bool IsDirectory(string path)
        {
            try
            {
                return File.GetAttributes(path).HasFlag(FileAttributes.Directory);
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _watcher?.Dispose();
            _batchTimer?.Dispose();
            _semaphore.Dispose();
            Logger.Info($"停止监控: {_config.LocalFolderPath}");
        }
    }
}