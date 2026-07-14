using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MinioCommon
{
    /// <summary>
    /// Result of running a SyncWorker process.
    /// </summary>
    public class WorkerResult
    {
        /// <summary>Process exit code. 0 = success, non-zero = failure, -1 = failed to start.</summary>
        public int ExitCode { get; set; }
        /// <summary>Standard error output captured from the worker process.</summary>
        public string ErrorOutput { get; set; }
    }

    /// <summary>
    /// Shared helpers used by MinioSync, SyncWorker, and FullSync tools.
    /// </summary>
    public static class SyncHelper
    {
        /// <summary>
        /// Builds CLI arguments string for SyncWorker.exe.
        /// Includes --path-prefix only when config.PathPrefix is non-empty.
        /// </summary>
        public static string BuildWorkerArgs(SyncConfig config, string fullPath, string relativePath, string action, string taskId)
        {
            var sb = new StringBuilder();
            sb.Append("--endpoint \"").Append(config.MinIOEndpoint).Append("\" ");
            sb.Append("--bucket \"").Append(config.BucketName).Append("\" ");
            sb.Append("--access-key \"").Append(config.AccessKey).Append("\" ");
            sb.Append("--secret-key \"").Append(config.SecretKey).Append("\" ");
            sb.Append("--action \"").Append(action).Append("\" ");
            sb.Append("--file \"").Append(fullPath).Append("\" ");
            sb.Append("--relative \"").Append(relativePath).Append("\" ");
            sb.Append("--task-id \"").Append(taskId).Append("\"");
            if (!string.IsNullOrEmpty(config.PathPrefix))
            {
                sb.Append(" --path-prefix \"").Append(config.PathPrefix).Append("\"");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Spawns SyncWorker.exe and waits for it to complete.
        /// Returns exit code (0 = success, non-zero = failure, -1 = failed to start).
        /// </summary>
        public static int SpawnWorkerAndWait(string workerPath, SyncConfig config, string fullPath, string relativePath, string action, string taskId)
        {
            return SpawnWorkerAndWait(workerPath, BuildWorkerArgs(config, fullPath, relativePath, action, taskId)).ExitCode;
        }

        /// <summary>
        /// Spawns SyncWorker.exe and waits for it to complete.
        /// Returns a WorkerResult with exit code and captured error output.
        /// </summary>
        public static WorkerResult SpawnWorkerAndWait(string workerPath, string arguments)
        {
            var result = new WorkerResult { ExitCode = -1 };

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
                if (process == null) return result;

                // Read both streams to avoid deadlocks
                var stderr = process.StandardError.ReadToEnd();
                process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                result.ExitCode = process.ExitCode;
                result.ErrorOutput = stderr?.Trim();
                return result;
            }
        }

        /// <summary>
        /// Spawns SyncWorker.exe with full context and waits, returning structured result.
        /// </summary>
        public static WorkerResult SpawnWorkerWithResult(string workerPath, SyncConfig config, string fullPath, string relativePath, string action, string taskId)
        {
            return SpawnWorkerAndWait(workerPath, BuildWorkerArgs(config, fullPath, relativePath, action, taskId));
        }

        /// <summary>
        /// Recursively collects all files in a directory that pass the ShouldIgnore filter.
        /// </summary>
        public static List<string> CollectFiles(string directory, string[] allowedExtensions, string[] excludeSuffixes = null)
        {
            var results = new List<string>();
            CollectFilesInternal(directory, allowedExtensions, excludeSuffixes, results);
            return results;
        }

        private static void CollectFilesInternal(string directory, string[] allowedExtensions, string[] excludeSuffixes, List<string> results)
        {
            try
            {
                foreach (var file in Directory.GetFiles(directory))
                {
                    if (!ShouldIgnore(file, allowedExtensions, excludeSuffixes))
                        results.Add(file);
                }

                foreach (var subDir in Directory.GetDirectories(directory))
                {
                    CollectFilesInternal(subDir, allowedExtensions, excludeSuffixes, results);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Skip directories we can't access
            }
        }

        /// <summary>
        /// Returns true if the file should be skipped.
        /// Skipped when: matches built-in exclusions (.tmp, .bak, .~lock, ~$*),
        /// matches user-configured ExcludeSuffixes, or (when FileExtensions is set)
        /// doesn't match any allowed extension.
        /// </summary>
        public static bool ShouldIgnore(string path, string[] allowedExtensions, string[] excludeSuffixes = null)
        {
            if (string.IsNullOrEmpty(path)) return true;

            var ext = Path.GetExtension(path);
            var name = Path.GetFileName(path);

            // Built-in exclusions
            if (string.Equals(ext, ".tmp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ext, ".bak", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ext, ".~lock", StringComparison.OrdinalIgnoreCase))
                return true;

            if (name != null && name.StartsWith("~$")) return true;

            // User-configured exclusions
            if (excludeSuffixes != null && excludeSuffixes.Length > 0)
            {
                foreach (var suffix in excludeSuffixes)
                {
                    if (string.IsNullOrEmpty(suffix)) continue;
                    if (string.Equals(ext, suffix, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            // Allowed extensions filter
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
