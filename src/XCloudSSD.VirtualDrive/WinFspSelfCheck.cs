using System.Security.AccessControl;
using Fsp;
using FileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace XCloudSSD.VirtualDrive;

/// <summary>
/// Mounts an empty read-only volume, checks Windows can see it, and unmounts. Proves that the WinFsp
/// driver and its .NET layer load correctly from the shipped executable (used by CI and for diagnostics).
/// </summary>
public static class WinFspSelfCheck
{
    public static (bool Ok, string Message) Run()
    {
        try
        {
            var used = Environment.GetLogicalDrives().Select(d => char.ToUpperInvariant(d[0])).ToHashSet();
            var letter = Enumerable.Range('K', 'Z' - 'K' + 1).Select(i => (char)i).Reverse().FirstOrDefault(c => !used.Contains(c));
            if (letter == default) return (false, "No free drive letter.");
            using var host = new FileSystemHost(new EmptyFs());
            var status = host.Mount($"{letter}:", null, false, 0);
            if (status < 0) return (false, $"WinFsp mount failed: NTSTATUS 0x{status:X8}");
            try
            {
                for (var i = 0; i < 50 && !Directory.Exists($"{letter}:\\"); i++) Thread.Sleep(100);
                var di = new DriveInfo($"{letter}:\\");
                if (!di.IsReady) return (false, "Volume mounted but not ready.");
                return (true, $"WinFsp OK: mounted {letter}: label '{di.VolumeLabel}', fs {di.DriveFormat}, WinFsp {WinFspInfo.Detect().Version}");
            }
            finally
            {
                host.Unmount();
            }
        }
        catch (Exception ex)
        {
            var root = ex is TypeInitializationException { InnerException: { } i } ? i : ex;
            return (false, $"{root.GetType().Name}: {root.Message}");
        }
    }

    private sealed class EmptyFs : FileSystemBase
    {
        private static readonly byte[] Sd = CreateSd();

        private static byte[] CreateSd()
        {
            var sd = new RawSecurityDescriptor("O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FRFX;;;WD)");
            var b = new byte[sd.BinaryLength];
            sd.GetBinaryForm(b, 0);
            return b;
        }

        private static FileInfo RootInfo() => new() { FileAttributes = 0x10, LastWriteTime = (ulong)DateTime.UtcNow.ToFileTimeUtc() };

        public override int Init(object host0)
        {
            var host = (FileSystemHost)host0;
            host.SectorSize = 4096;
            host.SectorsPerAllocationUnit = 1;
            host.FileSystemName = "XCloudSSD-Check";
            return STATUS_SUCCESS;
        }

        public override int GetVolumeInfo(out VolumeInfo volumeInfo)
        {
            volumeInfo = default;
            volumeInfo.TotalSize = 1 << 20;
            volumeInfo.SetVolumeLabel("XCloudSSD Check");
            return STATUS_SUCCESS;
        }

        public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
        {
            fileAttributes = 0x10;
            if (fileName != "\\") return STATUS_OBJECT_NAME_NOT_FOUND;
            if (securityDescriptor is not null) securityDescriptor = Sd;
            return STATUS_SUCCESS;
        }

        public override int Open(string fileName, uint createOptions, uint grantedAccess, out object fileNode,
            out object fileDesc, out FileInfo fileInfo, out string normalizedName)
        {
            fileNode = default!;
            fileDesc = default!;
            normalizedName = default!;
            fileInfo = RootInfo();
            return fileName == "\\" ? STATUS_SUCCESS : STATUS_OBJECT_NAME_NOT_FOUND;
        }

        public override int GetFileInfo(object fileNode, object fileDesc, out FileInfo fileInfo)
        {
            fileInfo = RootInfo();
            return STATUS_SUCCESS;
        }

        public override bool ReadDirectoryEntry(object fileNode, object fileDesc, string pattern, string marker,
            ref object context, out string fileName, out FileInfo fileInfo)
        {
            fileName = default!;
            fileInfo = default;
            return false;
        }
    }
}
