using System.Text.Json;
using XCloudSSD.Core;

namespace XCloudSSD.Metadata;

/// <summary>JSON-backed <see cref="ISparseSizeStore"/> for one account, flushed at most every two seconds.</summary>
public sealed class SparseSizeStore : ISparseSizeStore, IDisposable
{
    private readonly string _file;
    private readonly object _lock = new();
    private readonly Dictionary<string, long> _sizes;
    private readonly Timer _timer;
    private bool _dirty;

    public SparseSizeStore(string directory, string accountId)
    {
        Directory.CreateDirectory(directory);
        _file = Path.Combine(directory, $"sparse-{accountId}.json");
        try
        {
            _sizes = File.Exists(_file) ? JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(_file)) ?? new() : new();
        }
        catch (JsonException)
        {
            _sizes = new();
        }
        _timer = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public long? Get(string fileId)
    {
        lock (_lock) return _sizes.TryGetValue(fileId, out var v) ? v : null;
    }

    public void Set(string fileId, long? logicalSize)
    {
        lock (_lock)
        {
            if (logicalSize is { } v) _sizes[fileId] = v;
            else if (!_sizes.Remove(fileId)) return;
            _dirty = true;
        }
    }

    public void Flush()
    {
        lock (_lock)
        {
            if (!_dirty) return;
            try
            {
                var tmp = _file + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_sizes));
                File.Move(tmp, _file, overwrite: true);
                _dirty = false;
            }
            catch (IOException)
            {
            }
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        Flush();
    }
}
