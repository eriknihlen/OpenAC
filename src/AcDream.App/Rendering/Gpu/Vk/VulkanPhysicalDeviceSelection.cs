using System.Globalization;
using Silk.NET.Vulkan;

namespace AcDream.App.Rendering.Gpu.Vk;

internal sealed record VulkanPhysicalDeviceChoice(
    VulkanPhysicalDeviceCandidate Device,
    string Reason);

internal static class VulkanPhysicalDeviceSelection
{
    internal static int PreferenceRank(PhysicalDeviceType type) => type switch
    {
        PhysicalDeviceType.DiscreteGpu => 0,
        PhysicalDeviceType.IntegratedGpu => 1,
        PhysicalDeviceType.VirtualGpu => 2,
        PhysicalDeviceType.Cpu => 3,
        _ => 4,
    };

    internal static VulkanPhysicalDeviceChoice? Choose(
        IReadOnlyList<VulkanPhysicalDeviceCandidate> candidates,
        string? deviceOverride)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(deviceOverride))
        {
            string trimmed = deviceOverride.Trim();
            if (IsDecimalIndex(trimmed))
            {
                int index = int.Parse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture);
                VulkanPhysicalDeviceCandidate? byIndex =
                    candidates.FirstOrDefault(candidate => candidate.Index == index);
                if (byIndex is not null)
                {
                    return new VulkanPhysicalDeviceChoice(
                        byIndex,
                        $"ACDREAM_VULKAN_DEVICE={trimmed} selected device index {index}.");
                }
            }
            else
            {
                VulkanPhysicalDeviceCandidate? byName = candidates.FirstOrDefault(
                    candidate => candidate.DeviceName.Contains(
                        trimmed,
                        StringComparison.OrdinalIgnoreCase));
                if (byName is not null)
                {
                    return new VulkanPhysicalDeviceChoice(
                        byName,
                        $"ACDREAM_VULKAN_DEVICE={trimmed} matched device name '{byName.DeviceName}'.");
                }
            }

            VulkanPhysicalDeviceCandidate automatic = Rank(candidates);
            return new VulkanPhysicalDeviceChoice(
                automatic,
                $"ACDREAM_VULKAN_DEVICE={trimmed} matched no enumerated device; " +
                $"fell back to the automatic choice '{automatic.DeviceName}' " +
                $"({automatic.DeviceType}, {Gib(automatic.DeviceLocalHeapBytes)} device-local).");
        }

        VulkanPhysicalDeviceCandidate chosen = Rank(candidates);
        return new VulkanPhysicalDeviceChoice(
            chosen,
            $"automatic: '{chosen.DeviceName}' ({chosen.DeviceType}, " +
            $"{Gib(chosen.DeviceLocalHeapBytes)} device-local) ranked first of " +
            $"{candidates.Count} enumerated device(s).");
    }

    internal static VulkanPhysicalDeviceCandidate Rank(
        IReadOnlyList<VulkanPhysicalDeviceCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
            throw new ArgumentException("At least one candidate is required.", nameof(candidates));

        VulkanPhysicalDeviceCandidate best = candidates[0];
        for (int i = 1; i < candidates.Count; i++)
        {
            if (Compare(candidates[i], best) < 0)
                best = candidates[i];
        }

        return best;
    }

    /// <summary>Negative when <paramref name="left"/> is the better device.</summary>
    internal static int Compare(
        VulkanPhysicalDeviceCandidate left,
        VulkanPhysicalDeviceCandidate right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        int byType = PreferenceRank(left.DeviceType).CompareTo(PreferenceRank(right.DeviceType));
        if (byType != 0)
            return byType;

        int byHeap = right.DeviceLocalHeapBytes.CompareTo(left.DeviceLocalHeapBytes);
        return byHeap != 0 ? byHeap : left.Index.CompareTo(right.Index);
    }

    /// <summary>An index override is decimal digits and nothing else.</summary>
    internal static bool IsDecimalIndex(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        foreach (char character in value)
        {
            if (character is < '0' or > '9')
                return false;
        }

        return true;
    }

    private static string Gib(ulong bytes)
        => (bytes / (1024d * 1024d * 1024d)).ToString("0.##", CultureInfo.InvariantCulture) + " GiB";
}

internal sealed record VulkanQueueFamilyChoice(
    uint GraphicsFamily,
    uint PresentFamily)
{
    internal bool IsUnified => GraphicsFamily == PresentFamily;
}

/// <summary>One enumerated queue family, reduced to the two facts the selector needs.</summary>
internal readonly record struct VulkanQueueFamilyCandidate(
    uint Index,
    bool SupportsGraphics,
    bool SupportsPresent);

internal static class VulkanQueueFamilySelection
{
    internal static VulkanQueueFamilyChoice? Choose(
        IReadOnlyList<VulkanQueueFamilyCandidate> families)
    {
        ArgumentNullException.ThrowIfNull(families);

        foreach (VulkanQueueFamilyCandidate family in families)
        {
            if (family.SupportsGraphics && family.SupportsPresent)
                return new VulkanQueueFamilyChoice(family.Index, family.Index);
        }

        uint? graphics = null;
        uint? present = null;
        foreach (VulkanQueueFamilyCandidate family in families)
        {
            if (graphics is null && family.SupportsGraphics)
                graphics = family.Index;
            if (present is null && family.SupportsPresent)
                present = family.Index;
        }

        return graphics is { } g && present is { } p
            ? new VulkanQueueFamilyChoice(g, p)
            : null;
    }

    internal static uint? ChooseGraphicsOnly(
        IReadOnlyList<VulkanQueueFamilyCandidate> families)
    {
        ArgumentNullException.ThrowIfNull(families);
        foreach (VulkanQueueFamilyCandidate family in families)
        {
            if (family.SupportsGraphics)
                return family.Index;
        }

        return null;
    }
}
