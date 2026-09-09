using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AcDream.App.Platform;

internal static unsafe class Win32GlfwActiveWindowGuard
{
    private const string GlfwModuleName = "glfw3.dll";
    private const string User32ModuleName = "USER32.dll";
    private const string GetActiveWindowImport = "GetActiveWindow";
    private const uint PageReadWrite = 0x04;
    private const ushort DosSignature = 0x5A4D;
    private const uint PeSignature = 0x00004550;
    private const ushort Pe32Magic = 0x010B;
    private const ushort Pe32PlusMagic = 0x020B;
    private const int ImportDescriptorSize = 20;

    private static readonly uint CurrentProcessId =
        checked((uint)Environment.ProcessId);
    private static int _installState;

    internal static bool IsInstalled => Volatile.Read(ref _installState) == 1;

    internal static void Install()
    {
        if (!OperatingSystem.IsWindows()
            || Interlocked.CompareExchange(ref _installState, 2, 0) != 0)
        {
            return;
        }

        try
        {
            nint module = GetModuleHandleW(GlfwModuleName);
            if (module == 0
                || !TryFindImportSlot(
                    module,
                    User32ModuleName,
                    GetActiveWindowImport,
                    out nint slot))
            {
                Volatile.Write(ref _installState, -1);
                Console.Error.WriteLine(
                    "windowing: could not install the GLFW foreign-active-window guard");
                return;
            }

            nint replacement = (nint)(delegate* unmanaged[Stdcall]<nint>)
                &GetCurrentProcessActiveWindow;
            if (!VirtualProtect(
                    slot,
                    checked((nuint)IntPtr.Size),
                    PageReadWrite,
                    out uint oldProtection))
            {
                Volatile.Write(ref _installState, -1);
                Console.Error.WriteLine(
                    "windowing: GLFW active-window import was not writable");
                return;
            }

            try
            {
                *(nint*)slot = replacement;
            }
            finally
            {
                _ = VirtualProtect(
                    slot,
                    checked((nuint)IntPtr.Size),
                    oldProtection,
                    out _);
            }

            Volatile.Write(ref _installState, 1);
            Console.WriteLine(
                "windowing: GLFW foreign-active-window guard installed.");
        }
        catch (Exception failure)
        {
            Volatile.Write(ref _installState, -1);
            Console.Error.WriteLine(
                $"windowing: GLFW active-window guard failed: {failure.Message}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint GetCurrentProcessActiveWindow()
    {
        nint window = GetActiveWindow();
        if (window == 0)
            return 0;

        _ = GetWindowThreadProcessId(window, out uint ownerProcessId);
        return AcceptWindow(window, ownerProcessId, CurrentProcessId);
    }

    internal static nint AcceptWindow(
        nint window,
        uint ownerProcessId,
        uint currentProcessId) =>
        window != 0
        && ownerProcessId != 0
        && ownerProcessId == currentProcessId
            ? window
            : 0;

    private static bool TryFindImportSlot(
        nint module,
        string importedModule,
        string importedFunction,
        out nint slot)
    {
        slot = 0;
        byte* image = (byte*)module;
        if (*(ushort*)image != DosSignature)
            return false;

        int peOffset = *(int*)(image + 0x3C);
        if (peOffset <= 0 || *(uint*)(image + peOffset) != PeSignature)
            return false;

        byte* optionalHeader = image + peOffset + 24;
        ushort magic = *(ushort*)optionalHeader;
        int dataDirectoryOffset;
        int thunkSize;
        ulong ordinalFlag;
        if (magic == Pe32PlusMagic)
        {
            dataDirectoryOffset = 112;
            thunkSize = 8;
            ordinalFlag = 0x8000000000000000UL;
        }
        else if (magic == Pe32Magic)
        {
            dataDirectoryOffset = 96;
            thunkSize = 4;
            ordinalFlag = 0x80000000UL;
        }
        else
        {
            return false;
        }

        uint sizeOfImage = *(uint*)(optionalHeader + 56);
        uint importRva = *(uint*)(optionalHeader + dataDirectoryOffset + 8);
        uint importSize = *(uint*)(optionalHeader + dataDirectoryOffset + 12);
        if (!Contains(sizeOfImage, importRva, ImportDescriptorSize))
            return false;

        int descriptorLimit = importSize >= ImportDescriptorSize
            ? checked((int)(importSize / ImportDescriptorSize))
            : checked((int)((sizeOfImage - importRva) / ImportDescriptorSize));
        for (int descriptorIndex = 0;
             descriptorIndex < descriptorLimit;
             descriptorIndex++)
        {
            byte* descriptor = image
                + importRva
                + descriptorIndex * ImportDescriptorSize;
            uint originalFirstThunk = *(uint*)descriptor;
            uint nameRva = *(uint*)(descriptor + 12);
            uint firstThunk = *(uint*)(descriptor + 16);
            if (originalFirstThunk == 0 && nameRva == 0 && firstThunk == 0)
                break;
            if (!MatchesAsciiZ(image, sizeOfImage, nameRva, importedModule, true))
                continue;
            if (originalFirstThunk == 0
                || !Contains(sizeOfImage, originalFirstThunk, thunkSize)
                || !Contains(sizeOfImage, firstThunk, thunkSize))
            {
                return false;
            }

            int thunkLimit = checked((int)Math.Min(
                (sizeOfImage - originalFirstThunk) / (uint)thunkSize,
                (sizeOfImage - firstThunk) / (uint)thunkSize));
            for (int thunkIndex = 0; thunkIndex < thunkLimit; thunkIndex++)
            {
                ulong nameThunk = thunkSize == 8
                    ? *(ulong*)(image + originalFirstThunk + thunkIndex * thunkSize)
                    : *(uint*)(image + originalFirstThunk + thunkIndex * thunkSize);
                if (nameThunk == 0)
                    break;
                if ((nameThunk & ordinalFlag) != 0)
                    continue;

                uint importByNameRva = checked((uint)nameThunk);
                if (!Contains(sizeOfImage, importByNameRva, 3)
                    || !MatchesAsciiZ(
                        image,
                        sizeOfImage,
                        importByNameRva + 2,
                        importedFunction,
                        false))
                {
                    continue;
                }

                slot = (nint)(image + firstThunk + thunkIndex * thunkSize);
                return true;
            }

            return false;
        }

        return false;
    }

    private static bool Contains(uint imageSize, uint offset, int length) =>
        length >= 0
        && offset < imageSize
        && (ulong)offset + (uint)length <= imageSize;

    private static bool MatchesAsciiZ(
        byte* image,
        uint imageSize,
        uint offset,
        string expected,
        bool ignoreCase)
    {
        if (!Contains(imageSize, offset, expected.Length + 1))
            return false;

        for (int i = 0; i < expected.Length; i++)
        {
            char actual = (char)image[offset + (uint)i];
            char wanted = expected[i];
            if (ignoreCase)
            {
                actual = char.ToUpperInvariant(actual);
                wanted = char.ToUpperInvariant(wanted);
            }
            if (actual != wanted)
                return false;
        }
        return image[offset + (uint)expected.Length] == 0;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandleW(string moduleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtect(
        nint address,
        nuint size,
        uint newProtection,
        out uint oldProtection);

    [DllImport("user32.dll")]
    private static extern nint GetActiveWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        nint window,
        out uint processId);
}
