namespace XCloudSSD.Core;

public enum MountEngine
{
    /// <summary>rclone mount with --vfs-cache-mode full (cache on a local disk).</summary>
    Rclone = 0,
    /// <summary>XCloudSSD's own WinFsp filesystem: RAM buffer only, no disk cache.</summary>
    Direct = 1,
}

public enum DeleteMode
{
    /// <summary>Move to the provider's trash (recoverable; still counts toward quota).</summary>
    Trash = 0,
    /// <summary>Delete permanently (frees quota immediately).</summary>
    Permanent = 1,
}

/// <summary>Service-wide settings, persisted in the local database.</summary>
public sealed class AppSettings
{
    public bool MountAtStartup { get; set; } = true;
    public bool KeepMounted { get; set; } = true;
    public bool AutoReconnect { get; set; } = true;
    public bool RestorePreviousConfiguration { get; set; } = true;

    /// <summary>Total RAM shared by read buffers and pending writes across all disks.</summary>
    public int RamBufferMB { get; set; } = RecommendedRamBufferMB();

    /// <summary>Settings layout version; lets an upgrade lift the old 512 MB default.</summary>
    public int SettingsVersion { get; set; }

    /// <summary>
    /// Default RAM buffer: a quarter of physical RAM, between 1024 and 4096 MB. Game installs write many
    /// files at once, and every byte waiting for upload has to sit in RAM (there is no disk cache).
    /// </summary>
    public static int RecommendedRamBufferMB()
    {
        var total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var quarter = total > 0 ? (int)Math.Min(int.MaxValue, total / 4 >> 20) : 1024;
        return Math.Clamp(quarter, 1024, 4096);
    }

    /// <summary>Upgrades settings saved by older versions.</summary>
    public void Migrate()
    {
        if (SettingsVersion < 2)
        {
            if (RamBufferMB == 512) RamBufferMB = RecommendedRamBufferMB();
            SettingsVersion = 2;
        }
        if (SettingsVersion < 3)
        {
            // v1.3: drives are mounted by rclone with a local cache (user decision).
            Engine = MountEngine.Rclone;
            SettingsVersion = 3;
        }
        if (SettingsVersion < 4)
        {
            // v1.3.3: start uploads after a file has been quiet for a minute (fewer cancelled/restarted uploads while a
            // launcher is still writing) and fewer parallel uploads (slow uplinks timed out TeraBox parts).
            if (WriteBackSeconds == 5) WriteBackSeconds = 60;
            if (RcloneTransfers == 4) RcloneTransfers = 3;
            SettingsVersion = 4;
        }
    }

    /// <summary>What mounts the drives: rclone with a local VFS cache (default) or XCloudSSD's own RAM-only filesystem.</summary>
    public MountEngine Engine { get; set; } = MountEngine.Rclone;
    /// <summary>Folder for rclone's file cache. Empty = %ProgramData%\XCloudSSD\Cache (on C:).</summary>
    public string CacheDirectory { get; set; } = "";
    /// <summary>Soft limit of the cache per drive; files still waiting for upload are never evicted.</summary>
    public int CacheMaxGB { get; set; } = 100;
    /// <summary>rclone evicts cached (already uploaded) files to keep at least this much free on the cache disk.</summary>
    public int CacheMinFreeGB { get; set; } = 10;
    /// <summary>Uploaded files stay cached this long after their last use.</summary>
    public int CacheMaxAgeHours { get; set; } = 168;
    /// <summary>Seconds after a file is closed before its upload starts.</summary>
    public int WriteBackSeconds { get; set; } = 60;
    /// <summary>Parallel uploads per drive.</summary>
    public int RcloneTransfers { get; set; } = 3;

    public static string DefaultCacheDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "XCloudSSD", "Cache");

    public string EffectiveCacheDirectory => string.IsNullOrWhiteSpace(CacheDirectory) ? DefaultCacheDirectory : CacheDirectory.Trim();
    /// <summary>Range size fetched per cloud read request.</summary>
    public int ReadChunkKB { get; set; } = 4096;
    /// <summary>Chunks prefetched ahead when sequential access is detected.</summary>
    public int ReadAheadChunks { get; set; } = 4;
    /// <summary>Upload chunk size (rounded to the provider granularity).</summary>
    public int UploadChunkMB { get; set; } = 16;
    public int MaxParallelRequestsPerDisk { get; set; } = 8;

    /// <summary>0 = unlimited.</summary>
    public int UploadLimitKBps { get; set; }
    /// <summary>0 = unlimited.</summary>
    public int DownloadLimitKBps { get; set; }

    public DeleteMode DeleteMode { get; set; } = DeleteMode.Trash;
    /// <summary>Report "NTFS" as the filesystem name for app compatibility (Steam, launchers).</summary>
    public bool ReportNtfs { get; set; } = true;
    public int MetadataCacheSeconds { get; set; } = 30;
    public int ChangePollSeconds { get; set; } = 30;

    public bool Notifications { get; set; } = true;
    public string LogLevel { get; set; } = "Information";

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
