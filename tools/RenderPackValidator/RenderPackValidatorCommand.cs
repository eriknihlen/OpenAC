using System.Reflection;
using System.Runtime.Loader;
using AcDream.Plugin.Abstractions.Rendering;

namespace AcDream.Tools.RenderPackValidator;

internal static class RenderPackValidatorCommand
{
    internal static int Run(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (args.Count != 1 || args[0] is "-h" or "--help")
        {
            TextWriter target = args.Count == 1 ? output : error;
            target.WriteLine("usage: RenderPackValidator <built-pack-directory>");
            target.WriteLine("The directory must contain plugin.json and its built entry DLL.");
            return args.Count == 1 ? 0 : 2;
        }

        string packDirectory;
        try
        {
            packDirectory = Path.GetFullPath(args[0]);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            error.WriteLine($"FAIL: invalid pack directory: {exception.Message}");
            return 2;
        }

        string manifestPath = Path.Combine(packDirectory, "plugin.json");
        if (!File.Exists(manifestPath))
        {
            error.WriteLine($"FAIL: plugin.json was not found in '{packDirectory}'.");
            return 1;
        }

        string manifestJson;
        try
        {
            manifestJson = File.ReadAllText(manifestPath);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            error.WriteLine($"FAIL: plugin.json could not be read: {exception.Message}");
            return 1;
        }

        ValidationOutcome parsed = PackManifest.Parse(manifestJson);
        if (!parsed.Success)
        {
            error.WriteLine($"FAIL: {parsed.Reason}");
            return 1;
        }

        PackManifest manifest = parsed.Manifest!;
        string entryPath = ResolveInside(packDirectory, manifest.EntryDll);
        if (!File.Exists(entryPath))
        {
            error.WriteLine($"FAIL: entry DLL '{manifest.EntryDll}' does not exist.");
            return 1;
        }

        PackLoadContext? loadContext = null;
        try
        {
            loadContext = new PackLoadContext(entryPath);
            Assembly assembly = loadContext.LoadFromAssemblyPath(entryPath);
            Type[] entryPoints = GetLoadableTypes(assembly)
                .Where(static type =>
                    !type.IsAbstract
                    && !type.IsInterface
                    && typeof(IRenderPackPlugin).IsAssignableFrom(type))
                .ToArray();
            if (entryPoints.Length != 1)
            {
                error.WriteLine(
                    $"FAIL: entry DLL must contain exactly one public constructible "
                    + $"IRenderPackPlugin; found {entryPoints.Length}.");
                return 1;
            }

            if (!entryPoints[0].IsVisible
                || entryPoints[0].GetConstructor(Type.EmptyTypes) is null)
            {
                error.WriteLine(
                    $"FAIL: render-pack entry point '{entryPoints[0].FullName}' "
                    + "has no public parameterless constructor.");
                return 1;
            }

            var plugin = (IRenderPackPlugin)Activator.CreateInstance(entryPoints[0])!;
            using var registrations = new CapturingRenderPackRegistry();
            plugin.Register(registrations);
            if (registrations.Entries.Count == 0)
            {
                error.WriteLine("FAIL: render-pack entry point registered no packs.");
                return 1;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CapturedRenderPack entry in registrations.Entries)
            {
                RenderPackSdkValidationResult descriptor =
                    RenderPackSdkValidator.ValidateDescriptor(entry.Descriptor);
                if (!descriptor.Success)
                {
                    error.WriteLine($"FAIL: {descriptor.Reason}");
                    return 1;
                }
                if (!seen.Add(entry.Descriptor.Id))
                {
                    error.WriteLine(
                        $"FAIL: render-pack id '{entry.Descriptor.Id}' was registered more than once.");
                    return 1;
                }

                RenderPackSdkValidationResult assets =
                    RenderPackSdkValidator.ValidateAssets(entry.Descriptor, entry.Assets);
                if (!assets.Success)
                {
                    error.WriteLine($"FAIL: {assets.Reason}");
                    return 1;
                }

                output.WriteLine(
                    $"OK: {entry.Descriptor.Id} {entry.Descriptor.PackVersion} "
                    + $"(API {entry.Descriptor.PackApiVersion}, "
                    + $"{entry.Descriptor.QualityPresets.Count} preset(s)).");
            }

            output.WriteLine(
                $"Validated {registrations.Entries.Count} render pack(s) from '{manifest.Id}'.");
            return 0;
        }
        catch (Exception exception)
        {
            error.WriteLine(
                $"FAIL: pack entry point could not be inspected: "
                + exception.GetBaseException().Message);
            return 1;
        }
        finally
        {
            loadContext?.Unload();
        }
    }

    private static string ResolveInside(string root, string relativePath)
    {
        string resolved = Path.GetFullPath(Path.Combine(root, relativePath));
        string prefix = Path.TrimEndingDirectorySeparator(root)
            + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!resolved.StartsWith(prefix, comparison))
            throw new UnauthorizedAccessException("entryDll escapes the pack directory.");
        return resolved;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }

    private sealed class PackLoadContext(string entryPath)
        : AssemblyLoadContext("render-pack-sdk-validator", isCollectible: true)
    {
        private const string AbstractionsAssemblyName = "AcDream.Plugin.Abstractions";
        private readonly AssemblyDependencyResolver _resolver = new(entryPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name == AbstractionsAssemblyName)
                return null;
            string? path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
}

internal sealed class CapturingRenderPackRegistry : IRenderPackRegistry, IDisposable
{
    private readonly List<CapturedRenderPack> _entries = [];
    private bool _disposed;

    internal IReadOnlyList<CapturedRenderPack> Entries => _entries;

    public IDisposable Register(RenderPackDescriptor descriptor, IRenderPackAssets assets)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(assets);
        var entry = new CapturedRenderPack(descriptor, assets);
        _entries.Add(entry);
        return new Registration(_entries, entry);
    }

    public void Dispose()
    {
        _disposed = true;
        _entries.Clear();
    }

    private sealed class Registration(
        List<CapturedRenderPack> entries,
        CapturedRenderPack entry) : IDisposable
    {
        private List<CapturedRenderPack>? _entries = entries;

        public void Dispose() =>
            Interlocked.Exchange(ref _entries, null)?.Remove(entry);
    }
}

internal sealed record CapturedRenderPack(
    RenderPackDescriptor Descriptor,
    IRenderPackAssets Assets);
