using System.Text.Json;
using System.Text.Json.Serialization;
using XCloudSSD.Core;
using XCloudSSD.Core.Ipc;

namespace XCloudSSD.Metadata;

/// <summary>
/// Small local store: account configuration, settings, key/value state and the transfer journal.
/// Stored as JSON files written atomically (temp file + replace). Holds metadata only — never file content.
/// The transfer journal is flushed at most every two seconds so thousands of small files do not cause
/// thousands of disk writes.
/// </summary>
public sealed class AppDatabase : IDisposable
{
    private const int JournalLimit = 2000;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _dir;
    private readonly object _lock = new();
    private readonly List<AccountRecord> _accounts;
    private readonly Dictionary<string, string> _values;
    private readonly List<TransferDto> _transfers;
    private long _nextTransferId;
    private bool _journalDirty;
    private readonly Timer _flushTimer;

    /// <param name="path">Directory for the store, or a file path whose directory is used.</param>
    public AppDatabase(string path)
    {
        _dir = Path.HasExtension(path) ? Path.GetDirectoryName(Path.GetFullPath(path))! : path;
        Directory.CreateDirectory(_dir);
        _accounts = Read<List<AccountRecord>>("accounts.json") ?? new();
        _values = Read<Dictionary<string, string>>("state.json") ?? new();
        _transfers = Read<List<TransferDto>>("transfers.json") ?? new();
        _nextTransferId = _transfers.Count == 0 ? 0 : _transfers.Max(t => t.Id);
        _flushTimer = new Timer(_ => FlushJournal(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private T? Read<T>(string name)
    {
        var file = Path.Combine(_dir, name);
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<T>(File.ReadAllText(file), Json) : default;
        }
        catch (JsonException)
        {
            // Keep the unreadable file for diagnosis and start fresh.
            File.Copy(file, file + ".corrupt", overwrite: true);
            return default;
        }
    }

    private void Write<T>(string name, T value)
    {
        var file = Path.Combine(_dir, name);
        var tmp = file + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(fs, value, Json);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, file, overwrite: true);
    }

    // ---------------- accounts ----------------

    public List<AccountRecord> GetAccounts()
    {
        lock (_lock) return _accounts.OrderBy(a => a.AddedAt).Select(Clone).ToList();
    }

    public void UpsertAccount(AccountRecord a)
    {
        lock (_lock)
        {
            var letter = char.ToUpperInvariant(a.DriveLetter);
            if (_accounts.Any(x => x.Id != a.Id && char.ToUpperInvariant(x.DriveLetter) == letter))
                throw new InvalidOperationException($"Drive letter {letter}: is already used by another account.");
            var copy = Clone(a);
            copy.DriveLetter = letter;
            var idx = _accounts.FindIndex(x => x.Id == a.Id);
            if (idx >= 0) _accounts[idx] = copy;
            else _accounts.Add(copy);
            Write("accounts.json", _accounts);
        }
    }

    public void DeleteAccount(string id)
    {
        lock (_lock)
        {
            _accounts.RemoveAll(a => a.Id == id);
            _transfers.RemoveAll(t => t.AccountId == id);
            foreach (var k in _values.Keys.Where(k => k.EndsWith(":" + id, StringComparison.Ordinal)).ToList()) _values.Remove(k);
            Write("accounts.json", _accounts);
            Write("state.json", _values);
            _journalDirty = true;
        }
    }

    private static AccountRecord Clone(AccountRecord a) => new()
    {
        Id = a.Id,
        Provider = a.Provider,
        DisplayName = a.DisplayName,
        Email = a.Email,
        DriveLetter = a.DriveLetter,
        Enabled = a.Enabled,
        AddedAt = a.AddedAt,
        Label = a.Label,
    };

    // ---------------- settings & state ----------------

    public AppSettings LoadSettings()
    {
        var json = GetValue("app_settings");
        if (json is null) return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(json, Json) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public void SaveSettings(AppSettings s) => SetValue("app_settings", JsonSerializer.Serialize(s, Json));

    public string? GetValue(string key)
    {
        lock (_lock) return _values.TryGetValue(key, out var v) ? v : null;
    }

    public void SetValue(string key, string value)
    {
        lock (_lock)
        {
            if (_values.TryGetValue(key, out var old) && old == value) return;
            _values[key] = value;
            Write("state.json", _values);
        }
    }

    // ---------------- transfer journal ----------------

    public long InsertTransfer(TransferDto t)
    {
        lock (_lock)
        {
            t.Id = ++_nextTransferId;
            _transfers.Add(Copy(t));
            if (_transfers.Count > JournalLimit) _transfers.RemoveRange(0, _transfers.Count - JournalLimit);
            _journalDirty = true;
            return t.Id;
        }
    }

    public void UpdateTransfer(TransferDto t)
    {
        lock (_lock)
        {
            var idx = _transfers.FindIndex(x => x.Id == t.Id);
            if (idx < 0) return;
            _transfers[idx] = Copy(t);
            _journalDirty = true;
        }
    }

    public List<TransferDto> GetTransfers(int limit = 500)
    {
        lock (_lock) return _transfers.OrderByDescending(t => t.Id).Take(limit).Select(Copy).ToList();
    }

    /// <summary>
    /// After a crash or power loss, uploads that were in flight cannot be finished: their pending bytes
    /// lived only in RAM. Mark them Interrupted so the user can see exactly which files were affected.
    /// </summary>
    public int MarkInFlightAsInterrupted()
    {
        lock (_lock)
        {
            var n = 0;
            foreach (var t in _transfers.Where(t => t.State is TransferState.Pending or TransferState.Uploading
                         or TransferState.Downloading or TransferState.Processing or TransferState.Retrying or TransferState.Offline))
            {
                t.State = TransferState.Interrupted;
                t.Error = "Service stopped before the upload was committed. The unsaved data was held only in RAM; copy the file again.";
                t.Updated = DateTimeOffset.UtcNow;
                n++;
            }
            if (n > 0) WriteJournal();
            return n;
        }
    }

    public int ClearFinishedTransfers()
    {
        lock (_lock)
        {
            var n = _transfers.RemoveAll(t => t.State is TransferState.Completed or TransferState.Failed or TransferState.Interrupted);
            WriteJournal();
            return n;
        }
    }

    public void FlushJournal()
    {
        lock (_lock)
        {
            if (!_journalDirty) return;
            try
            {
                WriteJournal();
            }
            catch (IOException)
            {
                // Try again on the next tick.
            }
        }
    }

    private void WriteJournal()
    {
        Write("transfers.json", _transfers);
        _journalDirty = false;
    }

    private static TransferDto Copy(TransferDto t) => new()
    {
        Id = t.Id, AccountId = t.AccountId, DriveLetter = t.DriveLetter, Path = t.Path, Direction = t.Direction,
        State = t.State, BytesDone = t.BytesDone, BytesTotal = t.BytesTotal, Attempts = t.Attempts, Error = t.Error,
        Started = t.Started, Updated = t.Updated,
    };

    public void Dispose()
    {
        _flushTimer.Dispose();
        FlushJournal();
    }
}
