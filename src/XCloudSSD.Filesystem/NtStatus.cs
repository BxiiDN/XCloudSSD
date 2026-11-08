using XCloudSSD.Core;
using XCloudSSD.Transfer;

namespace XCloudSSD.Filesystem;

/// <summary>NTSTATUS codes returned to the Windows I/O manager through WinFsp.</summary>
public static class NtStatus
{
    public const int Success = 0;
    public const int EndOfFile = unchecked((int)0xC0000011);
    public const int InvalidDeviceRequest = unchecked((int)0xC0000010);
    public const int InvalidParameter = unchecked((int)0xC000000D);
    public const int AccessDenied = unchecked((int)0xC0000022);
    public const int ObjectNameInvalid = unchecked((int)0xC0000033);
    public const int ObjectNameNotFound = unchecked((int)0xC0000034);
    public const int ObjectNameCollision = unchecked((int)0xC0000035);
    public const int ObjectPathNotFound = unchecked((int)0xC000003A);
    public const int DiskFull = unchecked((int)0xC000007F);
    public const int InsufficientResources = unchecked((int)0xC000009A);
    public const int IoTimeout = unchecked((int)0xC00000B5);
    public const int FileIsADirectory = unchecked((int)0xC00000BA);
    public const int NotSupported = unchecked((int)0xC00000BB);
    public const int UnexpectedIoError = unchecked((int)0xC00000E9);
    public const int DirectoryNotEmpty = unchecked((int)0xC0000101);
    public const int FileCorruptError = unchecked((int)0xC0000102);
    public const int NotADirectory = unchecked((int)0xC0000103);
    public const int Cancelled = unchecked((int)0xC0000120);
    public const int IoDeviceError = unchecked((int)0xC0000185);
    public const int NetworkUnreachable = unchecked((int)0xC000023C);

    public static int FromException(Exception ex)
    {
        while (ex is AggregateException { InnerException: { } inner }) ex = inner;
        return ex switch
        {
            CloudException ce => ce.Kind switch
            {
                CloudErrorKind.NotFound => ObjectNameNotFound,
                CloudErrorKind.AlreadyExists => ObjectNameCollision,
                CloudErrorKind.Unauthorized or CloudErrorKind.Forbidden => AccessDenied,
                CloudErrorKind.QuotaExceeded => DiskFull,
                CloudErrorKind.Offline => NetworkUnreachable,
                CloudErrorKind.Timeout => IoTimeout,
                CloudErrorKind.NotSupported => NotSupported,
                CloudErrorKind.IntegrityMismatch => FileCorruptError,
                _ => IoDeviceError,
            },
            WriteSessionException we => we.Failure switch
            {
                WriteFailure.BufferExhausted => InsufficientResources,
                WriteFailure.BelowStreamedData => NotSupported,
                _ => IoDeviceError,
            },
            OperationCanceledException => Cancelled,
            HttpRequestException => NetworkUnreachable,
            _ => UnexpectedIoError,
        };
    }
}
