using AcDream.App.Rendering.Gpu.Vk;

namespace AcDream.App.Composition;

internal abstract class GameWindowGraphics : IDisposable
{
    public virtual VulkanGraphicsContext? Vulkan => null;

    /// <summary>
    /// The backend's world-pass seam. Every remaining renderer records into the
    /// pass this publishes rather than opening its own.
    /// </summary>
    public virtual AcDream.App.Rendering.IWorldPassScope? WorldPassScope => null;

    public abstract void Dispose();
}

internal sealed class VulkanGameWindowGraphics : GameWindowGraphics
{
    public VulkanGameWindowGraphics(VulkanGraphicsContext context)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        WorldPassScopeCore = new VulkanWorldPassScope(context.SampleCount);
    }

    public VulkanGraphicsContext Context { get; }

    public override VulkanGraphicsContext? Vulkan => Context;

    public VulkanWorldPassScope WorldPassScopeCore { get; }

    public override AcDream.App.Rendering.IWorldPassScope? WorldPassScope =>
        WorldPassScopeCore;

    public override void Dispose() => Context.Dispose();
}
