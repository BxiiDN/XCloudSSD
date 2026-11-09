namespace XCloudSSD.Core;

/// <summary>Cloud provider family.</summary>
public enum ProviderKind
{
    GoogleDrive = 1,
    TeraBox = 2,
}

/// <summary>Connection state of one account / virtual disk.</summary>
public enum ConnectionState
{
    Disconnected = 0,
    Connecting = 1,
    Online = 2,
    Offline = 3,
    AuthRequired = 4,
    Unavailable = 5,
    Error = 6,
}

/// <summary>What a provider adapter can really do. Reported to the UI as-is.</summary>
public sealed record ProviderCapabilities(
    bool CanMount,
    bool RangeRead,
    bool ResumableUpload,
    bool Rename,
    bool Move,
    bool Trash,
    bool QuotaInfo,
    bool ChangeFeed,
    bool CustomProperties,
    string Notes)
{
    public static readonly ProviderCapabilities None =
        new(false, false, false, false, false, false, false, false, false, "No capabilities.");
}

public sealed record QuotaInfo(long? TotalBytes, long UsedBytes)
{
    /// <summary>Free bytes, or null when the provider reports an unlimited quota.</summary>
    public long? FreeBytes => TotalBytes is { } t ? Math.Max(0, t - UsedBytes) : null;
}

public sealed record AccountIdentity(string DisplayName, string Email)
{
    /// <summary>Provider's stable user id (TeraBox "uk"), used to detect the same account added twice.</summary>
    public string? UserId { get; init; }
}

/// <summary>A file or folder as seen in the cloud.</summary>
public sealed class CloudItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool IsFolder { get; init; }
    /// <summary>Logical file size seen by Windows (may include a sparse zero tail).</summary>
    public long Size { get; init; }
    /// <summary>Bytes actually stored in the cloud. Null = same as <see cref="Size"/>.</summary>
    public long? StoredSize { get; init; }
    /// <summary>Bytes readable from the cloud; anything after this up to <see cref="Size"/> reads as zeros.</summary>
    public long ContentLength => StoredSize ?? Size;
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset Modified { get; init; }
    public string? Md5 { get; init; }
    /// <summary>Windows file attributes persisted in the cloud (custom property), if any.</summary>
    public uint? WindowsAttributes { get; init; }
    public IReadOnlyList<string> Parents { get; init; } = Array.Empty<string>();
    /// <summary>True for provider-native documents that have no binary content (e.g. Google Docs).</summary>
    public bool IsVirtualDocument { get; init; }
}

public sealed class MetadataUpdate
{
    public string? NewName { get; init; }
    public string? AddParentId { get; init; }
    public string? RemoveParentId { get; init; }
    public uint? WindowsAttributes { get; init; }
    public DateTimeOffset? Modified { get; init; }
    /// <summary>
    /// Logical size to record for a sparse file (reserved space that has not been written yet).
    /// The stored content stays shorter; the remainder reads as zeros and is never uploaded.
    /// </summary>
    public long? LogicalSize { get; init; }
}

public sealed record UploadTarget(string? ExistingFileId, string? ParentId, string? Name, uint? WindowsAttributes, DateTimeOffset? Modified)
{
    /// <summary>Total size when known up front (lets providers pick a part size / upload mode).</summary>
    public long? SizeHint { get; init; }

    public static UploadTarget NewFile(string parentId, string name, uint? attrs = null, DateTimeOffset? modified = null)
        => new(null, parentId, name, attrs, modified);

    public static UploadTarget Replace(string fileId, uint? attrs = null, DateTimeOffset? modified = null)
        => new(fileId, null, null, attrs, modified);
}

public sealed record ChangedItem(string Id, bool Removed, CloudItem? Item);

public sealed record ChangeSet(string NextToken, IReadOnlyList<ChangedItem> Changes);

/// <summary>Persisted account configuration (no secrets).</summary>
public sealed class AccountRecord
{
    public required string Id { get; init; }
    public required ProviderKind Provider { get; init; }
    public string DisplayName { get; set; } = "";
    public string Email { get; set; } = "";
    public char DriveLetter { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>Volume label shown in Explorer, e.g. "XCloudSSD Google 01". User-renamable (max 32 chars).</summary>
    public string Label { get; set; } = "";
    /// <summary>Provider user id (TeraBox "uk") of the signed-in account, once known.</summary>
    public string RemoteUserId { get; set; } = "";

    public static string DefaultLabel(ProviderKind kind, int ordinal)
        => kind == ProviderKind.GoogleDrive
            ? $"XCloudSSD Google {ordinal:00}"
            : $"XCloudSSD TeraBox {ordinal:00}";
}
