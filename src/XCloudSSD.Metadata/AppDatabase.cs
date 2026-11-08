using System.Text.Json;
using Microsoft.Data.Sqlite;
using XCloudSSD.Core;
using XCloudSSD.Core.Ipc;

namespace XCloudSSD.Metadata;

/// <summary>
/// Small local SQLite database: account configuration, settings and the transfer journal.
/// It stores metadata only — never file content.
/// </summary>
public sealed class AppDatabase
{
    private readonly string _cs;
    private readonly object _lock = new();

    public AppDatabase(string path)
    {
        _cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        Migrate();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        using var p = c.CreateCommand();
        p.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;";
        p.ExecuteNonQuery();
        return c;
    }

    private void Migrate()
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_version (v INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS accounts (
                    id TEXT PRIMARY KEY,
                    provider INTEGER NOT NULL,
                    label TEXT NOT NULL,
                    display_name TEXT NOT NULL,
                    email TEXT NOT NULL,
                    drive_letter TEXT NOT NULL,
                    enabled INTEGER NOT NULL,
                    added_at TEXT NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS ux_accounts_letter ON accounts(drive_letter);
                CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS transfers (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    account_id TEXT NOT NULL,
                    path TEXT NOT NULL,
                    direction INTEGER NOT NULL,
                    state INTEGER NOT NULL,
                    bytes_done INTEGER NOT NULL,
                    bytes_total INTEGER NOT NULL,
                    attempts INTEGER NOT NULL,
                    error TEXT,
                    started TEXT NOT NULL,
                    updated TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_transfers_state ON transfers(state);
                """;
            cmd.ExecuteNonQuery();
        }
    }

    // ---------------- accounts ----------------

    public List<AccountRecord> GetAccounts()
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT id, provider, label, display_name, email, drive_letter, enabled, added_at FROM accounts ORDER BY added_at";
            using var r = cmd.ExecuteReader();
            var list = new List<AccountRecord>();
            while (r.Read())
            {
                list.Add(new AccountRecord
                {
                    Id = r.GetString(0),
                    Provider = (ProviderKind)r.GetInt32(1),
                    Label = r.GetString(2),
                    DisplayName = r.GetString(3),
                    Email = r.GetString(4),
                    DriveLetter = r.GetString(5)[0],
                    Enabled = r.GetInt32(6) != 0,
                    AddedAt = DateTimeOffset.Parse(r.GetString(7)),
                });
            }
            return list;
        }
    }

    public void UpsertAccount(AccountRecord a)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO accounts (id, provider, label, display_name, email, drive_letter, enabled, added_at)
                VALUES ($id, $p, $l, $d, $e, $dl, $en, $at)
                ON CONFLICT(id) DO UPDATE SET label=$l, display_name=$d, email=$e, drive_letter=$dl, enabled=$en;
                """;
            cmd.Parameters.AddWithValue("$id", a.Id);
            cmd.Parameters.AddWithValue("$p", (int)a.Provider);
            cmd.Parameters.AddWithValue("$l", a.Label);
            cmd.Parameters.AddWithValue("$d", a.DisplayName);
            cmd.Parameters.AddWithValue("$e", a.Email);
            cmd.Parameters.AddWithValue("$dl", char.ToUpperInvariant(a.DriveLetter).ToString());
            cmd.Parameters.AddWithValue("$en", a.Enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$at", a.AddedAt.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteAccount(string id)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM accounts WHERE id=$id; DELETE FROM transfers WHERE account_id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    // ---------------- settings ----------------

    public AppSettings LoadSettings()
    {
        var json = GetValue("app_settings");
        if (json is null) return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public void SaveSettings(AppSettings s) => SetValue("app_settings", JsonSerializer.Serialize(s));

    public string? GetValue(string key)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT value FROM settings WHERE key=$k";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetValue(string key, string value)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO settings(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value=$v";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }
    }

    // ---------------- transfer journal ----------------

    public long InsertTransfer(TransferDto t)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO transfers (account_id, path, direction, state, bytes_done, bytes_total, attempts, error, started, updated)
                VALUES ($a, $p, $d, $s, $bd, $bt, $at, $e, $st, $up);
                SELECT last_insert_rowid();
                """;
            Bind(cmd, t);
            return (long)cmd.ExecuteScalar()!;
        }
    }

    public void UpdateTransfer(TransferDto t)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                UPDATE transfers SET path=$p, state=$s, bytes_done=$bd, bytes_total=$bt, attempts=$at, error=$e, updated=$up
                WHERE id=$id
                """;
            Bind(cmd, t);
            cmd.Parameters.AddWithValue("$id", t.Id);
            cmd.ExecuteNonQuery();
        }
    }

    public List<TransferDto> GetTransfers(int limit = 500)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                SELECT id, account_id, path, direction, state, bytes_done, bytes_total, attempts, error, started, updated
                FROM transfers ORDER BY id DESC LIMIT $n
                """;
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            var list = new List<TransferDto>();
            while (r.Read())
            {
                list.Add(new TransferDto
                {
                    Id = r.GetInt64(0),
                    AccountId = r.GetString(1),
                    Path = r.GetString(2),
                    Direction = (TransferDirection)r.GetInt32(3),
                    State = (TransferState)r.GetInt32(4),
                    BytesDone = r.GetInt64(5),
                    BytesTotal = r.GetInt64(6),
                    Attempts = r.GetInt32(7),
                    Error = r.IsDBNull(8) ? null : r.GetString(8),
                    Started = DateTimeOffset.Parse(r.GetString(9)),
                    Updated = DateTimeOffset.Parse(r.GetString(10)),
                });
            }
            return list;
        }
    }

    /// <summary>
    /// After a crash or power loss, uploads that were in flight cannot be finished: their pending bytes
    /// lived only in RAM. Mark them Interrupted so the user can see exactly which files were affected.
    /// </summary>
    public int MarkInFlightAsInterrupted()
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                UPDATE transfers SET state=$i, error=$e, updated=$u
                WHERE state IN ($p1, $p2, $p3, $p4, $p5, $p6)
                """;
            cmd.Parameters.AddWithValue("$i", (int)TransferState.Interrupted);
            cmd.Parameters.AddWithValue("$e", "Service stopped before the upload was committed. The unsaved data was held only in RAM; copy the file again.");
            cmd.Parameters.AddWithValue("$u", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$p1", (int)TransferState.Pending);
            cmd.Parameters.AddWithValue("$p2", (int)TransferState.Uploading);
            cmd.Parameters.AddWithValue("$p3", (int)TransferState.Downloading);
            cmd.Parameters.AddWithValue("$p4", (int)TransferState.Processing);
            cmd.Parameters.AddWithValue("$p5", (int)TransferState.Retrying);
            cmd.Parameters.AddWithValue("$p6", (int)TransferState.Offline);
            return cmd.ExecuteNonQuery();
        }
    }

    public int ClearFinishedTransfers()
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM transfers WHERE state IN ($c, $f, $i)";
            cmd.Parameters.AddWithValue("$c", (int)TransferState.Completed);
            cmd.Parameters.AddWithValue("$f", (int)TransferState.Failed);
            cmd.Parameters.AddWithValue("$i", (int)TransferState.Interrupted);
            return cmd.ExecuteNonQuery();
        }
    }

    private static void Bind(SqliteCommand cmd, TransferDto t)
    {
        cmd.Parameters.AddWithValue("$a", t.AccountId);
        cmd.Parameters.AddWithValue("$p", t.Path);
        cmd.Parameters.AddWithValue("$d", (int)t.Direction);
        cmd.Parameters.AddWithValue("$s", (int)t.State);
        cmd.Parameters.AddWithValue("$bd", t.BytesDone);
        cmd.Parameters.AddWithValue("$bt", t.BytesTotal);
        cmd.Parameters.AddWithValue("$at", t.Attempts);
        cmd.Parameters.AddWithValue("$e", (object?)t.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$st", t.Started.ToString("O"));
        cmd.Parameters.AddWithValue("$up", t.Updated.ToString("O"));
    }
}
