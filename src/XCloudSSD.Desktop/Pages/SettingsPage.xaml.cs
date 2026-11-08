using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using XCloudSSD.Core;
using XCloudSSD.Core.Ipc;
using XCloudSSD.Desktop.Infrastructure;

namespace XCloudSSD.Desktop.Pages;

public partial class SettingsPage : UserControl, IPage
{
    private const string GuideUrl = "https://github.com/BxiiDN/XCloudSSD/blob/main/docs/SETUP-GOOGLE.md";
    private AppSettings? _settings;
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        LogLevelBox.ItemsSource = new[] { "Debug", "Information", "Warning", "Error" };
        ThemeBox.ItemsSource = new[] { "Dark", "Light" };
    }

    private Window Owner => Window.GetWindow(this)!;

    public void OnShown() => _ = LoadAsync();

    public void OnStatus(ServiceStatusDto? s)
    {
        DiagInfo.Text = s is null
            ? L.T("ServiceStopped")
            : L.F("DiagInfo", s.Version, s.WinFspInstalled ? s.WinFspVersion : L.T("NotInstalled"), AppPaths.DataRoot);
    }

    private async Task LoadAsync()
    {
        _loading = true;
        LanguageBox.ItemsSource = Loc.Available();
        LanguageBox.SelectedValue = Loc.Instance.Code;
        ThemeBox.SelectedItem = App.Prefs.Theme == "Light" ? "Light" : "Dark";
        NotifyBox.IsChecked = App.Prefs.Notifications;
        StartApp.IsChecked = DesktopSettings.StartWithWindows;
        DeleteModeBox.ItemsSource = new[] { L.T("DeleteTrash"), L.T("DeletePermanent") };
        _loading = false;
        try
        {
            _settings = await ServiceHub.Client.CallAsync<AppSettings>(Commands.GetSettings);
            var client = await ServiceHub.Client.CallAsync<OAuthClientDto>(Commands.GetOAuthClient);
            if (_settings is not null) Fill(_settings);
            ClientIdBox.Text = client?.ClientId ?? "";
            ClientStatus.Text = client?.HasSecret == true ? L.T("ClientConfigured") : L.T("ClientMissing");
        }
        catch (Exception ex)
        {
            SaveStatus.Text = ex.Message;
        }
    }

    private void Fill(AppSettings s)
    {
        MountAtStartup.IsChecked = s.MountAtStartup;
        KeepMounted.IsChecked = s.KeepMounted;
        AutoReconnect.IsChecked = s.AutoReconnect;
        RestoreConfig.IsChecked = s.RestorePreviousConfiguration;
        RamBox.Text = s.RamBufferMB.ToString();
        ReadChunkBox.Text = s.ReadChunkKB.ToString();
        ReadAheadBox.Text = s.ReadAheadChunks.ToString();
        UploadChunkBox.Text = s.UploadChunkMB.ToString();
        ParallelBox.Text = s.MaxParallelRequestsPerDisk.ToString();
        UpLimitBox.Text = s.UploadLimitKBps.ToString();
        DownLimitBox.Text = s.DownloadLimitKBps.ToString();
        DeleteModeBox.SelectedIndex = s.DeleteMode == DeleteMode.Permanent ? 1 : 0;
        NtfsBox.IsChecked = s.ReportNtfs;
        PollBox.Text = s.ChangePollSeconds.ToString();
        LogLevelBox.SelectedItem = s.LogLevel;
    }

    private static int Num(TextBox box, int fallback) => int.TryParse(box.Text.Trim(), out var v) ? v : fallback;

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = _settings?.Clone() ?? new AppSettings();
            s.MountAtStartup = MountAtStartup.IsChecked == true;
            s.KeepMounted = KeepMounted.IsChecked == true;
            s.AutoReconnect = AutoReconnect.IsChecked == true;
            s.RestorePreviousConfiguration = RestoreConfig.IsChecked == true;
            s.RamBufferMB = Num(RamBox, s.RamBufferMB);
            s.ReadChunkKB = Num(ReadChunkBox, s.ReadChunkKB);
            s.ReadAheadChunks = Num(ReadAheadBox, s.ReadAheadChunks);
            s.UploadChunkMB = Num(UploadChunkBox, s.UploadChunkMB);
            s.MaxParallelRequestsPerDisk = Num(ParallelBox, s.MaxParallelRequestsPerDisk);
            s.UploadLimitKBps = Math.Max(0, Num(UpLimitBox, s.UploadLimitKBps));
            s.DownloadLimitKBps = Math.Max(0, Num(DownLimitBox, s.DownloadLimitKBps));
            s.DeleteMode = DeleteModeBox.SelectedIndex == 1 ? DeleteMode.Permanent : DeleteMode.Trash;
            s.ReportNtfs = NtfsBox.IsChecked == true;
            s.ChangePollSeconds = Math.Max(5, Num(PollBox, s.ChangePollSeconds));
            s.LogLevel = LogLevelBox.SelectedItem as string ?? "Information";
            s.Notifications = NotifyBox.IsChecked == true;

            _settings = await ServiceHub.Client.CallAsync<AppSettings>(Commands.SetSettings, s);
            if (_settings is not null) Fill(_settings);

            App.Prefs.Notifications = NotifyBox.IsChecked == true;
            App.Prefs.Save();
            DesktopSettings.StartWithWindows = StartApp.IsChecked == true;
            SaveStatus.Text = L.F("SavedAt", DateTime.Now.ToString("T"));
        }
        catch (Exception ex)
        {
            Dialogs.Error(Owner, ex);
        }
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageBox.SelectedValue is not string code) return;
        Loc.Instance.Load(code);
        App.Prefs.Language = code;
        App.Prefs.Save();
        DeleteModeBox.ItemsSource = new[] { L.T("DeleteTrash"), L.T("DeletePermanent") };
        if (_settings is not null) DeleteModeBox.SelectedIndex = _settings.DeleteMode == DeleteMode.Permanent ? 1 : 0;
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeBox.SelectedItem is not string theme) return;
        App.ApplyTheme(theme);
        App.Prefs.Theme = theme;
        App.Prefs.Save();
    }

    private void LangFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.UserLanguagesDir) { UseShellExecute = true });

    private void LangTemplate_Click(object sender, RoutedEventArgs e)
    {
        var path = Loc.Instance.ExportTemplate();
        Dialogs.Info(Owner, L.T("ExportTemplate"), L.F("TemplateExported", path));
    }

    private void ImportClient_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "JSON (*.json)|*.json", Title = L.T("ImportClientJson") };
        if (dlg.ShowDialog(Owner) != true) return;
        try
        {
            // Google Cloud Console "Desktop app" credentials: {"installed": {"client_id": ..., "client_secret": ...}}
            using var doc = JsonDocument.Parse(File.ReadAllText(dlg.FileName));
            var root = doc.RootElement.TryGetProperty("installed", out var inst) ? inst
                : doc.RootElement.TryGetProperty("web", out var web) ? web : doc.RootElement;
            ClientIdBox.Text = root.GetProperty("client_id").GetString() ?? "";
            ClientSecretBox.Password = root.GetProperty("client_secret").GetString() ?? "";
            SaveClient_Click(sender, e);
        }
        catch (Exception ex)
        {
            Dialogs.Error(Owner, new InvalidDataException(L.T("ClientJsonInvalid") + " " + ex.Message));
        }
    }

    private async void SaveClient_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dto = await ServiceHub.Client.CallAsync<OAuthClientDto>(Commands.SetOAuthClient, new OAuthClientDto
            {
                ClientId = ClientIdBox.Text.Trim(),
                ClientSecret = string.IsNullOrEmpty(ClientSecretBox.Password) ? null : ClientSecretBox.Password,
            });
            ClientSecretBox.Password = "";
            ClientStatus.Text = dto?.HasSecret == true ? L.T("ClientConfigured") : L.T("ClientMissing");
            await ServiceHub.RefreshAsync();
        }
        catch (Exception ex)
        {
            Dialogs.Error(Owner, ex);
        }
    }

    private void ClientHelp_Click(object sender, RoutedEventArgs e) => ServiceHub.OpenUrl(GuideUrl);

    private void OpenLogs_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.LogsDir) { UseShellExecute = true });

    private async void ExportDiag_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                $"XCloudSSD-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            var status = ServiceHub.Last;
            List<TransferDto>? transfers = null;
            try
            {
                transfers = await ServiceHub.Client.CallAsync<List<TransferDto>>(Commands.Transfers);
            }
            catch (Exception)
            {
            }
            await Task.Run(() =>
            {
                using var zip = ZipFile.Open(target, ZipArchiveMode.Create);
                foreach (var log in Directory.GetFiles(AppPaths.LogsDir, "*.log").OrderByDescending(f => File.GetLastWriteTimeUtc(f)).Take(7))
                {
                    // Logs may be open by the service; copy via shared read.
                    using var src = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var dst = zip.CreateEntry("logs/" + Path.GetFileName(log)).Open();
                    src.CopyTo(dst);
                }
                // Status contains no tokens or secrets (the service never returns them).
                var statusEntry = zip.CreateEntry("status.json");
                using (var w = new StreamWriter(statusEntry.Open()))
                    w.Write(JsonSerializer.Serialize(new { status, transfers }, IpcJson.Options));
            });
            Dialogs.Info(Owner, L.T("ExportDiagnostics"), L.F("DiagnosticsSaved", target));
        }
        catch (Exception ex)
        {
            Dialogs.Error(Owner, ex);
        }
    }

    private void StartService_Click(object sender, RoutedEventArgs e) => ServiceHub.StartService();
}
