using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;
using Silk.NET.GLFW;

namespace AcDream.App.Platform;

internal sealed record PackagedVulkanLayout(
    string LoaderPath,
    string DriverLibraryPath,
    string DriverManifestPath);

internal static unsafe class GraphicalVulkanLoader
{
    internal const string DriverFilesEnvironmentVariable = "VK_DRIVER_FILES";

    private static readonly object Gate = new();
    private static string? _packagedLoaderPath;
    private static nint _packagedLoaderHandle;

    internal static void ConfigureForGlfw(
        GraphicalHostOperatingSystem operatingSystem,
        Glfw glfw)
    {
        ArgumentNullException.ThrowIfNull(glfw);
        if (operatingSystem != GraphicalHostOperatingSystem.MacOS)
            return;

        PackagedVulkanLayout? layout = ResolvePackagedLayout(
            AppContext.BaseDirectory,
            File.Exists);
        if (layout is null)
            return;

        lock (Gate)
        {
            if (_packagedLoaderPath is not null)
            {
                if (!string.Equals(
                        _packagedLoaderPath,
                        layout.LoaderPath,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The packaged Vulkan loader was already configured " +
                        "from a different application directory.");
                }

                return;
            }

            string? previousDriverFiles = Environment.GetEnvironmentVariable(
                DriverFilesEnvironmentVariable);
            Environment.SetEnvironmentVariable(
                DriverFilesEnvironmentVariable,
                layout.DriverManifestPath);

            nint loaderHandle = NativeLibrary.Load(layout.LoaderPath);
            try
            {
                if (!NativeLibrary.TryGetExport(
                        loaderHandle,
                        "vkGetInstanceProcAddr",
                        out nint getInstanceProcAddress))
                {
                    throw new EntryPointNotFoundException(
                        $"The packaged Vulkan loader '{layout.LoaderPath}' " +
                        "does not export vkGetInstanceProcAddr.");
                }

                if (!glfw.Context.TryGetProcAddress(
                        "glfwInitVulkanLoader",
                        out nint initVulkanLoader))
                {
                    throw new EntryPointNotFoundException(
                        "The published GLFW library does not export " +
                        "glfwInitVulkanLoader; GLFW 3.4 or newer is required.");
                }

                ((delegate* unmanaged[Cdecl]<nint, void>)initVulkanLoader)(
                    getInstanceProcAddress);
                _packagedLoaderHandle = loaderHandle;
                _packagedLoaderPath = layout.LoaderPath;
            }
            catch
            {
                NativeLibrary.Free(loaderHandle);
                Environment.SetEnvironmentVariable(
                    DriverFilesEnvironmentVariable,
                    previousDriverFiles);
                throw;
            }
        }
    }

    internal static Silk.NET.Vulkan.Vk CreateApi()
    {
        lock (Gate)
        {
            if (_packagedLoaderPath is not { } loaderPath)
                return Silk.NET.Vulkan.Vk.GetApi();
            if (_packagedLoaderHandle == 0)
            {
                throw new InvalidOperationException(
                    "The packaged Vulkan loader handle is unavailable.");
            }

            return new Silk.NET.Vulkan.Vk(
                new DefaultNativeContext(loaderPath));
        }
    }

    internal static PackagedVulkanLayout? ResolvePackagedLayout(
        string applicationDirectory,
        Func<string, bool> fileExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        ArgumentNullException.ThrowIfNull(fileExists);

        string baseDirectory = Path.GetFullPath(applicationDirectory);
        var layout = new PackagedVulkanLayout(
            Path.Combine(
                baseDirectory,
                "Frameworks",
                "libvulkan.1.dylib"),
            Path.Combine(
                baseDirectory,
                "Frameworks",
                "libMoltenVK.dylib"),
            Path.Combine(
                baseDirectory,
                "Resources",
                "vulkan",
                "icd.d",
                "MoltenVK_icd.json"));

        (string Label, string Path)[] files =
        [
            ("Vulkan loader", layout.LoaderPath),
            ("MoltenVK driver", layout.DriverLibraryPath),
            ("MoltenVK driver manifest", layout.DriverManifestPath),
        ];
        if (files.All(file => !fileExists(file.Path)))
            return null;

        string[] missing = files
            .Where(file => !fileExists(file.Path))
            .Select(file => $"{file.Label}: {file.Path}")
            .ToArray();
        if (missing.Length != 0)
        {
            throw new InvalidOperationException(
                "The packaged macOS Vulkan runtime is incomplete. Missing " +
                string.Join("; ", missing));
        }

        return layout;
    }
}
