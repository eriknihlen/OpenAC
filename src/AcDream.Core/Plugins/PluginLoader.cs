using System.Reflection;
using AcDream.Plugin.Abstractions;
using AcDream.Plugin.Abstractions.Rendering;

namespace AcDream.Core.Plugins;

public static class PluginLoader
{
    public static LoadedPlugin Load(
        string pluginDirectory,
        PluginManifest manifest,
        IPluginHost host,
        IRenderPackRegistry? renderPacks = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(host);

        LoadedPlugin? failure = Prepare(
            pluginDirectory,
            manifest,
            registerRenderPack: renderPacks is not null,
            out PreparedPlugin? prepared);
        return failure ?? Activate(prepared!, host, renderPacks);
    }

    /// <summary>
    /// The first half of a load: reads the plugin's assembly into a load
    /// context of its own and finds its entry types, without creating or
    /// starting anything. A reload does this before it lets go of the running
    /// copy, so a new copy that cannot even be read leaves the old one
    /// running. Returns the failure, carrying the load context to unload when
    /// one was made, or null with <paramref name="prepared"/> set.
    /// </summary>
    internal static LoadedPlugin? Prepare(
        string pluginDirectory,
        PluginManifest manifest,
        bool registerRenderPack,
        out PreparedPlugin? prepared)
    {
        prepared = null;
        if (!PluginApi.IsSupported(manifest.ApiVersion))
            return new LoadedPlugin(
                manifest,
                Plugin: null,
                LoadContext: null,
                Error: new PluginApiVersionException(
                    $"plugin '{manifest.Id}' declares apiVersion {manifest.ApiVersion}, "
                    + $"but this build supports {PluginApi.MinimumSupported}"
                    + $"..{PluginApi.Current}"));

        var dllPath = Path.Combine(pluginDirectory, manifest.EntryDll);
        if (!File.Exists(dllPath))
            return new LoadedPlugin(
                manifest,
                Plugin: null,
                LoadContext: null,
                Error: new FileNotFoundException($"entry dll not found: {dllPath}", dllPath));

        PluginAssemblyLoadContext? alc = null;
        try
        {
            PluginPackageSnapshot package = PluginPackageSnapshot.Read(pluginDirectory);
            alc = new PluginAssemblyLoadContext(package, dllPath);
            var asm = alc.LoadEntry(dllPath);

            IEnumerable<Type> types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException rtle)
            {
                types = rtle.Types.OfType<Type>();
            }

            Type[] concreteTypes = types
                .Where(static type => !type.IsAbstract && !type.IsInterface)
                .ToArray();
            Type? pluginType = manifest.Declares(PluginKind.Gameplay)
                ? concreteTypes.FirstOrDefault(
                    static type => typeof(IAcDreamPlugin).IsAssignableFrom(type))
                : null;
            bool declaresRenderPack =
                manifest.Declares(PluginKind.RenderPack) && registerRenderPack;
            Type? renderPackType = null;
            if (declaresRenderPack)
            {
                Type[] renderPackTypes = concreteTypes
                    .Where(static type => typeof(IRenderPackPlugin).IsAssignableFrom(type))
                    .ToArray();
                if (renderPackTypes.Length != 1)
                {
                    return new LoadedPlugin(
                        manifest,
                        Plugin: null,
                        LoadContext: alc,
                        Error: new InvalidOperationException(
                            $"render-pack entry DLL '{manifest.EntryDll}' must contain exactly "
                            + "one IRenderPackPlugin implementation; found "
                            + renderPackTypes.Length));
                }

                renderPackType = renderPackTypes[0];
                if (!renderPackType.IsVisible
                    || renderPackType.GetConstructor(Type.EmptyTypes) is null)
                {
                    return new LoadedPlugin(
                        manifest,
                        Plugin: null,
                        LoadContext: alc,
                        Error: new InvalidOperationException(
                            $"render-pack entry type '{renderPackType.FullName}' must be public, "
                            + "non-abstract, and expose a public parameterless constructor"));
                }
            }

            if (manifest.Declares(PluginKind.Gameplay) && pluginType is null)
            {
                return new LoadedPlugin(
                    manifest,
                    Plugin: null,
                    LoadContext: alc,
                    Error: new InvalidOperationException(
                        $"no IAcDreamPlugin implementation found in {manifest.EntryDll}"));
            }

            if (declaresRenderPack && renderPackType is null)
            {
                return new LoadedPlugin(
                    manifest,
                    Plugin: null,
                    LoadContext: alc,
                    Error: new InvalidOperationException(
                        $"no IRenderPackPlugin implementation found in {manifest.EntryDll}"));
            }

            if (pluginType is null && renderPackType is null)
            {
                return new LoadedPlugin(
                    manifest,
                    Plugin: null,
                    LoadContext: alc,
                    Error: new InvalidOperationException(
                        "the host did not supply any facility declared by this plugin"));
            }

            prepared = new PreparedPlugin(
                manifest,
                alc,
                pluginType,
                renderPackType,
                package);
            return null;
        }
        catch (Exception ex)
        {
            return new LoadedPlugin(
                manifest,
                Plugin: null,
                LoadContext: alc,
                Error: ex);
        }
    }

    /// <summary>
    /// The second half of a load: creates the entry types found by
    /// <see cref="Prepare"/>, hands the gameplay entry its host and lets a
    /// render pack register. Enabling is left to the caller.
    /// </summary>
    internal static LoadedPlugin Activate(
        PreparedPlugin prepared,
        IPluginHost host,
        IRenderPackRegistry? renderPacks)
    {
        PluginManifest manifest = prepared.Manifest;
        IAcDreamPlugin? instance = null;
        IRenderPackPlugin? renderPackInstance = null;
        try
        {
            CountingRenderPackRegistry? countedRenderPacks =
                prepared.RenderPackType is not null && renderPacks is not null
                    ? new CountingRenderPackRegistry(renderPacks, prepared.Directory)
                    : null;
            object? sharedInstance = null;
            if (prepared.PluginType is { } pluginType)
            {
                sharedInstance = Activator.CreateInstance(pluginType);
                instance = (IAcDreamPlugin?)sharedInstance
                    ?? throw new InvalidOperationException(
                        $"could not construct IAcDreamPlugin {pluginType.FullName}");
                instance.Initialize(host);
            }

            if (prepared.RenderPackType is { } renderPackType
                && countedRenderPacks is not null)
            {
                object renderObject = ReferenceEquals(renderPackType, prepared.PluginType)
                    ? sharedInstance!
                    : Activator.CreateInstance(renderPackType)
                        ?? throw new InvalidOperationException(
                            $"could not construct IRenderPackPlugin {renderPackType.FullName}");
                renderPackInstance = (IRenderPackPlugin)renderObject;
                renderPackInstance.Register(countedRenderPacks);
                if (countedRenderPacks.RegistrationCount == 0)
                {
                    throw new InvalidOperationException(
                        $"render-pack entry point '{renderPackType.FullName}' registered no packs");
                }
            }

            return new LoadedPlugin(
                manifest,
                instance,
                prepared.LoadContext,
                Error: null,
                renderPackInstance);
        }
        catch (Exception ex)
        {
            return new LoadedPlugin(
                manifest,
                Plugin: instance,
                LoadContext: prepared.LoadContext,
                Error: ex,
                RenderPackPlugin: renderPackInstance);
        }
    }

    private sealed class CountingRenderPackRegistry(
        IRenderPackRegistry inner,
        string pluginDirectory) :
        IRenderPackRegistry
    {
        private int _registrationCount;

        public string? PluginDirectory => pluginDirectory;

        internal int RegistrationCount => Volatile.Read(ref _registrationCount);

        public IDisposable Register(
            RenderPackDescriptor descriptor,
            IRenderPackAssets assets)
        {
            IDisposable registration = inner.Register(descriptor, assets)
                ?? throw new InvalidOperationException(
                    "The render-pack registry returned a null registration handle.");
            Interlocked.Increment(ref _registrationCount);
            return new CountedRegistration(this, registration);
        }

        private sealed class CountedRegistration(
            CountingRenderPackRegistry owner,
            IDisposable inner) : IDisposable
        {
            private CountingRenderPackRegistry? _owner = owner;
            private IDisposable? _inner = inner;

            public void Dispose()
            {
                CountingRenderPackRegistry? activeOwner =
                    Interlocked.Exchange(ref _owner, null);
                if (activeOwner is null)
                    return;
                Interlocked.Decrement(ref activeOwner._registrationCount);
                Interlocked.Exchange(ref _inner, null)?.Dispose();
            }
        }
    }
}

/// <summary>
/// A plugin whose assembly is loaded and whose entry types are known, but
/// which has not been created yet.
/// </summary>
internal sealed record PreparedPlugin(
    PluginManifest Manifest,
    PluginAssemblyLoadContext LoadContext,
    Type? PluginType,
    Type? RenderPackType,
    PluginPackageSnapshot Package)
{
    /// <summary>The plugin's folder, as a full path.</summary>
    internal string Directory => Package.Directory;
}
