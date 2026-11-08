namespace XCloudSSD.Core;

public enum CloudErrorKind
{
    Unknown,
    NotFound,
    AlreadyExists,
    Unauthorized,
    Forbidden,
    QuotaExceeded,
    RateLimited,
    Offline,
    Timeout,
    Transient,
    NotSupported,
    Conflict,
    IntegrityMismatch,
}

public sealed class CloudException : Exception
{
    public CloudErrorKind Kind { get; }
    public int? HttpStatus { get; }

    public CloudException(CloudErrorKind kind, string message, int? httpStatus = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        HttpStatus = httpStatus;
    }

    public bool IsRetryable => Kind is CloudErrorKind.RateLimited or CloudErrorKind.Transient
        or CloudErrorKind.Timeout or CloudErrorKind.Offline;
}
