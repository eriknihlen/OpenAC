using System.Runtime.InteropServices;
using AcDream.Headless;

using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancel = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += cancel;
PosixSignalRegistration? terminate = null;
if (!OperatingSystem.IsWindows())
{
    terminate = PosixSignalRegistration.Create(
        PosixSignal.SIGTERM,
        context =>
        {
            context.Cancel = true;
            cancellation.Cancel();
        });
}

try
{
    return HeadlessEntryPoint.Run(
        args,
        Console.In,
        Console.Out,
        Console.Error,
        cancellation.Token,
        standardInputIsTerminal: !Console.IsInputRedirected,
        standardOutputIsTerminal: !Console.IsOutputRedirected);
}
finally
{
    terminate?.Dispose();
    Console.CancelKeyPress -= cancel;
}
