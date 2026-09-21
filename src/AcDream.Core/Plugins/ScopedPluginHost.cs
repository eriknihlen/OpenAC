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
    private readonly ScopedAutomationSurface _automation;
    private readonly ScopedHotkeyRegistry _hotkeys;
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
        _automation = new ScopedAutomationSurface(inner, pluginId);
        _hotkeys = new ScopedHotkeyRegistry(inner.Hotkeys, pluginId);
    }

    public bool HasUi => _inner.HasUi;
    public IPluginLogger Log => _inner.Log;
    public IGameState State => _inner.State;
    public IEvents Events => _events;
    public ISelectionService Selection => _selection;
    public IUiRegistry Ui => _ui;
    public IPluginStorage Storage => _storage;
    public IPluginStorage VtankProfiles => _inner.VtankProfiles;
    public IPluginResourceCatalog Resources => _inner.Resources;
    public IPluginCommandRegistry Commands => _commands;
    public IPluginLootClassifierRegistry LootClassifiers => _lootClassifiers;
    public IReadOnlyDictionary<string, string> SessionSettings =>
        _inner is IPerPluginSessionSettings perPlugin
            ? perPlugin.SessionSettingsFor(_pluginId)
            : _inner.SessionSettings;

    public IPluginClipboard Clipboard => _inner.Clipboard;
    public IHostWindow Window => _inner.Window;
    public IAutomationSurface Automation => _automation;
    public IHotkeyRegistry Hotkeys => _hotkeys;
    public IPluginMapRegistry Maps => _inner.Maps;
    public IPluginMapResourceCatalog MapResources => _inner.MapResources;
    public IPluginRenderRegistry Rendering => _inner.Rendering;

    private sealed class ScopedPluginStorage(
        IPluginStorage inner,
        string pluginId) : IPluginStorage
    {
        public bool IsAvailable => inner.IsAvailable;
        public string? RootPath => inner.RootPath is { } root
            ? Path.Combine(root, pluginId)
            : null;
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
        _automation.Dispose();
        _hotkeys.Dispose();
    }

    /// <summary>
    /// Resolves the host's <see cref="IAutomationSurface"/> fresh on every
    /// call instead of pinning the instance seen at construction, so a host
    /// that swaps its automation surface mid-session (a reconnect that
    /// rebuilds it, for example) is observed here too. Only <see cref="Chat"/>
    /// is actually wrapped, and it re-wraps lazily when the live chat
    /// instance changes.
    /// </summary>
    private sealed class ScopedAutomationSurface(IPluginHost host, string pluginId)
        : IAutomationSurface, IDisposable
    {
        private readonly object _gate = new();
        private IPluginChat? _chatSource;
        private ScopedPluginChat? _chatWrapper;
        private INavigationAutomation? _navigationSource;
        private INavigationAutomation? _navigationScope;
        private bool _disposed;

        private IAutomationSurface Inner => host.Automation;

        public bool IsAvailable => Inner.IsAvailable;
        public ICharacterInfo Character => Inner.Character;
        public ISpellCatalog Spells => Inner.Spells;
        public IMagicCommands Magic => Inner.Magic;

        public IPluginChat Chat
        {
            get
            {
                IPluginChat currentSource = Inner.Chat;
                lock (_gate)
                {
                    if (_chatWrapper is null
                        || !ReferenceEquals(_chatSource, currentSource))
                    {
                        _chatWrapper?.Dispose();
                        _chatSource = currentSource;
                        _chatWrapper = new ScopedPluginChat(currentSource);
                        // The surface itself may already have been disposed
                        // (this host is being asked for a fresh chat after
                        // its plugin unloaded); hand back a wrapper that is
                        // immediately, correctly disposed rather than a live
                        // one nothing will ever clean up.
                        if (_disposed)
                            _chatWrapper.Dispose();
                    }
                    return _chatWrapper;
                }
            }
        }

        public IDialogAutomation Dialogs => Inner.Dialogs;
        public ICombatAutomation Combat => Inner.Combat;
        public IEquipmentAutomation Equipment => Inner.Equipment;
        public IItemAutomation Items => Inner.Items;
        public ILootAutomation Loot => Inner.Loot;
        public IFellowshipAutomation Fellowship => Inner.Fellowship;
        public IEnchantmentAutomation Enchantments => Inner.Enchantments;
        // A host whose navigation can tell plugins apart hands this plugin its own view,
        // so its walks and pauses are its own and go with it when it is disabled.
        public INavigationAutomation Navigation
        {
            get
            {
                INavigationAutomation source = Inner.Navigation;
                lock (_gate)
                {
                    // The plugin is gone: nothing it could drive the character with.
                    if (_disposed)
                        return NoOpAutomationSurface.Instance.Navigation;
                    if (!ReferenceEquals(_navigationSource, source))
                    {
                        (_navigationSource as IScopedNavigationSource)?.Release(pluginId);
                        _navigationSource = source;
                        _navigationScope = source is IScopedNavigationSource scoped
                            ? scoped.ScopeTo(pluginId)
                            : source;
                    }
                    return _navigationScope!;
                }
            }
        }
        public IWorldObjectAutomation Objects => Inner.Objects;
        public IWorldTimeAutomation WorldTime => Inner.WorldTime;
        public ILoginAutomation Login => Inner.Login;
        public INetworkAutomation Network => Inner.Network;
        public IRecoveryAutomation Recovery => Inner.Recovery;
        public IProjectileAutomation Projectiles => Inner.Projectiles;
        public ISelectionAutomation Selection => Inner.Selection;
        public ITradeAutomation Trade => Inner.Trade;
        public IVendorAutomation Vendor => Inner.Vendor;

        public void Dispose()
        {
            INavigationAutomation? navigation;
            lock (_gate)
            {
                _disposed = true;
                _chatWrapper?.Dispose();
                navigation = _navigationSource;
                _navigationSource = null;
                _navigationScope = null;
            }
            // The plugin is going: its walk stops and its pauses are dropped.
            (navigation as IScopedNavigationSource)?.Release(pluginId);
        }
    }

    private sealed class ScopedPluginChat(IPluginChat inner)
        : IPluginChat, IDisposable
    {
        private readonly object _gate = new();
        private readonly List<IDisposable> _filters = [];
        private readonly List<Action<PluginChatLinkClicked>> _linkClickedSubscriptions = [];
        private readonly List<Action<PluginChatMessage>> _subscriptions = [];
        private bool _disposed;

        public IReadOnlyList<PluginChatMessage> CaptureMessages(
            ulong afterSequence) =>
            inner.CaptureMessages(afterSequence);

        public void PostSystemMessage(string text)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            inner.PostSystemMessage(text);
        }

        public void PostMessage(string text, int logTextType)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            inner.PostMessage(text, logTextType);
        }

        public bool Submit(string text)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return inner.Submit(text);
        }

        public event Action<PluginChatLinkClicked> LinkClicked
        {
            add
            {
                ArgumentNullException.ThrowIfNull(value);
                lock (_gate)
                    ObjectDisposedException.ThrowIf(_disposed, this);

                try
                {
                    inner.LinkClicked += value;
                }
                catch
                {
                    try { inner.LinkClicked -= value; }
                    catch { }
                    throw;
                }
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        _linkClickedSubscriptions.Add(value);
                        return;
                    }
                }

                try { inner.LinkClicked -= value; }
                catch { }
                throw new ObjectDisposedException(nameof(ScopedPluginChat));
            }
            remove
            {
                if (value is null)
                    return;
                inner.LinkClicked -= value;
                lock (_gate)
                {
                    for (int index = _linkClickedSubscriptions.Count - 1; index >= 0; index--)
                    {
                        if (_linkClickedSubscriptions[index] != value)
                            continue;
                        _linkClickedSubscriptions.RemoveAt(index);
                        break;
                    }
                }
            }
        }

        public event Action<PluginChatMessage> Received
        {
            add
            {
                ArgumentNullException.ThrowIfNull(value);

                // Check before subscribing to the host's chat: otherwise an
                // already-unloaded plugin would still briefly ride the live
                // subscription and could receive a line before the
                // subscribe-then-unwind below catches up.
                lock (_gate)
                    ObjectDisposedException.ThrowIf(_disposed, this);

                try
                {
                    inner.Received += value;
                }
                catch
                {
                    try { inner.Received -= value; }
                    catch { }
                    throw;
                }
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        _subscriptions.Add(value);
                        return;
                    }
                }

                try { inner.Received -= value; }
                catch { }
                throw new ObjectDisposedException(nameof(ScopedPluginChat));
            }
            remove
            {
                if (value is null)
                    return;
                inner.Received -= value;
                lock (_gate)
                {
                    for (int index = _subscriptions.Count - 1; index >= 0; index--)
                    {
                        if (_subscriptions[index] != value)
                            continue;
                        _subscriptions.RemoveAt(index);
                        break;
                    }
                }
            }
        }

        public IDisposable RegisterFilter(Func<PluginChatMessage, bool> suppress)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            IDisposable registration = inner.RegisterFilter(suppress);
            lock (_gate)
            {
                if (!_disposed)
                {
                    _filters.Add(registration);
                    return new IndividualFilter(this, registration);
                }
            }
            registration.Dispose();
            throw new ObjectDisposedException(nameof(ScopedPluginChat));
        }

        private void RemoveFilter(IDisposable registration)
        {
            lock (_gate)
            {
                if (!_filters.Remove(registration))
                    return;
            }
            registration.Dispose();
        }

        public void Dispose()
        {
            IDisposable[] filters;
            Action<PluginChatLinkClicked>[] linkClickedSubscriptions;
            Action<PluginChatMessage>[] subscriptions;
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                filters = _filters.ToArray();
                _filters.Clear();
                linkClickedSubscriptions = _linkClickedSubscriptions.ToArray();
                _linkClickedSubscriptions.Clear();
                subscriptions = _subscriptions.ToArray();
                _subscriptions.Clear();
            }

            for (int index = filters.Length - 1; index >= 0; index--)
            {
                try { filters[index].Dispose(); }
                catch { }
            }

            for (int index = linkClickedSubscriptions.Length - 1; index >= 0; index--)
            {
                try { inner.LinkClicked -= linkClickedSubscriptions[index]; }
                catch { }
            }

            for (int index = subscriptions.Length - 1; index >= 0; index--)
            {
                try { inner.Received -= subscriptions[index]; }
                catch { }
            }
        }

        private sealed class IndividualFilter(
            ScopedPluginChat owner,
            IDisposable registration) : IDisposable
        {
            private ScopedPluginChat? _owner = owner;

            public void Dispose() => Interlocked.Exchange(ref _owner, null)?
                .RemoveFilter(registration);
        }
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

        public bool TryNeedsIdentification(
            string classifierId,
            in PluginLootClassificationContext context) =>
            inner.TryNeedsIdentification(classifierId, context);

        public bool TryClassifyWithProfile(
            string classifierId,
            string profileName,
            in PluginLootClassificationContext context,
            out PluginLootClassification classification) =>
            inner.TryClassifyWithProfile(
                classifierId,
                profileName,
                context,
                out classification);

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

    private sealed class ScopedHotkeyRegistry(IHotkeyRegistry inner, string pluginId)
        : IHotkeyRegistry,
          IDisposable
    {
        private readonly object _gate = new();
        private readonly List<IPluginHotkeyRegistration> _registrations = [];
        private bool _disposed;

        public IPluginHotkeyRegistration Register(
            string id,
            string displayName,
            PluginKeyChord defaultChord,
            Action handler)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            IPluginHotkeyRegistration registration =
                inner.Register(pluginId + ":" + id, displayName, defaultChord, handler);
            lock (_gate)
            {
                if (!_disposed)
                {
                    _registrations.Add(registration);
                    return registration;
                }
            }
            registration.Dispose();
            throw new ObjectDisposedException(nameof(ScopedHotkeyRegistry));
        }

        public void Dispose()
        {
            IPluginHotkeyRegistration[] registrations;
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
        private readonly List<Action> _loginCompleteRegistrations = [];
        private readonly List<Action> _logoffRegistrations = [];
        private readonly List<Action<string>> _localPlayerDiedRegistrations = [];
        private readonly List<Action<PluginObjectChange>> _objectChangedRegistrations = [];
        private readonly List<Action<PluginGoToReport>> _navigationChangedRegistrations = [];
        private readonly List<Action<uint>> _containerOpenedRegistrations = [];
        private readonly List<Action<uint>> _containerClosedRegistrations = [];
        private readonly List<Action<PluginConfirmation>> _confirmationRequestedRegistrations = [];
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

        public event Action LoginComplete
        {
            add
            {
                ArgumentNullException.ThrowIfNull(value);
                try
                {
                    inner.LoginComplete += value;
                }
                catch
                {
                    try { inner.LoginComplete -= value; }
                    catch { }
                    throw;
                }
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        _loginCompleteRegistrations.Add(value);
                        return;
                    }
                }

                try { inner.LoginComplete -= value; }
                catch { }
                throw new ObjectDisposedException(nameof(ScopedEvents));
            }
            remove
            {
                if (value is null)
                    return;
                inner.LoginComplete -= value;
                lock (_gate)
                    _loginCompleteRegistrations.Remove(value);
            }
        }

        public event Action Logoff
        {
            add
            {
                ArgumentNullException.ThrowIfNull(value);
                try
                {
                    inner.Logoff += value;
                }
                catch
                {
                    try { inner.Logoff -= value; }
                    catch { }
                    throw;
                }
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        _logoffRegistrations.Add(value);
                        return;
                    }
                }

                try { inner.Logoff -= value; }
                catch { }
                throw new ObjectDisposedException(nameof(ScopedEvents));
            }
            remove
            {
                if (value is null)
                    return;
                inner.Logoff -= value;
                lock (_gate)
                    _logoffRegistrations.Remove(value);
            }
        }

        public event Action<string> LocalPlayerDied
        {
            add
            {
                ArgumentNullException.ThrowIfNull(value);
                try
                {
                    inner.LocalPlayerDied += value;
                }
                catch
                {
                    try { inner.LocalPlayerDied -= value; }
                    catch { }
                    throw;
                }
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        _localPlayerDiedRegistrations.Add(value);
                        return;
                    }
                }

                try { inner.LocalPlayerDied -= value; }
                catch { }
                throw new ObjectDisposedException(nameof(ScopedEvents));
            }
            remove
            {
                if (value is null)
                    return;
                inner.LocalPlayerDied -= value;
                lock (_gate)
                    _localPlayerDiedRegistrations.Remove(value);
            }
        }

        public event Action<PluginObjectChange> ObjectChanged
        {
            add
            {
                ArgumentNullException.ThrowIfNull(value);
                try
                {
                    inner.ObjectChanged += value;
                }
                catch
                {
                    try { inner.ObjectChanged -= value; }
                    catch { }
                    throw;
                }
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        _objectChangedRegistrations.Add(value);
                        return;
                    }
                }

                try { inner.ObjectChanged -= value; }
                catch { }
                throw new ObjectDisposedException(nameof(ScopedEvents));
            }
            remove
            {
                if (value is null)
                    return;
                inner.ObjectChanged -= value;
                lock (_gate)
                    _objectChangedRegistrations.Remove(value);
            }
        }

        public event Action<PluginGoToReport> NavigationChanged
        {
            add
            {
                ArgumentNullException.ThrowIfNull(value);
                try
                {
                    inner.NavigationChanged += value;
                }
                catch
                {
                    try { inner.NavigationChanged -= value; }
                    catch { }
                    throw;
                }
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        _navigationChangedRegistrations.Add(value);
                        return;
                    }
                }

                try { inner.NavigationChanged -= value; }
                catch { }
                throw new ObjectDisposedException(nameof(ScopedEvents));
            }
            remove
            {
                if (value is null)
                    return;
                inner.NavigationChanged -= value;
                lock (_gate)
                    _navigationChangedRegistrations.Remove(value);
            }
        }

        public event Action<uint> ContainerOpened
        {
            add
            {
                ArgumentNullException.ThrowIfNull(value);
                try
                {
                    inner.ContainerOpened += value;
                }
                catch
                {
                    try { inner.ContainerOpened -= value; }
                    catch { }
                    throw;
                }
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        _containerOpenedRegistrations.Add(value);
                        return;
                    }
                }

                try { inner.ContainerOpened -= value; }
                catch { }
                throw new ObjectDisposedException(nameof(ScopedEvents));
            }
            remove
            {
                if (value is null)
                    return;
                inner.ContainerOpened -= value;
                lock (_gate)
                    _containerOpenedRegistrations.Remove(value);
            }
        }

        public event Action<uint> ContainerClosed
        {
            add
            {
                ArgumentNullException.ThrowIfNull(value);
                try
                {
                    inner.ContainerClosed += value;
                }
                catch
                {
                    try { inner.ContainerClosed -= value; }
                    catch { }
                    throw;
                }
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        _containerClosedRegistrations.Add(value);
                        return;
                    }
                }

                try { inner.ContainerClosed -= value; }
                catch { }
                throw new ObjectDisposedException(nameof(ScopedEvents));
            }
            remove
            {
                if (value is null)
                    return;
                inner.ContainerClosed -= value;
                lock (_gate)
                    _containerClosedRegistrations.Remove(value);
            }
        }

        public event Action<PluginConfirmation> ConfirmationRequested
        {
            add
            {
                ArgumentNullException.ThrowIfNull(value);
                try
                {
                    inner.ConfirmationRequested += value;
                }
                catch
                {
                    try { inner.ConfirmationRequested -= value; }
                    catch { }
                    throw;
                }
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        _confirmationRequestedRegistrations.Add(value);
                        return;
                    }
                }

                try { inner.ConfirmationRequested -= value; }
                catch { }
                throw new ObjectDisposedException(nameof(ScopedEvents));
            }
            remove
            {
                if (value is null)
                    return;
                inner.ConfirmationRequested -= value;
                lock (_gate)
                    _confirmationRequestedRegistrations.Remove(value);
            }
        }

        public void Dispose()
        {
            Action<WorldEntitySnapshot>[] registrations;
            Action<double>[] tickRegistrations;
            Action[] loginCompleteRegistrations;
            Action[] logoffRegistrations;
            Action<string>[] localPlayerDiedRegistrations;
            Action<PluginObjectChange>[] objectChangedRegistrations;
            Action<PluginGoToReport>[] navigationChangedRegistrations;
            Action<uint>[] containerOpenedRegistrations;
            Action<uint>[] containerClosedRegistrations;
            Action<PluginConfirmation>[] confirmationRequestedRegistrations;
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                registrations = _registrations.ToArray();
                _registrations.Clear();
                tickRegistrations = _tickRegistrations.ToArray();
                _tickRegistrations.Clear();
                loginCompleteRegistrations = _loginCompleteRegistrations.ToArray();
                _loginCompleteRegistrations.Clear();
                logoffRegistrations = _logoffRegistrations.ToArray();
                _logoffRegistrations.Clear();
                localPlayerDiedRegistrations = _localPlayerDiedRegistrations.ToArray();
                _localPlayerDiedRegistrations.Clear();
                objectChangedRegistrations = _objectChangedRegistrations.ToArray();
                _objectChangedRegistrations.Clear();
                navigationChangedRegistrations = _navigationChangedRegistrations.ToArray();
                _navigationChangedRegistrations.Clear();
                containerOpenedRegistrations = _containerOpenedRegistrations.ToArray();
                _containerOpenedRegistrations.Clear();
                containerClosedRegistrations = _containerClosedRegistrations.ToArray();
                _containerClosedRegistrations.Clear();
                confirmationRequestedRegistrations =
                    _confirmationRequestedRegistrations.ToArray();
                _confirmationRequestedRegistrations.Clear();
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

            for (int index = loginCompleteRegistrations.Length - 1; index >= 0; index--)
            {
                try { inner.LoginComplete -= loginCompleteRegistrations[index]; }
                catch { }
            }

            for (int index = logoffRegistrations.Length - 1; index >= 0; index--)
            {
                try { inner.Logoff -= logoffRegistrations[index]; }
                catch { }
            }

            for (int index = localPlayerDiedRegistrations.Length - 1; index >= 0; index--)
            {
                try { inner.LocalPlayerDied -= localPlayerDiedRegistrations[index]; }
                catch { }
            }

            for (int index = objectChangedRegistrations.Length - 1; index >= 0; index--)
            {
                try { inner.ObjectChanged -= objectChangedRegistrations[index]; }
                catch { }
            }

            for (int index = navigationChangedRegistrations.Length - 1; index >= 0; index--)
            {
                try { inner.NavigationChanged -= navigationChangedRegistrations[index]; }
                catch { }
            }

            for (int index = containerOpenedRegistrations.Length - 1; index >= 0; index--)
            {
                try { inner.ContainerOpened -= containerOpenedRegistrations[index]; }
                catch { }
            }

            for (int index = containerClosedRegistrations.Length - 1; index >= 0; index--)
            {
                try { inner.ContainerClosed -= containerClosedRegistrations[index]; }
                catch { }
            }

            for (int index = confirmationRequestedRegistrations.Length - 1; index >= 0; index--)
            {
                try
                {
                    inner.ConfirmationRequested -= confirmationRequestedRegistrations[index];
                }
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

        // Client-window control is global (retained top-level windows the
        // player toggles with a keybind or toolbar button), not scoped to
        // this plugin's own registered views, so it forwards straight to
        // the host without threading the owner through.
        public bool ToggleClientWindow(PluginClientWindow window) =>
            _inner.ToggleClientWindow(window);

        public bool ShowClientWindow(PluginClientWindow window) =>
            _inner.ShowClientWindow(window);

        public bool HideClientWindow(PluginClientWindow window) =>
            _inner.HideClientWindow(window);

        public bool IsClientWindowVisible(PluginClientWindow window) =>
            _inner.IsClientWindowVisible(window);

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
