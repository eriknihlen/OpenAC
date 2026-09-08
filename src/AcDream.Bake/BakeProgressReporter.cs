namespace AcDream.Bake;

internal static class BakeProgressReporter
{
    public static void Write(
        TextWriter humanOutput,
        IBakeProgressSink? machineOutput,
        string phase,
        long completed,
        int total,
        int failures,
        TimeSpan elapsed,
        double etaSeconds,
        long privateBytes,
        long managedBytes)
    {
        ArgumentNullException.ThrowIfNull(humanOutput);
        humanOutput.WriteLine(
            $"[{elapsed:hh\\:mm\\:ss}] extracted {completed:N0}/{total:N0}, "
            + $"failures={failures:N0}, elapsed={elapsed.TotalSeconds:F0}s, "
            + $"ETA={etaSeconds:F0}s, "
            + $"private={privateBytes / 1024.0 / 1024.0:F0}MB, "
            + $"managed={managedBytes / 1024.0 / 1024.0:F0}MB");
        machineOutput?.Progress(
            phase,
            completed,
            total,
            failures,
            elapsed.TotalSeconds,
            etaSeconds,
            privateBytes,
            managedBytes);
    }
}
