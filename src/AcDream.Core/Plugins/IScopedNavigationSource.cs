using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Plugins;

/// <summary>
/// A navigation surface that can hand each plugin its own view, so the host
/// knows which plugin asked for a walk or a pause, can refuse a second plugin
/// while the first one's walk is under way, and can let a plugin's walks and
/// pauses go when that plugin is disabled or unloaded.
/// </summary>
public interface IScopedNavigationSource
{
    /// <summary>The navigation surface as one plugin sees it; every walk and pause asked for through it is that plugin's.</summary>
    INavigationAutomation ScopeTo(string ownerId);

    /// <summary>Ends the walk the owner started, if it is still under way, and drops every pause it registered.</summary>
    void Release(string ownerId);
}
