using AcDream.App.Platform;
using AcDream.App.Rendering;
using Silk.NET.Core.Native;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace AcDream.App.Rendering.Gpu.Vk;

internal sealed unsafe class VulkanGraphicsContext : IDisposable
{
    private const ulong AcquireTimeoutNanoseconds = 1_000_000_000ul;

    private readonly IWindow _window;
    private readonly RuntimeOptions _options;
    private readonly GraphicalHostPlatformServices _platform;
    private readonly FramePacingPolicy _pacing;
    private readonly Action<string> _log;

    private Silk.NET.Vulkan.Vk? _vk;
    private Instance _instance;
    private KhrSurface? _surfaceApi;
    private SurfaceKHR _surface;
    private PhysicalDevice _physicalDevice;
    private Device _device;
    private KhrSwapchain? _swapchainApi;
    private Queue _graphicsQueue;
    private Queue _presentQueue;
    private VulkanQueueFamilyChoice? _families;
    private VulkanSwapchain? _swapchain;
    private VulkanGpuDevice? _gpuDevice;
    private VulkanDebugNames _debugNames = VulkanDebugNames.Disabled;
    private VulkanDeviceFeatureSupport? _features;
    private VulkanDeviceLimitSupport? _limits;
    private VulkanFormatSupport? _formats;
    private IReadOnlyList<string> _instanceExtensions = [];
    private bool _recreateAtFrameBoundary;
    private bool _disposed;
    private SwapchainBackbuffer? _swapchainBackbuffer;
    private OffscreenBackbuffer? _offscreen;
    private bool _offscreenActive;

    private VulkanGraphicsContext(
        IWindow window,
        RuntimeOptions options,
        GraphicalHostPlatformServices platform,
        FramePacingPolicy pacing,
        Action<string> log)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _pacing = pacing;
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    internal static VulkanGraphicsContext Acquire(
        IWindow window,
        RuntimeOptions options,
        GraphicalHostPlatformServices platform,
        FramePacingPolicy pacing,
        int requestedSampleCount,
        GpuMemoryProfile? memoryProfile = null,
        Action<string>? log = null)
    {
        var context = new VulkanGraphicsContext(
            window,
            options,
            platform,
            pacing,
            log ?? Console.WriteLine);
        try
        {
            context.CreateInstanceAndSurface();
            context.SelectDeviceAndGate();
            context.CreateDevice(requestedSampleCount, memoryProfile ?? GpuMemoryProfile.Default);
            return context;
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    internal VulkanCapabilityRecord? Capabilities { get; private set; }

    internal VulkanGpuDevice Device =>
        _gpuDevice ?? throw new InvalidOperationException(
            "The Vulkan RHI device has not been created.");

    /// <summary>Samples the backbuffer pass renders with. 1 when MSAA is off or unsupported.</summary>
    internal int SampleCount { get; private set; } = 1;

    internal uint Width => _swapchain?.Configuration?.Width ?? 0u;

    internal uint Height => _swapchain?.Configuration?.Height ?? 0u;

    internal static string ShaderSpirvDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "Rendering", "Shaders", "spv");

    internal bool PrepareFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_recreateAtFrameBoundary && _swapchain!.IsCreated && !_offscreenActive)
            return true;

        if (!RecreateSwapchain())
            return false;

        VulkanSwapchainConfiguration resized = _swapchain!.Configuration!;
        if (_offscreenActive)
        {
            // The window has a surface again: back to presenting, the offscreen images let go.
            Device.SetBackbuffer(_swapchainBackbuffer);
            _offscreenActive = false;
            _offscreen?.Dispose();
            _offscreen = null;
            _log("vulkan: offscreen frames done; presenting again");
        }
        Device.ConfigureBackbufferAttachments(
            resized.Width,
            resized.Height,
            resized.ImageFormat,
            SampleCount);
        return true;
    }

    /// <summary>
    /// Opens a frame into offscreen images of the given size instead of the
    /// swapchain, for a window that has no area to present to (minimised;
    /// its swapchain may or may not have survived) but a watcher who wants
    /// the picture anyway. The images are the swapchain's format, so every
    /// pipeline is the one the window uses. <see cref="PrepareFrame"/>
    /// switches back once the window has area again. Returns false before
    /// the first swapchain ever existed (no format to match).
    /// </summary>
    internal bool PrepareOffscreenFrame(uint width, uint height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (width == 0 || height == 0 || _swapchain?.Configuration is not { } configuration)
            return false;
        if (_offscreen is null || _offscreen.Width != width || _offscreen.Height != height)
        {
            VulkanInterop.Check(_vk!.DeviceWaitIdle(_device), "vkDeviceWaitIdle (offscreen)");
            _offscreen?.Dispose();
            _offscreen = new OffscreenBackbuffer(_vk!, _device, _physicalDevice, configuration.ImageFormat, width, height);
            _log($"vulkan: offscreen frames {width}x{height} while the window has no surface");
        }
        if (!_offscreenActive)
        {
            Device.SetBackbuffer(_offscreen);
            _offscreenActive = true;
        }
        Device.ConfigureBackbufferAttachments(width, height, configuration.ImageFormat, SampleCount);
        return true;
    }

    internal void NoteFrameClosed()
    {
        if (_gpuDevice is not null && !_gpuDevice.PresentSucceeded)
            _recreateAtFrameBoundary = true;
    }

    /// <summary>Arms recreation, used when the acquire itself reported out-of-date.</summary>
    internal void RequestRecreate() => _recreateAtFrameBoundary = true;

    private void CreateInstanceAndSurface()
    {
        _vk = GraphicalVulkanLoader.CreateApi();
        if (_window.VkSurface is null)
        {
            throw new NotSupportedException(
                "The windowing backend did not expose a Vulkan surface. " +
                "acdream requires GLFW 3.4 built with Vulkan support.");
        }

        byte** requiredNames = _window.VkSurface.GetRequiredExtensions(out uint requiredCount);
        var required = new List<string>((int)requiredCount);
        for (uint i = 0; i < requiredCount; i++)
            required.Add(VulkanInterop.ReadString(requiredNames[i]));

        VulkanInstanceFactory.Created instance = VulkanInstanceFactory.Create(
            _vk,
            required,
            enableOptionalExtensions: _options.DevTools);
        _instance = instance.Instance;
        _instanceExtensions = instance.EnabledExtensions;

        if (!_vk.TryGetInstanceExtension(_instance, out KhrSurface surfaceApi))
        {
            throw new NotSupportedException(
                "VK_KHR_surface is required but its entry points could not be loaded.");
        }

        _surfaceApi = surfaceApi;
        _surface = _window.VkSurface.Create<AllocationCallbacks>(
            _instance.ToHandle(),
            null).ToSurface();
    }

    private void SelectDeviceAndGate()
    {
        Silk.NET.Vulkan.Vk vk = _vk!;
        IReadOnlyList<VulkanPhysicalDeviceCandidate> candidates =
            VulkanPhysicalDeviceInspector.Enumerate(vk, _instance, out PhysicalDevice[] handles);
        VulkanPhysicalDeviceChoice? choice = VulkanPhysicalDeviceSelection.Choose(
            candidates,
            _options.VulkanDeviceOverride);
        if (choice is null)
        {
            throw new NotSupportedException(
                "No Vulkan physical device was enumerated. Install or update a " +
                "Vulkan 1.3 driver for this GPU.");
        }

        _physicalDevice = handles[choice.Device.Index];

        _features = VulkanPhysicalDeviceInspector.ReadFeatures(vk, _physicalDevice);

        IReadOnlyList<VulkanQueueFamilyCandidate> queueFamilies =
            VulkanPhysicalDeviceInspector.ReadQueueFamilies(
                vk,
                _physicalDevice,
                _surfaceApi,
                _surface);
        VulkanQueueFamilyChoice? families = VulkanQueueFamilySelection.Choose(queueFamilies);
        if (families is null)
        {
            throw new NotSupportedException(
                $"'{choice.Device.DeviceName}' exposes no queue family that can both " +
                "render and present to the window surface.");
        }

        _families = families;
        VulkanLogicalDeviceFactory.Created created = VulkanLogicalDeviceFactory.Create(
            vk,
            _physicalDevice,
            families,
            requireSwapchain: true,
            availableFeatures: _features);
        _device = created.Device;
        _graphicsQueue = created.GraphicsQueue;
        _presentQueue = created.PresentQueue;

        if (!vk.TryGetDeviceExtension(_instance, _device, out KhrSwapchain swapchainApi))
        {
            throw new NotSupportedException(
                "VK_KHR_swapchain is required but its entry points could not be loaded.");
        }

        _swapchainApi = swapchainApi;
        _swapchain = new VulkanSwapchain(
            vk,
            _surfaceApi!,
            swapchainApi,
            _physicalDevice,
            _device,
            _surface,
            families);

        (SurfaceCapabilitiesKHR surfaceCapabilities,
            IReadOnlyList<SurfaceFormatKHR> formats,
            IReadOnlyList<PresentModeKHR> presentModes) = _swapchain.QuerySurface();

        Vector2D<int> framebuffer = _window.FramebufferSize;
        VulkanSwapchainConfiguration planned = VulkanSwapchainConfigurationFactory.Create(
            surfaceCapabilities,
            formats,
            presentModes,
            _pacing,
            (uint)Math.Max(0, framebuffer.X),
            (uint)Math.Max(0, framebuffer.Y));

        var surfaceSupport = new VulkanSurfaceSupport(
            PresentSupported: true,
            SelectedFormat: planned.ImageFormat,
            SelectedColorSpace: planned.ColorSpace,
            SelectedPresentMode: planned.PresentMode,
            SelectedImageCount: planned.ImageCount,
            SelectedWidth: planned.Width,
            SelectedHeight: planned.Height,
            SupportsTransferSource:
                VulkanSwapchainConfigurationFactory.SupportsTransferSource(surfaceCapabilities),
            AvailableFormats: [.. formats.Select(format => format.Format).Distinct()],
            AvailablePresentModes: [.. presentModes]);

        VulkanFunctionProbeResult probe = VulkanActiveDeviceProbe.Run(
            vk,
            _physicalDevice,
            _device,
            _graphicsQueue,
            families.GraphicsFamily);

        _limits = VulkanPhysicalDeviceInspector.ReadLimits(vk, _physicalDevice);
        _formats = VulkanPhysicalDeviceInspector.ReadFormats(
            vk,
            _physicalDevice,
            VulkanSwapchainConfigurationFactory.OffersUnormFormat(formats));

        var record = new VulkanCapabilityRecord(
            DateTimeOffset.UtcNow,
            _platform.RuntimeIdentifier,
            _platform.OperatingSystem,
            _platform.WindowBackend.RequestedProtocol,
            GlfwNativePlatformProbe.GetActiveProtocol(_platform.OperatingSystem),
            VulkanApiVersion.Describe(
                VulkanApiVersion.Make(
                    VulkanCapabilityRequirements.RequiredApiMajor,
                    VulkanCapabilityRequirements.RequiredApiMinor,
                    0)),
            VulkanApiVersion.Describe(choice.Device.ApiVersion),
            choice.Device.ApiVersion,
            choice.Device.DeviceName,
            VulkanPhysicalDeviceInspector.DescribeDriver(choice.Device),
            choice.Device.DeviceType,
            choice.Device.Index,
            choice.Reason,
            _options.VulkanDeviceOverride,
            ForcedUnsupportedFeature: null,
            candidates,
            _instanceExtensions,
            created.EnabledExtensions,
            families.GraphicsFamily,
            families.PresentFamily,
            _features,
            _limits,
            _formats,
            surfaceSupport,
            probe,
            SupportFailures: []);

        record = VulkanCapabilityRequirements.Reevaluate(record);
        record = VulkanCapabilityRequirements.ApplyForcedUnsupported(
            record,
            _options.VulkanForcedUnsupportedFeature);
        Capabilities = record;

        string reportPath = Path.Combine(
            _platform.Paths.DiagnosticsDirectory,
            VulkanCapabilityGuard.ReportFileName);
        VulkanCapabilityReportWriter.Write(reportPath, record);
        VulkanCapabilityGuard.ThrowIfUnsupported(record, reportPath);

        _log(
            "vulkan: capability gate passed " +
            $"({record.ActiveDisplayProtocol}, {record.DeviceName}, " +
            $"{record.DeviceApiVersion}, {record.DriverInfo}); " +
            $"swapchain {planned.ImageFormat}/{planned.PresentMode} " +
            $"{planned.Width}x{planned.Height} x{planned.ImageCount}; " +
            $"report={reportPath}");
        _log($"vulkan: device selection — {choice.Reason}");
    }

    private void CreateDevice(int requestedSampleCount, GpuMemoryProfile memoryProfile)
    {
        Silk.NET.Vulkan.Vk vk = _vk!;
        _debugNames = VulkanDebugNames.Create(vk, _instance, _device, [.. _instanceExtensions]);

        if (!RecreateSwapchain())
        {
            throw new InvalidOperationException(
                "The swapchain could not be created for the initial framebuffer size.");
        }

        VulkanSwapchainConfiguration configuration = _swapchain!.Configuration!;
        _gpuDevice = new VulkanGpuDevice(
            vk,
            _physicalDevice,
            _device,
            _graphicsQueue,
            _presentQueue,
            _families!.GraphicsFamily,
            _features!,
            _limits!,
            _formats!,
            Capabilities!.DeviceName,
            Capabilities.DriverInfo,
            Capabilities.DeviceApiVersion,
            _debugNames,
            _swapchainBackbuffer = new SwapchainBackbuffer(_swapchain!, _presentQueue),
            ShaderSpirvDirectory(),
            _platform.Paths.CacheDirectory,
            memoryProfile: memoryProfile,
            retainBackbufferCapture:
                !string.IsNullOrWhiteSpace(_options.AutomationArtifactDirectory));

        SampleCount = (int)Math.Min(
            (uint)Math.Max(1, requestedSampleCount),
            Math.Max(1u, _gpuDevice.Capabilities.MaxSampleCount));
        _gpuDevice.ConfigureBackbufferAttachments(
            configuration.Width,
            configuration.Height,
            configuration.ImageFormat,
            SampleCount);

        _log(
            $"vulkan: RHI backend up — {_gpuDevice.Allocator.Describe()}, " +
            $"{SampleCount}x MSAA, {memoryProfile.Name} memory profile, pipeline cache " +
            (_gpuDevice.PipelineCacheLoadedFromDisk ? "reused" : "cold") +
            $", debug names {(_debugNames.IsEnabled ? "on" : "off")}");
    }

    private bool RecreateSwapchain()
    {
        Vector2D<int> framebuffer = _window.FramebufferSize;
        uint width = (uint)Math.Max(0, framebuffer.X);
        uint height = (uint)Math.Max(0, framebuffer.Y);
        if (VulkanSwapchainRecreationPolicy.OnFramebufferSize(width, height)
            == VulkanSwapchainAction.Idle)
        {
            // No log here: while minimised this runs at frame rate.
            return false;
        }

        VulkanInterop.Check(_vk!.DeviceWaitIdle(_device), "vkDeviceWaitIdle (recreate)");
        _recreateAtFrameBoundary = false;
        bool recreated = _swapchain!.Recreate(_pacing, width, height);
        _log($"vulkan: swapchain recreated {width}x{height} ok={recreated}");
        return recreated;
    }

    /// <summary>
    /// Adapts the swapchain to the narrow surface the RHI device needs. The
    /// device deliberately does not own presentation: format, extent,
    /// present-mode and the recreation policy are pure decisions that are already
    /// unit-tested, and duplicating that judgement inside the backend would fork
    /// it.
    /// </summary>
    private sealed class SwapchainBackbuffer(VulkanSwapchain swapchain, Queue presentQueue)
        : IVulkanBackbuffer
    {
        public Format ImageFormat => swapchain.Configuration!.ImageFormat;

        public uint Width => swapchain.Configuration!.Width;

        public uint Height => swapchain.Configuration!.Height;

        public bool TryAcquire(Semaphore acquired, out uint imageIndex)
        {
            VulkanSwapchainAction action = swapchain.TryAcquire(
                acquired,
                AcquireTimeoutNanoseconds,
                out imageIndex);
            return action is VulkanSwapchainAction.Continue
                or VulkanSwapchainAction.RecreateAtFrameBoundary;
        }

        public Image ImageAt(uint imageIndex) => swapchain.ImageAt(imageIndex);

        public ImageView ViewAt(uint imageIndex) => swapchain.ViewAt(imageIndex);

        public Semaphore RenderCompleteAt(uint imageIndex) =>
            swapchain.RenderCompleteAt(imageIndex);

        public bool Present(uint imageIndex) =>
            swapchain.Present(presentQueue, imageIndex) is VulkanSwapchainAction.Continue;
    }

    /// <summary>
    /// Two colour images of the swapchain's format that stand in for it
    /// while the window is minimised: acquired in turn, never presented,
    /// so a watcher on the phone still gets frames. No semaphores: the
    /// device orders the work through its timeline alone.
    /// </summary>
    private sealed class OffscreenBackbuffer : IVulkanBackbuffer, IDisposable
    {
        private const int ImageCount = 2;
        private readonly Silk.NET.Vulkan.Vk _vk;
        private readonly Device _device;
        private readonly Image[] _images = new Image[ImageCount];
        private readonly ImageView[] _views = new ImageView[ImageCount];
        private readonly DeviceMemory[] _memory = new DeviceMemory[ImageCount];
        private uint _next;
        private bool _disposed;

        public OffscreenBackbuffer(Silk.NET.Vulkan.Vk vk, Device device, PhysicalDevice physicalDevice, Format format, uint width, uint height)
        {
            _vk = vk;
            _device = device;
            ImageFormat = format;
            Width = width;
            Height = height;
            for (int index = 0; index < ImageCount; index++)
            {
                var imageInfo = new ImageCreateInfo
                {
                    SType = StructureType.ImageCreateInfo,
                    ImageType = ImageType.Type2D,
                    Format = format,
                    Extent = new Extent3D(width, height, 1),
                    MipLevels = 1,
                    ArrayLayers = 1,
                    Samples = SampleCountFlags.Count1Bit,
                    Tiling = ImageTiling.Optimal,
                    Usage = VulkanSwapchainConfigurationFactory.RequiredUsage,
                    SharingMode = SharingMode.Exclusive,
                    InitialLayout = ImageLayout.Undefined,
                };
                VulkanInterop.Check(vk.CreateImage(device, &imageInfo, null, out _images[index]), "vkCreateImage (offscreen)");
                vk.GetImageMemoryRequirements(device, _images[index], out MemoryRequirements requirements);
                uint typeIndex = VulkanActiveDeviceProbe.FindMemoryType(vk, physicalDevice, requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit)
                    ?? VulkanActiveDeviceProbe.FindMemoryType(vk, physicalDevice, requirements.MemoryTypeBits, MemoryPropertyFlags.None)
                    ?? throw new InvalidOperationException("No memory type can hold an offscreen backbuffer image.");
                var allocation = new MemoryAllocateInfo
                {
                    SType = StructureType.MemoryAllocateInfo,
                    AllocationSize = requirements.Size,
                    MemoryTypeIndex = typeIndex,
                };
                VulkanInterop.Check(vk.AllocateMemory(device, &allocation, null, out _memory[index]), "vkAllocateMemory (offscreen)");
                VulkanInterop.Check(vk.BindImageMemory(device, _images[index], _memory[index], 0), "vkBindImageMemory (offscreen)");
                var viewInfo = new ImageViewCreateInfo
                {
                    SType = StructureType.ImageViewCreateInfo,
                    Image = _images[index],
                    ViewType = ImageViewType.Type2D,
                    Format = format,
                    SubresourceRange = new ImageSubresourceRange
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        BaseMipLevel = 0,
                        LevelCount = 1,
                        BaseArrayLayer = 0,
                        LayerCount = 1,
                    },
                };
                VulkanInterop.Check(vk.CreateImageView(device, &viewInfo, null, out _views[index]), "vkCreateImageView (offscreen)");
            }
        }

        public Format ImageFormat { get; }

        public uint Width { get; }

        public uint Height { get; }

        public bool Presents => false;

        public ImageLayout FinalLayout => ImageLayout.General;

        public bool TryAcquire(Semaphore acquired, out uint imageIndex)
        {
            imageIndex = _next;
            _next = (_next + 1) % ImageCount;
            return !_disposed;
        }

        public Image ImageAt(uint imageIndex) => _images[imageIndex];

        public ImageView ViewAt(uint imageIndex) => _views[imageIndex];

        public Semaphore RenderCompleteAt(uint imageIndex) => default;

        public bool Present(uint imageIndex) => true;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            for (int index = 0; index < ImageCount; index++)
            {
                if (_views[index].Handle != 0)
                    _vk.DestroyImageView(_device, _views[index], null);
                if (_images[index].Handle != 0)
                    _vk.DestroyImage(_device, _images[index], null);
                if (_memory[index].Handle != 0)
                    _vk.FreeMemory(_device, _memory[index], null);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        Silk.NET.Vulkan.Vk? vk = _vk;
        if (vk is not null && _device.Handle != 0)
            vk.DeviceWaitIdle(_device);

        _gpuDevice?.Dispose();
        _gpuDevice = null;
        _offscreen?.Dispose();
        _offscreen = null;
        _swapchain?.Dispose();
        _swapchain = null;

        if (vk is not null && _device.Handle != 0)
        {
            vk.DestroyDevice(_device, null);
            _device = default;
        }

        _debugNames.Dispose();
        _debugNames = VulkanDebugNames.Disabled;

        if (vk is not null && _surfaceApi is not null && _surface.Handle != 0)
        {
            _surfaceApi.DestroySurface(_instance, _surface, null);
            _surface = default;
        }

        _swapchainApi?.Dispose();
        _swapchainApi = null;
        _surfaceApi?.Dispose();
        _surfaceApi = null;

        if (vk is not null && _instance.Handle != 0)
        {
            vk.DestroyInstance(_instance, null);
            _instance = default;
        }

        vk?.Dispose();
        _vk = null;
    }
}
