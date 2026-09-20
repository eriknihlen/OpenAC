namespace AcDream.Plugin.Abstractions;

/// <summary>
/// How a plugin's window should be presented: its identity, its title bar,
/// its shelf button, and whether it starts open.
/// </summary>
/// <param name="WindowId">
/// The plugin's own short name for this window. It is part of the key the
/// host stores the window's position under, so changing it loses the saved
/// position.
/// </param>
/// <param name="Title">The caption shown in the window's title bar.</param>
public sealed record PluginPanelDescriptor(string WindowId, string Title)
{
    /// <summary>
    /// A word or two of initials drawn on the shelf button when no icon id
    /// is given. Null falls back to the host's own default.
    /// </summary>
    public string? IconText { get; init; }

    /// <summary>
    /// The icon drawn on the shelf button, either a full image id or a bare
    /// index. 0 means no icon, in which case <see cref="IconText"/> is used.
    /// </summary>
    public uint IconSurfaceId { get; init; }

    /// <summary>Whether the window is open as soon as it is registered.</summary>
    public bool StartVisible { get; init; } = true;

    /// <summary>Whether this window receives a button in the shared sidepanel.</summary>
    public bool ShowInSidePanel { get; init; } = true;
}

/// <summary>
/// Identifies which plugin a window belongs to, so two plugins can use the
/// same window name without colliding.
/// </summary>
/// <param name="Id">The plugin's manifest id.</param>
/// <param name="DisplayName">The plugin's name as shown to the player.</param>
public readonly record struct PluginUiOwner(string Id, string DisplayName);

/// <summary>
/// Registers a plugin's own windows, described in the host's panel markup,
/// and drives the client's own windows. A host that draws nothing accepts
/// the registrations and answers every query with <c>false</c>.
/// </summary>
public interface IUiRegistry
{
    /// <param name="markupPath">Absolute path to the plugin's panel markup file.</param>
    /// <param name="binding">Object whose properties the markup's {Bindings} resolve against.</param>
    void AddMarkupPanel(string markupPath, object binding);

    /// <summary>
    /// Registers a window for the plugin's lifetime, with a descriptor
    /// giving its id, title and shelf button.
    /// </summary>
    /// <param name="descriptor">How the window is identified and presented.</param>
    /// <param name="markupPath">Absolute path to the plugin's panel markup file.</param>
    /// <param name="binding">Object whose properties the markup's {Bindings} resolve against.</param>
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

    /// <summary>
    /// Same as <see cref="RegisterPanel"/>, but the markup is passed as text
    /// instead of read from a file.
    /// </summary>
    /// <param name="descriptor">How the window is identified and presented.</param>
    /// <param name="markupContent">The panel markup itself.</param>
    /// <param name="binding">Object whose properties the markup's {Bindings} resolve against.</param>
    IDisposable RegisterPanelContent(
        PluginPanelDescriptor descriptor,
        string markupContent,
        object binding) => NoOpUiRegistration.Instance;

    /// <summary>Queries this plugin's own registered view by title or stable id.</summary>
    bool ViewExists(string viewName) => false;

    /// <summary>
    /// Whether one of this plugin's own windows is currently shown. False
    /// when no such window is registered.
    /// </summary>
    bool IsViewVisible(string viewName) => false;

    /// <summary>
    /// Whether one of this plugin's own windows contains a named control.
    /// False when the window or the control is not found.
    /// </summary>
    bool ControlExists(string viewName, string controlName) => false;

    /// <summary>
    /// Replaces the text of a named button, toggle or label in one of this
    /// plugin's own windows, dropping whatever binding the markup gave it.
    /// Returns false when the control is not found or carries no text.
    /// </summary>
    bool SetControlLabel(string viewName, string controlName, string label) => false;

    /// <summary>
    /// Shows or hides a named control in one of this plugin's own windows,
    /// dropping whatever binding the markup gave its visibility. Returns
    /// false when the control is not found.
    /// </summary>
    bool SetControlVisible(string viewName, string controlName, bool visible) => false;

    /// <summary>Requests binding reevaluation for a plugin view.</summary>
    bool InvalidateView(string viewName, string? propertyName = null) => false;

    /// <summary>Moves keyboard focus to a named input control.</summary>
    bool FocusControl(string viewName, string controlName) => false;

    /// <summary>
    /// Shows one of the client's own windows if it is hidden, hides it if
    /// shown. Returns whether the window ended up visible; a no-window host
    /// or a window this build does not mount always returns <c>false</c>.
    /// </summary>
    bool ToggleClientWindow(PluginClientWindow window) => false;

    /// <summary>Shows one of the client's own windows. Returns whether it is now visible.</summary>
    bool ShowClientWindow(PluginClientWindow window) => false;

    /// <summary>Hides one of the client's own windows. Returns whether the host recognized it.</summary>
    bool HideClientWindow(PluginClientWindow window) => false;

    /// <summary>Whether one of the client's own windows is currently visible.</summary>
    bool IsClientWindowVisible(PluginClientWindow window) => false;
}

/// <summary>
/// The host-side registry, which takes the owning plugin on every call. Each
/// plugin is handed a wrapper over this that fills its own owner in, so
/// plugins use <see cref="IUiRegistry"/> and never this interface.
/// </summary>
public interface IScopedUiRegistry : IUiRegistry
{
    /// <summary>
    /// Registers a window from a markup file and returns a token that
    /// removes it again.
    /// </summary>
    IDisposable RegisterMarkupPanel(string markupPath, object binding);

    /// <summary>Registers a window from a markup file on behalf of one plugin.</summary>
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

    /// <summary>
    /// Whether one plugin registered a window with this title or id. False
    /// on a host that draws nothing.
    /// </summary>
    bool ViewExists(PluginUiOwner owner, string viewName) => false;

    /// <summary>Whether one plugin's named window is currently shown.</summary>
    bool IsViewVisible(PluginUiOwner owner, string viewName) => false;

    /// <summary>Whether one plugin's named window contains a named control.</summary>
    bool ControlExists(
        PluginUiOwner owner,
        string viewName,
        string controlName) => false;

    /// <summary>
    /// Replaces the text of a named control in one plugin's window. Returns
    /// false when the control is not found or carries no text.
    /// </summary>
    bool SetControlLabel(
        PluginUiOwner owner,
        string viewName,
        string controlName,
        string label) => false;

    /// <summary>
    /// Shows or hides a named control in one plugin's window. Returns false
    /// when the control is not found.
    /// </summary>
    bool SetControlVisible(
        PluginUiOwner owner,
        string viewName,
        string controlName,
        bool visible) => false;

    /// <summary>Requests binding reevaluation for one plugin view.</summary>
    bool InvalidateView(PluginUiOwner owner, string viewName, string? propertyName = null) => false;

    /// <summary>Moves keyboard focus to one plugin input control.</summary>
    bool FocusControl(PluginUiOwner owner, string viewName, string controlName) => false;
}

/// <summary>Shared empty registration returned by UI-less/legacy hosts.</summary>
public sealed class NoOpUiRegistration : IDisposable
{
    /// <summary>The shared instance; this type holds no state.</summary>
    public static NoOpUiRegistration Instance { get; } = new();

    private NoOpUiRegistration()
    {
    }

    /// <summary>Does nothing; there is no window to remove.</summary>
    public void Dispose()
    {
    }
}

/// <summary>
/// The UI registry a host that draws nothing hands out: registrations are
/// accepted and discarded, and every query answers <c>false</c>.
/// </summary>
public sealed class NoOpUiRegistry : IScopedUiRegistry
{
    /// <summary>The shared instance; this type holds no state.</summary>
    public static NoOpUiRegistry Instance { get; } = new();

    private NoOpUiRegistry()
    {
    }

    /// <inheritdoc/>
    public void AddMarkupPanel(string markupPath, object binding)
    {
    }

    /// <inheritdoc/>
    public void AddPanel(
        PluginPanelDescriptor descriptor,
        string markupPath,
        object binding)
    {
    }

    /// <inheritdoc/>
    public IDisposable RegisterPanel(
        PluginPanelDescriptor descriptor,
        string markupPath,
        object binding) => NoOpUiRegistration.Instance;

    /// <inheritdoc/>
    public IDisposable RegisterPanelContent(
        PluginPanelDescriptor descriptor,
        string markupContent,
        object binding) => NoOpUiRegistration.Instance;

    /// <inheritdoc/>
    public IDisposable RegisterMarkupPanel(string markupPath, object binding) =>
        NoOpRegistration.Instance;

    /// <inheritdoc/>
    public IDisposable RegisterPanel(
        PluginUiOwner owner,
        PluginPanelDescriptor descriptor,
        string markupPath,
        object binding) => NoOpRegistration.Instance;

    /// <inheritdoc/>
    public IDisposable RegisterPanelContent(
        PluginUiOwner owner,
        PluginPanelDescriptor descriptor,
        string markupContent,
        object binding) => NoOpUiRegistration.Instance;

    /// <inheritdoc/>
    public bool ToggleClientWindow(PluginClientWindow window) => false;

    /// <inheritdoc/>
    public bool ShowClientWindow(PluginClientWindow window) => false;

    /// <inheritdoc/>
    public bool HideClientWindow(PluginClientWindow window) => false;

    /// <inheritdoc/>
    public bool IsClientWindowVisible(PluginClientWindow window) => false;

    private sealed class NoOpRegistration : IDisposable
    {
        internal static NoOpRegistration Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
