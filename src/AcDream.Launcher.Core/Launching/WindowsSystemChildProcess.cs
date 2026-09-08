using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AcDream.Launcher.Core.Launching;

internal sealed class WindowsSystemChildProcess : ILauncherChildProcess
{
    private readonly LauncherProcessSpec _spec;
    private readonly IWindowsConsoleControl _consoleControl;
    private Process? _process;
    private TextWriter? _standardInput;
    private int _processGroupId;
    private bool _raisingEnabled;
    private BoundedProcessOutputCapture? _stderrCapture;
    private FileStream? _stderrReadStream;
    private Thread? _stderrPumpThread;

    internal WindowsSystemChildProcess(
        LauncherProcessSpec spec,
        IWindowsConsoleControl? consoleControl = null)
    {
        _spec = spec ?? throw new ArgumentNullException(nameof(spec));
        _consoleControl = consoleControl ?? WindowsConsoleControl.Instance;
    }

    public bool HasExited => RequireProcess().HasExited;

    public int ExitCode => RequireProcess().ExitCode;

    public TextWriter StandardInput => _standardInput
        ?? throw new InvalidOperationException("The child process has not started.");

    public event EventHandler? Exited;

    public void Start()
    {
        if (_process is not null)
        {
            throw new InvalidOperationException("The child process already started.");
        }

        WindowsProcessStartResult started = WindowsProcessNative.Start(_spec);
        try
        {
            _process = Process.GetProcessById(started.ProcessId);
            _process.EnableRaisingEvents = true;
            _process.Exited += OnExited;
            _raisingEnabled = true;
            _standardInput = started.TakeStandardInput();
            _processGroupId = started.ProcessId;
            if (!string.IsNullOrWhiteSpace(_spec.StderrLogPath))
            {
                SafeFileHandle stderrRead = started.TakeStandardErrorRead()
                    ?? throw new InvalidOperationException(
                        "The launcher child stderr pipe was not created.");
                _stderrCapture = new BoundedProcessOutputCapture(_spec.StderrLogPath);
                _stderrReadStream = new FileStream(
                    stderrRead,
                    FileAccess.Read,
                    4096,
                    isAsync: false);
                _stderrPumpThread = new Thread(PumpStderr)
                {
                    IsBackground = true,
                    Name = "acdream-launcher-stderr-pump",
                };
                _stderrPumpThread.Start();
            }

            started.Resume();
        }
        catch
        {
            started.Terminate();
            _standardInput?.Dispose();
            _standardInput = null;
            _stderrReadStream?.Dispose();
            _stderrReadStream = null;
            _stderrCapture?.Dispose();
            _stderrCapture = null;
            if (_process is not null)
            {
                if (_raisingEnabled)
                {
                    _process.Exited -= OnExited;
                }

                _process.Dispose();
                _process = null;
            }

            throw;
        }
        finally
        {
            started.Dispose();
        }
    }

    private void PumpStderr()
    {
        FileStream? stream = _stderrReadStream;
        BoundedProcessOutputCapture? capture = _stderrCapture;
        if (stream is null || capture is null)
        {
            return;
        }

        byte[] buffer = new byte[4096];
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                capture.Append(buffer.AsSpan(0, read));
            }
        }
        catch (Exception error)
            when (error is IOException or ObjectDisposedException)
        {
        }
    }

    public bool TryRequestGracefulStop()
    {
        try
        {
            if (!_spec.SupportsConsoleGracefulStop
                || _process is not { HasExited: false } process
                || _processGroupId <= 0)
            {
                return false;
            }

            return _consoleControl.TrySendBreak(process.Id, _processGroupId);
        }
        catch
        {
            return false;
        }
    }

    public bool CloseMainWindow() => RequireProcess().CloseMainWindow();

    public void Kill() => RequireProcess().Kill(entireProcessTree: true);

    public bool WaitForExit(TimeSpan timeout) => RequireProcess().WaitForExit(timeout);

    public void Dispose()
    {
        _standardInput?.Dispose();
        _standardInput = null;
        if (_stderrReadStream is not null)
        {
            _stderrReadStream.Dispose();
            _stderrReadStream = null;
            _stderrPumpThread?.Join(TimeSpan.FromSeconds(2));
            _stderrPumpThread = null;
        }

        _stderrCapture?.Dispose();
        _stderrCapture = null;
        if (_process is not null)
        {
            if (_raisingEnabled)
            {
                _process.Exited -= OnExited;
            }

            _process.Dispose();
            _process = null;
        }
    }

    private Process RequireProcess() => _process
        ?? throw new InvalidOperationException("The child process has not started.");

    private void OnExited(object? sender, EventArgs e) =>
        Exited?.Invoke(this, EventArgs.Empty);
}

internal interface IWindowsConsoleControl
{
    bool TrySendBreak(int childProcessId, int childProcessGroupId);
}

internal sealed class WindowsConsoleControl : IWindowsConsoleControl
{
    private const uint CtrlBreakEvent = 1;

    internal static WindowsConsoleControl Instance { get; } = new();

    private WindowsConsoleControl()
    {
    }

    public bool TrySendBreak(int childProcessId, int childProcessGroupId)
    {
        if (!OperatingSystem.IsWindows()
            || childProcessId <= 0
            || childProcessGroupId <= 0)
        {
            return false;
        }

        lock (WindowsConsoleSynchronization.Gate)
        {
            bool attachedHere = false;
            try
            {
                uint[] processes = new uint[1];
                if (Native.GetConsoleProcessList(processes, 1) == 0)
                {
                    if (!Native.AttachConsole((uint)childProcessId))
                    {
                        return false;
                    }

                    attachedHere = true;
                }

                return Native.GenerateConsoleCtrlEvent(
                    CtrlBreakEvent,
                    (uint)childProcessGroupId);
            }
            catch
            {
                return false;
            }
            finally
            {
                if (attachedHere)
                {
                    _ = Native.FreeConsole();
                }
            }
        }
    }

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AttachConsole(uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FreeConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GenerateConsoleCtrlEvent(
            uint controlEvent,
            uint processGroupId);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint GetConsoleProcessList(
            [Out] uint[] processList,
            uint processCount);
    }
}

internal static class WindowsConsoleSynchronization
{
    internal static object Gate { get; } = new();
}

internal sealed class WindowsProcessStartResult : IDisposable
{
    private readonly SafeKernelHandle _processHandle;
    private readonly SafeKernelHandle _threadHandle;
    private SafeFileHandle? _standardInput;
    private SafeFileHandle? _stderrRead;
    private bool _resumed;

    internal WindowsProcessStartResult(
        int processId,
        SafeKernelHandle processHandle,
        SafeKernelHandle threadHandle,
        SafeFileHandle standardInput,
        SafeFileHandle? stderrRead = null)
    {
        ProcessId = processId;
        _processHandle = processHandle;
        _threadHandle = threadHandle;
        _standardInput = standardInput;
        _stderrRead = stderrRead;
    }

    internal int ProcessId { get; }

    internal TextWriter TakeStandardInput()
    {
        SafeFileHandle handle = _standardInput
            ?? throw new InvalidOperationException("Standard input was already claimed.");
        var stream = new FileStream(handle, FileAccess.Write, 4096, isAsync: false);
        _standardInput = null;
        try
        {
            return new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
            };
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal SafeFileHandle? TakeStandardErrorRead()
    {
        SafeFileHandle? handle = _stderrRead;
        _stderrRead = null;
        return handle;
    }

    internal void Resume()
    {
        if (WindowsProcessNative.ResumeThread(_threadHandle) == uint.MaxValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "The Windows launcher child could not be resumed.");
        }

        _resumed = true;
    }

    internal void Terminate()
    {
        if (!_processHandle.IsInvalid)
        {
            _ = WindowsProcessNative.TerminateProcess(_processHandle, 74);
        }
    }

    public void Dispose()
    {
        if (!_resumed)
        {
            Terminate();
        }

        _standardInput?.Dispose();
        _stderrRead?.Dispose();
        _threadHandle.Dispose();
        _processHandle.Dispose();
    }
}

internal static class WindowsProcessNative
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint StartfUseStdHandles = 0x00000100;
    private const short SwHide = 0;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint DuplicateSameAccess = 0x00000002;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private static readonly IntPtr ProcThreadAttributeHandleList = new(0x00020002);

    internal static WindowsProcessStartResult Start(LauncherProcessSpec spec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.ExecutablePath);
        ArgumentNullException.ThrowIfNull(spec.Arguments);

        lock (WindowsConsoleSynchronization.Gate)
        {
            bool allocatedConsole = false;
            try
            {
                if (!HasConsole())
                {
                    if (!AllocConsole())
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(),
                            "The Windows launcher could not allocate the child console.");
                    }

                    allocatedConsole = true;
                    IntPtr consoleWindow = GetConsoleWindow();
                    if (consoleWindow != IntPtr.Zero)
                    {
                        _ = ShowWindow(consoleWindow, SwHide);
                    }
                }

                return StartCore(spec);
            }
            finally
            {
                if (allocatedConsole)
                {
                    _ = FreeConsole();
                }
            }
        }
    }

    private static WindowsProcessStartResult StartCore(LauncherProcessSpec spec)
    {
        SafeFileHandle? parentInput = null;
        SafeFileHandle? parentStderrRead = null;
        SafeHandle? childError = null;
        try
        {
            using SafeFileHandle childInput = CreateChildInputPipe(
                out SafeFileHandle createdParentInput);
            parentInput = createdParentInput;
            using SafeKernelHandle childOutput = DuplicateOrOpenNull(StdOutputHandle);
            childError = string.IsNullOrWhiteSpace(spec.StderrLogPath)
                ? DuplicateOrOpenNull(StdErrorHandle)
                : CreateChildOutputPipe(out parentStderrRead);
            using var attributes = new ProcessThreadAttributeList(
                childInput.DangerousGetHandle(),
                childOutput.DangerousGetHandle(),
                childError.DangerousGetHandle());

            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = StartfUseStdHandles,
                    StandardInput = childInput.DangerousGetHandle(),
                    StandardOutput = childOutput.DangerousGetHandle(),
                    StandardError = childError.DangerousGetHandle(),
                },
                AttributeList = attributes.Pointer,
            };
            string executable = ResolveExecutable(spec.ExecutablePath);
            string commandLineText = BuildCommandLine(executable, spec.Arguments);
            var commandLine = new StringBuilder(commandLineText, commandLineText.Length + 1);
            string? workingDirectory = string.IsNullOrWhiteSpace(spec.WorkingDirectory)
                ? null
                : Path.GetFullPath(spec.WorkingDirectory);

            if (!CreateProcessW(
                    executable,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles: true,
                    CreateSuspended | CreateNewProcessGroup | ExtendedStartupInfoPresent,
                    IntPtr.Zero,
                    workingDirectory,
                    ref startup,
                    out ProcessInformation information))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "The Windows launcher child could not be created.");
            }

            var processHandle = new SafeKernelHandle(
                information.Process,
                ownsHandle: true);
            var threadHandle = new SafeKernelHandle(
                information.Thread,
                ownsHandle: true);
            try
            {
                var result = new WindowsProcessStartResult(
                    checked((int)information.ProcessId),
                    processHandle,
                    threadHandle,
                    parentInput,
                    parentStderrRead);
                parentInput = null;
                parentStderrRead = null;
                return result;
            }
            catch
            {
                _ = TerminateProcess(processHandle, 74);
                threadHandle.Dispose();
                processHandle.Dispose();
                throw;
            }
        }
        finally
        {
            parentInput?.Dispose();
            parentStderrRead?.Dispose();
            childError?.Dispose();
        }
    }

    private static bool HasConsole()
    {
        uint[] processes = new uint[1];
        return GetConsoleProcessList(processes, 1) != 0;
    }

    internal static uint ResumeThread(SafeKernelHandle thread) =>
        NativeResumeThread(thread);

    internal static bool TerminateProcess(SafeKernelHandle process, uint exitCode) =>
        NativeTerminateProcess(process, exitCode);

    internal static string BuildCommandLine(
        string executable,
        IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder();
        AppendQuotedArgument(builder, executable);
        foreach (string argument in arguments)
        {
            ArgumentNullException.ThrowIfNull(argument);
            builder.Append(' ');
            AppendQuotedArgument(builder, argument);
        }

        return builder.ToString();
    }

    private static void AppendQuotedArgument(StringBuilder builder, string value)
    {
        builder.Append('"');
        int backslashes = 0;
        foreach (char character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes);
            backslashes = 0;
            builder.Append(character);
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
    }

    private static SafeFileHandle CreateChildInputPipe(out SafeFileHandle parentInput)
    {
        var security = new SecurityAttributes
        {
            Length = Marshal.SizeOf<SecurityAttributes>(),
            InheritHandle = true,
        };
        if (!CreatePipe(out IntPtr read, out IntPtr write, ref security, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "The launcher child stdin pipe could not be created.");
        }

        var child = new SafeFileHandle(read, ownsHandle: true);
        parentInput = new SafeFileHandle(write, ownsHandle: true);
        if (!SetHandleInformation(
                parentInput,
                HandleFlagInherit,
                0))
        {
            int error = Marshal.GetLastWin32Error();
            child.Dispose();
            parentInput.Dispose();
            throw new Win32Exception(error,
                "The launcher child stdin pipe could not be isolated.");
        }

        return child;
    }

    private static SafeFileHandle CreateChildOutputPipe(out SafeFileHandle parentRead)
    {
        var security = new SecurityAttributes
        {
            Length = Marshal.SizeOf<SecurityAttributes>(),
            InheritHandle = true,
        };
        if (!CreatePipe(out IntPtr read, out IntPtr write, ref security, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "The launcher child stderr pipe could not be created.");
        }

        var child = new SafeFileHandle(write, ownsHandle: true);
        parentRead = new SafeFileHandle(read, ownsHandle: true);
        if (!SetHandleInformation(
                parentRead,
                HandleFlagInherit,
                0))
        {
            int error = Marshal.GetLastWin32Error();
            child.Dispose();
            parentRead.Dispose();
            throw new Win32Exception(error,
                "The launcher child stderr pipe could not be isolated.");
        }

        return child;
    }

    private static SafeKernelHandle DuplicateOrOpenNull(int standardHandle)
    {
        IntPtr source = GetStdHandle(standardHandle);
        if (source != IntPtr.Zero && source != new IntPtr(-1))
        {
            IntPtr current = GetCurrentProcess();
            if (DuplicateHandle(
                    current,
                    source,
                    current,
                    out IntPtr duplicate,
                    0,
                    inheritHandle: true,
                    DuplicateSameAccess))
            {
                return new SafeKernelHandle(duplicate, ownsHandle: true);
            }
        }

        IntPtr nul = CreateFileW(
            "NUL",
            GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal,
            IntPtr.Zero);
        if (nul == IntPtr.Zero || nul == new IntPtr(-1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "The launcher child fallback output handle could not be opened.");
        }

        var handle = new SafeKernelHandle(nul, ownsHandle: true);
        if (!SetHandleInformation(handle, HandleFlagInherit, HandleFlagInherit))
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error,
                "The launcher child fallback output handle could not be inherited.");
        }

        return handle;
    }

    private static string ResolveExecutable(string executable)
    {
        if (Path.IsPathFullyQualified(executable))
        {
            return Path.GetFullPath(executable);
        }

        var buffer = new StringBuilder(32_768);
        uint length = SearchPathW(
            null,
            executable,
            null,
            (uint)buffer.Capacity,
            buffer,
            IntPtr.Zero);
        if (length == 0 || length >= buffer.Capacity)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"Launcher child executable '{executable}' was not found.");
        }

        return Path.GetFullPath(buffer.ToString());
    }

    private sealed class ProcessThreadAttributeList : IDisposable
    {
        private IntPtr _pointer;
        private IntPtr _handles;
        private bool _initialized;

        internal ProcessThreadAttributeList(params IntPtr[] handles)
        {
            nuint size = 0;
            _ = InitializeProcThreadAttributeList(
                IntPtr.Zero,
                1,
                0,
                ref size);
            _pointer = Marshal.AllocHGlobal(checked((nint)size));
            if (!InitializeProcThreadAttributeList(_pointer, 1, 0, ref size))
            {
                int error = Marshal.GetLastWin32Error();
                Dispose();
                throw new Win32Exception(error,
                    "The launcher child handle list could not be initialized.");
            }
            _initialized = true;

            _handles = Marshal.AllocHGlobal(handles.Length * IntPtr.Size);
            for (int index = 0; index < handles.Length; index++)
            {
                Marshal.WriteIntPtr(_handles, index * IntPtr.Size, handles[index]);
            }

            if (!UpdateProcThreadAttribute(
                    _pointer,
                    0,
                    ProcThreadAttributeHandleList,
                    _handles,
                    checked((nuint)(handles.Length * IntPtr.Size)),
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                Dispose();
                throw new Win32Exception(error,
                    "The launcher child inherited-handle list could not be set.");
            }
        }

        internal IntPtr Pointer => _pointer;

        public void Dispose()
        {
            if (_pointer != IntPtr.Zero)
            {
                if (_initialized)
                {
                    DeleteProcThreadAttributeList(_pointer);
                    _initialized = false;
                }
                Marshal.FreeHGlobal(_pointer);
                _pointer = IntPtr.Zero;
            }

            if (_handles != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_handles);
                _handles = IntPtr.Zero;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Length;
        internal IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] internal bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal int Size;
        internal string? Reserved;
        internal string? Desktop;
        internal string? Title;
        internal int X;
        internal int Y;
        internal int XSize;
        internal int YSize;
        internal int XCountChars;
        internal int YCountChars;
        internal int FillAttribute;
        internal uint Flags;
        internal short ShowWindow;
        internal short Reserved2Size;
        internal IntPtr Reserved2;
        internal IntPtr StandardInput;
        internal IntPtr StandardOutput;
        internal IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        internal StartupInfo StartupInfo;
        internal IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal IntPtr Process;
        internal IntPtr Thread;
        internal uint ProcessId;
        internal uint ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(
        out IntPtr readPipe,
        out IntPtr writePipe,
        ref SecurityAttributes pipeAttributes,
        uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(
        SafeHandle handle,
        uint mask,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(
        [Out] uint[] processList,
        uint processCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int commandShow);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(
        IntPtr sourceProcess,
        IntPtr sourceHandle,
        IntPtr targetProcess,
        out IntPtr targetHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint SearchPathW(
        string? path,
        string fileName,
        string? extension,
        uint bufferLength,
        StringBuilder buffer,
        IntPtr filePart);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr attributeList,
        int attributeCount,
        int flags,
        ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr attributeList,
        uint flags,
        IntPtr attribute,
        IntPtr value,
        nuint size,
        IntPtr previousValue,
        IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", EntryPoint = "ResumeThread", SetLastError = true)]
    private static extern uint NativeResumeThread(SafeKernelHandle thread);

    [DllImport("kernel32.dll", EntryPoint = "TerminateProcess", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeTerminateProcess(
        SafeKernelHandle process,
        uint exitCode);
}

internal sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeKernelHandle(IntPtr handle, bool ownsHandle)
        : base(ownsHandle)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle() => CloseHandle(handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
