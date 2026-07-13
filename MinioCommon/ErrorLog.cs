using System;
using System.IO;
using System.Threading;

namespace MinioCommon
{
    /// <summary>
    /// Writes failed file paths (one per line) to a daily error log so the user
    /// can later retry them via `FullSync.exe --list error-YYYY-MM-DD-{configId}-PID.txt`.
    ///
    /// The error file is named per-configId because multiple configs may run
    /// simultaneously (e.g. the MinioSync daemon watching several folders), and
    /// the user needs to know which config the failed files belong to.
    ///
    /// File format is plain text — each line is one absolute file path, suitable
    /// to be fed back into LoadFileList (which also supports blank lines and
    /// '#' comments for manual editing before a retry run).
    ///
    /// Call <see cref="Initialize"/> once at startup with the logs directory,
    /// then call <see cref="Record(string, string)"/> for each failed path.
    /// Best-effort: write failures are silently swallowed.
    /// </summary>
    public static class ErrorLog
    {
        private static string _logDir;
        private static readonly object _fileLock = new object();
        private static int _recordedCount;

        /// <summary>Number of paths recorded since Initialize().</summary>
        public static int RecordedCount => _recordedCount;

        /// <summary>Computes the error file path for the given configId, today's date, and PID.</summary>
        public static string GetErrorFilePath(string configId)
        {
            if (string.IsNullOrEmpty(_logDir)) return null;
            var safeId = SanitizeConfigId(configId);
            return Path.Combine(_logDir,
                $"error-{DateTime.Now:yyyy-MM-dd}-{safeId}-{Environment.ProcessId}.txt");
        }

        /// <summary>
        /// Initializes the error log directory. Safe to call multiple times.
        /// </summary>
        public static void Initialize(string logDirectory)
        {
            if (string.IsNullOrEmpty(logDirectory)) return;
            _logDir = logDirectory;
            try { Directory.CreateDirectory(_logDir); } catch { /* ignore */ }
        }

        /// <summary>
        /// Appends one file path to the error log for the given configId.
        /// No-op if Initialize wasn't called or path/configId is null/empty.
        /// </summary>
        public static void Record(string configId, string fullPath)
        {
            if (string.IsNullOrEmpty(_logDir) || string.IsNullOrEmpty(fullPath)) return;

            var path = GetErrorFilePath(configId);
            if (path == null) return;

            lock (_fileLock)
            {
                try
                {
                    File.AppendAllText(path, fullPath + Environment.NewLine);
                    Interlocked.Increment(ref _recordedCount);
                }
                catch
                {
                    // Best-effort — don't let error-logging break the main flow
                }
            }
        }

        /// <summary>
        /// Replaces any character that's not letter/digit/dash/underscore with '_'
        /// so the configId can safely appear in a filename across platforms.
        /// </summary>
        private static string SanitizeConfigId(string configId)
        {
            if (string.IsNullOrEmpty(configId)) return "default";
            var chars = configId.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '-' && chars[i] != '_')
                    chars[i] = '_';
            }
            return new string(chars);
        }
    }
}