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
    private readonly IPluginStatusBoard _statusBoardView;
    private bool _disposed;
    private readonly ScopedWorldLines _worldLines;
    private readonly string? _pluginDirectory;
    private readonly bool _isHotReload;

    internal ScopedPluginHost(
        IPluginHost inner,
        string pluginId,
        string pluginDisplayName,
        string? pluginDirectory = null,
        PluginStatusBoard? statusBoard = null,
        bool isHotReload = false)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _pluginDirectory = pluginDirectory;
        _isHotReload = isHotReload;
        _worldLines = new ScopedWorldLines(inner.WorldLines);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDisplayName);
        _pluginId = pluginId;
        _events = new ScopedEvents(inner.Events);
        _selection = new ScopedSelectionService(inner.Selection);
        _ui = new ScopedUiRegistry(
            inner.Ui,
            new PluginUiOwner(pluginId, pluginDisplayName),
            pluginDirectory);
        _storage = ScopedPluginStorage.ForPlugin(inner.Storage, pluginId);
        _commands = new ScopedPluginCommandRegistry(inner.Commands);
        _lootClassifiers = new ScopedLootClassifierRegistry(
            inner.LootClassifiers,
            pluginId,
            pluginDisplayName);
        _automation = new ScopedAutomationSurface(inner, pluginId);
        _hotkeys = new ScopedHotkeyRegistry(inner.Hotkeys, pluginId);
        _statusBoardView = statusBoard is null
            ? inner.StatusBoard
            : new PluginStatusBoard.Scoped(statusBoard, pluginId);
    }

    public bool HasUi => _inner.HasUi;
    public IPluginLogger Log => _inner.Log;
    public IGameState State => _inner.State;
    public IEvents Events => _events;
    public ISelectionService Selection => _selection;
    public IUiRegistry Ui => _ui;
    public IPluginWorldLines WorldLines => _worldLines;
    public IPluginStorage Storage => _storage;
    public IPluginStorage VtankProfiles => _inner.VtankProfiles;
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
    public IPluginResourceCatalog Resources => _inner.Resources;
    public IPluginMapRegistry Maps => _inner.Maps;
    public IPluginMapResourceCatalog MapResources => _inner.MapResources;
    public IPluginRenderRegistry Rendering => _inner.Rendering;
    public IPluginStatusBoard StatusBoard => _statusBoardView;
    public bool IsHotReload => _isHotReload;
    public string? PluginDirectory => _pluginDirectory;

    /// <summary>
    /// Tells this plugin, and only this plugin, that the character is in the
    /// world: what a plugin started while the character was already there
    /// would otherwise never hear.
    /// </summary>
    internal void RaiseLoginComplete(Action<string, Exception> report) =>
        _events.RaiseLoginCompleteToThisPlugin(report);

    /// <summary>
    /// One plugin's view of the shared storage root: everything it keeps lives
    /// in <c>&lt;id&gt;/files</c>, which is inside the plugin's own folder when the
    /// root is the plugins folder, and which installs, updates and code removal
    /// leave alone.
    /// </summary>
    private sealed class ScopedPluginStorage : IPluginStorage
    {
        private const string FilesFolderName = "files";

        private readonly IPluginStorage _inner;
        private readonly string _scope;

        private ScopedPluginStorage(IPluginStorage inner, string scope)
        {
            _inner = inner;
            _scope = scope;
        }

        /// <summary>The storage of one plugin: its own folder's <c>files</c>.</summary>
        public static ScopedPluginStorage ForPlugin(IPluginStorage inner, string pluginId) =>
            new(inner, Path.Combine(pluginId, FilesFolderName));

        public bool IsAvailable => _inner.IsAvailable;
        public string? RootPath => _inner.RootPath is { } root
            ? Path.Combine(root, _scope)
            : null;
        public string? ReadText(string key) =>
            _inner.ReadText(ScopedKey(key));
        public IReadOnlyList<string> List(string prefix)
        {
            ArgumentNullException.ThrowIfNull(prefix);
            // An empty prefix is the plugin's whole folder, as it is on the
            // storage underneath.
            string scopedPrefix = prefix.Length == 0
                ? _scope
                : ScopedKey(prefix);
            string ownerPrefix = _scope + Path.DirectorySeparatorChar;
            return _inner.List(scopedPrefix)
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
            _inner.WriteText(ScopedKey(key), content);
        public bool Delete(string key) => _inner.Delete(ScopedKey(key));
        public bool EnsureDirectory(string prefix) =>
            _inner.EnsureDirectory(ScopedKey(prefix.TrimEnd('/')));

        // A named scope -- one character's or one world's files -- is a folder
        // inside the plugin's own, so it stays within what the plugin owns.
        public IPluginStorage OpenScope(PluginStorageScope scope) =>
            new ScopedPluginStorage(_inner, Path.Combine(_scope, ValidateKey(scope.Name)));

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
            Path.Combine(_scope, ValidateKey(key));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _events.Dispose();
        _worldLines.Dispose();
        _selection.Dispose();
        _ui.Dispose();
        _commands.Dispose();
        _lootClassifiers.Dispose();
        _automation.Dispose();
        _hotkeys.Dispose();
        // A status line never outlives the plugin that wrote it.
        (_statusBoardView as PluginStatusBoard.Scoped)?.Close();
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
        private IWorldLabelAutomation? _labelSource;
        private IWorldLabelAutomation? _labelScope;
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
        public IAllegianceAutomation Allegiance => Inner.Allegiance;
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
        public IRecallAutomation Recalls => Inner.Recalls;
        public IWorldTimeAutomation WorldTime => Inner.WorldTime;
        public ILoginAutomation Login => Inner.Login;
        public INetworkAutomation Network => Inner.Network;
        public IRecoveryAutomation Recovery => Inner.Recovery;
        public IProjectileAutomation Projectiles => Inner.Projectiles;
        public ICharacterOptionsAutomation CharacterOptions => Inner.CharacterOptions;
        // The same shape as navigation: a host whose labels can tell plugins
        // apart hands this plugin its own set, so the cap is per plugin and
        // the set goes with the plugin when it is disabled.
        public IWorldLabelAutomation Labels
        {
            get
            {
                IWorldLabelAutomation source = Inner.Labels;
                lock (_gate)
                {
                    if (_disposed)
                        return NoOpAutomationSurface.Instance.Labels;
                    if (!ReferenceEquals(_labelSource, source))
                    {
                        (_labelSource as IScopedWorldLabelSource)?.Release(pluginId);
                        _labelSource = source;
                        _labelScope = source is IScopedWorldLabelSource scoped
                            ? scoped.ScopeTo(pluginId)
                            : source;
                    }
                    return _labelScope!;
                }
            }
        }
        public IDungeonMapAutomation DungeonMap => Inner.DungeonMap;
        public ISelectionAutomation Selection => Inner.Selection;
        public ITradeAutomation Trade => Inner.Trade;
        public IVendorAutomation Vendor => Inner.Vendor;

        public void Dispose()
        {
            INavigationAutomation? navigation;
            IWorldLabelAutomation? labels;
            lock (_gate)
            {
                _disposed = true;
                _chatWrapper?.Dispose();
                navigation = _navigationSource;
                _navigationSource = null;
                _navigationScope = null;
                labels = _labelSource;
                _labelSource = null;
                _labelScope = null;
            }
            // The plugin is going: its walk stops, its pauses are dropped,
            // and its labels come down.
            (navigation as IScopedNavigationSource)?.Release(pluginId);
            (labels as IScopedWorldLabelSource)?.Release(pluginId);
        }
    }

    private sealed class ScopedPluginChat(IPluginChat inner)
        : IPluginChat, IDisposable
    {
        private readonly object _gate = new();
        private readonly List<IDisposable> _filters = [];
        private readonly List<IDisposable> _interceptors = [];
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

        public bool IsInputActive => inner.IsInputActive;

        public bool Compose(string text)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return inner.Compose(text);
        }

        public bool Submit(string text)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return inner.Submit(text);
        }

        public IDisposable RegisterInputInterceptor(
            Func<string, PluginChatInputDecision> intercept)
        {
            ArgumentNullException.ThrowIfNull(intercept);
            ObjectDisposedException.ThrowIf(_disposed, this);
            // The cap is checked before the host is touched, so a refused
            // registration leaves nothing behind on the host to revoke.
            lock (_gate)
            {
                if (_interceptors.Count >= IPluginChat.MaximumInputInterceptors)
                {
                    throw new InvalidOperationException(
                        $"This plugin already has {IPluginChat.MaximumInputInterceptors} chat input interceptors installed.");
                }
            }
            IDisposable registration = inner.RegisterInputInterceptor(intercept);
            lock (_gate)
            {
                if (!_disposed
                    && _interceptors.Count < IPluginChat.MaximumInputInterceptors)
                {
                    _interceptors.Add(registration);
                    return new IndividualInterceptor(this, registration);
                }
            }
            registration.Dispose();
            if (_disposed)
                throw new ObjectDisposedException(nameof(ScopedPluginChat));
            throw new InvalidOperationException(
                $"This plugin already has {IPluginChat.MaximumInputInterceptors} chat input interceptors installed.");
        }

        private void RemoveInterceptor(IDisposable registration)
        {
            lock (_gate)
            {
                if (!_interceptors.Remove(registration))
                    return;
            }
            registration.Dispose();
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
            IDisposable[] interceptors;
            Action<PluginChatLinkClicked>[] linkClickedSubscriptions;
            Action<PluginChatMessage>[] subscriptions;
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                filters = _filters.ToArray();
                _filters.Clear();
                interceptors = _interceptors.ToArray();
                _interceptors.Clear();
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

            for (int index = interceptors.Length - 1; index >= 0; index--)
            {
                try { interceptors[index].Dispose(); }
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

        private sealed class IndividualInterceptor(
            ScopedPluginChat owner,
            IDisposable registration) : IDisposable
        {
            private ScopedPluginChat? _owner = owner;

            public void Dispose() => Interlocked.Exchange(ref _owner, null)?
                .RemoveInterceptor(registration);
        }
    }

    /// <summary>
    /// The layers one plugin drew, let go together when the plugin is.
    /// </summary>
    private sealed class ScopedWorldLines(IPluginWorldLines inner) : IPluginWorldLines, IDisposable
    {
        private readonly List<IPluginWorldLineLayer> _layers = [];
        private bool _disposed;

        public IPluginWorldLineLayer? CreateLayer()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            IPluginWorldLineLayer? layer = inner.CreateLayer();
            if (layer is not null)
                _layers.Add(layer);
            return layer;
        }

        public void Dispose()
        {
            _disposed = true;
            foreach (IPluginWorldLineLayer layer in _layers)
                layer.Dispose();
            _layers.Clear();
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
        private readonly List<(Action<double> Handler, Action<double> Guarded)> _tickRegistrations = [];
        private readonly List<Action> _loginCompleteRegistrations = [];
        private readonly List<Action> _logoffRegistrations = [];
        private readonly List<Action<string>> _localPlayerDiedRegistrations = [];
        private readonly List<Action<PluginObjectChange>> _objectChangedRegistrations = [];
        private readonly List<Action<PluginPortalTransition>> _portalTransitionRegistrations = [];
        private readonly List<Action<PluginItemUseCompletion>> _itemUseCompletedRegistrations = [];
        private readonly List<Action<PluginGoToReport>> _navigationChangedRegistrations = [];
        private readonly List<Action<uint>> _containerOpenedRegistrations = [];
        private readonly List<Action<uint>> _containerClosedRegistrations = [];
        private readonly List<Action<PluginConfirmation>> _confirmationRequestedRegistrations = [];
        private readonly List<Action<PluginActivationCompletion>> _activationCompletedRegistrations = [];
        private bool _disposed;

        public event Action<double> Tick
        {
            add
            {
                ArgumentNullException.ThrowIfNull(value);
                // A plugin can be unloaded from inside a tick: a reload runs
                // on the tick thread, and the tick being raised has already
                // taken its list of handlers. The handler the host holds
                // checks that its plugin is still here, so an unloaded
                // plugin is never called by the rest of that same tick.
                Action<double> guarded = elapsed =>
                {
                    if (!Volatile.Read(ref _disposed))
                        value(elapsed);
                };
                try
                {
                    inner.Tick += guarded;
                }
                catch
                {
                    try { inner.Tick -= guarded; }
                    catch { }
                    throw;
                }
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        _tickRegistrations.Add((value, guarded));
                        return;
                    }
                }

                try { inner.Tick -= guarded; }
                catch { }
                throw new ObjectDisposedException(nameof(ScopedEvents));
            }
            remove
            {
                if (value is null)
                    return;
                Action<double>? guarded = null;
                lock (_gate)
                {
                    for (int index = _tickRegistrations.Count - 1; index >= 0; index--)
                    {
                        if (_tickRegistrations[index].Handler != value)
                            continue;
                        guarded = _tickRegistrations[index].Guarded;
                        _tickRegistrations.RemoveAt(index);
                        break;
                    }
                }
                if (guarded is not null)
                    inner.Tick -= guarded;
            }
        }

        /// <summary>
        /// Raises <see cref="LoginComplete"/> to this plugin's own handlers
        /// only, for a plugin started while the character was already in the
        /// world. A handler that throws is named through
        /// <paramref name="report"/> and does not stop the next one.
        /// </summary>
        internal void RaiseLoginCompleteToThisPlugin(Action<string, Exception> report)
        {
            Action[] handlers;
            lock (_gate)
            {
                if (_disposed)
                    return;
                handlers = _loginCompleteRegistrations.ToArray();
            }
            foreach (Action handler in handlers)
            {
                try { handler(); }
                catch (Exception error) { report("LoginComplete", error); }
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

        public event Action<PluginPortalTransition> PortalTransition
        {
            add
            {
                ArgumentNullException.ThrowIfNull(value);
                inner.PortalTransition += value;
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        _portalTransitionRegistrations.Add(value);
                        return;
                    }
                }
                inner.PortalTransition -= value;
                throw new ObjectDisposedException(nameof(ScopedEvents));
            }
            remove
            {
                if (value is null)
                    return;
                inner.PortalTransition -= value;
                lock (_gate)
                    _portalTransitionRegistrations.Remove(value);
            }
        }

        public event Action<PluginItemUseCompletion> ItemUseCompleted
        {
            add
            {
                ArgumentNullException.ThrowIfNull(value);
                inner.ItemUseCompleted += value;
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        _itemUseCompletedRegistrations.Add(value);
                        return;
                    }
                }
                inner.ItemUseCompleted -= value;
                throw new ObjectDisposedException(nameof(ScopedEvents));
            }
            remove
            {
                if (value is null)
                    return;
                inner.ItemUseCompleted -= value;
                lock (_gate)
                    _itemUseCompletedRegistrations.Remove(value);
            }
        }

        public event Action<PluginActivationCompletion> ActivationCompleted
        {
            add
            {
                ArgumentNullException.ThrowIfNull(value);
                inner.ActivationCompleted += value;
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        _activationCompletedRegistrations.Add(value);
                        return;
                    }
                }
                inner.ActivationCompleted -= value;
                throw new ObjectDisposedException(nameof(ScopedEvents));
            }
            remove
            {
                if (value is null)
                    return;
                inner.ActivationCompleted -= value;
                lock (_gate)
                    _activationCompletedRegistrations.Remove(value);
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
            Action<PluginPortalTransition>[] portalTransitionRegistrations;
            Action<PluginItemUseCompletion>[] itemUseCompletedRegistrations;
            Action<PluginGoToReport>[] navigationChangedRegistrations;
            Action<uint>[] containerOpenedRegistrations;
            Action<uint>[] containerClosedRegistrations;
            Action<PluginConfirmation>[] confirmationRequestedRegistrations;
            Action<PluginActivationCompletion>[] activationCompletedRegistrations;
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                registrations = _registrations.ToArray();
                _registrations.Clear();
                tickRegistrations = _tickRegistrations
                    .Select(static registration => registration.Guarded)
                    .ToArray();
                _tickRegistrations.Clear();
                loginCompleteRegistrations = _loginCompleteRegistrations.ToArray();
                _loginCompleteRegistrations.Clear();
                logoffRegistrations = _logoffRegistrations.ToArray();
                _logoffRegistrations.Clear();
                localPlayerDiedRegistrations = _localPlayerDiedRegistrations.ToArray();
                _localPlayerDiedRegistrations.Clear();
                objectChangedRegistrations = _objectChangedRegistrations.ToArray();
                _objectChangedRegistrations.Clear();
                portalTransitionRegistrations = _portalTransitionRegistrations.ToArray();
                _portalTransitionRegistrations.Clear();
                itemUseCompletedRegistrations = _itemUseCompletedRegistrations.ToArray();
                _itemUseCompletedRegistrations.Clear();
                activationCompletedRegistrations =
                    _activationCompletedRegistrations.ToArray();
                _activationCompletedRegistrations.Clear();
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

            for (int index = portalTransitionRegistrations.Length - 1; index >= 0; index--)
            {
                try { inner.PortalTransition -= portalTransitionRegistrations[index]; }
                catch { }
            }

            for (int index = itemUseCompletedRegistrations.Length - 1; index >= 0; index--)
            {
                try { inner.ItemUseCompleted -= itemUseCompletedRegistrations[index]; }
                catch { }
            }

            for (int index = activationCompletedRegistrations.Length - 1; index >= 0; index--)
            {
                try { inner.ActivationCompleted -= activationCompletedRegistrations[index]; }
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
        private readonly IPluginDirectoryUiRegistry? _directoryInner;
        private readonly PluginUiOwner _owner;
        private readonly string? _pluginDirectory;
        private readonly object _gate = new();
        private readonly List<IDisposable> _registrations = [];
        private bool _disposed;

        internal ScopedUiRegistry(
            IUiRegistry inner,
            PluginUiOwner owner,
            string? pluginDirectory)
        {
            _inner = inner as IScopedUiRegistry
                ?? throw new InvalidOperationException(
                    "Plugin hosts must expose an IScopedUiRegistry so UI registrations can be rolled back.");
            _directoryInner = inner as IPluginDirectoryUiRegistry;
            _owner = owner;
            _pluginDirectory = pluginDirectory;
        }

        public void AddMarkupPanel(string markupPath, object binding)
        {
            AddRegistration(RegisterPanelWithInner(
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
            AddRegistration(RegisterPanelWithInner(descriptor, markupPath, binding));
        }

        public IDisposable RegisterPanel(
            PluginPanelDescriptor descriptor,
            string markupPath,
            object binding)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            return TrackRegistration(RegisterPanelWithInner(descriptor, markupPath, binding));
        }

        public IDisposable RegisterPanelContent(
            PluginPanelDescriptor descriptor,
            string markupContent,
            object binding)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            return TrackRegistration(RegisterPanelContentWithInner(descriptor, markupContent, binding));
        }

        private IDisposable RegisterPanelWithInner(
            PluginPanelDescriptor descriptor,
            string markupPath,
            object binding)
        {
            string resolved = ResolveMarkupPath(markupPath);
            return _directoryInner is not null
                ? _directoryInner.RegisterPanel(_owner, _pluginDirectory, descriptor, resolved, binding)
                : _inner.RegisterPanel(_owner, descriptor, resolved, binding);
        }

        /// <summary>
        /// A relative markup path is read from the plugin's own folder, never
        /// from whatever the process's working folder happens to be. The
        /// host loads a plugin's assembly from memory, so a plugin that
        /// builds its path from its assembly's location gets an empty folder
        /// and a relative path; this is where that path belongs.
        /// </summary>
        private string ResolveMarkupPath(string markupPath)
        {
            return _pluginDirectory is not null
                && !string.IsNullOrWhiteSpace(markupPath)
                && !Path.IsPathRooted(markupPath)
                ? Path.GetFullPath(Path.Combine(_pluginDirectory, markupPath))
                : markupPath;
        }

        private IDisposable RegisterPanelContentWithInner(
            PluginPanelDescriptor descriptor,
            string markupContent,
            object binding) => _directoryInner is not null
                ? _directoryInner.RegisterPanelContent(_owner, _pluginDirectory, descriptor, markupContent, binding)
                : _inner.RegisterPanelContent(_owner, descriptor, markupContent, binding);

        public bool ViewExists(string viewName) =>
            _inner.ViewExists(_owner, viewName);

        public bool IsViewVisible(string viewName) =>
            _inner.IsViewVisible(_owner, viewName);

        public bool ShowPanel(string viewName) =>
            _inner.ShowPanel(_owner, viewName);

        public bool HidePanel(string viewName) =>
            _inner.HidePanel(_owner, viewName);

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

        // The plugin's image surface is asked for once and kept; when the
        // host's surface can be disposed it is tracked like any other
        // registration, so the plugin's images go with the plugin.
        private IPluginImages? _images;

        public IPluginImages Images
        {
            get
            {
                lock (_gate)
                {
                    if (_disposed)
                        throw new ObjectDisposedException(nameof(ScopedUiRegistry));
                    if (_images is null)
                    {
                        _images = _inner.ImagesFor(_owner);
                        if (_images is IDisposable disposable)
                            _registrations.Add(disposable);
                    }
                    return _images;
                }
            }
        }

        public IPluginCanvas RegisterCanvas(
            PluginCanvasDescriptor descriptor,
            Action<IPluginPainter> paint)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            ArgumentNullException.ThrowIfNull(paint);
            IPluginCanvas canvas = _inner.RegisterCanvas(_owner, descriptor, paint);
            return new IndividualCanvas(canvas, TrackRegistration(canvas));
        }

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

        /// <summary>
        /// The host's canvas as the plugin holds it: every call forwards, and
        /// disposing it goes through the tracked registration so the plugin's
        /// list and the host agree on what is still mounted.
        /// </summary>
        private sealed class IndividualCanvas(
            IPluginCanvas inner,
            IDisposable registration) : IPluginCanvas
        {
            public string CanvasId => inner.CanvasId;
            public int Width => inner.Width;
            public int Height => inner.Height;
            public bool IsAvailable => inner.IsAvailable;

            public bool IsVisible
            {
                get => inner.IsVisible;
                set => inner.IsVisible = value;
            }

            public PluginCanvasAnchor Anchor
            {
                get => inner.Anchor;
                set => inner.Anchor = value;
            }

            public PluginPoint Offset
            {
                get => inner.Offset;
                set => inner.Offset = value;
            }

            public Action<PluginPointerEvent>? PointerHandler
            {
                get => inner.PointerHandler;
                set => inner.PointerHandler = value;
            }

            public void Invalidate() => inner.Invalidate();

            public void ReleasePointer() => inner.ReleasePointer();

            public void Dispose()
            {
                registration.Dispose();
            }
        }
    }
}
