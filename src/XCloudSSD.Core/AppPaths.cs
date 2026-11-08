namespace XCloudSSD.Core;

/// <summary>
/// Local locations. Only configuration, the small account database, encrypted tokens and logs
/// live here. User file content and game content are never written to these folders.
/// </summary>
public static class AppPaths
{
    public const string ServiceName = "XCloudSSDService";
    public const string PipeName = "XCloudSSD.Control.v1";

    private static string? _overrideRoot;

    /// <summary>Used by tests to isolate state.</summary>
    public static void OverrideRoot(string root) => _overrideRoot = root;

    public static string DataRoot
    {
        get
        {
            var root = _overrideRoot ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "XCloudSSD");
            Directory.CreateDirectory(root);
            return root;
        }
    }

    public static string StoreDir => Ensure(Path.Combine(DataRoot, "store"));
    public static string SecretsDir => Ensure(Path.Combine(DataRoot, "secrets"));
    public static string LogsDir => Ensure(Path.Combine(DataRoot, "logs"));

    public static string UserLanguagesDir => Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XCloudSSD", "languages"));

    private static string Ensure(string p)
    {
        Directory.CreateDirectory(p);
        return p;
    }
}
