using System.Reflection;
using System.Reflection.Emit;
using AcDream.App.Rendering.Gpu.Vk;
using AcDream.App.Tests.Architecture;

namespace AcDream.App.Tests.Rendering.Gpu.Vk;

public sealed class VulkanGraphicsContextAcquisitionTests
{
    [Fact]
    public void ProductionAcquireProbesSelectedPhysicalDeviceBeforeLogicalDeviceCreation()
    {
        MethodInfo acquire = RequiredMethod(nameof(VulkanGraphicsContext.Acquire));
        IReadOnlyList<CompiledCall> acquireCalls = CompiledCallGraph.Read(acquire);
        int createInstance = IndexOf(acquireCalls, "CreateInstanceAndSurface");
        int selectAndGate = IndexOf(acquireCalls, "SelectDeviceAndGate");
        int createRhiDevice = IndexOf(acquireCalls, "CreateDevice");

        Assert.True(createInstance >= 0);
        Assert.True(selectAndGate > createInstance);
        Assert.True(createRhiDevice > selectAndGate);

        MethodInfo select = RequiredMethod("SelectDeviceAndGate");
        CompiledCall featureProbe = Assert.Single(
            CompiledCallGraph.Read(select),
            call => call.Target.DeclaringType == typeof(VulkanPhysicalDeviceInspector)
                && call.Target.Name == nameof(VulkanPhysicalDeviceInspector.ReadFeatures));
        CompiledCall logicalDeviceCreate = Assert.Single(
            CompiledCallGraph.Read(select),
            call => call.Target.DeclaringType == typeof(VulkanLogicalDeviceFactory)
                && call.Target.Name == nameof(VulkanLogicalDeviceFactory.Create));
        FieldInfo features = typeof(VulkanGraphicsContext).GetField(
            "_features",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        CompiledFieldReference featurePublication = Assert.Single(
            CompiledCallGraph.ReadFieldReferences(select),
            reference => reference.Field == features && reference.OpCode == OpCodes.Stfld);

        Assert.True(
            featureProbe.Offset < featurePublication.Offset,
            "The selected physical-device feature probe must precede publication to the context.");
        Assert.True(
            featurePublication.Offset < logicalDeviceCreate.Offset,
            "The production acquisition path must publish probed features before logical-device creation consumes them.");
    }

    private static MethodInfo RequiredMethod(string name) =>
        typeof(VulkanGraphicsContext).GetMethod(
            name,
            BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"Missing VulkanGraphicsContext.{name}.");

    private static int IndexOf(IReadOnlyList<CompiledCall> calls, string methodName) =>
        calls
            .Select((call, index) => (call, index))
            .Where(value => value.call.Target.DeclaringType == typeof(VulkanGraphicsContext)
                && value.call.Target.Name == methodName)
            .Select(value => value.index)
            .DefaultIfEmpty(-1)
            .Single();
}
