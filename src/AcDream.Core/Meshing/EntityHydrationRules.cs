namespace AcDream.Core.Meshing;

public static class EntityHydrationRules
{
    /// <summary>
    /// True when the entity should still be added to the landblock's entity
    /// set even with zero mesh refs, because it carries authored runtime
    /// behaviour of its own: dat-authored lights, or a default script.
    /// An entity with any mesh is always kept; the entity is dropped only
    /// when it has no geometry to draw, no lights to register, and no script
    /// to run.
    /// </summary>
    /// <remarks>
    /// A cell's static objects are created from their id alone, and the
    /// object's setup then installs its default script and script table
    /// before anything looks at the object's geometry. So a placement whose
    /// only part is an editor marker — the authoring convention for a pure
    /// particle-emitter or ambient-sound placement — is a live object that
    /// runs its script; it is not an empty placement. Dropping it silently
    /// removes the effect it exists to produce.
    /// </remarks>
    public static bool ShouldKeepEntity(
        int meshRefCount,
        int setupLightCount,
        bool hasDefaultScript)
        => meshRefCount > 0 || setupLightCount > 0 || hasDefaultScript;

    /// <summary>
    /// True when a part belongs in a placement's mesh set: every part that is
    /// not an editor marker, and an editor marker that has collision. A marker
    /// draws nothing, but its collision still stands in the world, as the
    /// invisible ledges and walls some dungeons are built from do.
    /// </summary>
    public static bool ShouldKeepPart(bool isEditorMarker, bool hasCollision)
        => !isEditorMarker || hasCollision;
}
