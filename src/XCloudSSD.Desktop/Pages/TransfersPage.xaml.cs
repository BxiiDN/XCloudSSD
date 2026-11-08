using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using XCloudSSD.Core.Ipc;
using XCloudSSD.Desktop.Infrastructure;

namespace XCloudSSD.Desktop.Pages;

public sealed class TransferRow
{
    public TransferRow(TransferDto t) => T = t;
    public TransferDto T { get; }
    public string FullPath => $"{T.DriveLetter}:{T.Path}";
    public string DirectionText => L.T(T.Direction == TransferDirection.Upload ? "Upload" : "Download");
    public string StateText => L.T("Transfer" + T.State);
    public double Fraction => T.BytesTotal > 0 ? Math.Min(1, (double)T.BytesDone / T.BytesTotal) : T.State == TransferState.Completed ? 1 : 0;
    public string ProgressText => $"{Fmt.Bytes(T.BytesDone)} / {Fmt.Bytes(T.BytesTotal)}";
    public int Attempts => T.Attempts;
    public string UpdatedText => Fmt.Time(T.Updated);
    public string? Error => T.Error;
}

public partial class TransfersPage : UserControl, IPage
{
    private bool _loading;
    private string _sig = "";

    public TransfersPage() => InitializeComponent();

    public void OnShown()
    {
        _sig = "";
        _ = LoadAsync();
    }

    public void OnStatus(ServiceStatusDto? status)
    {
        if (IsVisible) _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var list = await ServiceHub.Client.CallAsync<List<TransferDto>>(Commands.Transfers, null, 5000) ?? new();
            var sig = string.Join(";", list.Select(t => $"{t.Id}:{t.State}:{t.BytesDone}:{t.Attempts}")) + Loc.Instance.Code;
            if (sig != _sig)
            {
                _sig = sig;
                Items.ItemsSource = list.Select(t => new TransferRow(t)).ToList();
            }
            var active = list.Count(t => t.State is TransferState.Pending or TransferState.Uploading or TransferState.Downloading
                or TransferState.Processing or TransferState.Retrying or TransferState.Offline);
            Summary.Text = L.F("TransfersSummary", active, list.Count(t => t.State == TransferState.Failed || t.State == TransferState.Interrupted));
        }
        catch (Exception ex)
        {
            Summary.Text = ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ServiceHub.Client.CallAsync(Commands.ClearFinishedTransfers);
            _sig = "";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Dialogs.Error(Window.GetWindow(this)!, ex);
        }
    }
}
