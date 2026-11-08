using System.Globalization;
using System.Net;
using XCloudSSD.Cloud;
using XCloudSSD.Core;

namespace XCloudSSD.GoogleDrive;

/// <summary>
/// Google Drive resumable upload session
/// (https://developers.google.com/drive/api/guides/manage-uploads#resumable).
/// Chunks are PUT in order; after a network failure the session is queried for the committed
/// offset and the unacknowledged tail of the current chunk is re-sent from RAM.
/// </summary>
internal sealed class DriveUploadSession : IUploadSession
{
    public const int Chunk = 256 * 1024;
    private readonly GoogleDriveProvider _owner;
    private readonly Uri _session;
    private long _committed;
    private bool _done;

    public DriveUploadSession(GoogleDriveProvider owner, Uri session)
    {
        _owner = owner;
        _session = session;
    }

    public long Committed => Interlocked.Read(ref _committed);
    public int Granularity => Chunk;

    public async Task AppendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (_done) throw new InvalidOperationException("Upload already completed.");
        if (data.Length == 0) return;
        if (data.Length % Chunk != 0) throw new ArgumentException("Non-final chunks must be a multiple of 256 KiB.");
        await SendAsync(data, totalSize: null, ct).ConfigureAwait(false);
    }

    public async Task<CloudItem> CompleteAsync(ReadOnlyMemory<byte> finalData, long totalSize, CancellationToken ct)
    {
        if (_done) throw new InvalidOperationException("Upload already completed.");
        if (Committed + finalData.Length != totalSize)
            throw new ArgumentException($"Size mismatch: committed {Committed} + final {finalData.Length} != total {totalSize}.");
        var item = await SendAsync(finalData, totalSize, ct).ConfigureAwait(false)
                   ?? throw new CloudException(CloudErrorKind.Unknown, "Upload finished without a file resource.");
        _done = true;
        return item;
    }

    private async Task<CloudItem?> SendAsync(ReadOnlyMemory<byte> data, long? totalSize, CancellationToken ct)
    {
        var baseOffset = Committed;
        var target = baseOffset + data.Length;
        var failures = 0;
        while (true)
        {
            var sentFrom = Committed - baseOffset;
            var slice = data[(int)sentFrom..];
            var start = baseOffset + sentFrom;
            var totalText = totalSize?.ToString(CultureInfo.InvariantCulture) ?? "*";
            var range = slice.Length == 0
                ? $"bytes */{totalText}"
                : $"bytes {start}-{start + slice.Length - 1}/{totalText}";
            try
            {
                using var resp = await _owner.PutChunkAsync(_session, slice, range, ct).ConfigureAwait(false);
                var status = (int)resp.StatusCode;
                if (status is 200 or 201)
                {
                    Interlocked.Exchange(ref _committed, target);
                    var json = await resp.Content.ReadUtf8Async(ct).ConfigureAwait(false);
                    return GoogleDriveProvider.ParseItemFromJson(json);
                }
                if (status == 308)
                {
                    var persisted = ParsePersisted(resp);
                    Interlocked.Exchange(ref _committed, persisted);
                    if (persisted >= target && totalSize is null) return null;
                    if (persisted < baseOffset)
                        throw new CloudException(CloudErrorKind.Conflict, "Drive lost already-acknowledged upload data.");
                    continue; // re-send the unacknowledged part
                }
                if (status is 404 or 410)
                    throw new CloudException(CloudErrorKind.Conflict, "Upload session expired on the server.", status);
                await _owner.EnsureSuccessPublicAsync(resp, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (Retry.IsRetryable(ex) && failures < 8 && !ct.IsCancellationRequested)
            {
                failures++;
                await Task.Delay(Retry.Backoff(failures, ex), ct).ConfigureAwait(false);
                await QueryStatusAsync(totalSize, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task QueryStatusAsync(long? totalSize, CancellationToken ct)
    {
        try
        {
            var range = $"bytes */{totalSize?.ToString(CultureInfo.InvariantCulture) ?? "*"}";
            using var resp = await _owner.PutChunkAsync(_session, ReadOnlyMemory<byte>.Empty, range, ct).ConfigureAwait(false);
            if ((int)resp.StatusCode == 308) Interlocked.Exchange(ref _committed, ParsePersisted(resp));
        }
        catch (Exception ex) when (Retry.IsRetryable(ex))
        {
            // Still offline; the outer loop retries.
        }
    }

    private static long ParsePersisted(HttpResponseMessage resp)
    {
        // "Range: bytes=0-524287" → 524288 bytes persisted. Missing header → nothing persisted yet.
        if (!resp.Headers.TryGetValues("Range", out var values)) return 0;
        var v = values.FirstOrDefault();
        if (v is null) return 0;
        var dash = v.LastIndexOf('-');
        return dash > 0 && long.TryParse(v[(dash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var last)
            ? last + 1
            : 0;
    }

    public Task AbortAsync()
    {
        _done = true;
        // Unfinished Drive sessions expire on their own (about one week) and never create a file.
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
