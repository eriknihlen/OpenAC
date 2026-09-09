using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AcDream.App.Rendering.Gpu.Vk;

namespace AcDream.App.Diagnostics;

/// <summary>One bounded, local, best-effort report at the existing Run failure boundary.</summary>
internal static class LocalCrashReportWriter
{
    internal const int MaxReportBytes = 256 * 1024;
    internal const int MaxExceptionNodes = 8;
    private const string Truncated = "[truncated]";

    internal static string? TryWrite(
        Exception failure,
        string diagnosticsDirectory,
        Func<LocalCrashReportContext?>? captureContext = null,
        Action<string>? notify = null)
    {
        string? temporaryPath = null;
        bool ownsTemporaryFile = false;
        try
        {
            LocalCrashReportContext? context = null;
            bool contextCaptureFailed = false;
            try { context = captureContext?.Invoke(); }
            catch { contextCaptureFailed = true; }

            DateTimeOffset utc = DateTimeOffset.UtcNow;
            int processId = Environment.ProcessId;
            Assembly assembly = typeof(LocalCrashReportWriter).Assembly;
            var exceptions = new List<ExceptionEntry>(MaxExceptionNodes);
            AddException(failure, null, "root", null, exceptions);
            LocalCrashGpu? gpu = context?.Gpu;
            LocalCrashWorld? world = context?.World;
            var report = new
            {
                SchemaVersion = 1,
                Utc = utc,
                ProcessId = processId,
                AssemblyVersion = Bound(assembly.GetName().Version?.ToString(), 256),
                InformationalVersion = Bound(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion, 512),
                ModuleMvid = assembly.ManifestModule.ModuleVersionId,
                ContextCaptureFailed = contextCaptureFailed,
                Exceptions = exceptions,
                ExceptionTreeTruncated = exceptions.Any(node => node.ChildrenTruncated),
                Gpu = gpu is null ? null : new LocalCrashGpu(
                    Bound(gpu.DeviceName, 512), Bound(gpu.DriverInfo, 512),
                    Bound(gpu.InstanceApiVersion, 128), Bound(gpu.DeviceApiVersion, 128),
                    gpu.DeviceApiVersionPacked, gpu.Width, gpu.Height, gpu.SampleCount),
                World = world is null ? null : new
                {
                    world.CellId,
                    X = Finite(world.X), Y = Finite(world.Y), Z = Finite(world.Z),
                    State = Bound(world.State, 128),
                    NonFiniteCoordinates = IsNonFinite(world.X) || IsNonFinite(world.Y) || IsNonFinite(world.Z),
                },
            };

            // Strings and traversal are bounded before serialization; never cut JSON bytes.
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });
            if (bytes.Length > MaxReportBytes)
                throw new InvalidOperationException();

            string identity = $"crash-{utc:yyyyMMddTHHmmssfffffffZ}-{processId}-{Guid.NewGuid():N}";
            string finalPath = Path.Combine(diagnosticsDirectory, identity + ".json");
            temporaryPath = Path.Combine(diagnosticsDirectory, "." + identity + $"-{Guid.NewGuid():N}.tmp");
            Directory.CreateDirectory(diagnosticsDirectory);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsTemporaryFile = true;
                stream.Write(bytes);
                stream.Flush();
            }
            File.Move(temporaryPath, finalPath, overwrite: false);
            ownsTemporaryFile = false;
            Notify(notify, $"Local crash report saved: {finalPath}");
            return finalPath;
        }
        catch
        {
            Notify(notify, "Local crash report unavailable.");
            return null;
        }
        finally
        {
            if (ownsTemporaryFile && temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch { }
            }
        }
    }

    private static void AddException(
        Exception exception, int? parentIndex, string relation, int? aggregateIndex,
        List<ExceptionEntry> entries)
    {
        int index = entries.Count;
        var entry = new ExceptionEntry(
            parentIndex, relation, aggregateIndex,
            Bound(exception.GetType().FullName, 512), exception.HResult, MethodStack(exception),
            exception is VulkanCallException vk
                ? new VulkanEntry(Bound(vk.Operation, 512), (int)vk.Result, Bound(vk.Result.ToString(), 128))
                : null);
        entries.Add(entry);
        if (exception is AggregateException aggregate)
        {
            for (int i = 0; i < aggregate.InnerExceptions.Count; i++)
            {
                if (entries.Count == MaxExceptionNodes)
                {
                    entry.ChildrenTruncated = true;
                    break;
                }
                AddException(aggregate.InnerExceptions[i], index, "aggregate", i, entries);
            }
        }
        else if (exception.InnerException is { } inner)
        {
            if (entries.Count == MaxExceptionNodes)
                entry.ChildrenTruncated = true;
            else
                AddException(inner, index, "inner", null, entries);
        }
    }

    private static string MethodStack(Exception exception)
    {
        try
        {
            var trace = new StackTrace(exception, fNeedFileInfo: false);
            var text = new StringBuilder();
            for (int i = 0; i < trace.FrameCount && i < 48; i++)
            {
                MethodBase? method = trace.GetFrame(i)?.GetMethod();
                if (method is null)
                    continue;
                if (text.Length != 0)
                    text.Append('\n');
                text.Append(Bound(method.DeclaringType?.FullName, 256));
                text.Append('.');
                text.Append(Bound(method.Name, 256));
                if (text.Length > 3072)
                    return Bound(text.ToString(), 3072)!;
            }
            if (trace.FrameCount > 48)
                text.Append(Truncated);
            return Bound(text.ToString(), 3072)!;
        }
        catch { return "[unavailable]"; }
    }

    private static string? Bound(string? value, int limit)
    {
        if (value is null || value.Length <= limit)
            return value;
        int end = limit - Truncated.Length;
        if (end > 0 && char.IsHighSurrogate(value[end - 1]) && char.IsLowSurrogate(value[end]))
            end--;
        return string.Concat(value.AsSpan(0, end), Truncated);
    }

    private static bool IsNonFinite(float? value) => value.HasValue && !float.IsFinite(value.Value);
    private static float? Finite(float? value) => IsNonFinite(value) ? null : value;

    private static void Notify(Action<string>? notify, string text)
    {
        try { notify?.Invoke(text); }
        catch { }
    }

    private sealed record VulkanEntry(string? Operation, int Result, string? ResultName);

    private sealed record ExceptionEntry(
        int? ParentIndex, string Relation, int? AggregateIndex, string? Type,
        int HResult, string Stack, VulkanEntry? Vulkan)
    {
        public bool ChildrenTruncated { get; set; }
    }
}

internal sealed record LocalCrashReportContext(LocalCrashGpu? Gpu, LocalCrashWorld? World);

internal sealed record LocalCrashGpu(
    string? DeviceName, string? DriverInfo, string? InstanceApiVersion,
    string? DeviceApiVersion, uint? DeviceApiVersionPacked,
    uint? Width, uint? Height, int? SampleCount);

internal sealed record LocalCrashWorld(uint? CellId, float? X, float? Y, float? Z, string? State);
