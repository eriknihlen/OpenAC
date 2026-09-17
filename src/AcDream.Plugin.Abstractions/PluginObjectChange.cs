namespace AcDream.Plugin.Abstractions;

/// <summary>What changed about a world object reported by <see cref="IEvents.ObjectChanged"/>.</summary>
public enum PluginObjectChangeKind
{
    /// <summary>The object entered the client's object table for the first time.</summary>
    Created,

    /// <summary>A property or other non-positional field on the object changed.</summary>
    Updated,

    /// <summary>The client took delivery of appraisal data for the object.</summary>
    IdentReceived,

    /// <summary>The object's position changed enough to move it into a different cell.</summary>
    Moved,

    /// <summary>The object left the client's object table.</summary>
    Released,
}

/// <summary>One change to a world object, as reported by <see cref="IEvents.ObjectChanged"/>.</summary>
public readonly record struct PluginObjectChange(
    uint ObjectId,
    PluginObjectChangeKind Kind);
