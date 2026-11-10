using System.Collections.Concurrent;
using XCloudSSD.Core.Ipc;
using XCloudSSD.Metadata;

namespace XCloudSSD.Transfer;

/// <summary>
/// Journal of uploads/downloads shown in the Transfer Manager. Active entries live in memory and are
/// persisted to the local database on state changes (and at most every two seconds for progress).
/// </summary>
public sealed class TransferTracker
{
    private readonly AppDatabase? _db;
    private readonly ConcurrentDictionary<long, TransferHandle> _active = new();
    private long _memoryIds = -1;

    public TransferTracker(AppDatabase? db) => _db = db;

    public TransferHandle Begin(string accountId, char letter, string path, TransferDirection direction, long total)
    {
        var now = DateTimeOffset.UtcNow;
        var dto = new TransferDto
        {
            AccountId = accountId,
            DriveLetter = letter,
            Path = path,
            Direction = direction,
            State = TransferState.Pending,
            BytesTotal = total,
            Started = now,
            Updated = now,
        };
        dto.Id = _db?.InsertTransfer(dto) ?? Interlocked.Decrement(ref _memoryIds);
        var h = new TransferHandle(this, dto);
        _active[dto.Id] = h;
        return h;
    }

    public int ActiveCount => _active.Values.Count(h => !h.IsFinished);

    public int ActiveCountFor(string accountId) => _active.Values.Count(h => !h.IsFinished && h.Snapshot().AccountId == accountId);

    public List<TransferDto> List(int limit = 300)
    {
        var active = _active.Values.Select(h => h.Snapshot()).ToList();
        var ids = active.Select(a => a.Id).ToHashSet();
        var stored = _db?.GetTransfers(limit) ?? new List<TransferDto>();
        return active.Concat(stored.Where(s => !ids.Contains(s.Id)))
            .OrderByDescending(t => t.Updated)
            .Take(limit)
            .ToList();
    }

    public void ClearFinished()
    {
        foreach (var (id, h) in _active)
            if (h.IsFinished) _active.TryRemove(id, out _);
        _db?.ClearFinishedTransfers();
    }

    internal void Persist(TransferDto dto)
    {
        try
        {
            if (dto.Id > 0) _db?.UpdateTransfer(dto);
        }
        catch
        {
            // Journal persistence is best effort; the transfer itself is unaffected.
        }
    }

    internal void Finished(TransferHandle h)
    {
        // Keep finished entries visible briefly from memory; the database keeps the history.
        _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => _active.TryRemove(h.Id, out var __));
    }
}

public sealed class TransferHandle
{
    private readonly TransferTracker _owner;
    private readonly TransferDto _dto;
    private readonly object _lock = new();
    private DateTime _lastPersist = DateTime.MinValue;

    internal TransferHandle(TransferTracker owner, TransferDto dto)
    {
        _owner = owner;
        _dto = dto;
    }

    public long Id => _dto.Id;

    public bool IsFinished
    {
        get
        {
            lock (_lock) return _dto.State is TransferState.Completed or TransferState.Failed or TransferState.Interrupted;
        }
    }

    public TransferDto Snapshot()
    {
        lock (_lock)
        {
            return new TransferDto
            {
                Id = _dto.Id, AccountId = _dto.AccountId, DriveLetter = _dto.DriveLetter, Path = _dto.Path,
                Direction = _dto.Direction, State = _dto.State, BytesDone = _dto.BytesDone, BytesTotal = _dto.BytesTotal,
                Attempts = _dto.Attempts, Error = _dto.Error, Started = _dto.Started, Updated = _dto.Updated,
            };
        }
    }

    public void Progress(long done, long total)
    {
        TransferDto? persist = null;
        lock (_lock)
        {
            _dto.BytesDone = done;
            _dto.BytesTotal = Math.Max(total, done);
            if (_dto.State is TransferState.Pending or TransferState.Retrying or TransferState.Offline)
                _dto.State = _dto.Direction == TransferDirection.Upload ? TransferState.Uploading : TransferState.Downloading;
            _dto.Updated = DateTimeOffset.UtcNow;
            if ((DateTime.UtcNow - _lastPersist).TotalSeconds >= 2)
            {
                _lastPersist = DateTime.UtcNow;
                persist = Snapshot();
            }
        }
        if (persist is not null) _owner.Persist(persist);
    }

    /// <summary>Sets the attempt count reported by the uploader itself (e.g. rclone's tries).</summary>
    public void SetAttempts(int attempts)
    {
        lock (_lock) _dto.Attempts = Math.Max(0, attempts);
    }

    public void SetPath(string path)
    {
        lock (_lock) _dto.Path = path;
    }

    public void SetState(TransferState state, string? error = null)
    {
        TransferDto snap;
        bool finished;
        lock (_lock)
        {
            _dto.State = state;
            if (state is TransferState.Retrying) _dto.Attempts++;
            if (error is not null) _dto.Error = error;
            if (state == TransferState.Completed)
            {
                _dto.Error = null;
                _dto.BytesDone = _dto.BytesTotal;
            }
            _dto.Updated = DateTimeOffset.UtcNow;
            snap = Snapshot();
            finished = state is TransferState.Completed or TransferState.Failed or TransferState.Interrupted;
        }
        _owner.Persist(snap);
        if (finished) _owner.Finished(this);
    }
}
