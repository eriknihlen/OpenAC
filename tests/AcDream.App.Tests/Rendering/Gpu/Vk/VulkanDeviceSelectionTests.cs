using System.Collections.Generic;
using System.Linq;
using AcDream.App.Rendering.Gpu.Vk;
using Silk.NET.Vulkan;

namespace AcDream.App.Tests.Rendering.Gpu.Vk;

public sealed class VulkanDeviceSelectionTests
{
    private const ulong Gib = 1024ul * 1024ul * 1024ul;

    private static VulkanPhysicalDeviceCandidate Device(
        int index,
        string name,
        PhysicalDeviceType type,
        ulong heapBytes) => new(
            index,
            name,
            type,
            ApiVersion: VulkanApiVersion.Make(1, 3, 280),
            DriverVersion: 1,
            VendorId: 0x1002,
            DeviceId: 0x7550,
            DeviceLocalHeapBytes: heapBytes);

    [Fact]
    public void DiscreteBeatsIntegratedBeatsVirtualBeatsCpu()
    {
        // Vulkan's own enum ordering is Other < Integrated < Discrete < Virtual
        // < Cpu, which is neither our order nor monotone. Pin the mapping.
        Assert.True(
            VulkanPhysicalDeviceSelection.PreferenceRank(PhysicalDeviceType.DiscreteGpu)
            < VulkanPhysicalDeviceSelection.PreferenceRank(PhysicalDeviceType.IntegratedGpu));
        Assert.True(
            VulkanPhysicalDeviceSelection.PreferenceRank(PhysicalDeviceType.IntegratedGpu)
            < VulkanPhysicalDeviceSelection.PreferenceRank(PhysicalDeviceType.VirtualGpu));
        Assert.True(
            VulkanPhysicalDeviceSelection.PreferenceRank(PhysicalDeviceType.VirtualGpu)
            < VulkanPhysicalDeviceSelection.PreferenceRank(PhysicalDeviceType.Cpu));
        Assert.True(
            VulkanPhysicalDeviceSelection.PreferenceRank(PhysicalDeviceType.Cpu)
            < VulkanPhysicalDeviceSelection.PreferenceRank(PhysicalDeviceType.Other));
    }

    [Fact]
    public void TheDiscreteGpuWinsOverALargerIntegratedHeap()
    {
        // An integrated GPU can report the whole of system RAM as device-local.
        // Type preference must dominate the heap tie-break, not the other way up.
        List<VulkanPhysicalDeviceCandidate> candidates =
        [
            Device(0, "Intel Iris Xe", PhysicalDeviceType.IntegratedGpu, 32 * Gib),
            Device(1, "AMD Radeon RX 9070 XT", PhysicalDeviceType.DiscreteGpu, 16 * Gib),
        ];

        VulkanPhysicalDeviceChoice? choice =
            VulkanPhysicalDeviceSelection.Choose(candidates, deviceOverride: null);

        Assert.NotNull(choice);
        Assert.Equal("AMD Radeon RX 9070 XT", choice.Device.DeviceName);
        Assert.Contains("automatic", choice.Reason, System.StringComparison.Ordinal);
    }

    [Fact]
    public void TwoDiscreteGpusAreBrokenByTheLargerDeviceLocalHeap()
    {
        List<VulkanPhysicalDeviceCandidate> candidates =
        [
            Device(0, "Small Discrete", PhysicalDeviceType.DiscreteGpu, 8 * Gib),
            Device(1, "Big Discrete", PhysicalDeviceType.DiscreteGpu, 24 * Gib),
        ];

        VulkanPhysicalDeviceChoice? choice =
            VulkanPhysicalDeviceSelection.Choose(candidates, deviceOverride: null);

        Assert.Equal("Big Discrete", choice!.Device.DeviceName);
    }

    [Fact]
    public void IdenticalDevicesAreBrokenDeterministicallyByEnumerationIndex()
    {
        List<VulkanPhysicalDeviceCandidate> candidates =
        [
            Device(0, "Twin", PhysicalDeviceType.DiscreteGpu, 16 * Gib),
            Device(1, "Twin", PhysicalDeviceType.DiscreteGpu, 16 * Gib),
        ];

        Assert.Equal(
            0,
            VulkanPhysicalDeviceSelection.Choose(candidates, null)!.Device.Index);
        Assert.Equal(
            0,
            VulkanPhysicalDeviceSelection.Choose([.. candidates.AsEnumerable().Reverse()], null)!
                .Device.Index);
    }

    [Fact]
    public void CpuAndSoftwareDevicesAreLastButStillSelectable()
    {
        List<VulkanPhysicalDeviceCandidate> candidates =
        [
            Device(0, "llvmpipe", PhysicalDeviceType.Cpu, 0),
        ];

        VulkanPhysicalDeviceChoice? choice =
            VulkanPhysicalDeviceSelection.Choose(candidates, deviceOverride: null);

        Assert.Equal("llvmpipe", choice!.Device.DeviceName);
    }

    [Fact]
    public void NoEnumeratedDeviceYieldsNoChoice()
    {
        Assert.Null(VulkanPhysicalDeviceSelection.Choose([], deviceOverride: null));
    }

    [Fact]
    public void TheOverrideMatchesADecimalEnumerationIndex()
    {
        List<VulkanPhysicalDeviceCandidate> candidates =
        [
            Device(0, "AMD Radeon RX 9070 XT", PhysicalDeviceType.DiscreteGpu, 16 * Gib),
            Device(1, "Intel Iris Xe", PhysicalDeviceType.IntegratedGpu, 32 * Gib),
        ];

        VulkanPhysicalDeviceChoice? choice =
            VulkanPhysicalDeviceSelection.Choose(candidates, "1");

        Assert.Equal("Intel Iris Xe", choice!.Device.DeviceName);
        Assert.Contains("ACDREAM_VULKAN_DEVICE=1", choice.Reason, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("iris")]
    [InlineData("IRIS")]
    [InlineData("Intel Iris Xe")]
    public void TheOverrideMatchesACaseInsensitiveNameSubstring(string value)
    {
        List<VulkanPhysicalDeviceCandidate> candidates =
        [
            Device(0, "AMD Radeon RX 9070 XT", PhysicalDeviceType.DiscreteGpu, 16 * Gib),
            Device(1, "Intel Iris Xe", PhysicalDeviceType.IntegratedGpu, 32 * Gib),
        ];

        VulkanPhysicalDeviceChoice? choice =
            VulkanPhysicalDeviceSelection.Choose(candidates, value);

        Assert.Equal("Intel Iris Xe", choice!.Device.DeviceName);
        Assert.Contains("matched device name", choice.Reason, System.StringComparison.Ordinal);
    }

    [Fact]
    public void AnOverrideThatMatchesNothingFallsBackAndSaysSo()
    {
        List<VulkanPhysicalDeviceCandidate> candidates =
        [
            Device(0, "AMD Radeon RX 9070 XT", PhysicalDeviceType.DiscreteGpu, 16 * Gib),
        ];

        VulkanPhysicalDeviceChoice? choice =
            VulkanPhysicalDeviceSelection.Choose(candidates, "GeForce");

        Assert.Equal("AMD Radeon RX 9070 XT", choice!.Device.DeviceName);
        Assert.Contains("matched no enumerated device", choice.Reason, System.StringComparison.Ordinal);
    }

    [Fact]
    public void AnOutOfRangeIndexOverrideFallsBackRatherThanMatchingADigitInADeviceName()
    {
        List<VulkanPhysicalDeviceCandidate> candidates =
        [
            Device(0, "AMD Radeon RX 9070 XT", PhysicalDeviceType.DiscreteGpu, 16 * Gib),
        ];

        VulkanPhysicalDeviceChoice? choice =
            VulkanPhysicalDeviceSelection.Choose(candidates, "7");

        Assert.Equal(0, choice!.Device.Index);
        Assert.Contains("matched no enumerated device", choice.Reason, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ANameContainingDigitsStillMatchesAsASubstring()
    {
        List<VulkanPhysicalDeviceCandidate> candidates =
        [
            Device(0, "AMD Radeon RX 9070 XT", PhysicalDeviceType.DiscreteGpu, 24 * Gib),
            Device(1, "AMD Radeon RX 7900 XTX", PhysicalDeviceType.DiscreteGpu, 16 * Gib),
        ];

        VulkanPhysicalDeviceChoice? choice =
            VulkanPhysicalDeviceSelection.Choose(candidates, "RX 7900");

        Assert.Equal("AMD Radeon RX 7900 XTX", choice!.Device.DeviceName);
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("12", true)]
    [InlineData("", false)]
    [InlineData("-1", false)]
    [InlineData("7900 XTX", false)]
    [InlineData("Radeon", false)]
    public void OnlyAnAllDigitsOverrideIsTreatedAsAnIndex(string value, bool expected)
    {
        Assert.Equal(expected, VulkanPhysicalDeviceSelection.IsDecimalIndex(value));
    }

    [Fact]
    public void OneFamilyThatDoesBothIsPreferredOverASplitPair()
    {
        List<VulkanQueueFamilyCandidate> families =
        [
            new(0, SupportsGraphics: true, SupportsPresent: false),
            new(1, SupportsGraphics: false, SupportsPresent: true),
            new(2, SupportsGraphics: true, SupportsPresent: true),
        ];

        VulkanQueueFamilyChoice? choice = VulkanQueueFamilySelection.Choose(families);

        Assert.NotNull(choice);
        Assert.True(choice.IsUnified);
        Assert.Equal(2u, choice.GraphicsFamily);
        Assert.Equal(2u, choice.PresentFamily);
    }

    [Fact]
    public void ASplitDeviceStillProducesAUsablePair()
    {
        List<VulkanQueueFamilyCandidate> families =
        [
            new(0, SupportsGraphics: true, SupportsPresent: false),
            new(1, SupportsGraphics: false, SupportsPresent: true),
        ];

        VulkanQueueFamilyChoice? choice = VulkanQueueFamilySelection.Choose(families);

        Assert.NotNull(choice);
        Assert.False(choice.IsUnified);
        Assert.Equal(0u, choice.GraphicsFamily);
        Assert.Equal(1u, choice.PresentFamily);
    }

    [Fact]
    public void ADeviceWithNoPresentCapableFamilyYieldsNoChoice()
    {
        List<VulkanQueueFamilyCandidate> families =
        [
            new(0, SupportsGraphics: true, SupportsPresent: false),
        ];

        Assert.Null(VulkanQueueFamilySelection.Choose(families));
        // The headless probe has no surface and so no present requirement.
        Assert.Equal(0u, VulkanQueueFamilySelection.ChooseGraphicsOnly(families));
    }

    [Fact]
    public void AComputeOnlyDeviceHasNoGraphicsFamily()
    {
        List<VulkanQueueFamilyCandidate> families =
        [
            new(0, SupportsGraphics: false, SupportsPresent: false),
        ];

        Assert.Null(VulkanQueueFamilySelection.ChooseGraphicsOnly(families));
    }

    [Fact]
    public void RequiredExtensionsMustBeAdvertisedButOptionalOnesMayBeAbsent()
    {
        VulkanExtensionPlan plan = VulkanExtensionSelection.Resolve(
            available: ["VK_KHR_surface", "VK_KHR_win32_surface", "VK_EXT_debug_utils"],
            required: ["VK_KHR_surface", "VK_KHR_win32_surface"],
            optional:
            [
                VulkanExtensionSelection.DebugUtilsExtension,
                VulkanExtensionSelection.MemoryBudgetExtension,
            ]);

        Assert.True(plan.IsSatisfied);
        Assert.Contains("VK_KHR_surface", plan.Enabled);
        Assert.Contains(VulkanExtensionSelection.DebugUtilsExtension, plan.Enabled);
        Assert.Contains(VulkanExtensionSelection.MemoryBudgetExtension, plan.UnavailableOptional);
        Assert.Empty(plan.MissingRequired);
    }

    [Fact]
    public void AMissingRequiredExtensionIsReportedByName()
    {
        VulkanExtensionPlan plan = VulkanExtensionSelection.Resolve(
            available: ["VK_KHR_surface"],
            required: ["VK_KHR_surface", VulkanExtensionSelection.SwapchainExtension],
            optional: []);

        Assert.False(plan.IsSatisfied);
        Assert.Equal(
            [VulkanExtensionSelection.SwapchainExtension],
            plan.MissingRequired);
    }

    [Fact]
    public void AnExtensionNamedBothRequiredAndOptionalIsEnabledOnce()
    {
        VulkanExtensionPlan plan = VulkanExtensionSelection.Resolve(
            available: [VulkanExtensionSelection.DebugUtilsExtension],
            required: [VulkanExtensionSelection.DebugUtilsExtension],
            optional: [VulkanExtensionSelection.DebugUtilsExtension]);

        Assert.Single(plan.Enabled);
    }
}
