using System.Reflection;
using System.Runtime.CompilerServices;
using AcDream.Core.Physics;

namespace AcDream.Runtime.Tests.Physics;

public sealed class RuntimePhysicsOwnershipBoundaryTests
{
    [Fact]
    public void PhysicsWorldRootMutatorsAreRuntimeOnlyProductionInternals()
    {
        string[] rootMutators =
        [
            nameof(PhysicsEngine.AddLandblock),
            nameof(PhysicsEngine.RemoveLandblock),
            nameof(PhysicsEngine.DemoteLandblockToTerrain),
            nameof(PhysicsEngine.Clear),
        ];
        Type engine = typeof(PhysicsEngine);
        MethodInfo[] publicMethods = engine.GetMethods(
            BindingFlags.Instance | BindingFlags.Public);
        MethodInfo[] internalMethods = engine.GetMethods(
            BindingFlags.Instance | BindingFlags.NonPublic);
        foreach (string name in rootMutators)
        {
            Assert.DoesNotContain(
                publicMethods,
                method => method.Name == name);
            Assert.Contains(
                internalMethods,
                method => method.Name == name);
        }

        string[] friends = engine.Assembly
            .GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(attribute => attribute.AssemblyName.Split(',')[0])
            .ToArray();
        Assert.Contains("AcDream.Runtime", friends);
        Assert.DoesNotContain("AcDream.App", friends);
        Assert.DoesNotContain("acdream-headless", friends);
        Assert.Contains("AcDream.App.Tests", friends);
        Assert.Contains("AcDream.Headless.Tests", friends);
    }
}
