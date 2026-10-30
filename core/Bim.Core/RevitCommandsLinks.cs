using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace Bim.Core;

// File-system facts the link guard needs, injectable for tests. IsLink: the path is a symlink or junction (it has a
// LinkTarget; a OneDrive placeholder is a reparse point without one and is not a link, the same rule as CloudGuard).
// HardLinkCount: hard links of an existing file, null when unknown or not applicable, -1 when it could not be read.
public sealed record RevitFileSystemProbe(Func<string, bool> IsLink, Func<string, int?> HardLinkCount)
{
    public static RevitFileSystemProbe Real { get; } = new(RealIsLink, RealHardLinkCount);

    static bool RealIsLink(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            // Missing is fine, unless it is a dangling link.
            try { return new FileInfo(path).LinkTarget is not null; }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException) { return false; }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
        if ((attributes & FileAttributes.ReparsePoint) == 0) return false;
        FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
        // Unreadable link metadata fails closed.
        try { return info.LinkTarget is not null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
    }

    // Windows only: GetFileInformationByHandle reports nNumberOfLinks. Elsewhere no count is taken.
    static int? RealHardLinkCount(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var handle = NativeFile.CreateFileW(path, NativeFile.FileReadAttributes, NativeFile.ShareAll, IntPtr.Zero, NativeFile.OpenExisting, NativeFile.BackupSemantics | NativeFile.OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            return error is NativeFile.ErrorFileNotFound or NativeFile.ErrorPathNotFound ? null : -1;
        }
        return NativeFile.GetFileInformationByHandle(handle, out var information) ? (information.NumberOfLinks > int.MaxValue ? int.MaxValue : (int)information.NumberOfLinks) : -1;
    }

    static class NativeFile
    {
        public const uint FileReadAttributes = 0x80;
        public const uint ShareAll = 0x7;
        public const uint OpenExisting = 3;
        public const uint BackupSemantics = 0x02000000;
        public const uint OpenReparsePoint = 0x00200000;
        public const int ErrorFileNotFound = 2;
        public const int ErrorPathNotFound = 3;

        // Filled by GetFileInformationByHandle only.
#pragma warning disable CS0649
        [StructLayout(LayoutKind.Sequential)]
        public struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh, FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh, FileIndexLow;
        }
#pragma warning restore CS0649

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);
    }
}

// Second half of the copies-only rule for write commands: the text check (RevitCopiesGuard) cannot see a symlink or
// junction inside an enabled root that points at a production folder, nor a hard link to a production file.
public static class RevitLinkGuard
{
    // Null when allowed, else document_link_refused. Probes the matched root and every component below it down to the
    // document, spelled as in documentPath. Components above the root are the owner's concern, not checked.
    public static string? Check(string documentPath, string root, RevitFileSystemProbe probe)
    {
        if (RevitCopiesGuard.ComponentsFromRoot(documentPath, root) is not { } components) return RevitCommandCodes.DocumentPathInvalid;
        foreach (var component in components)
            if (probe.IsLink(component)) return RevitCommandCodes.DocumentLinkRefused;
        var links = probe.HardLinkCount(documentPath);
        return links is null or 1 ? null : RevitCommandCodes.DocumentLinkRefused;
    }
}
