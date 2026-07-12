using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace MinioCommon
{
    /// <summary>
    /// Shared helpers used by MinioSync, SyncWorker, and FullSync tools.
    /// </summary>
    public static class SyncHelper
    {
        /// <summary>
        /// Builds CLI arguments string for SyncWorker.exe.
        /// </summary>
        public static string BuildWorkerArgs(SyncConfig config, string fullPath, string relativePath, string action, string taskId)
        {
            return $"--endpoint \"{config.MinIOEndpoint}\" "
                 + $"--bucket \"{config.BucketName}\" "
                 + $"--access-key \"{config.AccessKey}\" "
                 + $"--secret-key \"{config.SecretKey}\" "
                 + $"--action \"{action}\" "
                 + $"--file \"{fullPath}\" "
                 + $"--relative \"{relativePath}\" "
                 + $"--task-id \"{taskId}\"";
        }

        /// <summary>
        /// Spawns SyncWorker.exe and waits for it to complete.
        /// Returns exit code (0 = success, non-zero = failure, -1 = failed to start).
        /// </summary>
        public static int SpawnWorkerAndWait(string workerPath, SyncConfig config, string fullPath, string relativePath, string action, string taskId)
        {
            return SpawnWorkerAndWait(workerPath, BuildWorkerArgs(config, fullPath, relativePath, action, taskId));
        }

        /// <summary>
        /// Spawns SyncWorker.exe with raw argument string and waits for it to complete.
        /// </summary>
        public static int SpawnWorkerAndWait(string workerPath, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = workerPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var process = Process.Start(psi))
            {
                if (process == null) return -1;

                // Read output to avoid deadlocks from buffered output
                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit();
                return process.ExitCode;
            }
        }

        /// <summary>
        /// Recursively collects all files in a directory that pass the ShouldIgnore filter.
        /// </summary>
        public static List<string> CollectFiles(string directory, string[] allowedExtensions)
        {
            var results = new List<string>();
            CollectFilesInternal(directory, allowedExtensions, results);
            return results;
        }

        private static void CollectFilesInternal(string directory, string[] allowedExtensions, List<string> results)
        {
            try
            {
                foreach (var file in Directory.GetFiles(directory))
                {
                    if (!ShouldIgnore(file, allowedExtensions))
                        results.Add(file);
                }

                foreach (var subDir in Directory.GetDirectories(directory))
                {
                    CollectFilesInternal(subDir, allowedExtensions, results);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Skip directories we can't access
            }
        }

        /// <summary>
        /// Returns true if the file should be skipped (temp file, wrong extension, etc.).
        /// </summary>
        public static bool ShouldIgnore(string path, string[] allowedExtensions)
        {
            if (string.IsNullOrEmpty(path)) return true;

            var ext = Path.GetExtension(path);
            if (string.Equals(ext, ".tmp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ext, ".bak", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ext, ".~lock", StringComparison.OrdinalIgnoreCase))
                return true;

            var name = Path.GetFileName(path);
            if (name != null && name.StartsWith("~$")) return true;

            if (allowedExtensions != null && allowedExtensions.Length > 0)
            {
                foreach (var allowed in allowedExtensions)
                {
                    if (string.Equals(ext, allowed, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                return true;
            }

            return false;
        }

        /// <summary>
        /// Gets a file's path relative to a base folder.
        /// </summary>
        public static string GetRelativePath(string folderPath, string fullPath)
        {
            folderPath = folderPath.TrimEnd('\\', '/');
            if (fullPath.StartsWith(folderPath, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath.Substring(folderPath.Length).TrimStart('\\', '/');
            }
            return fullPath;
        }
    }
}
