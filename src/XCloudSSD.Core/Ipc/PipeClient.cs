using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace XCloudSSD.Core.Ipc;

/// <summary>Client used by the desktop app to talk to XCloudSSDService over a local named pipe.</summary>
public sealed class PipeClient
{
    private int _nextId;
    private readonly string _pipeName;

    public PipeClient(string? pipeName = null) => _pipeName = pipeName ?? AppPaths.PipeName;

    public async Task<T?> CallAsync<T>(string cmd, object? args = null, int timeoutMs = 15000, CancellationToken ct = default)
    {
        var resp = await SendAsync(cmd, args, timeoutMs, ct).ConfigureAwait(false);
        if (!resp.Ok) throw new InvalidOperationException(resp.Error ?? "Service error");
        return IpcJson.FromElement<T>(resp.Data);
    }

    public async Task CallAsync(string cmd, object? args = null, int timeoutMs = 15000, CancellationToken ct = default)
    {
        var resp = await SendAsync(cmd, args, timeoutMs, ct).ConfigureAwait(false);
        if (!resp.Ok) throw new InvalidOperationException(resp.Error ?? "Service error");
    }

    public async Task<bool> IsServiceRunningAsync()
    {
        try
        {
            var r = await SendAsync(Commands.Ping, null, 1500, CancellationToken.None).ConfigureAwait(false);
            return r.Ok;
        }
        catch
        {
            return false;
        }
    }

    private async Task<PipeResponse> SendAsync(string cmd, object? args, int timeoutMs, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(Math.Min(timeoutMs, 3000), cts.Token).ConfigureAwait(false);

        var req = new PipeRequest
        {
            Id = Interlocked.Increment(ref _nextId),
            Cmd = cmd,
            Args = args is null ? null : JsonSerializer.SerializeToElement(args, args.GetType(), IpcJson.Options),
        };
        var line = JsonSerializer.Serialize(req, IpcJson.Options) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        await pipe.WriteAsync(bytes, cts.Token).ConfigureAwait(false);
        await pipe.FlushAsync(cts.Token).ConfigureAwait(false);

        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 8192, leaveOpen: true);
        var respLine = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false)
                       ?? throw new IOException("Service closed the connection.");
        return JsonSerializer.Deserialize<PipeResponse>(respLine, IpcJson.Options)
               ?? throw new IOException("Invalid service response.");
    }
}
