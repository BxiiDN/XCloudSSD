using System.Net;
using System.Net.Sockets;
using XCloudSSD.Core;

namespace XCloudSSD.Cloud;

/// <summary>Exponential backoff with jitter for transient cloud errors (429, 5xx, network, timeout).</summary>
public static class Retry
{
    public static async Task<T> RunAsync<T>(Func<int, Task<T>> action, int maxAttempts, CancellationToken ct,
        Action<int, Exception>? onRetry = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action(attempt).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < maxAttempts && IsRetryable(ex) && !ct.IsCancellationRequested)
            {
                onRetry?.Invoke(attempt, ex);
                await Task.Delay(Backoff(attempt, ex), ct).ConfigureAwait(false);
            }
        }
    }

    public static Task RunAsync(Func<int, Task> action, int maxAttempts, CancellationToken ct, Action<int, Exception>? onRetry = null)
        => RunAsync<bool>(async a => { await action(a).ConfigureAwait(false); return true; }, maxAttempts, ct, onRetry);

    public static bool IsRetryable(Exception ex) => ex switch
    {
        CloudException ce => ce.IsRetryable,
        HttpRequestException => true,
        IOException => true,
        SocketException => true,
        TimeoutException => true,
        TaskCanceledException => true,
        _ => false,
    };

    public static TimeSpan Backoff(int attempt, Exception? ex = null)
    {
        var baseMs = ex is CloudException { Kind: CloudErrorKind.RateLimited } ? 2000 : 500;
        var ms = Math.Min(30_000, baseMs * Math.Pow(2, attempt - 1));
        return TimeSpan.FromMilliseconds(ms * (0.75 + Random.Shared.NextDouble() * 0.5));
    }

    /// <summary>Converts low-level network failures into <see cref="CloudException"/>.</summary>
    public static CloudException Classify(Exception ex, CancellationToken ct) => ex switch
    {
        CloudException ce => ce,
        TaskCanceledException when !ct.IsCancellationRequested => new CloudException(CloudErrorKind.Timeout, "Cloud request timed out.", null, ex),
        HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError }
            => new CloudException(CloudErrorKind.Offline, "No connection to the cloud provider.", null, ex),
        HttpRequestException => new CloudException(CloudErrorKind.Transient, "Network error: " + ex.Message, null, ex),
        IOException or SocketException => new CloudException(CloudErrorKind.Transient, "Network error: " + ex.Message, null, ex),
        _ => new CloudException(CloudErrorKind.Unknown, ex.Message, null, ex),
    };

    public static CloudErrorKind KindForStatus(HttpStatusCode code, string? reason = null) => (int)code switch
    {
        400 => CloudErrorKind.Unknown,
        401 => CloudErrorKind.Unauthorized,
        403 when reason is "storageQuotaExceeded" or "quotaExceeded" => CloudErrorKind.QuotaExceeded,
        403 when reason is "userRateLimitExceeded" or "rateLimitExceeded" => CloudErrorKind.RateLimited,
        403 => CloudErrorKind.Forbidden,
        404 => CloudErrorKind.NotFound,
        408 => CloudErrorKind.Timeout,
        409 => CloudErrorKind.Conflict,
        412 => CloudErrorKind.Conflict,
        416 => CloudErrorKind.Unknown,
        429 => CloudErrorKind.RateLimited,
        >= 500 => CloudErrorKind.Transient,
        _ => CloudErrorKind.Unknown,
    };
}
