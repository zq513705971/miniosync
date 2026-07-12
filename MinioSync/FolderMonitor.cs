using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using MinioCommon;

namespace MinioSync
{
    /// <summary>
    /// Monitors a local folder for file changes.
    /// When files stabilize (no new events for FileStabilitySeconds),
    /// spawns SyncWorker.exe for each file (unlimited concurrency).
    /// </summary>
    internal class FolderMonitor : IDisposable
    {
        private readonly SyncConfig _config;
        private readonly string _workerExePath;
        private readonly FileSystemWatcher _watcher;
        private readonly Timer _batchTimer;

        private class PendingChange
        {
            public string Action { get; set; }
            public DateTime LastEventTime { get; set; }
        }

        private readonly ConcurrentDictionary<string, PendingChange> _pendingChanges
            = new ConcurrentDictionary<string, PendingChange>(StringComparer.OrdinalIgnoreCase);

        private int _processing;
        private bool _disposed;

        public string ConfigId => _config.Id ?? "";
        public string LocalFolderPath => _config.LocalFolderPath;
        public string BucketName => _config.BucketName;

        public FolderMonitor(SyncConfig config, string workerExePath)
        {
            _config = config;
            _workerExePath = workerExePath;

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
            Logger.Info($"开始监控 [{config.Id}]: {config.LocalFolderPath} (存储桶: {config.BucketName}, 间隔: {config.SyncIntervalSeconds}秒, 稳定等待: {config.FileStabilitySeconds}秒, 扩展名: {extFilter}, 排除后缀: {exclFilter}{prefixInfo})");
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
                // Directory deletion: FileSystemWatcher only fires ONE Deleted event
                // for the directory itself, NOT individual events for each file inside it.
                // Spawn SyncWorker to list and delete all objects under this prefix in MinIO.
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
        /// Collect stabilized files and spawn SyncWorker for each (unlimited concurrency).
        /// </summary>
        private void OnBatchTimer(object state)
        {
            if (_disposed) return;
            if (Interlocked.CompareExchange(ref _processing, 1, 0) != 0) return;

            try
            {
                var now = DateTime.UtcNow;
                var threshold = Math.Max(1, _config.FileStabilitySeconds);

                var stableKeys = new List<string>();
                foreach (var kvp in _pendingChanges)
                {
                    if ((now - kvp.Value.LastEventTime).TotalSeconds >= threshold)
                        stableKeys.Add(kvp.Key);
                }

                if (stableKeys.Count == 0) return;

                Logger.Info($"批次: {stableKeys.Count} 个文件已稳定");

                foreach (var key in stableKeys)
                {
                    PendingChange change;
                    if (_pendingChanges.TryRemove(key, out change))
                    {
                        if (change.Action == "delete-prefix")
                        {
                            // Directory deletion: pass the prefix (trailing with /) as --relative
                            var dirPrefix = key.Replace('\\', '/').TrimEnd('/') + "/";
                            LaunchWorker("", dirPrefix, "delete-prefix");
                        }
                        else
                        {
                            var fullPath = Path.Combine(_config.LocalFolderPath, key);
                            LaunchWorker(fullPath, key, change.Action);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"处理批次失败 '{_config.LocalFolderPath}'", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _processing, 0);
            }
        }

        /// <summary>Launches SyncWorker on a background thread, fire-and-forget.</summary>
        private void LaunchWorker(string fullPath, string relativePath, string action)
        {
            var taskId = Guid.NewGuid().ToString("N").Substring(0, 8);
            Logger.Info($"[{taskId}] 启动 SyncWorker: {action} {relativePath}");

            ThreadPool.QueueUserWorkItem(state =>
            {
                try
                {
                    var exitCode = SyncHelper.SpawnWorkerAndWait(
                        _workerExePath, _config, fullPath, relativePath, action, taskId);

                    if (exitCode != 0)
                        Logger.Warn($"[{taskId}] SyncWorker 退出码: {exitCode}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"[{taskId}] 启动 SyncWorker 失败: {relativePath}", ex);
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
            Logger.Info($"停止监控: {_config.LocalFolderPath}");
        }
    }
}
