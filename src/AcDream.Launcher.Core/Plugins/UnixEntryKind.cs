using System.Runtime.InteropServices;

namespace AcDream.Launcher.Core.Plugins;

/// <summary>Whether a path is a regular file or directory, the one thing <see cref="FileAttributes"/>
/// cannot say on Unix: a FIFO, socket or device reports the same attributes as a regular file, so
/// <see cref="DirectInstallCheck"/> asks <c>lstat</c> directly instead.</summary>
internal static partial class UnixEntryKind
{
    private const int TypeMask = 0xF000;
    private const int RegularFile = 0x8000;
    private const int Directory = 0x4000;

    public static bool IsRegularOrDirectory(string path)
    {
        int mode = OperatingSystem.IsMacOS() ? MacMode(path) : LinuxMode(path);
        int type = mode & TypeMask;
        return type == RegularFile || type == Directory;
    }

    private static int MacMode(string path)
    {
        if (LStatMac(path, out MacStat stat) != 0)
        {
            throw new IOException(
                $"lstat failed for '{path}' ({Marshal.GetLastPInvokeError()}).");
        }

        return stat.Mode;
    }

    private static int LinuxMode(string path)
    {
        if (LStatLinux(path, out LinuxStat stat) != 0)
        {
            throw new IOException(
                $"lstat failed for '{path}' ({Marshal.GetLastPInvokeError()}).");
        }

        // glibc's x86_64 struct stat places st_mode at offset 24; the generic layout every other
        // supported architecture (including arm64) uses places it at offset 16.
        return RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? (int)stat.ModeAtOffset24
            : (int)stat.ModeAtOffset16;
    }

    [LibraryImport(
        "/usr/lib/libSystem.B.dylib",
        EntryPoint = "lstat",
        StringMarshalling = StringMarshalling.Utf8,
        SetLastError = true)]
    private static partial int LStatMac(string path, out MacStat buffer);

    [LibraryImport(
        "libc",
        EntryPoint = "lstat",
        StringMarshalling = StringMarshalling.Utf8,
        SetLastError = true)]
    private static partial int LStatLinux(string path, out LinuxStat buffer);

    // Oversized on purpose: only Mode's offset needs to be right, and the extra padding leaves no
    // room for lstat to write past the end of either struct on any supported architecture.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct MacStat
    {
        [FieldOffset(4)]
        public ushort Mode;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStat
    {
        [FieldOffset(16)]
        public uint ModeAtOffset16;

        [FieldOffset(24)]
        public uint ModeAtOffset24;
    }
}
