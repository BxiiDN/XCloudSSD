using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using XCloudSSD.Core.Ipc;
using XCloudSSD.Desktop.Infrastructure;

namespace XCloudSSD.Desktop.Pages;

public partial class PerformancePage : UserControl, IPage
{
    private const int Samples = 80;
    private readonly Queue<double> _up = new();
    private readonly Queue<double> _down = new();
    private DateTime _lastProbe = DateTime.MinValue;
    private bool _probing;

    public PerformancePage() => InitializeComponent();

    public void OnShown()
    {
    }

    public void OnStatus(ServiceStatusDto? s)
    {
        if (s is null)
        {
            foreach (var t in new[] { Up, Down, CloudLatency, Requests, Ram, FsIo, Handles }) t.Text = "—";
            return;
        }
        var p = s.Perf;
        Up.Text = Fmt.Speed(p.UploadBytesPerSec);
        Down.Text = Fmt.Speed(p.DownloadBytesPerSec);
        CloudLatency.Text = Fmt.Ms(p.LatencyMs);
        Requests.Text = p.ActiveRequests.ToString();
        Ram.Text = $"{Fmt.Bytes(p.RamUsedBytes)} / {Fmt.Bytes(p.RamCapacityBytes)}";
        RamSub.Text = L.F("RamPeak", Fmt.Bytes(p.RamPeakBytes));
        FsIo.Text = $"{p.FsOpsPerSec:0} ops/s";
        FsIoSub.Text = L.F("FsIoSub", Fmt.Speed(p.FsReadBytesPerSec), Fmt.Speed(p.FsWriteBytesPerSec));
        Handles.Text = p.OpenHandles.ToString();
        QueueSub.Text = L.F("QueueSub", p.TransferQueue, p.RequestsTotal, p.RequestErrors);

        Push(_up, p.UploadBytesPerSec);
        Push(_down, p.DownloadBytesPerSec);
        DrawChart();

        if (IsVisible && (DateTime.UtcNow - _lastProbe).TotalSeconds >= 5) _ = ProbeAsync();
    }

    /// <summary>Measured TCP connect time to the Google Drive API endpoint.</summary>
    private async Task ProbeAsync()
    {
        if (_probing) return;
        _probing = true;
        _lastProbe = DateTime.UtcNow;
        try
        {
            using var client = new TcpClient();
            var sw = Stopwatch.StartNew();
            var connect = client.ConnectAsync("www.googleapis.com", 443);
            if (await Task.WhenAny(connect, Task.Delay(5000)) == connect && client.Connected)
                NetLatency.Text = Fmt.Ms(sw.Elapsed.TotalMilliseconds);
            else
                NetLatency.Text = L.T("Unreachable");
        }
        catch (Exception)
        {
            NetLatency.Text = L.T("Unreachable");
        }
        finally
        {
            _probing = false;
        }
    }

    private static void Push(Queue<double> q, double v)
    {
        q.Enqueue(Math.Max(0, v));
        while (q.Count > Samples) q.Dequeue();
    }

    private void Chart_SizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();

    private void DrawChart()
    {
        var w = Chart.ActualWidth;
        var h = Chart.ActualHeight;
        if (w <= 0 || h <= 0) return;
        var max = Math.Max(1024, Math.Max(_up.DefaultIfEmpty(0).Max(), _down.DefaultIfEmpty(0).Max()));
        ChartScale.Text = L.F("ChartMax", Fmt.Speed(max));
        UpLine.Points = Points(_up, w, h, max);
        DownLine.Points = Points(_down, w, h, max);
    }

    private static PointCollection Points(IEnumerable<double> values, double w, double h, double max)
    {
        var pts = new PointCollection();
        var list = values.ToList();
        var step = w / (Samples - 1);
        var x0 = w - step * (list.Count - 1);
        for (var i = 0; i < list.Count; i++)
            pts.Add(new Point(x0 + i * step, h - list[i] / max * (h - 4) - 2));
        return pts;
    }
}
