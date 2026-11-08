using System.Text.Json;
using System.Text.Json.Serialization;

namespace XCloudSSD.Core.Ipc;

public static class Commands
{
    public const string Ping = "ping";
    public const string Status = "status";
    public const string GetSettings = "settings.get";
    public const string SetSettings = "settings.set";
    public const string GetOAuthClient = "oauth.get";
    public const string SetOAuthClient = "oauth.set";
    public const string AddGoogle = "account.addGoogle";
    public const string AddTeraBox = "account.addTeraBox";
    public const string RemoveAccount = "account.remove";
    public const string RenameAccount = "account.rename";
    public const string DisconnectAccount = "account.disconnect";
    public const string ReconnectAccount = "account.reconnect";
    public const string RemountAccount = "account.remount";
    public const string ChangeDriveLetter = "account.driveLetter";
    public const string FreeDriveLetters = "drives.free";
    public const string Transfers = "transfers.list";
    public const string ClearFinishedTransfers = "transfers.clear";
    public const string Search = "files.search";
}

public sealed class PipeRequest
{
    public int Id { get; set; }
    public string Cmd { get; set; } = "";
    public JsonElement? Args { get; set; }
}

public sealed class PipeResponse
{
    public int Id { get; set; }
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public JsonElement? Data { get; set; }
}

public sealed class AccountDto
{
    public string Id { get; set; } = "";
    public ProviderKind Provider { get; set; }
    public string Label { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Email { get; set; } = "";
    public char DriveLetter { get; set; }
    public bool Enabled { get; set; }
    public ConnectionState State { get; set; }
    public string StateMessage { get; set; } = "";
    public bool Mounted { get; set; }
    public long? TotalBytes { get; set; }
    public long UsedBytes { get; set; }
    public long? FreeBytes { get; set; }
    public DateTimeOffset? QuotaUpdated { get; set; }
    public bool CanMount { get; set; }
    public string CapabilityNotes { get; set; } = "";
    public int ActiveTransfers { get; set; }
    public int OpenHandles { get; set; }
}

public sealed class PerfDto
{
    public double UploadBytesPerSec { get; set; }
    public double DownloadBytesPerSec { get; set; }
    public double LatencyMs { get; set; } = -1;
    public long ActiveRequests { get; set; }
    public long RequestsTotal { get; set; }
    public long RequestErrors { get; set; }
    public long RamUsedBytes { get; set; }
    public long RamCapacityBytes { get; set; }
    public long RamPeakBytes { get; set; }
    public double FsOpsPerSec { get; set; }
    public double FsReadBytesPerSec { get; set; }
    public double FsWriteBytesPerSec { get; set; }
    public long OpenHandles { get; set; }
    public int TransferQueue { get; set; }
    public DateTimeOffset SampledAt { get; set; }
}

public sealed class ServiceStatusDto
{
    public string Version { get; set; } = "";
    public bool InternetAvailable { get; set; }
    public bool WinFspInstalled { get; set; }
    public string? WinFspVersion { get; set; }
    public bool OAuthClientConfigured { get; set; }
    public List<AccountDto> Accounts { get; set; } = new();
    public PerfDto Perf { get; set; } = new();
}

public enum TransferDirection { Upload, Download }

public enum TransferState
{
    Pending, Uploading, Downloading, Processing, Completed, Failed, Interrupted, Retrying, Offline,
}

public sealed class TransferDto
{
    public long Id { get; set; }
    public string AccountId { get; set; } = "";
    public char DriveLetter { get; set; }
    public string Path { get; set; } = "";
    public TransferDirection Direction { get; set; }
    public TransferState State { get; set; }
    public long BytesDone { get; set; }
    public long BytesTotal { get; set; }
    public int Attempts { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset Started { get; set; }
    public DateTimeOffset Updated { get; set; }
}

public sealed class AddGoogleArgs
{
    public string Code { get; set; } = "";
    public string CodeVerifier { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public char? DriveLetter { get; set; }
}

public sealed class AddTeraBoxArgs
{
    public char? DriveLetter { get; set; }
}

public sealed class AccountArgs
{
    public string AccountId { get; set; } = "";
}

public sealed class RenameArgs
{
    public string AccountId { get; set; } = "";
    public string Label { get; set; } = "";
}

public sealed class DriveLetterArgs
{
    public string AccountId { get; set; } = "";
    public char Letter { get; set; }
}

public sealed class OAuthClientDto
{
    public string ClientId { get; set; } = "";
    /// <summary>Write-only from the UI. The service never returns the stored secret.</summary>
    public string? ClientSecret { get; set; }
    public bool HasSecret { get; set; }
}

public sealed class SearchArgs
{
    public string AccountId { get; set; } = "";
    public string Text { get; set; } = "";
    public int Max { get; set; } = 200;
}

public sealed class SearchResultDto
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public bool IsFolder { get; set; }
    public DateTimeOffset Modified { get; set; }
}

public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static T? FromElement<T>(JsonElement? e) => e is null ? default : e.Value.Deserialize<T>(Options);
}
