using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using XCloudSSD.Core;
using XCloudSSD.Core.Ipc;
using XCloudSSD.VirtualDrive;

namespace XCloudSSD.Service;

/// <summary>
/// Local control channel for the desktop app: one JSON request per line, one JSON response per line.
/// Only local, authenticated Windows users can connect (network clients are denied).
/// </summary>
public sealed class ControlPipeServer(DiskManager disks, ILogger<ControlPipeServer> log)
{
    private const int MaxInstances = 16;

    public async Task RunAsync(CancellationToken ct)
    {
        var workers = Enumerable.Range(0, 4).Select(_ => Task.Run(() => AcceptLoopAsync(ct), ct)).ToArray();
        try
        {
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private static PipeSecurity CreateSecurity()
    {
        var sec = new PipeSecurity();
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return sec;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(AppPaths.PipeName, PipeDirection.InOut, MaxInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, CreateSecurity());
                await pipe.WaitForConnectionAsync(ct);
                await HandleAsync(pipe, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning("Control pipe error: {Message}", ex.Message);
                await Task.Delay(200, CancellationToken.None);
            }
            finally
            {
                if (pipe is not null) await pipe.DisposeAsync();
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 8192, leaveOpen: true);
        var line = await reader.ReadLineAsync(ct);
        if (line is null) return;

        PipeResponse response;
        PipeRequest? req = null;
        try
        {
            req = JsonSerializer.Deserialize<PipeRequest>(line, IpcJson.Options) ?? throw new InvalidDataException("Empty request.");
            var data = await DispatchAsync(req, ct);
            response = new PipeResponse { Id = req.Id, Ok = true, Data = data };
        }
        catch (Exception ex)
        {
            var root = ex is AggregateException { InnerException: { } i } ? i : ex;
            log.LogWarning("Command {Cmd} failed: {Message}", req?.Cmd, root.Message);
            response = new PipeResponse { Id = req?.Id ?? 0, Ok = false, Error = root.Message };
        }

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response, IpcJson.Options) + "\n");
        await pipe.WriteAsync(bytes, ct);
        await pipe.FlushAsync(ct);
        try
        {
            pipe.WaitForPipeDrain();
        }
        catch (IOException)
        {
            // Client already gone.
        }
    }

    private async Task<JsonElement?> DispatchAsync(PipeRequest req, CancellationToken ct)
    {
        T Args<T>() => IpcJson.FromElement<T>(req.Args) ?? throw new InvalidDataException("Missing arguments.");
        switch (req.Cmd)
        {
            case Commands.Ping:
                return IpcJson.ToElement("pong");
            case Commands.Status:
                return IpcJson.ToElement(disks.GetStatus());
            case Commands.GetSettings:
                return IpcJson.ToElement(disks.Settings);
            case Commands.SetSettings:
                disks.UpdateSettings(Args<AppSettings>());
                return IpcJson.ToElement(disks.Settings);
            case Commands.GetOAuthClient:
                return IpcJson.ToElement(disks.GetOAuthClient());
            case Commands.SetOAuthClient:
                disks.SetOAuthClient(Args<OAuthClientDto>());
                return IpcJson.ToElement(disks.GetOAuthClient());
            case Commands.AddGoogle:
                return IpcJson.ToElement(await disks.AddGoogleAsync(Args<AddGoogleArgs>(), ct));
            case Commands.AddTeraBox:
                return IpcJson.ToElement(await disks.AddTeraBoxAsync(Args<AddTeraBoxArgs>(), ct));
            case Commands.RemoveAccount:
                await disks.RemoveAsync(Args<AccountArgs>().AccountId, ct);
                return null;
            case Commands.RenameAccount:
                {
                    var a = Args<RenameArgs>();
                    disks.Rename(a.AccountId, a.Label);
                    return null;
                }
            case Commands.DisconnectAccount:
                disks.Disconnect(Args<AccountArgs>().AccountId);
                return null;
            case Commands.ReconnectAccount:
                await disks.ReconnectAsync(Args<AccountArgs>().AccountId, ct);
                return null;
            case Commands.RemountAccount:
                await disks.RemountAsync(Args<AccountArgs>().AccountId, ct);
                return null;
            case Commands.ChangeDriveLetter:
                {
                    var a = Args<DriveLetterArgs>();
                    await disks.ChangeDriveLetterAsync(a.AccountId, a.Letter, ct);
                    return null;
                }
            case Commands.FreeDriveLetters:
                return IpcJson.ToElement(disks.FreeLetters().Select(c => c.ToString()).ToList());
            case Commands.Transfers:
                return IpcJson.ToElement(disks.Transfers.List());
            case Commands.ClearFinishedTransfers:
                disks.Transfers.ClearFinished();
                return null;
            case Commands.Search:
                return IpcJson.ToElement(await disks.SearchAsync(Args<SearchArgs>(), ct));
            default:
                throw new InvalidDataException($"Unknown command '{req.Cmd}'.");
        }
    }
}
