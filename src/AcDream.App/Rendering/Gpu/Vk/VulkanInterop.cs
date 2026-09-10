using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace AcDream.App.Rendering.Gpu.Vk;

internal sealed class VulkanCallException : InvalidOperationException
{
    internal VulkanCallException(string operation, Result result)
        : base($"Vulkan call failed: {operation} returned {result}.")
    {
        Operation = operation;
        Result = result;
    }

    internal string Operation { get; }

    internal Result Result { get; }
}

internal static unsafe class VulkanInterop
{
    internal static void Check(Result result, string operation)
    {
        if (result != Result.Success)
            throw new VulkanCallException(operation, result);
    }

    /// <summary>Read a NUL-terminated ASCII field out of a Vulkan struct.</summary>
    internal static string ReadString(byte* value)
        => value is null ? string.Empty : SilkMarshal.PtrToString((nint)value) ?? string.Empty;

    internal static nint AllocateStringArray(IReadOnlyList<string> values)
        => SilkMarshal.StringArrayToPtr(values.ToArray());

    internal static void FreeStringArray(nint handle)
    {
        if (handle != 0)
            SilkMarshal.Free(handle);
    }

    /// <summary>Enumerate the instance extension names the loader advertises.</summary>
    internal static IReadOnlyList<string> EnumerateInstanceExtensions(Silk.NET.Vulkan.Vk vk)
    {
        ArgumentNullException.ThrowIfNull(vk);
        uint count = 0;
        Check(
            vk.EnumerateInstanceExtensionProperties((byte*)null, ref count, null),
            "vkEnumerateInstanceExtensionProperties (count)");
        if (count == 0)
            return [];

        var properties = new ExtensionProperties[count];
        fixed (ExtensionProperties* first = properties)
        {
            Check(
                vk.EnumerateInstanceExtensionProperties((byte*)null, ref count, first),
                "vkEnumerateInstanceExtensionProperties");
        }

        return ReadNames(properties, count);
    }

    /// <summary>Enumerate the device extension names one physical device advertises.</summary>
    internal static IReadOnlyList<string> EnumerateDeviceExtensions(
        Silk.NET.Vulkan.Vk vk,
        PhysicalDevice device)
    {
        ArgumentNullException.ThrowIfNull(vk);
        uint count = 0;
        Check(
            vk.EnumerateDeviceExtensionProperties(device, (byte*)null, ref count, null),
            "vkEnumerateDeviceExtensionProperties (count)");
        if (count == 0)
            return [];

        var properties = new ExtensionProperties[count];
        fixed (ExtensionProperties* first = properties)
        {
            Check(
                vk.EnumerateDeviceExtensionProperties(device, (byte*)null, ref count, first),
                "vkEnumerateDeviceExtensionProperties");
        }

        return ReadNames(properties, count);
    }

    private static IReadOnlyList<string> ReadNames(ExtensionProperties[] properties, uint count)
    {
        var names = new List<string>((int)count);
        for (uint i = 0; i < count && i < properties.Length; i++)
        {
            ExtensionProperties entry = properties[i];
            names.Add(ReadString(entry.ExtensionName));
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }
}

internal sealed unsafe class VulkanInstanceFactory
{
    internal sealed record Created(
        Instance Instance,
        IReadOnlyList<string> EnabledExtensions,
        IReadOnlyList<string> UnavailableOptionalExtensions,
        uint ApiVersion);

    internal static Created Create(
        Silk.NET.Vulkan.Vk vk,
        IReadOnlyList<string> requiredExtensions,
        bool enableOptionalExtensions)
    {
        ArgumentNullException.ThrowIfNull(vk);
        ArgumentNullException.ThrowIfNull(requiredExtensions);

        IReadOnlyList<string> available = VulkanInterop.EnumerateInstanceExtensions(vk);
        VulkanExtensionPlan plan = VulkanExtensionSelection.Resolve(
            available,
            requiredExtensions,
            VulkanExtensionSelection.ResolveOptionalInstanceExtensions(
                enableOptionalExtensions));
        if (!plan.IsSatisfied)
        {
            throw new NotSupportedException(
                "The Vulkan loader does not advertise the required instance " +
                $"extension(s): {string.Join(", ", plan.MissingRequired)}.");
        }

        uint apiVersion = VulkanApiVersion.Make(
            VulkanCapabilityRequirements.RequiredApiMajor,
            VulkanCapabilityRequirements.RequiredApiMinor,
            0);

        nint applicationName = SilkMarshal.StringToPtr("acdream");
        nint engineName = SilkMarshal.StringToPtr("acdream");
        nint extensionNames = VulkanInterop.AllocateStringArray(plan.Enabled);
        try
        {
            var application = new ApplicationInfo
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = (byte*)applicationName,
                ApplicationVersion = VulkanApiVersion.Make(0, 1, 0),
                PEngineName = (byte*)engineName,
                EngineVersion = VulkanApiVersion.Make(0, 1, 0),
                ApiVersion = apiVersion,
            };
            bool portability = plan.Enabled.Contains(
                VulkanExtensionSelection.PortabilityEnumerationExtension,
                StringComparer.Ordinal);
            var create = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                Flags = portability
                    ? InstanceCreateFlags.EnumeratePortabilityBitKhr
                    : InstanceCreateFlags.None,
                PApplicationInfo = &application,
                EnabledExtensionCount = (uint)plan.Enabled.Count,
                PpEnabledExtensionNames = (byte**)extensionNames,
                EnabledLayerCount = 0,
                PpEnabledLayerNames = null,
            };

            VulkanInterop.Check(
                vk.CreateInstance(&create, null, out Instance instance),
                "vkCreateInstance");
            return new Created(
                instance,
                plan.Enabled,
                plan.UnavailableOptional,
                apiVersion);
        }
        finally
        {
            VulkanInterop.FreeStringArray(extensionNames);
            SilkMarshal.Free(engineName);
            SilkMarshal.Free(applicationName);
        }
    }
}

internal static unsafe class VulkanPhysicalDeviceInspector
{
    internal static IReadOnlyList<VulkanPhysicalDeviceCandidate> Enumerate(
        Silk.NET.Vulkan.Vk vk,
        Instance instance,
        out PhysicalDevice[] handles)
    {
        ArgumentNullException.ThrowIfNull(vk);

        uint count = 0;
        VulkanInterop.Check(
            vk.EnumeratePhysicalDevices(instance, ref count, null),
            "vkEnumeratePhysicalDevices (count)");
        handles = new PhysicalDevice[count];
        if (count == 0)
            return [];

        fixed (PhysicalDevice* first = handles)
        {
            VulkanInterop.Check(
                vk.EnumeratePhysicalDevices(instance, ref count, first),
                "vkEnumeratePhysicalDevices");
        }

        var candidates = new List<VulkanPhysicalDeviceCandidate>((int)count);
        for (int i = 0; i < handles.Length; i++)
        {
            PhysicalDeviceProperties properties;
            vk.GetPhysicalDeviceProperties(handles[i], &properties);
            candidates.Add(
                new VulkanPhysicalDeviceCandidate(
                    i,
                    VulkanInterop.ReadString(properties.DeviceName),
                    properties.DeviceType,
                    properties.ApiVersion,
                    properties.DriverVersion,
                    properties.VendorID,
                    properties.DeviceID,
                    LargestDeviceLocalHeap(vk, handles[i])));
        }

        return candidates;
    }

    /// <summary>
    /// Sum of the device-local heaps, which is the tie-break the plan specifies.
    /// Summed rather than maximised because a device that splits its VRAM across
    /// heaps is not smaller than one that does not.
    /// </summary>
    internal static ulong LargestDeviceLocalHeap(
        Silk.NET.Vulkan.Vk vk,
        PhysicalDevice device)
    {
        PhysicalDeviceMemoryProperties memory;
        vk.GetPhysicalDeviceMemoryProperties(device, &memory);
        ulong total = 0;
        for (uint i = 0; i < memory.MemoryHeapCount && i < 16; i++)
        {
            MemoryHeap heap = memory.MemoryHeaps[(int)i];
            if (heap.Flags.HasFlag(MemoryHeapFlags.DeviceLocalBit))
                total += heap.Size;
        }

        return total;
    }

    internal static VulkanDeviceFeatureSupport ReadFeatures(
        Silk.NET.Vulkan.Vk vk,
        PhysicalDevice device)
    {
        ArgumentNullException.ThrowIfNull(vk);

        var vulkan13 = new PhysicalDeviceVulkan13Features
        {
            SType = StructureType.PhysicalDeviceVulkan13Features,
        };
        var vulkan12 = new PhysicalDeviceVulkan12Features
        {
            SType = StructureType.PhysicalDeviceVulkan12Features,
            PNext = &vulkan13,
        };
        var vulkan11 = new PhysicalDeviceVulkan11Features
        {
            SType = StructureType.PhysicalDeviceVulkan11Features,
            PNext = &vulkan12,
        };
        var features2 = new PhysicalDeviceFeatures2
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &vulkan11,
        };
        vk.GetPhysicalDeviceFeatures2(device, &features2);

        PhysicalDeviceFeatures core = features2.Features;
        return new VulkanDeviceFeatureSupport
        {
            MultiDrawIndirect = core.MultiDrawIndirect,
            DrawIndirectFirstInstance = core.DrawIndirectFirstInstance,
            ShaderClipDistance = core.ShaderClipDistance,
            TextureCompressionBc = core.TextureCompressionBC,
            SamplerAnisotropy = core.SamplerAnisotropy,
            ShaderDrawParameters = vulkan11.ShaderDrawParameters,
            Multiview = vulkan11.Multiview,
            TimelineSemaphore = vulkan12.TimelineSemaphore,
            HostQueryReset = vulkan12.HostQueryReset,
            RuntimeDescriptorArray = vulkan12.RuntimeDescriptorArray,
            DescriptorBindingPartiallyBound = vulkan12.DescriptorBindingPartiallyBound,
            DescriptorBindingSampledImageUpdateAfterBind =
                vulkan12.DescriptorBindingSampledImageUpdateAfterBind,
            DescriptorBindingUpdateUnusedWhilePending =
                vulkan12.DescriptorBindingUpdateUnusedWhilePending,
            DescriptorBindingVariableDescriptorCount =
                vulkan12.DescriptorBindingVariableDescriptorCount,
            ShaderSampledImageArrayNonUniformIndexing =
                vulkan12.ShaderSampledImageArrayNonUniformIndexing,
            DynamicRendering = vulkan13.DynamicRendering,
            Synchronization2 = vulkan13.Synchronization2,
            Maintenance4 = vulkan13.Maintenance4,
            ShaderDemoteToHelperInvocation = vulkan13.ShaderDemoteToHelperInvocation,
        };
    }

    internal static VulkanDeviceLimitSupport ReadLimits(
        Silk.NET.Vulkan.Vk vk,
        PhysicalDevice device)
    {
        ArgumentNullException.ThrowIfNull(vk);

        var indexing = new PhysicalDeviceDescriptorIndexingProperties
        {
            SType = StructureType.PhysicalDeviceDescriptorIndexingProperties,
        };
        var properties2 = new PhysicalDeviceProperties2
        {
            SType = StructureType.PhysicalDeviceProperties2,
            PNext = &indexing,
        };
        vk.GetPhysicalDeviceProperties2(device, &properties2);

        PhysicalDeviceLimits limits = properties2.Properties.Limits;
        return new VulkanDeviceLimitSupport
        {
            MaxPushConstantsSize = limits.MaxPushConstantsSize,
            MaxClipDistances = limits.MaxClipDistances,
            MaxBoundDescriptorSets = limits.MaxBoundDescriptorSets,
            MaxDescriptorSetStorageBuffersDynamic = limits.MaxDescriptorSetStorageBuffersDynamic,
            MaxDescriptorSetStorageBuffers = limits.MaxDescriptorSetStorageBuffers,
            MaxPerStageDescriptorStorageBuffers = limits.MaxPerStageDescriptorStorageBuffers,
            MaxDescriptorSetUniformBuffersDynamic = limits.MaxDescriptorSetUniformBuffersDynamic,
            MaxDescriptorSetUpdateAfterBindSampledImages =
                indexing.MaxDescriptorSetUpdateAfterBindSampledImages,
            MaxPerStageDescriptorUpdateAfterBindSampledImages =
                indexing.MaxPerStageDescriptorUpdateAfterBindSampledImages,
            TimestampComputeAndGraphics = limits.TimestampComputeAndGraphics,
            MinStorageBufferOffsetAlignment = (uint)limits.MinStorageBufferOffsetAlignment,
            MaxStorageBufferRange = limits.MaxStorageBufferRange,
            MinUniformBufferOffsetAlignment = (uint)limits.MinUniformBufferOffsetAlignment,
            MaxImageDimension2D = limits.MaxImageDimension2D,
            MaxImageArrayLayers = limits.MaxImageArrayLayers,
            DeviceLocalHeapBytes = LargestDeviceLocalHeap(vk, device),
            MaxColorSampleCount = HighestSampleCount(
                limits.FramebufferColorSampleCounts & limits.FramebufferDepthSampleCounts),
        };
    }

    internal static uint HighestSampleCount(SampleCountFlags counts)
    {
        if (counts.HasFlag(SampleCountFlags.Count8Bit)) return 8;
        if (counts.HasFlag(SampleCountFlags.Count4Bit)) return 4;
        if (counts.HasFlag(SampleCountFlags.Count2Bit)) return 2;
        return 1;
    }

    internal static VulkanFormatSupport ReadFormats(
        Silk.NET.Vulkan.Vk vk,
        PhysicalDevice device,
        bool surfaceOffersUnorm)
    {
        ArgumentNullException.ThrowIfNull(vk);

        Format depthStencil = ChooseDepthStencilFormat(vk, device);
        FormatProperties rgba16;
        vk.GetPhysicalDeviceFormatProperties(device, Format.R16G16B16A16Sfloat, &rgba16);
        FormatFeatureFlags rgba16Features = rgba16.OptimalTilingFeatures;

        return new VulkanFormatSupport
        {
            SwapchainUnormFormat = surfaceOffersUnorm,
            DepthStencilFormat = depthStencil,
            DepthStencilSampled =
                depthStencil != Format.Undefined
                && SupportsOptimalSampling(vk, device, depthStencil),
            Rgba16FloatColorAttachment =
                rgba16Features.HasFlag(FormatFeatureFlags.ColorAttachmentBit),
            Rgba16FloatSampled =
                rgba16Features.HasFlag(FormatFeatureFlags.SampledImageBit),
            Rgba16FloatLinearFilter =
                rgba16Features.HasFlag(FormatFeatureFlags.SampledImageFilterLinearBit),
            MaxRgba16FloatSampleCount = ReadOptimalColorSampleCount(
                vk,
                device,
                Format.R16G16B16A16Sfloat),
            Bc1Sampled = SupportsOptimalSampling(vk, device, Format.BC1RgbaUnormBlock),
            Bc2Sampled = SupportsOptimalSampling(vk, device, Format.BC2UnormBlock),
            Bc3Sampled = SupportsOptimalSampling(vk, device, Format.BC3UnormBlock),
        };
    }

    internal static Format ChooseDepthStencilFormat(
        Silk.NET.Vulkan.Vk vk,
        PhysicalDevice device)
    {
        foreach (Format candidate in new[] { Format.D32SfloatS8Uint, Format.D24UnormS8Uint })
        {
            FormatProperties properties;
            vk.GetPhysicalDeviceFormatProperties(device, candidate, &properties);
            if (properties.OptimalTilingFeatures.HasFlag(
                    FormatFeatureFlags.DepthStencilAttachmentBit))
            {
                return candidate;
            }
        }

        return Format.Undefined;
    }

    internal static bool SupportsOptimalSampling(
        Silk.NET.Vulkan.Vk vk,
        PhysicalDevice device,
        Format format)
    {
        FormatProperties properties;
        vk.GetPhysicalDeviceFormatProperties(device, format, &properties);
        return properties.OptimalTilingFeatures.HasFlag(FormatFeatureFlags.SampledImageBit);
    }

    internal static uint ReadOptimalColorSampleCount(
        Silk.NET.Vulkan.Vk vk,
        PhysicalDevice device,
        Format format)
    {
        ImageFormatProperties properties;
        Result result = vk.GetPhysicalDeviceImageFormatProperties(
            device,
            format,
            ImageType.Type2D,
            ImageTiling.Optimal,
            ImageUsageFlags.ColorAttachmentBit,
            ImageCreateFlags.None,
            &properties);
        return result == Result.Success
            ? HighestSampleCount(properties.SampleCounts)
            : 0u;
    }

    /// <summary>
    /// Enumerate queue families, reporting present support only when a surface
    /// is supplied. The headless probe passes <c>null</c> and takes the
    /// graphics-only path.
    /// </summary>
    internal static IReadOnlyList<VulkanQueueFamilyCandidate> ReadQueueFamilies(
        Silk.NET.Vulkan.Vk vk,
        PhysicalDevice device,
        Silk.NET.Vulkan.Extensions.KHR.KhrSurface? surfaceApi,
        SurfaceKHR surface)
    {
        ArgumentNullException.ThrowIfNull(vk);

        uint count = 0;
        vk.GetPhysicalDeviceQueueFamilyProperties(device, ref count, null);
        if (count == 0)
            return [];

        var properties = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* first = properties)
            vk.GetPhysicalDeviceQueueFamilyProperties(device, ref count, first);

        var families = new List<VulkanQueueFamilyCandidate>((int)count);
        for (uint i = 0; i < count; i++)
        {
            bool graphics = properties[i].QueueFlags.HasFlag(QueueFlags.GraphicsBit);
            bool present = false;
            if (surfaceApi is not null)
            {
                VulkanInterop.Check(
                    surfaceApi.GetPhysicalDeviceSurfaceSupport(
                        device,
                        i,
                        surface,
                        out Bool32 supported),
                    "vkGetPhysicalDeviceSurfaceSupportKHR");
                present = supported;
            }

            families.Add(new VulkanQueueFamilyCandidate(i, graphics, present));
        }

        return families;
    }

    /// <summary>A short, stable driver identification string for bug reports.</summary>
    internal static string DescribeDriver(VulkanPhysicalDeviceCandidate device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return
            $"vendor 0x{device.VendorId:X4}, device 0x{device.DeviceId:X4}, " +
            $"driver {VulkanApiVersion.Major(device.DriverVersion)}." +
            $"{VulkanApiVersion.Minor(device.DriverVersion)}." +
            $"{VulkanApiVersion.Patch(device.DriverVersion)} " +
            $"(raw 0x{device.DriverVersion:X8})";
    }
}

internal sealed unsafe class VulkanLogicalDeviceFactory
{
    internal sealed record Created(
        Device Device,
        Queue GraphicsQueue,
        Queue PresentQueue,
        VulkanQueueFamilyChoice Families,
        IReadOnlyList<string> EnabledExtensions,
        IReadOnlyList<string> UnavailableOptionalExtensions);

    internal static Created Create(
        Silk.NET.Vulkan.Vk vk,
        PhysicalDevice physicalDevice,
        VulkanQueueFamilyChoice families,
        bool requireSwapchain,
        VulkanDeviceFeatureSupport availableFeatures)
    {
        ArgumentNullException.ThrowIfNull(vk);
        ArgumentNullException.ThrowIfNull(families);
        ArgumentNullException.ThrowIfNull(availableFeatures);

        IReadOnlyList<string> available =
            VulkanInterop.EnumerateDeviceExtensions(vk, physicalDevice);
        VulkanExtensionPlan plan = VulkanExtensionSelection.Resolve(
            available,
            requireSwapchain ? [VulkanExtensionSelection.SwapchainExtension] : [],
            VulkanExtensionSelection.OptionalDeviceExtensions);
        if (!plan.IsSatisfied)
        {
            throw new NotSupportedException(
                "The selected Vulkan device does not advertise the required " +
                $"extension(s): {string.Join(", ", plan.MissingRequired)}.");
        }

        var priority = 1f;
        uint[] uniqueFamilies = families.IsUnified
            ? [families.GraphicsFamily]
            : [families.GraphicsFamily, families.PresentFamily];
        var queueCreates = new DeviceQueueCreateInfo[uniqueFamilies.Length];
        for (int i = 0; i < uniqueFamilies.Length; i++)
        {
            queueCreates[i] = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = uniqueFamilies[i],
                QueueCount = 1,
                PQueuePriorities = &priority,
            };
        }

        var vulkan13 = new PhysicalDeviceVulkan13Features
        {
            SType = StructureType.PhysicalDeviceVulkan13Features,
            DynamicRendering = true,
            Synchronization2 = true,
            Maintenance4 = true,
            ShaderDemoteToHelperInvocation = true,
        };
        var vulkan12 = new PhysicalDeviceVulkan12Features
        {
            SType = StructureType.PhysicalDeviceVulkan12Features,
            PNext = &vulkan13,
            DescriptorIndexing = true,
            RuntimeDescriptorArray = true,
            DescriptorBindingPartiallyBound = true,
            DescriptorBindingSampledImageUpdateAfterBind = true,
            DescriptorBindingUpdateUnusedWhilePending = true,
            DescriptorBindingVariableDescriptorCount = true,
            ShaderSampledImageArrayNonUniformIndexing = true,
            TimelineSemaphore = true,
            HostQueryReset = true,
        };
        var vulkan11 = new PhysicalDeviceVulkan11Features
        {
            SType = StructureType.PhysicalDeviceVulkan11Features,
            PNext = &vulkan12,
            ShaderDrawParameters = true,
            Multiview = availableFeatures.Multiview,
        };
        var core = new PhysicalDeviceFeatures
        {
            MultiDrawIndirect = true,
            DrawIndirectFirstInstance = true,
            ShaderClipDistance = true,
            TextureCompressionBC = true,
            SamplerAnisotropy = true,
        };
        var features2 = new PhysicalDeviceFeatures2
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &vulkan11,
            Features = core,
        };

        nint extensionNames = VulkanInterop.AllocateStringArray(plan.Enabled);
        try
        {
            fixed (DeviceQueueCreateInfo* queues = queueCreates)
            {
                var create = new DeviceCreateInfo
                {
                    SType = StructureType.DeviceCreateInfo,
                    PNext = &features2,
                    QueueCreateInfoCount = (uint)queueCreates.Length,
                    PQueueCreateInfos = queues,
                    EnabledExtensionCount = (uint)plan.Enabled.Count,
                    PpEnabledExtensionNames = (byte**)extensionNames,
                    PEnabledFeatures = null,
                };

                VulkanInterop.Check(
                    vk.CreateDevice(physicalDevice, &create, null, out Device device),
                    "vkCreateDevice");

                vk.GetDeviceQueue(device, families.GraphicsFamily, 0, out Queue graphics);
                Queue present = graphics;
                if (!families.IsUnified)
                    vk.GetDeviceQueue(device, families.PresentFamily, 0, out present);

                return new Created(
                    device,
                    graphics,
                    present,
                    families,
                    plan.Enabled,
                    plan.UnavailableOptional);
            }
        }
        finally
        {
            VulkanInterop.FreeStringArray(extensionNames);
        }
    }
}
