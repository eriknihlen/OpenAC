namespace AcDream.App.Rendering.Gpu.Vk;

/// <summary>The extensions actually asked for, plus the optional ones that were not available.</summary>
internal sealed record VulkanExtensionPlan(
    IReadOnlyList<string> Enabled,
    IReadOnlyList<string> MissingRequired,
    IReadOnlyList<string> UnavailableOptional)
{
    internal bool IsSatisfied => MissingRequired.Count == 0;
}

internal static class VulkanExtensionSelection
{
    /// <summary>Object naming for RenderDoc and validation output. Never required.</summary>
    internal const string DebugUtilsExtension = "VK_EXT_debug_utils";

    /// <summary>Real allocator headroom for <c>GpuMemoryTracker</c>. Never required.</summary>
    internal const string MemoryBudgetExtension = "VK_EXT_memory_budget";

    internal const string PresentWaitExtension = "VK_KHR_present_wait";

    /// <summary>Presenting to a surface. Required on the real device; absent in the headless probe.</summary>
    internal const string SwapchainExtension = "VK_KHR_swapchain";

    internal static VulkanExtensionPlan Resolve(
        IReadOnlyList<string> available,
        IReadOnlyList<string> required,
        IReadOnlyList<string> optional)
    {
        ArgumentNullException.ThrowIfNull(available);
        ArgumentNullException.ThrowIfNull(required);
        ArgumentNullException.ThrowIfNull(optional);

        var advertised = new HashSet<string>(available, StringComparer.Ordinal);
        var enabled = new List<string>();
        var missing = new List<string>();
        var unavailable = new List<string>();

        foreach (string name in required)
        {
            if (advertised.Contains(name))
            {
                if (!enabled.Contains(name, StringComparer.Ordinal))
                    enabled.Add(name);
            }
            else if (!missing.Contains(name, StringComparer.Ordinal))
            {
                missing.Add(name);
            }
        }

        foreach (string name in optional)
        {
            if (advertised.Contains(name))
            {
                if (!enabled.Contains(name, StringComparer.Ordinal))
                    enabled.Add(name);
            }
            else if (!unavailable.Contains(name, StringComparer.Ordinal))
            {
                unavailable.Add(name);
            }
        }

        return new VulkanExtensionPlan(enabled, missing, unavailable);
    }

    internal static IReadOnlyList<string> OptionalInstanceExtensions { get; } =
        [DebugUtilsExtension];

    internal static IReadOnlyList<string> OptionalDeviceExtensions { get; } =
        [MemoryBudgetExtension, PresentWaitExtension];
}
