using System.Diagnostics;

namespace XCloudSSD.Core;

/// <summary>
/// Live counters measured from real HTTP requests and filesystem calls. Nothing here is estimated:
/// every value comes from an actual byte count, timer or in-flight counter.
/// </summary>
public sealed class Metrics
{
    private long _bytesUp, _bytesDown, _requests, _requestErrors, _fsOps, _fsReadBytes, _fsWriteBytes;
    private long _activeRequests, _openHandles;
    private long _latencyTicksSum, _latencySamples;
    private long _lastLatencyMs = -1;

    public static Metrics Global { get; } = new();

    public long BytesUp => Interlocked.Read(ref _bytesUp);
    public long BytesDown => Interlocked.Read(ref _bytesDown);
    public long Requests => Interlocked.Read(ref _requests);
    public long RequestErrors => Interlocked.Read(ref _requestErrors);
    public long FsOps => Interlocked.Read(ref _fsOps);
    public long FsReadBytes => Interlocked.Read(ref _fsReadBytes);
    public long FsWriteBytes => Interlocked.Read(ref _fsWriteBytes);
    public long ActiveRequests => Interlocked.Read(ref _activeRequests);
    public long OpenHandles => Interlocked.Read(ref _openHandles);
    public long LastLatencyMs => Interlocked.Read(ref _lastLatencyMs);

    public void AddUp(long n) { Interlocked.Add(ref _bytesUp, n); if (this != Global) Global.AddUp(n); }
    public void AddDown(long n) { Interlocked.Add(ref _bytesDown, n); if (this != Global) Global.AddDown(n); }
    public void FsOp() { Interlocked.Increment(ref _fsOps); if (this != Global) Global.FsOp(); }
    public void FsRead(long n) { Interlocked.Add(ref _fsReadBytes, n); if (this != Global) Global.FsRead(n); }
    public void FsWrite(long n) { Interlocked.Add(ref _fsWriteBytes, n); if (this != Global) Global.FsWrite(n); }
    public void HandleOpened() { Interlocked.Increment(ref _openHandles); if (this != Global) Global.HandleOpened(); }
    public void HandleClosed() { Interlocked.Decrement(ref _openHandles); if (this != Global) Global.HandleClosed(); }

    /// <summary>Tracks one cloud request from start to finish (latency = time to response headers).</summary>
    public RequestScope BeginRequest() => new(this);

    internal void RequestStarted()
    {
        Interlocked.Increment(ref _activeRequests);
        Interlocked.Increment(ref _requests);
        if (this != Global) Global.RequestStarted();
    }

    internal void RequestEnded(bool failed)
    {
        Interlocked.Decrement(ref _activeRequests);
        if (failed) Interlocked.Increment(ref _requestErrors);
        if (this != Global) Global.RequestEnded(failed);
    }

    internal void RecordLatency(TimeSpan t)
    {
        Interlocked.Add(ref _latencyTicksSum, t.Ticks);
        Interlocked.Increment(ref _latencySamples);
        Interlocked.Exchange(ref _lastLatencyMs, (long)t.TotalMilliseconds);
        if (this != Global) Global.RecordLatency(t);
    }

    /// <summary>Average latency since the last call (resets the window). -1 if no samples.</summary>
    public double TakeAverageLatencyMs()
    {
        var sum = Interlocked.Exchange(ref _latencyTicksSum, 0);
        var n = Interlocked.Exchange(ref _latencySamples, 0);
        return n == 0 ? -1 : TimeSpan.FromTicks(sum / n).TotalMilliseconds;
    }

    public readonly struct RequestScope : IDisposable
    {
        private readonly Metrics _m;
        private readonly long _start;
        private readonly bool[] _state; // [0]=latency recorded, [1]=failed

        internal RequestScope(Metrics m)
        {
            _m = m;
            _start = Stopwatch.GetTimestamp();
            _state = new bool[2];
            m.RequestStarted();
        }

        /// <summary>Call when response headers arrive.</summary>
        public void ResponseReceived()
        {
            if (_state[0]) return;
            _state[0] = true;
            _m.RecordLatency(Stopwatch.GetElapsedTime(_start));
        }

        public void Fail() => _state[1] = true;

        public void Dispose()
        {
            if (!_state[0]) ResponseReceived();
            _m.RequestEnded(_state[1]);
        }
    }
}
