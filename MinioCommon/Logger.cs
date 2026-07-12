using System;
using System.IO;

namespace MinioCommon
{
    /// <summary>
    /// Shared file logger with timestamps. Used by all tools in the MinioSync family.
    /// Call Initialize(logDir, logName) once at startup, then use Info/Warn/Error.
    /// </summary>
    public static class Logger
    {
        private static string _logDir;
        private static string _logName = "sync";
        private static readonly object _lock = new object();

        /// <summary>
        /// Initializes the logger.
        /// </summary>
        /// <param name="logDirectory">Directory for log files.</param>
        /// <param name="logName">Base name for the log file (e.g. "sync", "worker", "fullsync").</param>
        public static void Initialize(string logDirectory, string logName = "sync")
        {
            _logDir = logDirectory;
            _logName = logName ?? "sync";
            if (!Directory.Exists(_logDir))
                Directory.CreateDirectory(_logDir);
        }

        private static string GetLogFilePath()
        {
            return Path.Combine(_logDir, $"{_logName}-{DateTime.Now:yyyy-MM-dd}.log");
        }

        public static void Info(string message)
        {
            WriteLog("信息", message);
        }

        public static void Warn(string message)
        {
            WriteLog("警告", message);
        }

        public static void Error(string message)
        {
            WriteLog("错误", message);
        }

        public static void Error(string message, Exception ex)
        {
            WriteLog("错误", $"{message} | 异常: {ex?.Message}");
        }

        private static void WriteLog(string level, string message)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var logLine = $"[{timestamp}] [{level}] {message}";

            Console.WriteLine(logLine);

            if (_logDir == null) return;
            lock (_lock)
            {
                try
                {
                    var logFile = GetLogFilePath();
                    File.AppendAllText(logFile, logLine + Environment.NewLine);
                }
                catch
                {
                    // Best-effort — fail silently if log write fails
                }
            }
        }
    }
}
