using System.Collections.Generic;
using System.IO;

namespace MinioCommon
{
    /// <summary>
    /// Shared configuration model for MinIO sync tasks.
    /// Used by both MinioSync (daemon) and SyncWorker (worker).
    /// </summary>
    public class SyncConfig
    {
        /// <summary>Unique identifier for this config (used for commands like fullsync).</summary>
        public string Id { get; set; }

        /// <summary>
        /// Whether to enable real-time monitoring for this config.
        /// When false, the daemon skips it; FullSync tool still uses it regardless.
        /// </summary>
        public bool Enable { get; set; } = false;

        /// <summary>Local folder to monitor.</summary>
        public string LocalFolderPath { get; set; }

        /// <summary>
        /// Reference to a named MinIO profile defined in root's MinIOProfiles.
        /// When set, inline MinIO fields (MinIOEndpoint/BucketName/AccessKey/SecretKey)
        /// can be omitted and will be resolved from the profile.
        /// </summary>
        public string MinIOProfile { get; set; }

        // ---- Inline MinIO fields (used directly or overridden by profile) ----

        /// <summary>MinIO server endpoint (e.g. http://localhost:9000).</summary>
        public string MinIOEndpoint { get; set; }

        /// <summary>MinIO bucket name.</summary>
        public string BucketName { get; set; }

        /// <summary>MinIO access key.</summary>
        public string AccessKey { get; set; }

        /// <summary>MinIO secret key.</summary>
        public string SecretKey { get; set; }

        /// <summary>Sync interval in seconds between batch checks.</summary>
        public int SyncIntervalSeconds { get; set; } = 60;

        /// <summary>
        /// Optional list of file extensions to monitor (e.g. [".txt", ".csv"]).
        /// If null or empty, all files are monitored.
        /// </summary>
        public string[] FileExtensions { get; set; }

        /// <summary>
        /// Seconds to wait after the last file change event before considering
        /// the file stable (ready to sync). Default: 3.
        /// </summary>
        public int FileStabilitySeconds { get; set; } = 3;

        /// <summary>
        /// Optional path prefix prepended to every object key uploaded to MinIO.
        /// Default: empty (object key equals the relative path).
        /// Example: "myproject/" → object key becomes "myproject/sub/file.txt".
        /// </summary>
        public string PathPrefix { get; set; }

        /// <summary>
        /// Optional list of file suffixes to exclude (e.g. [".tmp", ".bak", ".swp"]).
        /// Files matching these suffixes are skipped during monitoring and full sync.
        /// Combined with built-in exclusions (.tmp, .bak, .~lock, ~$*).
        /// </summary>
        public string[] ExcludeSuffixes { get; set; }

        /// <summary>
        /// Maximum number of concurrent upload/delete tasks in this process.
        /// Used by MinioSync (daemon) and FullSync to throttle in-process parallelism
        /// via SemaphoreSlim. Default: 10. Set to 0 to disable the limit (unbounded).
        /// </summary>
        public int MaxConcurrentUploads { get; set; } = 10;

        /// <summary>
        /// Email addresses to notify when MinIO operations fail for this config.
        /// Requires root-level Email settings to be configured.
        /// </summary>
        public string[] NotifyEmails { get; set; }

        /// <summary>
        /// Validates the config has all required fields.
        /// Returns null if valid, or an error message if invalid.
        /// </summary>
        public string Validate()
        {
            if (string.IsNullOrWhiteSpace(LocalFolderPath))
                return "LocalFolderPath is required";

            if (string.IsNullOrWhiteSpace(MinIOEndpoint))
                return "MinIOEndpoint is required (either inline or via MinIOProfile)";

            if (string.IsNullOrWhiteSpace(BucketName))
                return "BucketName is required (either inline or via MinIOProfile)";

            if (string.IsNullOrWhiteSpace(AccessKey))
                return "AccessKey is required (either inline or via MinIOProfile)";

            if (string.IsNullOrWhiteSpace(SecretKey))
                return "SecretKey is required (either inline or via MinIOProfile)";

            if (string.IsNullOrWhiteSpace(Id))
                return "Id is required";

            if (!Directory.Exists(LocalFolderPath))
                return $"LocalFolderPath does not exist: {LocalFolderPath}";

            if (SyncIntervalSeconds <= 0)
                return "SyncIntervalSeconds must be greater than 0";

            return null;
        }
    }

    /// <summary>
    /// Named MinIO connection profile shared across multiple sync configs.
    /// </summary>
    public class MinIOProfile
    {
        public string Endpoint { get; set; }
        public string BucketName { get; set; }
        public string AccessKey { get; set; }
        public string SecretKey { get; set; }
        public string Region { get; set; } = "us-east-1";
    }

    /// <summary>
    /// SMTP email settings for sending failure notifications.
    /// </summary>
    public class EmailSettings
    {
        public string SmtpServer { get; set; }
        public int SmtpPort { get; set; } = 587;
        public bool UseSsl { get; set; } = true;
        public string SenderAddress { get; set; }
        public string SenderPassword { get; set; }
        public string SenderName { get; set; } = "MinioSync";
    }
}
