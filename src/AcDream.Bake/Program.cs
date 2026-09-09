using AcDream.Bake;


if (BakeCommandLine.IsHelpRequest(args))
{
    Console.Out.WriteLine(BakeCommandLine.Usage);
    return 0;
}

if (!BakeCommandLine.TryParse(args, Console.Error, out BakeCommandLineOptions? command))
{
    return 2;
}

if (!Directory.Exists(command!.DatDirectory))
{
    Console.Error.WriteLine($"error: directory not found: {command.DatDirectory}");
    return 2;
}

IBakeProgressSink? progress = command.ProgressJson
    ? new BakeProgressJsonWriter(Console.Out)
    : null;
try
{
    return BakeRunner.Run(new BakeOptions
    {
        DatDir = command.DatDirectory,
        OutPath = command.OutputPath,
        IdFilter = command.IdFilter,
        LandblockFilter = command.LandblockFilter,
        Threads = command.Threads,
        Progress = progress,
    });
}
catch (Exception exception)
{
    progress?.Error(exception.Message);
    Console.Error.WriteLine($"error: {exception.Message}");
    return 1;
}
