using System.Diagnostics;

namespace XCloudSSD.Core;

/// <summary>Token-bucket limiter for one direction (upload or download). 0 = unlimited.</summary>
public sealed class BandwidthThrottle
{
    private readonly object _lock = new();
    private long _bytesPerSecond;
    private double _tokens;
    private long _lastTimestamp = Stopwatch.GetTimestamp();

    public static BandwidthThrottle Upload { get; } = new();
    public static BandwidthThrottle Download { get; } = new();

    public void SetLimitKBps(int kbps)
    {
        lock (_lock)
        {
            _bytesPerSecond = Math.Max(0, kbps) * 1024L;
            _tokens = _bytesPerSecond;
        }
    }

    public async Task WaitAsync(long bytes, CancellationToken ct)
    {
        while (true)
        {
            TimeSpan wait;
            lock (_lock)
            {
                if (_bytesPerSecond <= 0) return;
                var now = Stopwatch.GetTimestamp();
                var elapsed = Stopwatch.GetElapsedTime(_lastTimestamp, now).TotalSeconds;
                _lastTimestamp = now;
                _tokens = Math.Min(_bytesPerSecond, _tokens + elapsed * _bytesPerSecond);
                // Allow bursts larger than one second of budget by letting tokens go negative.
                if (_tokens > 0 || bytes > _bytesPerSecond && _tokens >= _bytesPerSecond * 0.99)
                {
                    _tokens -= bytes;
                    return;
                }
                wait = TimeSpan.FromSeconds(Math.Min(1.0, -_tokens / _bytesPerSecond + 0.01));
            }
            await Task.Delay(wait, ct).ConfigureAwait(false);
        }
    }
}
