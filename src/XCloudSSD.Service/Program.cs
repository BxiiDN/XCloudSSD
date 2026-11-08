using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using XCloudSSD.Core;
using XCloudSSD.Metadata;
using XCloudSSD.Security;
using XCloudSSD.Service;
using XCloudSSD.VirtualDrive;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = AppPaths.ServiceName);
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(90));

var fileLogs = new FileLoggerProvider(AppPaths.LogsDir, "service");
builder.Logging.ClearProviders();
builder.Logging.AddProvider(fileLogs);
if (Environment.UserInteractive) builder.Logging.AddSimpleConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services.AddSingleton(fileLogs);
builder.Services.AddSingleton(_ => new AppDatabase(AppPaths.DatabaseFile));
builder.Services.AddSingleton(_ =>
{
    // As LocalSystem, credentials are encrypted with machine-scope DPAPI in an ACL-restricted folder.
    var isSystem = WindowsIdentity.GetCurrent().IsSystem;
    return new SecretStore(AppPaths.SecretsDir, machineScope: isSystem);
});
builder.Services.AddSingleton(sp => new DiskManager(
    sp.GetRequiredService<AppDatabase>(), sp.GetRequiredService<SecretStore>(), sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<ControlPipeServer>();
builder.Services.AddHostedService<ServiceWorker>();

await builder.Build().RunAsync();
