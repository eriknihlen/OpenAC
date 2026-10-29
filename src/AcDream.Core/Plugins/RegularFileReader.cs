using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AcDream.Core.Plugins;

/// <summary>
/// Reads a small file a plugin ships through one open handle, and only when it
/// is a regular file: never a directory, device, pipe (FIFO) or socket, whatever
/// its name. Opening never waits: on Linux and macOS the file is opened without
/// blocking, so a pipe nobody writes to cannot hold the reader up, and is then
/// refused for what it is. What is read is what was checked, since the check
/// and the read use the same handle.
/// </summary>
internal static class RegularFileReader
{
    internal const string NotARegularFile = "is not a regular file";

    /// <summary>
    /// Reads at most <paramref name="readLimit"/> bytes of <paramref name="path"/>.
    /// A caller that allows n bytes passes n + 1, so a longer file shows as one
    /// byte too many instead of being read whole.
    /// </summary>
    internal static bool TryRead(
        string path, int readLimit,
        [NotNullWhen(true)] out byte[]? bytes, [NotNullWhen(false)] out string? reason)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(readLimit);
        bytes = null;
        try
        {
            if (Directory.Exists(path))
            {
                reason = NotARegularFile;
                return false;
            }

            using SafeFileHandle handle = Open(path);
            if (!IsRegularFile(handle))
            {
                reason = NotARegularFile;
                return false;
            }

            long length = RandomAccess.GetLength(handle);
            var buffer = new byte[(int)Math.Clamp(length + 1, 1, readLimit)];
            int total = 0;
            while (true)
            {
                if (total == buffer.Length)
                {
                    if (buffer.Length == readLimit)
                        break;
                    // The file grew since its length was read: keep reading, up to the limit.
                    Array.Resize(ref buffer, (int)Math.Min((long)buffer.Length * 2, readLimit));
                }
                int read = RandomAccess.Read(handle, buffer.AsSpan(total), total);
                if (read == 0)
                    break;
                total += read;
            }

            bytes = total == buffer.Length ? buffer : buffer[..total];
            reason = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or DllNotFoundException
                                       or EntryPointNotFoundException)
        {
            reason = "could not be read: " + ex.Message;
            return false;
        }
    }

    private static SafeFileHandle Open(string path)
    {
        if (OperatingSystem.IsWindows())
            return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

        int descriptor;
        if (OperatingSystem.IsLinux())
            descriptor = LinuxOpen(path, LinuxReadOnlyNonBlocking);
        else if (OperatingSystem.IsMacOS())
            descriptor = MacOpen(path, MacReadOnlyNonBlocking);
        else
            throw new NotSupportedException("reading plugin files is not supported on this system");
        if (descriptor < 0)
            throw new IOException(Marshal.GetLastPInvokeErrorMessage());
        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    private static bool IsRegularFile(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
            return WindowsGetFileType(handle) == WindowsFileTypeDisk;

        // The runtime's own file-status call, whose layout is the same on every
        // Unix: a flags word, then the mode, whose type bits are the system's.
        var status = new byte[512];
        if (UnixFileStatus(handle, status) != 0)
            throw new IOException(Marshal.GetLastPInvokeErrorMessage());
        int mode = BitConverter.ToInt32(status, 4);
        return (mode & UnixFileTypeMask) == UnixRegularFile;
    }

    // Read only, never waiting (a pipe with no writer opens at once), never
    // becoming the controlling terminal, closed on exec.
    private const int LinuxReadOnlyNonBlocking = 0x800 | 0x100 | 0x80000;
    private const int MacReadOnlyNonBlocking = 0x4 | 0x20000 | 0x1000000;

    private const int UnixFileTypeMask = 0xF000;
    private const int UnixRegularFile = 0x8000;
    private const int WindowsFileTypeDisk = 0x0001;

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int LinuxOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "open", SetLastError = true)]
    private static extern int MacOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_FStat", SetLastError = true)]
    private static extern int UnixFileStatus(SafeFileHandle handle, [Out] byte[] status);

    [DllImport("kernel32.dll", EntryPoint = "GetFileType", SetLastError = true)]
    private static extern int WindowsGetFileType(SafeFileHandle handle);
}
