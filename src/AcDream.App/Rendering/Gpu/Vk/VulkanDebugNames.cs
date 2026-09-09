using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace AcDream.App.Rendering.Gpu.Vk;

internal sealed unsafe class VulkanDebugNames : IDisposable
{
    private readonly Device _device;
    private readonly ExtDebugUtils? _api;
    private bool _disposed;

    private VulkanDebugNames(Device device, ExtDebugUtils? api)
    {
        _device = device;
        _api = api;
    }

    /// <summary>A naming sink that does nothing — used when the extension is absent and by tests.</summary>
    internal static VulkanDebugNames Disabled { get; } = new(default, null);

    internal bool IsEnabled => _api is not null;

    internal static VulkanDebugNames Create(
        Silk.NET.Vulkan.Vk vk,
        Instance instance,
        Device device,
        IReadOnlyCollection<string> enabledInstanceExtensions)
    {
        ArgumentNullException.ThrowIfNull(vk);
        ArgumentNullException.ThrowIfNull(enabledInstanceExtensions);
        if (!enabledInstanceExtensions.Contains(VulkanExtensionSelection.DebugUtilsExtension))
            return Disabled;
        if (!vk.TryGetInstanceExtension(instance, out ExtDebugUtils api))
            return Disabled;
        return new VulkanDebugNames(device, api);
    }

    internal void NameBuffer(Buffer buffer, string name) =>
        Name(ObjectType.Buffer, buffer.Handle, name);

    internal void NameImage(Image image, string name) =>
        Name(ObjectType.Image, image.Handle, name);

    internal void NameImageView(ImageView view, string name) =>
        Name(ObjectType.ImageView, view.Handle, name);

    internal void NameSampler(Sampler sampler, string name) =>
        Name(ObjectType.Sampler, sampler.Handle, name);

    internal void NamePipeline(Pipeline pipeline, string name) =>
        Name(ObjectType.Pipeline, pipeline.Handle, name);

    internal void NameDeviceMemory(DeviceMemory memory, string name) =>
        Name(ObjectType.DeviceMemory, memory.Handle, name);

    internal void BeginLabel(CommandBuffer commands, string label)
    {
        if (_api is null || _disposed)
            return;

        nint text = SilkMarshal.StringToPtr(label);
        try
        {
            var info = new DebugUtilsLabelEXT
            {
                SType = StructureType.DebugUtilsLabelExt,
                PLabelName = (byte*)text,
            };
            _api.CmdBeginDebugUtilsLabel(commands, &info);
        }
        finally
        {
            SilkMarshal.Free(text);
        }
    }

    internal void EndLabel(CommandBuffer commands)
    {
        if (_api is null || _disposed)
            return;
        _api.CmdEndDebugUtilsLabel(commands);
    }

    private void Name(ObjectType type, ulong handle, string name)
    {
        if (_api is null || _disposed || handle == 0 || string.IsNullOrEmpty(name))
            return;

        nint text = SilkMarshal.StringToPtr(name);
        try
        {
            var info = new DebugUtilsObjectNameInfoEXT
            {
                SType = StructureType.DebugUtilsObjectNameInfoExt,
                ObjectType = type,
                ObjectHandle = handle,
                PObjectName = (byte*)text,
            };
            _api.SetDebugUtilsObjectName(_device, &info);
        }
        finally
        {
            SilkMarshal.Free(text);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _api?.Dispose();
    }
}
