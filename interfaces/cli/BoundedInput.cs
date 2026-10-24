using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Bim.Cli;

// A typed input refusal: Message is the code printed as {"status":"failed","code":...}.
internal sealed class InputRejectedException : IOException
{
    public InputRejectedException() { }
    public InputRejectedException(string code) : base(code) { }
    public InputRejectedException(string code, Exception inner) : base(code, inner) { }
}

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
    public const string AccessDenied = "input_access_denied";
    public const string IoError = "input_io_error";
    public const string TypeUnverifiable = "input_type_unverifiable";

    public static string ReadText(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try { return ReadChecked(path); }
        catch (InputRejectedException) { throw; }
        catch (UnauthorizedAccessException e) { throw new InputRejectedException(AccessDenied, e); }
        catch (IOException e) { throw new InputRejectedException(IoError, e); }
    }

    static string ReadChecked(string path)
    {
        // Windows device and pipe namespaces (\\.\, \\?\, including NUL/CON which resolve to \\.\) are never inputs.
        if (OperatingSystem.IsWindows() && (DeviceNamespace(path) || DeviceNamespace(Path.GetFullPath(path)))) throw new InputRejectedException(NotRegularFile);
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { throw new InputRejectedException(HasLinkTarget(new FileInfo(path)) ? LinkRejected : NotFound, e); }
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        // Symlinks and junctions have a LinkTarget. A OneDrive file is a ReparsePoint without one and is read like any
        // file: the same rule as Bim.Core.CloudGuard (HANDOFF section 7). Unlike CloudGuard, which refuses stores holding
        // cloud-only placeholders (CloudGuard.PlaceholderAttributes), a single CLI input is read and so hydrated.
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            FileSystemInfo entry = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
            if (HasLinkTarget(entry)) throw new InputRejectedException(LinkRejected);
        }
        if (isDirectory) throw new InputRejectedException(NotRegularFile);
        // Pre-open lstat: opening a FIFO for reading blocks until a writer appears, so the type is checked first.
        // A FIFO swapped in between this lstat and the open still blocks that open; accepted, because it needs
        // control of the input directory. The post-open fstat below is the authoritative check for everything else.
        if (!OperatingSystem.IsWindows()) RequireUnixRegular(LStat(Encoding.UTF8.GetBytes(path + "\0"), out var lstatMode), lstatMode);
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { throw new InputRejectedException(NotFound, e); }
        using (stream)
        {
            if (OperatingSystem.IsWindows()) { if (GetFileType(stream.SafeFileHandle) != FileTypeDisk) throw new InputRejectedException(NotRegularFile); }
            else RequireUnixRegular(FStat(stream.SafeFileHandle, out var fstatMode), fstatMode);
            // Bounded read: never trusts a prior size query; stops as soon as one byte beyond the cap arrives.
            var buffer = new byte[64 * 1024];
            using var bytes = new MemoryStream();
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (bytes.Length + read > MaxBytes) throw new InputRejectedException(TooLarge);
                bytes.Write(buffer, 0, read);
            }
            bytes.Position = 0;
            // Exactly File.ReadAllText(path): UTF-8 by default, byte order marks detected and stripped.
            using var reader = new StreamReader(bytes, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
    }

    static bool DeviceNamespace(string path)
    {
        var normalized = path.Replace('/', '\\');
        return normalized.StartsWith(@"\\.\", StringComparison.Ordinal) || normalized.StartsWith(@"\\?\", StringComparison.Ordinal);
    }

    // A dangling link can make attribute queries report "not found"; the link itself is still rejected.
    static bool HasLinkTarget(FileSystemInfo entry)
    {
        try { return entry.LinkTarget != null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    const int FileTypeDisk = 1;
    [DllImport("kernel32.dll")]
    private static extern int GetFileType(SafeFileHandle handle);

    // lstat/fstat through the runtime's own native shim (libSystem.Native, shipped with every .NET 8 runtime on Unix);
    // no public .NET 8 API reports the file type before open. Its FileStatus struct starts with int32 Flags then
    // int32 Mode, where Mode carries the POSIX S_IFMT type bits. A missing shim or entry point fails closed with
    // input_type_unverifiable. CI covers linux-x64, macOS (arm64) and Windows; linux-arm64 is uncovered.
    const int TypeMask = 0xF000, RegularType = 0x8000, StatusBytes = 512;
    [DllImport("libSystem.Native", EntryPoint = "SystemNative_LStat")]
    private static extern int NativeLStat(byte[] path, [Out] byte[] status);
    [DllImport("libSystem.Native", EntryPoint = "SystemNative_FStat")]
    private static extern int NativeFStat(SafeFileHandle fd, [Out] byte[] status);
    static bool LStat(byte[] path, out int mode) => Stat(status => NativeLStat(path, status), out mode);
    static bool FStat(SafeFileHandle fd, out int mode) => Stat(status => NativeFStat(fd, status), out mode);
    static bool Stat(Func<byte[], int> call, out int mode)
    {
        var status = new byte[StatusBytes];
        int result;
        try { result = call(status); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { throw new InputRejectedException(TypeUnverifiable, e); }
        mode = BitConverter.ToInt32(status, 4);
        return result == 0;
    }
    static void RequireUnixRegular(bool succeeded, int mode)
    {
        if (!succeeded) throw new InputRejectedException(IoError);
        if ((mode & TypeMask) != RegularType) throw new InputRejectedException(NotRegularFile);
    }
}
