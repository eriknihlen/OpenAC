using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Silk.NET.Core.Native;
using Silk.NET.Shaderc;

namespace AcDream.Tools.ShaderCompiler;

internal static class Program
{
    private static unsafe int Main(string[] args)
    {
        if (args.Length < 2
            || args.Length > 3
            || (args.Length == 3 && !string.Equals(args[2], "--force", StringComparison.Ordinal)))
        {
            Console.Error.WriteLine("usage: ShaderCompiler <shaders-dir> <output-dir> [--force]");
            return 2;
        }

        string sourceDirectory = Path.GetFullPath(args[0]);
        string outputDirectory = Path.GetFullPath(args[1]);
        bool force = args.Length == 3;
        Directory.CreateDirectory(outputDirectory);
        string manifestPath = Path.Combine(outputDirectory, "shaders.manifest.json");
        IReadOnlyDictionary<(string Name, string Stage), ShaderStageResult> previousStages =
            force ? new Dictionary<(string, string), ShaderStageResult>() : LoadPreviousStages(manifestPath);

        string[] names = Directory
            .EnumerateFiles(sourceDirectory, "*.vert")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name is not null)
            .Select(name => name!)
            .Where(name => File.Exists(Path.Combine(sourceDirectory, $"{name}.frag")))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var shaderc = Shaderc.GetApi();
        Compiler* compiler = shaderc.CompilerInitialize();
        CompileOptions* options = shaderc.CompileOptionsInitialize();
        shaderc.CompileOptionsSetTargetEnv(options, TargetEnv.Vulkan, (uint)EnvVersion.Vulkan13);
        shaderc.CompileOptionsSetTargetSpirv(options, SpirvVersion.Shaderc16);
        shaderc.CompileOptionsSetOptimizationLevel(options, OptimizationLevel.Performance);

        var entries = new List<ShaderManifestEntry>();
        int failures = 0;
        try
        {
            foreach (string name in names)
            {
                var stages = new List<ShaderStageResult>();
                // Shared across the pair so a fragment input takes the location
                // its vertex output was given, by name.
                var locations = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (string stage in (string[])["vert", "frag"])
                {
                    string path = Path.Combine(sourceDirectory, $"{name}.{stage}");
                    string source = File.ReadAllText(path);
                    if (source.Contains("#include \"", StringComparison.Ordinal))
                        source = GlslIncludeExpander.Expand(source, sourceDirectory);
                    string hash = Sha256(source);
                    string target = Path.Combine(outputDirectory, $"{name}.{stage}.spv");

                    if (previousStages.TryGetValue((name, stage), out ShaderStageResult? previous)
                        && previous.Compiled
                        && string.Equals(previous.SourceSha256, hash, StringComparison.Ordinal)
                        && File.Exists(target))
                    {
                        stages.Add(new ShaderStageResult(stage, hash, true, null));
                        continue;
                    }

                    string transformed;
                    try
                    {
                        transformed = GlslVaryingLocations.Apply(
                            VulkanGlslPreamble.Apply(source, stage),
                            stage,
                            locations);
                    }
                    catch (Exception error)
                    {
                        stages.Add(new ShaderStageResult(stage, hash, false, error.Message));
                        continue;
                    }

                    if (TryCompile(shaderc, compiler, options, transformed, $"{name}.{stage}", stage, out byte[] spirv, out string message))
                    {
                        File.WriteAllBytes(target, spirv);
                        stages.Add(new ShaderStageResult(stage, hash, true, null));
                    }
                    else
                    {
                        if (File.Exists(target))
                            File.Delete(target);
                        stages.Add(new ShaderStageResult(stage, hash, false, Summarise(message)));
                    }
                }

                bool ok = stages.All(stage => stage.Compiled);
                if (!ok)
                {
                    failures++;
                    foreach (string stage in (string[])["vert", "frag"])
                    {
                        string orphan = Path.Combine(outputDirectory, $"{name}.{stage}.spv");
                        if (File.Exists(orphan))
                            File.Delete(orphan);
                    }
                }
                entries.Add(new ShaderManifestEntry(name, ok, stages));
                Console.WriteLine(ok
                    ? $"[shaders] {name}: ok"
                    : $"[shaders] {name}: NOT VULKAN-EXPRESSIBLE YET — {FirstReason(stages)}");
            }
        }
        finally
        {
            shaderc.CompileOptionsRelease(options);
            shaderc.CompilerRelease(compiler);
        }

        var manifest = new ShaderManifest(
            "Campaign V slice V6c. Regenerate with tools/compile-shaders.ps1.",
            entries.OrderBy(entry => entry.Name, StringComparer.Ordinal).ToList());
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(manifest, ShaderManifestJson.Options) + Environment.NewLine);

        Console.WriteLine(
            $"[shaders] {entries.Count - failures}/{entries.Count} pair(s) compiled; manifest at {manifestPath}");
        return 0;
    }

    private static unsafe bool TryCompile(
        Shaderc shaderc,
        Compiler* compiler,
        CompileOptions* options,
        string source,
        string name,
        string stage,
        out byte[] spirv,
        out string message)
    {
        ShaderKind kind = stage == "vert" ? ShaderKind.VertexShader : ShaderKind.FragmentShader;
        byte[] sourceBytes = Encoding.UTF8.GetBytes(source);
        byte[] nameBytes = Encoding.UTF8.GetBytes(name + "\0");
        byte[] entryBytes = Encoding.UTF8.GetBytes("main\0");

        fixed (byte* sourcePointer = sourceBytes)
        fixed (byte* namePointer = nameBytes)
        fixed (byte* entryPointer = entryBytes)
        {
            CompilationResult* result = shaderc.CompileIntoSpv(
                compiler,
                sourcePointer,
                (nuint)sourceBytes.Length,
                kind,
                namePointer,
                entryPointer,
                options);
            try
            {
                CompilationStatus status = shaderc.ResultGetCompilationStatus(result);
                message = SilkMarshal.PtrToString((nint)shaderc.ResultGetErrorMessage(result)) ?? string.Empty;
                if (status != CompilationStatus.Success)
                {
                    spirv = [];
                    return false;
                }

                nuint length = shaderc.ResultGetLength(result);
                spirv = new byte[(int)length];
                new ReadOnlySpan<byte>(shaderc.ResultGetBytes(result), (int)length).CopyTo(spirv);
                return true;
            }
            finally
            {
                shaderc.ResultRelease(result);
            }
        }
    }

    private static string Summarise(string message)
    {
        string[] lines = message
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "unknown compiler failure" : lines[0];
    }

    private static string FirstReason(IEnumerable<ShaderStageResult> stages) =>
        stages.FirstOrDefault(stage => !stage.Compiled)?.Message ?? "unknown";

    private static string Sha256(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n"));
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static IReadOnlyDictionary<(string Name, string Stage), ShaderStageResult> LoadPreviousStages(
        string manifestPath)
    {
        if (!File.Exists(manifestPath))
            return new Dictionary<(string, string), ShaderStageResult>();

        try
        {
            ShaderManifest? manifest = JsonSerializer.Deserialize<ShaderManifest>(
                File.ReadAllText(manifestPath),
                ShaderManifestJson.Options);
            return manifest?.Shaders
                .SelectMany(shader => shader.Stages.Select(stage => (shader.Name, Stage: stage)))
                .ToDictionary(item => (item.Name, item.Stage.Stage), item => item.Stage)
                ?? new Dictionary<(string, string), ShaderStageResult>();
        }
        catch (JsonException)
        {
            return new Dictionary<(string, string), ShaderStageResult>();
        }
    }
}

internal sealed record ShaderStageResult(
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("sourceSha256")] string SourceSha256,
    [property: JsonPropertyName("compiled")] bool Compiled,
    [property: JsonPropertyName("message")] string? Message);

internal sealed record ShaderManifestEntry(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("vulkanReady")] bool VulkanReady,
    [property: JsonPropertyName("stages")] IReadOnlyList<ShaderStageResult> Stages);

internal sealed record ShaderManifest(
    [property: JsonPropertyName("note")] string Note,
    [property: JsonPropertyName("shaders")] IReadOnlyList<ShaderManifestEntry> Shaders);

internal static class ShaderManifestJson
{
    internal static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
