namespace XCloudSSD.Core;

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
    public int RamBufferMB { get; set; } = 512;
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
