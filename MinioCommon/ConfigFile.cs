using System.Collections.Generic;

namespace MinioCommon
{
    /// <summary>
    /// Root model for the single config.json file.
    /// </summary>
    public class ConfigFile
    {
        /// <summary>Config file schema version. Currently 1.</summary>
        public int Version { get; set; }

        /// <summary>List of sync task configurations.</summary>
        public List<SyncConfig> Configs { get; set; }
    }
}
