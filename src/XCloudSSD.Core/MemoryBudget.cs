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

    /// <summary>Reserve bytes, reclaiming read buffers if necessary.</summary>
    public bool TryReserve(long bytes, IReclaimable? exclude = null)
    {
        if (bytes <= 0) return true;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            lock (_lock)
            {
                if (_used + bytes <= Capacity)
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
