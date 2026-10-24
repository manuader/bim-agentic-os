using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Bim.Cli;

// Every CLI file input goes through ReadText. Failures surface as {"status":"failed","code":...} through the
// CLI's top-level handler (the evaluate verb keeps its report envelope and carries the code in the reason).
internal static class BoundedInput
{
    // Same cap as interfaces/mcp/McpServer.cs MaxLineChars (8 MiB), applied here to the file's bytes.
    public const int MaxBytes = 8 * 1024 * 1024;
    public const string TooLarge = "input_too_large";
    public const string NotRegularFile = "input_not_regular_file";
    public const string LinkRejected = "input_link_rejected";
    public const string NotFound = "input_not_found";

    public static string ReadText(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { throw new IOException(HasLinkTarget(new FileInfo(path)) ? LinkRejected : NotFound, e); }
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        // Symlinks and junctions have a LinkTarget. A OneDrive file is a ReparsePoint without one and is read like any
        // file: the same rule as Bim.Core.CloudGuard (HANDOFF section 7). Unlike CloudGuard, which refuses stores holding
        // cloud-only placeholders (CloudGuard.PlaceholderAttributes), a single CLI input is read and so hydrated.
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            FileSystemInfo entry = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
            if (HasLinkTarget(entry)) throw new IOException(LinkRejected);
        }
        if (isDirectory) throw new IOException(NotRegularFile);
        // Checked before opening: opening a FIFO for reading blocks until a writer appears.
        if (!OperatingSystem.IsWindows() && !UnixRegularFile(path)) throw new IOException(NotRegularFile);
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { throw new IOException(NotFound, e); }
        using (stream)
        {
            if (OperatingSystem.IsWindows() && GetFileType(stream.SafeFileHandle) != FileTypeDisk) throw new IOException(NotRegularFile);
            // Bounded read: never trusts a prior size query; stops as soon as one byte beyond the cap arrives.
            var buffer = new byte[64 * 1024];
            using var bytes = new MemoryStream();
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (bytes.Length + read > MaxBytes) throw new IOException(TooLarge);
                bytes.Write(buffer, 0, read);
            }
            bytes.Position = 0;
            // Exactly File.ReadAllText(path): UTF-8 by default, byte order marks detected and stripped.
            using var reader = new StreamReader(bytes, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
    }

    // A dangling link can make attribute queries report "not found"; the link itself is still rejected.
    static bool HasLinkTarget(FileSystemInfo entry)
    {
        try { return entry.LinkTarget != null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    const int FileTypeDisk = 1;
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int GetFileType(SafeFileHandle handle);

    // lstat through the runtime's own native shim (libSystem.Native, shipped with every .NET 8 runtime on Unix).
    // Its FileStatus struct starts with int32 Flags then int32 Mode, where Mode carries the POSIX S_IFMT type bits.
    // Any failure fails closed: an input whose type cannot be established is not read.
    const int TypeMask = 0xF000, RegularType = 0x8000;
    [DllImport("libSystem.Native", EntryPoint = "SystemNative_LStat", SetLastError = true)]
    private static extern int LStat(byte[] path, [Out] byte[] status);
    static bool UnixRegularFile(string path)
    {
        var encoded = Encoding.UTF8.GetBytes(path + "\0");
        var status = new byte[512];
        try { if (LStat(encoded, status) != 0) return false; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
        return (BitConverter.ToInt32(status, 4) & TypeMask) == RegularType;
    }
}
