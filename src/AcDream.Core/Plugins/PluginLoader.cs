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
        IAcDreamPlugin? instance = null;
        IRenderPackPlugin? renderPackInstance = null;
        try
        {
            alc = new PluginAssemblyLoadContext(pluginDirectory, dllPath);
            var asm = alc.LoadFromAssemblyPath(dllPath);

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
            bool registerRenderPack =
                manifest.Declares(PluginKind.RenderPack) && renderPacks is not null;
            CountingRenderPackRegistry? countedRenderPacks = registerRenderPack
                ? new CountingRenderPackRegistry(renderPacks!)
                : null;
            Type? renderPackType = null;
            if (registerRenderPack)
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

            if (registerRenderPack && renderPackType is null)
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

            object? sharedInstance = null;
            if (pluginType is not null)
            {
                sharedInstance = Activator.CreateInstance(pluginType);
                instance = (IAcDreamPlugin?)sharedInstance
                    ?? throw new InvalidOperationException(
                        $"could not construct IAcDreamPlugin {pluginType.FullName}");
                instance.Initialize(host);
            }

            if (renderPackType is not null)
            {
                object renderObject = ReferenceEquals(renderPackType, pluginType)
                    ? sharedInstance!
                    : Activator.CreateInstance(renderPackType)
                        ?? throw new InvalidOperationException(
                            $"could not construct IRenderPackPlugin {renderPackType.FullName}");
                renderPackInstance = (IRenderPackPlugin)renderObject;
                renderPackInstance.Register(countedRenderPacks!);
                if (countedRenderPacks!.RegistrationCount == 0)
                {
                    throw new InvalidOperationException(
                        $"render-pack entry point '{renderPackType.FullName}' registered no packs");
                }
            }

            return new LoadedPlugin(
                manifest,
                instance,
                alc,
                Error: null,
                renderPackInstance);
        }
        catch (Exception ex)
        {
            return new LoadedPlugin(
                manifest,
                Plugin: instance,
                LoadContext: alc,
                Error: ex,
                RenderPackPlugin: renderPackInstance);
        }
    }

    private sealed class CountingRenderPackRegistry(IRenderPackRegistry inner) :
        IRenderPackRegistry
    {
        private int _registrationCount;

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
