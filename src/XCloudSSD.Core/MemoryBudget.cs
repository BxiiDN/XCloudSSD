namespace XCloudSSD.Core;

/// <summary>Something that holds reclaimable RAM (e.g. read chunks) and can give it back.</summary>
public interface IReclaimable
{
    /// <summary>Frees up to <paramref name="bytes"/> of reclaimable memory. Returns bytes freed.</summary>
    long Reclaim(long bytes);
}

/// <summary>
/// Hard upper bound for RAM used as I/O buffer (read chunks + pending writes). There is no
/// disk fallback: when the budget is exhausted and nothing can be reclaimed, the reservation fails.
/// </summary>
public sealed class MemoryBudget
{
    private readonly object _lock = new();
    private readonly List<WeakReference<IReclaimable>> _reclaimers = new();
    private long _used;
    private long _peak;

    public MemoryBudget(long capacityBytes) => Capacity = capacityBytes;

    public long Capacity { get; private set; }
    public long Used => Interlocked.Read(ref _used);
    public long Peak => Interlocked.Read(ref _peak);

    public void Resize(long capacityBytes)
    {
        lock (_lock) Capacity = Math.Max(16L << 20, capacityBytes);
    }

    public void Register(IReclaimable r)
    {
        lock (_lock) _reclaimers.Add(new WeakReference<IReclaimable>(r));
    }

    public void Unregister(IReclaimable r)
    {
        lock (_lock) _reclaimers.RemoveAll(w => !w.TryGetTarget(out var t) || ReferenceEquals(t, r));
    }

    /// <summary>
    /// Share of the budget kept free for "priority" reservations: pages that let a stalled upload move forward.
    /// Without it, a buffer full of out-of-order data could never receive the missing piece that unblocks it.
    /// </summary>
    public const double PriorityReserve = 0.15;

    /// <summary>Reserve bytes, reclaiming read buffers if necessary.</summary>
    public bool TryReserve(long bytes, IReclaimable? exclude = null) => TryReserve(bytes, exclude, priority: false);

    public bool TryReserve(long bytes, IReclaimable? exclude, bool priority)
    {
        if (bytes <= 0) return true;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            lock (_lock)
            {
                var limit = priority ? Capacity : (long)(Capacity * (1 - PriorityReserve));
                if (_used + bytes <= limit)
                {
                    _used += bytes;
                    if (_used > _peak) _peak = _used;
                    return true;
                }
            }
            ReclaimFromOthers(bytes, exclude);
        }
        return false;
    }

    public void Release(long bytes)
    {
        if (bytes <= 0) return;
        lock (_lock)
        {
            _used -= bytes;
            if (_used < 0) _used = 0;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>
    /// Back-pressure: waits (without holding any caller lock) until the bytes fit, e.g. while other files upload
    /// and free their pages. Returns false after <paramref name="timeout"/>.
    /// </summary>
    public bool ReserveOrWait(long bytes, bool priority, TimeSpan timeout, Func<bool>? makeProgress, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (TryReserve(bytes, null, priority)) return true;
            if (makeProgress?.Invoke() == true && TryReserve(bytes, null, priority)) return true;
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero || ct.IsCancellationRequested) return false;
            lock (_lock) Monitor.Wait(_lock, remaining < TimeSpan.FromMilliseconds(500) ? remaining : TimeSpan.FromMilliseconds(500));
        }
    }

    private void ReclaimFromOthers(long needed, IReclaimable? exclude)
    {
        List<IReclaimable> targets = new();
        lock (_lock)
        {
            _reclaimers.RemoveAll(w => !w.TryGetTarget(out _));
            foreach (var w in _reclaimers)
                if (w.TryGetTarget(out var r) && !ReferenceEquals(r, exclude)) targets.Add(r);
        }
        long freed = 0;
        foreach (var r in targets)
        {
            freed += r.Reclaim(needed - freed);
            if (freed >= needed) break;
        }
        if (freed < needed && exclude is not null)
            exclude.Reclaim(needed - freed);
    }
}
