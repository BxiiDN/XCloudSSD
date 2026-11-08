using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using XCloudSSD.Authentication;
using XCloudSSD.Core;
using XCloudSSD.Core.Ipc;
using XCloudSSD.TeraBox;

namespace XCloudSSD.Desktop.Infrastructure;

/// <summary>Account/disk commands shared by the Disks and Accounts pages. Every action calls the service.</summary>
public static class AccountActions
{
    private static Window Owner => Application.Current.MainWindow!;

    public static AccountDto? Find(string id) => ServiceHub.Last?.Accounts.FirstOrDefault(a => a.Id == id);

    private static async Task Run(Func<Task> action)
    {
        try
        {
            Mouse(true);
            await action();
            await ServiceHub.RefreshAsync();
        }
        catch (Exception ex)
        {
            Dialogs.Error(Owner, ex);
        }
        finally
        {
            Mouse(false);
        }
    }

    private static void Mouse(bool busy) =>
        System.Windows.Input.Mouse.OverrideCursor = busy ? System.Windows.Input.Cursors.Wait : null;

    public static void OpenInExplorer(string id)
    {
        if (Find(id) is not { } a) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"{a.DriveLetter}:\\") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Dialogs.Error(Owner, ex);
        }
    }

    public static Task Remount(string id) => Run(() => ServiceHub.Client.CallAsync(Commands.RemountAccount, new AccountArgs { AccountId = id }, 120_000));

    public static async Task ChangeLetter(string id)
    {
        if (Find(id) is not { } a) return;
        List<string> free;
        try
        {
            free = await ServiceHub.Client.CallAsync<List<string>>(Commands.FreeDriveLetters) ?? new();
        }
        catch (Exception ex)
        {
            Dialogs.Error(Owner, ex);
            return;
        }
        var choice = Dialogs.Choose(Owner, L.T("ChangeLetter"), L.F("ChangeLetterPrompt", a.Label, a.DriveLetter), free.Select(f => f + ":"));
        if (choice is null) return;
        await Run(() => ServiceHub.Client.CallAsync(Commands.ChangeDriveLetter, new DriveLetterArgs { AccountId = id, Letter = choice[0] }, 120_000));
    }

    public static async Task Rename(string id)
    {
        if (Find(id) is not { } a) return;
        var name = Dialogs.Input(Owner, L.T("Rename"), L.T("RenamePrompt"), a.Label);
        if (string.IsNullOrWhiteSpace(name) || name == a.Label) return;
        await Run(() => ServiceHub.Client.CallAsync(Commands.RenameAccount, new RenameArgs { AccountId = id, Label = name }));
    }

    public static async Task Toggle(string id)
    {
        if (Find(id) is not { } a) return;
        if (a.Enabled)
        {
            if (!Dialogs.Confirm(Owner, L.T("Disconnect"), L.F("DisconnectConfirm", a.Label, a.DriveLetter))) return;
            await Run(() => ServiceHub.Client.CallAsync(Commands.DisconnectAccount, new AccountArgs { AccountId = id }, 120_000));
        }
        else
        {
            await Run(() => ServiceHub.Client.CallAsync(Commands.ReconnectAccount, new AccountArgs { AccountId = id }, 120_000));
        }
    }

    public static Task Reconnect(string id) =>
        Run(() => ServiceHub.Client.CallAsync(Commands.ReconnectAccount, new AccountArgs { AccountId = id }, 120_000));

    public static async Task Remove(string id)
    {
        if (Find(id) is not { } a) return;
        if (!Dialogs.Confirm(Owner, L.T("RemoveAccount"), L.F("RemoveConfirm", a.Email, a.DriveLetter), L.T("Remove"))) return;
        await Run(() => ServiceHub.Client.CallAsync(Commands.RemoveAccount, new AccountArgs { AccountId = id }, 180_000));
    }

    public static void Info(string id)
    {
        if (Find(id) is not { } a) return;
        var text = string.Join(Environment.NewLine, new[]
        {
            $"{L.T("Provider")}: {Fmt.Provider(a.Provider)}",
            $"{L.T("Name")}: {a.DisplayName}",
            $"{L.T("Email")}: {a.Email}",
            $"{L.T("DiskName")}: {a.Label}",
            $"{L.T("DriveLetter")}: {a.DriveLetter}:",
            $"{L.T("Status")}: {Fmt.State(a.State)} {a.StateMessage}",
            $"{L.T("Mounted")}: {(a.Mounted ? L.T("Yes") : L.T("No"))}",
            "",
            $"{L.T("CloudCapacity")}: {(a.TotalBytes is null ? L.T("Unlimited") : Fmt.Bytes(a.TotalBytes))}",
            $"{L.T("CloudUsed")}: {Fmt.Bytes(a.UsedBytes)}",
            $"{L.T("CloudAvailable")}: {(a.FreeBytes is null ? L.T("Unlimited") : Fmt.Bytes(a.FreeBytes))}",
            $"{L.T("DiskCapacity")}: {(a.TotalBytes is null ? L.T("UnlimitedShown") : Fmt.Bytes(a.TotalBytes))}",
            $"{L.T("QuotaUpdated")}: {(a.QuotaUpdated is { } q ? Fmt.Time(q) : "—")}",
            "",
            $"{L.T("OpenHandles")}: {a.OpenHandles}",
            $"{L.T("ActiveTransfers")}: {a.ActiveTransfers}",
            "",
            a.CapabilityNotes,
        });
        Dialogs.Info(Owner, a.Label, text);
    }

    // ------------------------------------------------------------------ adding accounts

    public static async Task AddGoogle()
    {
        OAuthClientDto? client;
        try
        {
            client = await ServiceHub.Client.CallAsync<OAuthClientDto>(Commands.GetOAuthClient);
        }
        catch (Exception ex)
        {
            Dialogs.Error(Owner, ex);
            return;
        }
        if (client is null || string.IsNullOrEmpty(client.ClientId) || !client.HasSecret)
        {
            Dialogs.Info(Owner, L.T("AddGoogle"), L.T("NeedOAuthClient"));
            ((MainWindow)Owner).Navigate("settings");
            return;
        }

        Dialogs.Info(Owner, L.T("AddGoogle"), L.T("BrowserSignInInfo"));
        try
        {
            Mouse(true);
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(5));
            var result = await LoopbackReceiver.SignInWithGoogleAsync(client.ClientId, cts.Token);
            var added = await ServiceHub.Client.CallAsync<AccountDto>(Commands.AddGoogle, new AddGoogleArgs
            {
                Code = result.Code,
                CodeVerifier = result.Verifier,
                RedirectUri = result.RedirectUri,
            }, 180_000);
            await ServiceHub.RefreshAsync();
            Mouse(false);
            Owner.Activate();
            if (added is not null)
                Dialogs.Info(Owner, L.T("AddGoogle"), L.F("AccountAdded", added.Email, added.Label, added.DriveLetter));
        }
        catch (OperationCanceledException ex)
        {
            Mouse(false);
            Dialogs.Info(Owner, L.T("AddGoogle"), L.T("SignInCancelled") + " " + ex.Message);
        }
        catch (Exception ex)
        {
            Mouse(false);
            Dialogs.Error(Owner, ex);
        }
    }

    public static void AddTeraBox()
    {
        var choice = Dialogs.Actions(Owner, L.T("AddTeraBox"), L.T("TeraBoxUnavailable"),
            L.T("OpenTeraBoxSite"), L.T("OpenTeraBoxPlatform"), L.T("Close"));
        if (choice == 0) ServiceHub.OpenUrl(TeraBoxProvider.OfficialSite);
        else if (choice == 1) ServiceHub.OpenUrl(TeraBoxProvider.OpenPlatformDocs);
    }
}
