namespace XCloudSSD.Core;

/// <summary>
/// One authenticated cloud account. Every virtual disk owns exactly one provider instance,
/// so namespaces, credentials and quotas are never shared between accounts.
/// </summary>
public interface ICloudProvider : IDisposable
{
    ProviderKind Kind { get; }
    ProviderCapabilities Capabilities { get; }

    /// <summary>Id of the folder that is mapped to the drive root (e.g. "X:\"). Valid after <see cref="InitializeAsync"/>.</summary>
    string RootId { get; }

    /// <summary>Resolves account-level ids (such as the real root folder id). Called once before mounting.</summary>
    Task InitializeAsync(CancellationToken ct);

    Task<AccountIdentity> GetIdentityAsync(CancellationToken ct);
    Task<QuotaInfo> GetQuotaAsync(CancellationToken ct);

    Task<IReadOnlyList<CloudItem>> ListChildrenAsync(string folderId, CancellationToken ct);
    Task<CloudItem?> GetItemAsync(string id, CancellationToken ct);

    /// <summary>Reads exactly the requested range (or less at EOF) straight from the cloud. Returns bytes read.</summary>
    Task<int> ReadRangeAsync(string fileId, long offset, Memory<byte> buffer, CancellationToken ct);

    Task<CloudItem> CreateFolderAsync(string parentId, string name, CancellationToken ct);
    Task<CloudItem> CreateEmptyFileAsync(string parentId, string name, uint? windowsAttributes, CancellationToken ct);

    /// <summary>Starts a resumable upload that streams content in order, chunk by chunk.</summary>
    Task<IUploadSession> BeginUploadAsync(UploadTarget target, CancellationToken ct);

    Task<CloudItem> UpdateMetadataAsync(string id, MetadataUpdate update, CancellationToken ct);
    Task DeleteAsync(string id, bool permanent, CancellationToken ct);

    Task<string> GetChangeTokenAsync(CancellationToken ct);
    Task<ChangeSet> GetChangesAsync(string token, CancellationToken ct);

    Task<IReadOnlyList<CloudItem>> SearchAsync(string text, int max, CancellationToken ct);
}

/// <summary>
/// An in-order streaming upload. Data is sent to the cloud as it is appended; nothing is
/// staged on a physical disk. The upload is only "done" after <see cref="CompleteAsync"/>
/// returns the committed item and its checksum has been verified by the caller.
/// </summary>
public interface IUploadSession : IAsyncDisposable
{
    /// <summary>Bytes the server has acknowledged so far.</summary>
    long Committed { get; }

    /// <summary>Non-final chunks must be a multiple of this size.</summary>
    int Granularity { get; }

    Task AppendAsync(ReadOnlyMemory<byte> data, CancellationToken ct);

    /// <summary>Sends the final bytes and commits the file with the given total size.</summary>
    Task<CloudItem> CompleteAsync(ReadOnlyMemory<byte> finalData, long totalSize, CancellationToken ct);

    Task AbortAsync();
}

/// <summary>Lets a provider use a cached root id when it cannot reach the cloud at start-up.</summary>
public interface IRootOverride
{
    void UseRootId(string rootId);
}
