using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using XCloudSSD.Core;

namespace XCloudSSD.Security;

/// <summary>
/// Stores per-account credentials encrypted with Windows DPAPI. Each account has its own file,
/// encrypted with an account-specific entropy, so credentials are isolated between accounts.
/// Nothing is ever stored in plaintext.
/// </summary>
public sealed class SecretStore
{
    private static readonly byte[] AppEntropy = Encoding.UTF8.GetBytes("XCloudSSD/secret-store/v1");
    private readonly string _dir;
    private readonly DataProtectionScope _scope;
    private readonly object _lock = new();

    /// <param name="dir">Directory for encrypted blobs.</param>
    /// <param name="machineScope">
    /// True for the Windows service (LocalSystem): blobs are machine-bound and the folder ACL is
    /// restricted to SYSTEM and Administrators. False for per-user use (tests, portable mode).
    /// </param>
    public SecretStore(string dir, bool machineScope)
    {
        _dir = dir;
        _scope = machineScope ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser;
        Directory.CreateDirectory(dir);
        if (machineScope) TryRestrictAcl(dir);
    }

    public void Save(string key, IReadOnlyDictionary<string, string> values)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(values);
        var blob = ProtectedData.Protect(json, Entropy(key), _scope);
        CryptographicOperations.ZeroMemory(json);
        lock (_lock)
        {
            var path = PathFor(key);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, blob);
            File.Move(tmp, path, overwrite: true);
        }
    }

    public Dictionary<string, string>? Load(string key)
    {
        byte[] blob;
        lock (_lock)
        {
            var path = PathFor(key);
            if (!File.Exists(path)) return null;
            blob = File.ReadAllBytes(path);
        }
        var json = ProtectedData.Unprotect(blob, Entropy(key), _scope);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    public void Delete(string key)
    {
        lock (_lock)
        {
            var path = PathFor(key);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    public bool Exists(string key) => File.Exists(PathFor(key));

    private string PathFor(string key)
    {
        foreach (var c in key)
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_' && c != '.')
                throw new ArgumentException("Invalid secret key.", nameof(key));
        return Path.Combine(_dir, key + ".secret");
    }

    private static byte[] Entropy(string key) => AppEntropy.Concat(Encoding.UTF8.GetBytes(key)).ToArray();

    private static void TryRestrictAcl(string dir)
    {
        try
        {
            var di = new DirectoryInfo(dir);
            var sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            di.SetAccessControl(sec);
        }
        catch
        {
            // Not fatal (e.g. not elevated). DPAPI encryption still applies.
        }
    }
}
