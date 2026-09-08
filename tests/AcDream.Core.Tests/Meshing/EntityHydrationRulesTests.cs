using AcDream.Core.Meshing;
using Xunit;

namespace AcDream.Core.Tests.Meshing;

public class EntityHydrationRulesTests
{
    [Fact]
    public void ShouldKeepEntity_NoMeshNoLights_False()
    {
        Assert.False(EntityHydrationRules.ShouldKeepEntity(meshRefCount: 0, setupLightCount: 0));
    }

    [Fact]
    public void ShouldKeepEntity_NoMeshWithLights_True()
    {
        Assert.True(EntityHydrationRules.ShouldKeepEntity(meshRefCount: 0, setupLightCount: 1));
    }

    [Fact]
    public void ShouldKeepEntity_HasMesh_TrueRegardlessOfLights()
    {
        Assert.True(EntityHydrationRules.ShouldKeepEntity(meshRefCount: 1, setupLightCount: 0));
        Assert.True(EntityHydrationRules.ShouldKeepEntity(meshRefCount: 3, setupLightCount: 2));
    }
}
