using AcDream.Core.Meshing;
using Xunit;

namespace AcDream.Core.Tests.Meshing;

public class EntityHydrationRulesTests
{
    [Fact]
    public void ShouldKeepEntity_NothingAuthored_False()
    {
        Assert.False(EntityHydrationRules.ShouldKeepEntity(
            meshRefCount: 0, setupLightCount: 0, hasDefaultScript: false));
    }

    [Fact]
    public void ShouldKeepEntity_NoMeshWithLights_True()
    {
        Assert.True(EntityHydrationRules.ShouldKeepEntity(
            meshRefCount: 0, setupLightCount: 1, hasDefaultScript: false));
    }

    [Fact]
    public void ShouldKeepEntity_HasMesh_TrueRegardlessOfLights()
    {
        Assert.True(EntityHydrationRules.ShouldKeepEntity(
            meshRefCount: 1, setupLightCount: 0, hasDefaultScript: false));
        Assert.True(EntityHydrationRules.ShouldKeepEntity(
            meshRefCount: 3, setupLightCount: 2, hasDefaultScript: false));
    }

    /// <summary>
    /// The authoring convention for a placement whose whole purpose is an
    /// effect — a particle emitter, an ambient sound — is one editor-marker
    /// part, no lights, and a default script. The object is still created
    /// and still runs its script, so it must survive hydration.
    /// </summary>
    [Fact]
    public void ShouldKeepEntity_ScriptOnlyPlacement_True()
    {
        Assert.True(EntityHydrationRules.ShouldKeepEntity(
            meshRefCount: 0, setupLightCount: 0, hasDefaultScript: true));
    }

    [Fact]
    public void ShouldKeepPart_OrdinaryPart_True()
    {
        Assert.True(EntityHydrationRules.ShouldKeepPart(
            isEditorMarker: false, hasCollision: false));
        Assert.True(EntityHydrationRules.ShouldKeepPart(
            isEditorMarker: false, hasCollision: true));
    }

    [Fact]
    public void ShouldKeepPart_MarkerWithoutCollision_False()
    {
        Assert.False(EntityHydrationRules.ShouldKeepPart(
            isEditorMarker: true, hasCollision: false));
    }

    /// <summary>
    /// An invisible ledge is authored as a placement whose only part is an
    /// editor marker with collision. It draws nothing, but a character stands
    /// on it, so the part must survive hydration.
    /// </summary>
    [Fact]
    public void ShouldKeepPart_MarkerWithCollision_True()
    {
        Assert.True(EntityHydrationRules.ShouldKeepPart(
            isEditorMarker: true, hasCollision: true));
    }
}
