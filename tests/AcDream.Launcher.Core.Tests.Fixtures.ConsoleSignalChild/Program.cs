using System.Text.Json;

if (args.Length >= 1 && args[0] == "write-stderr")
{
    if (args.Length < 4
        || !int.TryParse(args[1], out int exitCode)
        || !int.TryParse(args[2], out int lineCount))
    {
        return 64;
    }

    string lineText = args[3];
    for (int index = 0; index < lineCount; index++)
    {
        Console.Error.WriteLine($"{lineText}{index}");
        Console.Error.Flush();
    }

    return exitCode;
}

if (args.Length < 4
    || args[0] != "wait-for-break"
    || string.IsNullOrWhiteSpace(args[1])
    || string.IsNullOrWhiteSpace(args[2]))
{
    return 64;
}

string readyPath = Path.GetFullPath(args[1]);
string breakPath = Path.GetFullPath(args[2]);
string label = args[3];
string[] payloadArguments = args.Skip(4).ToArray();
using var stopped = new ManualResetEventSlim(false);
ConsoleCancelEventHandler handler = (_, eventArgs) =>
{
    if (eventArgs.SpecialKey != ConsoleSpecialKey.ControlBreak)
    {
        return;
    }

    eventArgs.Cancel = true;
    try
    {
        File.WriteAllText(breakPath, label);
    }
    finally
    {
        stopped.Set();
    }
};
Console.CancelKeyPress += handler;
try
{
    string stdin = Console.In.ReadToEnd();
    Directory.CreateDirectory(Path.GetDirectoryName(readyPath)!);
    File.WriteAllText(
        readyPath,
        JsonSerializer.Serialize(new
        {
            processId = Environment.ProcessId,
            label,
            arguments = payloadArguments,
            stdinLength = stdin.Length,
            stdinLineCount = stdin.Count(character => character == '\n'),
        }));

    return stopped.Wait(TimeSpan.FromSeconds(30)) ? 0 : 75;
}
finally
{
    Console.CancelKeyPress -= handler;
}
