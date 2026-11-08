using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using XCloudSSD.Core;
using XCloudSSD.VirtualDrive;

namespace XCloudSSD.Service;

/// <summary>XCloudSSDService main loop: restores mounts, serves the control pipe, flushes on stop.</summary>
public sealed class ServiceWorker(DiskManager disks, ControlPipeServer pipe, FileLoggerProvider fileLogs, ILogger<ServiceWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Enum.TryParse<LogLevel>(disks.Settings.LogLevel, out var level)) fileLogs.MinLevel = level;
        var (installed, version, _) = WinFspInfo.Detect();
        log.LogInformation("XCloudSSDService starting. WinFsp installed: {Installed} {Version}", installed, version);
        if (!installed) log.LogError("WinFsp is not installed; virtual disks cannot be mounted. Reinstall XCloudSSD.");

        await disks.StartAsync(stoppingToken);
        await pipe.RunAsync(stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        log.LogInformation("XCloudSSDService stopping: committing pending uploads and unmounting.");
        var unsaved = disks.StopAll();
        if (unsaved.Count > 0) log.LogError("Not saved before stop: {Files}", string.Join(", ", unsaved));
        await base.StopAsync(cancellationToken);
    }
}
