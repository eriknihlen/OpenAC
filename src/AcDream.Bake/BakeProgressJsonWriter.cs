using System.Text.Json;

namespace AcDream.Bake;

public interface IBakeProgressSink
{
    void Started(uint bakeToolVersion, string outputPath);

    void Progress(
        string phase,
        long completed,
        long total,
        int failures,
        double elapsedSeconds,
        double etaSeconds,
        long privateBytes,
        long managedBytes);

    void Completed(uint bakeToolVersion, long outputBytes, int failures);

    void Error(string message);
}

public sealed class BakeProgressJsonWriter(TextWriter output) : IBakeProgressSink
{
    public const int CurrentVersion = 1;

    private readonly TextWriter _output = output
        ?? throw new ArgumentNullException(nameof(output));
    private readonly object _gate = new();

    public void Started(uint bakeToolVersion, string outputPath) =>
        Write(new
        {
            v = CurrentVersion,
            e = "started",
            t = DateTimeOffset.UtcNow,
            bakeToolVersion,
            outputPath,
        });

    public void Progress(
        string phase,
        long completed,
        long total,
        int failures,
        double elapsedSeconds,
        double etaSeconds,
        long privateBytes,
        long managedBytes) =>
        Write(new
        {
            v = CurrentVersion,
            e = "progress",
            t = DateTimeOffset.UtcNow,
            phase,
            completed,
            total,
            failures,
            elapsedSeconds,
            etaSeconds,
            privateBytes,
            managedBytes,
        });

    public void Completed(uint bakeToolVersion, long outputBytes, int failures) =>
        Write(new
        {
            v = CurrentVersion,
            e = "completed",
            t = DateTimeOffset.UtcNow,
            bakeToolVersion,
            outputBytes,
            failures,
        });

    public void Error(string message) =>
        Write(new
        {
            v = CurrentVersion,
            e = "error",
            t = DateTimeOffset.UtcNow,
            message,
        });

    private void Write<T>(T value)
    {
        string line = JsonSerializer.Serialize(value);
        lock (_gate)
        {
            _output.WriteLine(line);
            _output.Flush();
        }
    }
}
