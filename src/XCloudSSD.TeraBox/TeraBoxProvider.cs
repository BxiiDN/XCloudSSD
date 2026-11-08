using XCloudSSD.Core;

namespace XCloudSSD.TeraBox;

/// <summary>
/// TeraBox integration status, stated plainly.
///
/// TeraBox's official programmatic access is the "TeraBox Open Platform", which is offered to
/// partners on request (contact form / e-mail) and whose endpoint documentation is not publicly
/// readable. XCloudSSD does not use the private web API, does not read browser cookies, and does
/// not invent endpoints. Until Open Platform credentials and documentation are available, this
/// adapter reports that it cannot mount and every file operation fails with a clear message.
/// </summary>
public sealed class TeraBoxProvider : ICloudProvider
{
    public const string OfficialSite = "https://www.terabox.com/";
    public const string OpenPlatformDocs = "https://www.terabox.com/integrations/docs";

    public const string UnavailableMessage =
        "TeraBox file access requires TeraBox Open Platform partner credentials. TeraBox grants them on request " +
        "(see " + OpenPlatformDocs + "). XCloudSSD does not use unofficial endpoints or browser cookies, so TeraBox " +
        "disks cannot be mounted yet.";

    public static readonly ProviderCapabilities Status = new(
        CanMount: false, RangeRead: false, ResumableUpload: false, Rename: false, Move: false, Trash: false,
        QuotaInfo: false, ChangeFeed: false, CustomProperties: false, Notes: UnavailableMessage);

    public ProviderKind Kind => ProviderKind.TeraBox;
    public ProviderCapabilities Capabilities => Status;
    public string RootId => "/";

    private static CloudException NotAvailable() => new(CloudErrorKind.NotSupported, UnavailableMessage);

    public Task InitializeAsync(CancellationToken ct) => throw NotAvailable();
    public Task<AccountIdentity> GetIdentityAsync(CancellationToken ct) => throw NotAvailable();
    public Task<QuotaInfo> GetQuotaAsync(CancellationToken ct) => throw NotAvailable();
    public Task<IReadOnlyList<CloudItem>> ListChildrenAsync(string folderId, CancellationToken ct) => throw NotAvailable();
    public Task<CloudItem?> GetItemAsync(string id, CancellationToken ct) => throw NotAvailable();
    public Task<int> ReadRangeAsync(string fileId, long offset, Memory<byte> buffer, CancellationToken ct) => throw NotAvailable();
    public Task<CloudItem> CreateFolderAsync(string parentId, string name, CancellationToken ct) => throw NotAvailable();
    public Task<CloudItem> CreateEmptyFileAsync(string parentId, string name, uint? windowsAttributes, CancellationToken ct) => throw NotAvailable();
    public Task<IUploadSession> BeginUploadAsync(UploadTarget target, CancellationToken ct) => throw NotAvailable();
    public Task<CloudItem> UpdateMetadataAsync(string id, MetadataUpdate update, CancellationToken ct) => throw NotAvailable();
    public Task DeleteAsync(string id, bool permanent, CancellationToken ct) => throw NotAvailable();
    public Task<string> GetChangeTokenAsync(CancellationToken ct) => throw NotAvailable();
    public Task<ChangeSet> GetChangesAsync(string token, CancellationToken ct) => throw NotAvailable();
    public Task<IReadOnlyList<CloudItem>> SearchAsync(string text, int max, CancellationToken ct) => throw NotAvailable();

    public void Dispose()
    {
    }
}
