namespace AcDream.Plugin.Abstractions;

public sealed record PluginPanelDescriptor(string WindowId, string Title)
{
    public string? IconText { get; init; }

    public uint IconSurfaceId { get; init; }

    public bool StartVisible { get; init; } = true;

    /// <summary>Whether this window receives a button in the shared sidepanel.</summary>
    public bool ShowInSidePanel { get; init; } = true;
}

public readonly record struct PluginUiOwner(string Id, string DisplayName);

public interface IUiRegistry
{
    /// <param name="markupPath">Absolute path to the plugin's panel markup file.</param>
    /// <param name="binding">Object whose properties the markup's {Bindings} resolve against.</param>
    void AddMarkupPanel(string markupPath, object binding);

    void AddPanel(
        PluginPanelDescriptor descriptor,
        string markupPath,
        object binding)
        => AddMarkupPanel(markupPath, binding);

    /// <summary>
    /// Registers a window whose lifetime may be ended independently while the
    /// plugin keeps running. Disposing the token removes the retained window
    /// and its sidepanel entry.
    /// </summary>
    IDisposable RegisterPanel(
        PluginPanelDescriptor descriptor,
        string markupPath,
        object binding)
    {
        AddPanel(descriptor, markupPath, binding);
        return NoOpUiRegistration.Instance;
    }

    IDisposable RegisterPanelContent(
        PluginPanelDescriptor descriptor,
        string markupContent,
        object binding) => NoOpUiRegistration.Instance;

    /// <summary>Queries this plugin's own registered view by title or stable id.</summary>
    bool ViewExists(string viewName) => false;
    bool IsViewVisible(string viewName) => false;
    bool ControlExists(string viewName, string controlName) => false;
    bool SetControlLabel(string viewName, string controlName, string label) => false;
    bool SetControlVisible(string viewName, string controlName, bool visible) => false;
}

public interface IScopedUiRegistry : IUiRegistry
{
    IDisposable RegisterMarkupPanel(string markupPath, object binding);

    IDisposable RegisterPanel(
        PluginUiOwner owner,
        PluginPanelDescriptor descriptor,
        string markupPath,
        object binding)
        => RegisterMarkupPanel(markupPath, binding);

    /// <summary>Host-owned registration for in-memory plugin markup.</summary>
    IDisposable RegisterPanelContent(
        PluginUiOwner owner,
        PluginPanelDescriptor descriptor,
        string markupContent,
        object binding) => NoOpUiRegistration.Instance;

    bool ViewExists(PluginUiOwner owner, string viewName) => false;
    bool IsViewVisible(PluginUiOwner owner, string viewName) => false;
    bool ControlExists(
        PluginUiOwner owner,
        string viewName,
        string controlName) => false;
    bool SetControlLabel(
        PluginUiOwner owner,
        string viewName,
        string controlName,
        string label) => false;
    bool SetControlVisible(
        PluginUiOwner owner,
        string viewName,
        string controlName,
        bool visible) => false;
}

/// <summary>Shared empty registration returned by UI-less/legacy hosts.</summary>
public sealed class NoOpUiRegistration : IDisposable
{
    public static NoOpUiRegistration Instance { get; } = new();

    private NoOpUiRegistration()
    {
    }

    public void Dispose()
    {
    }
}

public sealed class NoOpUiRegistry : IScopedUiRegistry
{
    public static NoOpUiRegistry Instance { get; } = new();

    private NoOpUiRegistry()
    {
    }

    public void AddMarkupPanel(string markupPath, object binding)
    {
    }

    public void AddPanel(
        PluginPanelDescriptor descriptor,
        string markupPath,
        object binding)
    {
    }

    public IDisposable RegisterPanel(
        PluginPanelDescriptor descriptor,
        string markupPath,
        object binding) => NoOpUiRegistration.Instance;

    public IDisposable RegisterPanelContent(
        PluginPanelDescriptor descriptor,
        string markupContent,
        object binding) => NoOpUiRegistration.Instance;

    public IDisposable RegisterMarkupPanel(string markupPath, object binding) =>
        NoOpRegistration.Instance;

    public IDisposable RegisterPanel(
        PluginUiOwner owner,
        PluginPanelDescriptor descriptor,
        string markupPath,
        object binding) => NoOpRegistration.Instance;

    public IDisposable RegisterPanelContent(
        PluginUiOwner owner,
        PluginPanelDescriptor descriptor,
        string markupContent,
        object binding) => NoOpUiRegistration.Instance;

    private sealed class NoOpRegistration : IDisposable
    {
        internal static NoOpRegistration Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
