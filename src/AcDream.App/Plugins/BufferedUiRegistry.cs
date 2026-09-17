using System.Collections.Generic;
using AcDream.App.UI;
using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions;

namespace AcDream.App.Plugins;

public sealed class BufferedUiRegistry : IScopedUiRegistry, IPluginDirectoryUiRegistry
{
    public readonly record struct Pending(
        PluginUiOwner Owner,
        PluginPanelDescriptor Descriptor,
        string MarkupPath,
        object Binding)
    {
        internal long RegistrationId { get; init; }
        internal string? MarkupContent { get; init; }
        internal string? PluginDirectory { get; init; }

        /// <summary>Stable, manifest-scoped retained-window persistence key.</summary>
        public string WindowName =>
            $"plugin:{Owner.Id}:{Descriptor.WindowId}";
    }

    private sealed class Registration(
        PluginUiOwner owner,
        PluginPanelDescriptor descriptor,
        string markupPath,
        object binding,
        string? markupContent = null,
        string? pluginDirectory = null)
    {
        internal PluginUiOwner Owner { get; } = owner;
        internal PluginPanelDescriptor Descriptor { get; } = descriptor;
        internal string MarkupPath { get; } = markupPath;
        internal object Binding { get; } = binding;
        internal string? MarkupContent { get; } = markupContent;
        internal string? PluginDirectory { get; } = pluginDirectory;
        internal bool Drained { get; set; }
        internal UiRoot? Root { get; set; }
        internal UiElement? Element { get; set; }
        internal Action? WindowCleanup { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<long, Registration> _registrations = [];
    private long _nextRegistrationId;

    // Client-window control: RetailUiRuntime binds its own generic
    // string-keyed window seam here once, during its own construction --
    // the same single main/tick thread every plugin callback already runs
    // on (see docs/plugin-ui-markup.md: "assign from the thread that calls
    // Tick"). Before the retail UI runtime exists, or on a no-window host,
    // these stay unbound and every client-window call reports unavailable.
    private Func<PluginClientWindow, bool>? _toggleClientWindow;
    private Func<PluginClientWindow, bool>? _showClientWindow;
    private Func<PluginClientWindow, bool>? _hideClientWindow;
    private Func<PluginClientWindow, bool>? _isClientWindowVisible;

    internal void BindClientWindowControl(
        Func<PluginClientWindow, bool> toggle,
        Func<PluginClientWindow, bool> show,
        Func<PluginClientWindow, bool> hide,
        Func<PluginClientWindow, bool> isVisible)
    {
        ArgumentNullException.ThrowIfNull(toggle);
        ArgumentNullException.ThrowIfNull(show);
        ArgumentNullException.ThrowIfNull(hide);
        ArgumentNullException.ThrowIfNull(isVisible);
        lock (_gate)
        {
            _toggleClientWindow = toggle;
            _showClientWindow = show;
            _hideClientWindow = hide;
            _isClientWindowVisible = isVisible;
        }
    }

    /// <summary>
    /// Unbinds the client-window delegates so a disposed RetailUiRuntime
    /// cannot be invoked through a stale closure once a new one mounts
    /// (for example across a reconnect). Idempotent.
    /// </summary>
    internal void UnbindClientWindowControl()
    {
        lock (_gate)
        {
            _toggleClientWindow = null;
            _showClientWindow = null;
            _hideClientWindow = null;
            _isClientWindowVisible = null;
        }
    }

    public bool ToggleClientWindow(PluginClientWindow window)
    {
        Func<PluginClientWindow, bool>? toggle;
        lock (_gate) toggle = _toggleClientWindow;
        return toggle?.Invoke(window) ?? false;
    }

    public bool ShowClientWindow(PluginClientWindow window)
    {
        Func<PluginClientWindow, bool>? show;
        lock (_gate) show = _showClientWindow;
        return show?.Invoke(window) ?? false;
    }

    public bool HideClientWindow(PluginClientWindow window)
    {
        Func<PluginClientWindow, bool>? hide;
        lock (_gate) hide = _hideClientWindow;
        return hide?.Invoke(window) ?? false;
    }

    public bool IsClientWindowVisible(PluginClientWindow window)
    {
        Func<PluginClientWindow, bool>? isVisible;
        lock (_gate) isVisible = _isClientWindowVisible;
        return isVisible?.Invoke(window) ?? false;
    }

    public void AddMarkupPanel(string markupPath, object binding)
        => _ = RegisterMarkupPanel(markupPath, binding);

    public void AddPanel(
        PluginPanelDescriptor descriptor,
        string markupPath,
        object binding)
        => _ = RegisterPanel(
            new PluginUiOwner("unscoped", descriptor.Title),
            descriptor,
            markupPath,
            binding);

    public IDisposable RegisterPanel(
        PluginPanelDescriptor descriptor,
        string markupPath,
        object binding) => RegisterPanel(
            new PluginUiOwner("unscoped", descriptor.Title),
            descriptor,
            markupPath,
            binding);

    public IDisposable RegisterPanelContent(
        PluginPanelDescriptor descriptor,
        string markupContent,
        object binding) => RegisterPanelContent(
            new PluginUiOwner("unscoped", descriptor.Title),
            descriptor,
            markupContent,
            binding);

    public bool ViewExists(string viewName) =>
        ViewExists(new PluginUiOwner("unscoped", "Plugin"), viewName);

    public bool IsViewVisible(string viewName) =>
        IsViewVisible(new PluginUiOwner("unscoped", "Plugin"), viewName);

    public bool ControlExists(string viewName, string controlName) =>
        ControlExists(
            new PluginUiOwner("unscoped", "Plugin"), viewName, controlName);

    public bool SetControlLabel(
        string viewName,
        string controlName,
        string label) => SetControlLabel(
            new PluginUiOwner("unscoped", "Plugin"), viewName, controlName, label);

    public bool SetControlVisible(
        string viewName,
        string controlName,
        bool visible) => SetControlVisible(
            new PluginUiOwner("unscoped", "Plugin"), viewName, controlName, visible);

    public IDisposable RegisterMarkupPanel(string markupPath, object binding)
        => RegisterPanel(
            new PluginUiOwner("legacy", "Plugin"),
            new PluginPanelDescriptor(
                Path.GetFileNameWithoutExtension(markupPath),
                Path.GetFileNameWithoutExtension(markupPath)),
            markupPath,
            binding);

    public IDisposable RegisterPanel(
        PluginUiOwner owner,
        PluginPanelDescriptor descriptor,
        string markupPath,
        object binding) => RegisterPanel(owner, pluginDirectory: null, descriptor, markupPath, binding);

    public IDisposable RegisterPanel(
        PluginUiOwner owner,
        string? pluginDirectory,
        PluginPanelDescriptor descriptor,
        string markupPath,
        object binding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner.DisplayName);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.WindowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(markupPath);
        ArgumentNullException.ThrowIfNull(binding);
        long id;
        lock (_gate)
        {
            id = checked(++_nextRegistrationId);
            _registrations.Add(
                id,
                new Registration(
                    owner,
                    descriptor,
                    markupPath,
                    binding,
                    pluginDirectory: pluginDirectory));
        }
        return new RegistrationToken(this, id);
    }

    public IDisposable RegisterPanelContent(
        PluginUiOwner owner,
        PluginPanelDescriptor descriptor,
        string markupContent,
        object binding) =>
        RegisterPanelContent(owner, pluginDirectory: null, descriptor, markupContent, binding);

    public IDisposable RegisterPanelContent(
        PluginUiOwner owner,
        string? pluginDirectory,
        PluginPanelDescriptor descriptor,
        string markupContent,
        object binding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner.DisplayName);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.WindowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(markupContent);
        ArgumentNullException.ThrowIfNull(binding);
        long id;
        lock (_gate)
        {
            id = checked(++_nextRegistrationId);
            _registrations.Add(
                id,
                new Registration(
                    owner,
                    descriptor,
                    $"<inline:{descriptor.WindowId}>",
                    binding,
                    markupContent,
                    pluginDirectory));
        }
        return new RegistrationToken(this, id);
    }

    /// <summary>Returns each not-yet-drained active registration once.</summary>
    public IReadOnlyList<Pending> Drain()
    {
        lock (_gate)
        {
            var pending = new List<Pending>(_registrations.Count);
            foreach ((long id, Registration registration) in _registrations)
            {
                if (registration.Drained)
                    continue;
                registration.Drained = true;
                pending.Add(new Pending(
                    registration.Owner,
                    registration.Descriptor,
                    registration.MarkupPath,
                    registration.Binding)
                {
                    RegistrationId = id,
                    MarkupContent = registration.MarkupContent,
                    PluginDirectory = registration.PluginDirectory,
                });
            }
            return pending;
        }
    }

    internal void CompleteMount(Pending pending, UiRoot root, UiElement element)
    {
        bool stillRegistered;
        lock (_gate)
        {
            stillRegistered = _registrations.TryGetValue(
                pending.RegistrationId,
                out Registration? registration);
            if (stillRegistered)
            {
                registration!.Root = root;
                registration.Element = element;
            }
        }

        if (!stillRegistered)
            root.RemoveChild(element);
    }

    internal void CompleteWindowMount(Pending pending, Action cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        bool stillRegistered;
        lock (_gate)
        {
            stillRegistered = _registrations.TryGetValue(
                pending.RegistrationId,
                out Registration? registration);
            if (stillRegistered)
                registration!.WindowCleanup = cleanup;
        }
        if (!stillRegistered)
            cleanup();
    }

    internal void FailMount(Pending pending) => Remove(pending.RegistrationId);

    /// <summary>
    /// Whether a registration is waiting to be mounted. Plugins may register a
    /// window at any time - one driven by a server feed only knows its windows
    /// once the character is in the world - so the runtime polls this each tick
    /// and drains again, instead of mounting only what was registered before the
    /// UI came up.
    /// </summary>
    internal bool HasUndrained
    {
        get
        {
            lock (_gate)
            {
                foreach (Registration registration in _registrations.Values)
                {
                    if (!registration.Drained)
                        return true;
                }
                return false;
            }
        }
    }

    internal int RegistrationCount
    {
        get
        {
            lock (_gate)
                return _registrations.Count;
        }
    }

    public bool ViewExists(PluginUiOwner owner, string viewName)
    {
        lock (_gate)
            return FindRegistrationLocked(owner, viewName) is not null;
    }

    public bool IsViewVisible(PluginUiOwner owner, string viewName)
    {
        UiElement? view;
        lock (_gate)
            view = FindRegistrationLocked(owner, viewName)?.Element;
        return view?.Visible == true;
    }

    public bool ControlExists(
        PluginUiOwner owner,
        string viewName,
        string controlName) =>
        FindControl(owner, viewName, controlName) is not null;

    public bool SetControlLabel(
        PluginUiOwner owner,
        string viewName,
        string controlName,
        string label)
    {
        UiElement? control = FindControl(owner, viewName, controlName);
        switch (control)
        {
            case UiSimpleButton button:
                button.TextSource = null;
                button.Text = label;
                return true;
            case UiMarkupToggle toggle:
                toggle.TextSource = null;
                toggle.Text = label;
                return true;
            case UiLabel text:
                text.TextSource = null;
                text.Text = label;
                return true;
            default:
                return false;
        }
    }

    public bool SetControlVisible(
        PluginUiOwner owner,
        string viewName,
        string controlName,
        bool visible)
    {
        UiElement? control = FindControl(owner, viewName, controlName);
        if (control is null)
            return false;
        control.VisibleSource = null;
        control.Visible = visible;
        return true;
    }

    private UiElement? FindControl(
        PluginUiOwner owner,
        string viewName,
        string controlName)
    {
        UiElement? view;
        lock (_gate)
            view = FindRegistrationLocked(owner, viewName)?.Element;
        return view is null ? null : FindByName(view, controlName);
    }

    private Registration? FindRegistrationLocked(
        PluginUiOwner owner,
        string viewName) => _registrations.Values.FirstOrDefault(registration =>
            registration.Owner == owner
            && (registration.Descriptor.WindowId.Equals(
                    viewName, StringComparison.Ordinal)
                || registration.Descriptor.Title.Equals(
                    viewName, StringComparison.Ordinal)));

    private static UiElement? FindByName(UiElement root, string name)
    {
        if (root.Name?.Equals(name, StringComparison.Ordinal) == true)
            return root;
        foreach (UiElement child in root.Children)
        {
            UiElement? found = FindByName(child, name);
            if (found is not null)
                return found;
        }
        return null;
    }

    private void Remove(long id)
    {
        UiRoot? root;
        UiElement? element;
        Action? windowCleanup;
        lock (_gate)
        {
            if (!_registrations.Remove(id, out Registration? registration))
                return;
            root = registration.Root;
            element = registration.Element;
            windowCleanup = registration.WindowCleanup;
        }

        windowCleanup?.Invoke();
        if (root is not null && element is not null)
            root.RemoveChild(element);
    }

    private sealed class RegistrationToken(
        BufferedUiRegistry owner,
        long registrationId) : IDisposable
    {
        private BufferedUiRegistry? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Remove(registrationId);
    }
}
