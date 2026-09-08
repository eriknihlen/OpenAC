using System.Runtime.InteropServices;

namespace AcDream.App.Platform;

public static class PlatformKeyNameProvider
{
    public static Func<byte, bool, string?>? ForCurrentProcess()
        => OperatingSystem.IsWindows() ? WindowsKeyName : null;

    private static string? WindowsKeyName(byte scan, bool extended)
    {
        Span<char> buffer = stackalloc char[64];
        int lParam = (scan << 16) | (extended ? 1 << 24 : 0);
        int length;
        unsafe
        {
            fixed (char* p = buffer)
                length = GetKeyNameTextW(lParam, p, buffer.Length);
        }
        return length > 0 ? new string(buffer[..length]) : null;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern unsafe int GetKeyNameTextW(int lParam, char* lpString, int cchSize);
}
