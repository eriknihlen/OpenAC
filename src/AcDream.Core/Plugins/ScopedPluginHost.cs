using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Plugins;

internal sealed class ScopedPluginHost : IPluginHost, IDisposable
{
    private readonly IPluginHost _inner;
    private readonly string _pluginId;
    private readonly ScopedEvents _events;
    private readonly ScopedSelectionService _selection;
    private readonly ScopedUiRegistry _ui;
    private readonly ScopedPluginStorage _storage;
    private readonly ScopedPluginCommandRegistry _commands;
    private readonly ScopedLootClassifierRegistry _lootClassifiers;
    private bool _disposed;

    internal ScopedPluginHost(
        IPluginHost inner,
        string pluginId,
        string pluginDisplayName)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDisplayName);
        _pluginId = pluginId;
        _events = new ScopedEvents(inner.Events);
        _selection = new ScopedSelectionService(inner.Selection);
        _ui = new ScopedUiRegistry(
            inner.Ui,
            new PluginUiOwner(pluginId, pluginDisplayName));
        _storage = new ScopedPluginStorage(inner.Storage, pluginId);
        _commands = new ScopedPluginCommandRegistry(inner.Commands);
        _lootClassifiers = new ScopedLootClassifierRegistry(
            inner.LootClassifiers,
            pluginId,
            pluginDisplayName);
    }

    public bool HasUi => _inner.HasUi;
    public IPluginLogger Log => _inner.Log;
    public IGameState State => _inner.State;
    public IEvents Events => _events;
    public ISelectionService Selection => _selection;
    public IUiRegistry Ui => _ui;
    public IPluginStorage Storage => _storage;
    public IPluginStorage VtankProfiles => _inner.VtankProfiles;
    public IPluginCommandRegistry Commands => _commands;
    public IPluginLootClassifierRegistry LootClassifiers => _lootClassifiers;
    public IReadOnlyDictionary<string, string> SessionSettings =>
        _inner is IPerPluginSessionSettings perPlugin
            ? perPlugin.SessionSettingsFor(_pluginId)
            : _inner.SessionSettings;

    public IAutomationSurface Automation => _inner.Automation;

    private sealed class ScopedPluginStorage(
        IPluginStorage inner,
        string pluginId) : IPluginStorage
    {
        public bool IsAvailable => inner.IsAvailable;
        public string? ReadText(string key) =>
            inner.ReadText(ScopedKey(key));
        public IReadOnlyList<string> List(string prefix)
        {
            string scopedPrefix = ScopedKey(prefix);
            string ownerPrefix = pluginId + Path.DirectorySeparatorChar;
            return inner.List(scopedPrefix)
                .Select(key => key.Replace('/', Path.DirectorySeparatorChar))
                .Where(key => key.StartsWith(
                    ownerPrefix,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
                .Select(key => key[ownerPrefix.Length..]
                    .Replace(Path.DirectorySeparatorChar, '/'))
                .ToArray();
        }
        public void WriteText(string key, string content) =>
            inner.WriteText(ScopedKey(key), content);
        public bool Delete(string key) => inner.Delete(ScopedKey(key));

        private static string ValidateKey(string key)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            if (Path.IsPathRooted(key)
                || key.Contains("..", StringComparison.Ordinal)
                || key.Contains('\\'))
            {
                throw new ArgumentException("Invalid plugin storage key.", nameof(key));
            }
            return key.Replace('/', Path.DirectorySeparatorChar);
        }

        private string ScopedKey(string key) =>
            Path.Combine(pluginId, ValidateKey(key));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _events.Dispose();
        _selection.Dispose();
        _ui.Dispose();
        _commands.Dispose();
        _lootClassifiers.Dispose();
    }

    private sealed class ScopedLootClassifierRegistry(
        IPluginLootClassifierRegistry inner,
        string pluginId,
        string pluginDisplayName)
        : IPluginLootClassifierRegistry,
          IDisposable
    {
        private readonly object _gate = new();
        private readonly List<IDisposable> _registrations = [];
        private bool _disposed;

        public IReadOnlyList<PluginLootClassifierInfo> Available =>
            inner.Available;

        public IDisposable Register(
            string classifierId,
            string displayName,
            IPluginLootClassifier classifier)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentException.ThrowIfNullOrWhiteSpace(classifierId);
            string local = classifierId.Trim();
            if (local.Contains('/') || local.Contains('\\'))
            {
                throw new ArgumentException(
                    "A classifier id cannot contain a path separator.",
                    nameof(classifierId));
            }
            string effectiveName = string.IsNullOrWhiteSpace(displayName)
                ? pluginDisplayName
                : displayName.Trim();
            IDisposable registration = inner.Register(
                $"{pluginId}/{local}",
                effectiveName,
                classifier);
            lock (_gate)
            {
                if (!_disposed)
                {
                    _registrations.Add(registration);
                    return registration;
                }
            }
            registration.Dispose();
            throw new ObjectDisposedException(nameof(ScopedLootClassifierRegistry));
        }

        public bool TryClassify(
            string classifierId,
            in PluginLootClassificationContext context,
            out PluginLootClassification classification) =>
            inner.TryClassify(classifierId, context, out classification);

        public bool TryNotifyLooted(
            string classifierId,
            in PluginLootedItem item) =>
            inner.TryNotifyLooted(classifierId, item);

        public bool TryNotifyItemRemoved(
            string classifierId,
            uint objectId) =>
            inner.TryNotifyItemRemoved(classifierId, objectId);

        public void Dispose()
        {
            IDisposable[] registrations;
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                registrations = _registrations.ToArray();
                _registrations.Clear();
            }
            for (int index = registrations.Length - 1; index >= 0; index--)
                registrations[index].Dispose();
        }
    }

    private sealed class ScopedPluginCommandRegistry(IPluginCommandRegistry inner)
        : IPluginCommandRegistry,
          IDisposable
    {
        private readonly object _gate = new();
        private readonly List<IDisposable> _registrations = [];
        private bool _disposed;

        public IDisposable Register(string verb, Action<PluginCommand> handler)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            IDisposable registration = inner.Register(verb, handler);
            lock (_gate)
            {
                if (!_disposed)
                {
                    _registrations.Add(registration);
                    return registration;
                }
            }
            registration.Dispose();
            throw new ObjectDisposedException(nameof(ScopedPluginCommandRegistry));
        }

        public void Dispose()
        {
            IDisposable[] registrations;
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                registrations = _registrations.ToArray();
                _registrations.Clear();
            }
            for (int index = registrations.Length - 1; index >= 0; index--)
                registrations[index].Dispose();
        }
    }

    private sealed class ScopedSelectionService(ISelectionService inner)
        : ISelectionService,
          IDisposable
    {
        private readonly object _gate = new();
        private readonly List<Action<SelectionChangedEvent>> _registrations = [];
        private bool _disposed;

        public uint? SelectedObjectId => inner.SelectedObjectId;
        public uint? PreviousObjectId => inner.PreviousObjectId;

        public event Action<SelectionChangedEvent> Changed
        {
            add
            {
                ArgumentNullException.ThrowIfNull(value);
                try
                {
                    inner.Changed += value;
                }
                catch
                {
                    try { inner.Changed -= value; }
                    catch { }
                    throw;
                }
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        _registrations.Add(value);
                        return;
                    }
                }

                try { inner.Changed -= value; }
                catch { }
                throw new ObjectDisposedException(nameof(ScopedSelectionService));
            }
            remove
            {
                if (value is null)
                    return;
                inner.Changed -= value;
                lock (_gate)
                    RemoveLast(value);
            }
        }

        public bool Select(uint objectId)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return inner.Select(objectId);
            }
        }

        public bool Clear()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return inner.Clear();
            }
        }

        public void Dispose()
        {
            Action<SelectionChangedEvent>[] registrations;
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                registrations = _registrations.ToArray();
                _registrations.Clear();
            }

            for (int index = registrations.Length - 1; index >= 0; index--)
            {
                try { inner.Changed -= registrations[index]; }
                catch { }
            }
        }

        private void RemoveLast(Action<SelectionChangedEvent> handler)
        {
            for (int index = _registrations.Count - 1; index >= 0; index--)
            {
                if (_registrations[index] != handler)
                    continue;
                _registrations.RemoveAt(index);
                return;
            }
        }
    }

    private sealed class ScopedEvents(IEvents inner) : IEvents, IDisposable
    {
        private readonly object _gate = new();
        private readonly List<Action<WorldEntitySnapshot>> _registrations = [];
        private readonly List<Action<double>> _tickRegistrations = [];
        private bool _disposed;

        public event Action<double> Tick
        {
            add
            {
                ArgumentNullException.ThrowIfNull(value);
                try
                {
                    inner.Tick += value;
                }
                catch
                {
                    try { inner.Tick -= value; }
                    catch { }
                    throw;
                }
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        _tickRegistrations.Add(value);
                        return;
                    }
                }

                try { inner.Tick -= value; }
                catch { }
                throw new ObjectDisposedException(nameof(ScopedEvents));
            }
            remove
            {
                if (value is null)
                    return;
                inner.Tick -= value;
                lock (_gate)
                {
                    for (int index = _tickRegistrations.Count - 1; index >= 0; index--)
                    {
                        if (_tickRegistrations[index] != value)
                            continue;
                        _tickRegistrations.RemoveAt(index);
                        break;
                    }
                }
            }
        }

        public event Action<WorldEntitySnapshot> EntitySpawned
        {
            add
            {
                ArgumentNullException.ThrowIfNull(value);
                try
                {
                    inner.EntitySpawned += value;
                }
                catch
                {
                    try { inner.EntitySpawned -= value; }
                    catch { }
                    throw;
                }
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        _registrations.Add(value);
                        return;
                    }
                }

                try { inner.EntitySpawned -= value; }
                catch { }
                throw new ObjectDisposedException(nameof(ScopedEvents));
            }
            remove
            {
                if (value is null)
                    return;
                inner.EntitySpawned -= value;
                lock (_gate)
                    RemoveLast(value);
            }
        }

        public void Dispose()
        {
            Action<WorldEntitySnapshot>[] registrations;
            Action<double>[] tickRegistrations;
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                registrations = _registrations.ToArray();
                _registrations.Clear();
                tickRegistrations = _tickRegistrations.ToArray();
                _tickRegistrations.Clear();
            }

            for (int index = registrations.Length - 1; index >= 0; index--)
            {
                try { inner.EntitySpawned -= registrations[index]; }
                catch { }
            }

            for (int index = tickRegistrations.Length - 1; index >= 0; index--)
            {
                try { inner.Tick -= tickRegistrations[index]; }
                catch { }
            }
        }

        private void RemoveLast(Action<WorldEntitySnapshot> handler)
        {
            for (int index = _registrations.Count - 1; index >= 0; index--)
            {
                if (_registrations[index] != handler)
                    continue;
                _registrations.RemoveAt(index);
                return;
            }
        }
    }

    private sealed class ScopedUiRegistry : IUiRegistry, IDisposable
    {
        private readonly IScopedUiRegistry _inner;
        private readonly PluginUiOwner _owner;
        private readonly object _gate = new();
        private readonly List<IDisposable> _registrations = [];
        private bool _disposed;

        internal ScopedUiRegistry(IUiRegistry inner, PluginUiOwner owner)
        {
            _inner = inner as IScopedUiRegistry
                ?? throw new InvalidOperationException(
                    "Plugin hosts must expose an IScopedUiRegistry so UI registrations can be rolled back.");
            _owner = owner;
        }

        public void AddMarkupPanel(string markupPath, object binding)
        {
            AddRegistration(_inner.RegisterPanel(
                _owner,
                new PluginPanelDescriptor(
                    Path.GetFileNameWithoutExtension(markupPath),
                    _owner.DisplayName),
                markupPath,
                binding));
        }

        public void AddPanel(
            PluginPanelDescriptor descriptor,
            string markupPath,
            object binding)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            AddRegistration(_inner.RegisterPanel(
                _owner,
                descriptor,
                markupPath,
                binding));
        }

        public IDisposable RegisterPanel(
            PluginPanelDescriptor descriptor,
            string markupPath,
            object binding)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            return TrackRegistration(_inner.RegisterPanel(
                _owner,
                descriptor,
                markupPath,
                binding));
        }

        public IDisposable RegisterPanelContent(
            PluginPanelDescriptor descriptor,
            string markupContent,
            object binding)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            return TrackRegistration(_inner.RegisterPanelContent(
                _owner,
                descriptor,
                markupContent,
                binding));
        }

        public bool ViewExists(string viewName) =>
            _inner.ViewExists(_owner, viewName);

        public bool IsViewVisible(string viewName) =>
            _inner.IsViewVisible(_owner, viewName);

        public bool ControlExists(string viewName, string controlName) =>
            _inner.ControlExists(_owner, viewName, controlName);

        public bool SetControlLabel(
            string viewName,
            string controlName,
            string label) =>
            _inner.SetControlLabel(_owner, viewName, controlName, label);

        public bool SetControlVisible(
            string viewName,
            string controlName,
            bool visible) =>
            _inner.SetControlVisible(_owner, viewName, controlName, visible);

        private void AddRegistration(IDisposable registration)
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    _registrations.Add(registration);
                    return;
                }
            }

            registration.Dispose();
            throw new ObjectDisposedException(nameof(ScopedUiRegistry));
        }

        private IDisposable TrackRegistration(IDisposable registration)
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    _registrations.Add(registration);
                    return new IndividualRegistration(this, registration);
                }
            }

            registration.Dispose();
            throw new ObjectDisposedException(nameof(ScopedUiRegistry));
        }

        private void RemoveRegistration(IDisposable registration)
        {
            lock (_gate)
            {
                if (!_registrations.Remove(registration))
                    return;
            }
            registration.Dispose();
        }

        public void Dispose()
        {
            IDisposable[] registrations;
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                registrations = _registrations.ToArray();
                _registrations.Clear();
            }

            for (int index = registrations.Length - 1; index >= 0; index--)
            {
                try { registrations[index].Dispose(); }
                catch { }
            }
        }

        private sealed class IndividualRegistration(
            ScopedUiRegistry owner,
            IDisposable registration) : IDisposable
        {
            private ScopedUiRegistry? _owner = owner;

            public void Dispose() => Interlocked.Exchange(ref _owner, null)?
                .RemoveRegistration(registration);
        }
    }
}
