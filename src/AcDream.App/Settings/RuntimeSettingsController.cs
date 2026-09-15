using AcDream.Core.Audio;
using AcDream.Core.Net.Messages;
using AcDream.UI.Abstractions.Panels.Settings;
using AcDream.UI.Abstractions.Settings;

namespace AcDream.App.Settings;

internal interface IRuntimeSettingsStorage
{
    SettingsStore? LayoutStore { get; }

    string Location { get; }

    DisplaySettings LoadDisplay();

    AudioSettings LoadAudio();

    AudioMixerOptions LoadAudioMixer();

    ChatSettings LoadChat();

    CharacterSettings LoadCharacter(string toonKey);

    CameraTurningSettings LoadCameraTurning();

    void SaveDisplay(DisplaySettings display);

    void SaveAudio(AudioSettings audio);

    void SaveAudioMixer(AudioMixerOptions mixer);

    void SaveChat(ChatSettings chat);

    void SaveCameraTurning(CameraTurningSettings cameraTurning);
}

internal sealed class JsonRuntimeSettingsStorage : IRuntimeSettingsStorage
{
    private readonly SettingsStore _store;

    public JsonRuntimeSettingsStorage(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Location = path;
        _store = new SettingsStore(path);
    }

    public SettingsStore LayoutStore => _store;

    public string Location { get; }

    public DisplaySettings LoadDisplay() => _store.LoadDisplay();

    public AudioSettings LoadAudio() => _store.LoadAudio();

    public AudioMixerOptions LoadAudioMixer() => _store.LoadAudioMixer();

    public ChatSettings LoadChat() => _store.LoadChat();

    public CharacterSettings LoadCharacter(string toonKey) =>
        _store.LoadCharacter(toonKey);

    public CameraTurningSettings LoadCameraTurning() => _store.LoadCameraTurning();

    public void SaveDisplay(DisplaySettings display) => _store.SaveDisplay(display);

    public void SaveAudio(AudioSettings audio) => _store.SaveAudio(audio);

    public void SaveAudioMixer(AudioMixerOptions mixer) =>
        _store.SaveAudioMixer(mixer);

    public void SaveChat(ChatSettings chat) => _store.SaveChat(chat);

    public void SaveCameraTurning(CameraTurningSettings cameraTurning) =>
        _store.SaveCameraTurning(cameraTurning);
}

internal sealed record RuntimeSettingsSnapshot(
    DisplaySettings Display,
    AudioSettings Audio,
    ChatSettings Chat,
    CharacterSettings Character,
    QualitySettings Quality);

internal interface IRuntimeSettingsStartupTarget
{
    RuntimeDisplayApplyResult ApplyDisplay(DisplaySettings display);

    void ApplyAudio(AudioSettings audio);
}

internal interface IRuntimeSettingsTargets
{
    RuntimeDisplayApplyResult ApplyDisplayWindowState(DisplaySettings display);

    void ApplyAudio(AudioSettings audio);

    void ApplyQuality(QualitySettings quality);

    /// <summary>
    /// Whether unowned world content (released meshes, emptied atlases,
    /// composite and particle textures) is kept for a revisit. Off while the
    /// world is not drawn, so the memory the UI-only switch promises actually
    /// goes back.
    /// </summary>
    void SetUnownedContentRetained(bool retained)
    {
    }

    void ApplyUiLock(bool locked);

    void SetSingleCharacterOption(uint optionId, bool value);

    void SetChatOpacity(float defaultOpacity, float activeOpacity);

    void ApplyCameraTurning(CameraTurningSettings cameraTurning);
}

internal interface IRuntimeSettingsPreviewSource
{
    bool HasDraftPreview { get; }

    DisplaySettings DisplayPreview { get; }

    AudioSettings AudioPreview { get; }
}

internal sealed class RuntimeSettingsController :
    IRuntimeSettingsPreviewSource
{
    private const string DefaultToonKey = "default";

    private readonly IRuntimeSettingsStorage _storage;
    private readonly Func<QualityPreset, QualitySettings> _resolveQuality;
    private readonly Func<uint, bool>? _characterOptionValue;
    private readonly Action<string> _log;
    private IRuntimeSettingsTargets? _runtimeTargets;
    private CharacterSettings _defaultCharacter;
    private bool _startupDisplayApplied;
    private bool _startupAudioApplied;
    private bool _startupApplied;
    private bool? _lastAppliedUiLocked;
    private SessionDisplayWindowTarget? _sessionDisplay;

    internal bool IsGameplayDisplay => _sessionDisplay?.IsGameplay ?? false;

    internal IRuntimeDisplayWindowTarget? DisplayWindowTarget => _sessionDisplay;

    internal void BindDisplayWindow(IRuntimeDisplayWindowTarget target, bool fixedAutomationViewport = false,
        bool directCharacterLaunch = false)
    {
        if (_sessionDisplay is not null)
            throw new InvalidOperationException("The display window is already bound.");
        _sessionDisplay = new SessionDisplayWindowTarget(target, fixedAutomationViewport, directCharacterLaunch);
    }

    internal void SetGameplayDisplay(bool active) => _sessionDisplay?.SetGameplay(active);

    internal void EndDirectLaunch() => _sessionDisplay?.EndDirectLaunch();

    public RuntimeSettingsController(
        IRuntimeSettingsStorage storage,
        Func<QualityPreset, QualitySettings>? resolveQuality = null,
        Action<string>? log = null,
        Func<uint, bool>? characterOptionValue = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        Display = _storage.LoadDisplay();
        _resolveQuality = resolveQuality
            ?? (preset => ResolveQuality(
                preset,
                EffectiveDisplay.LandscapeDrawDistance,
                EffectiveDisplay.UiOnly));
        _log = log ?? Console.WriteLine;
        _characterOptionValue = characterOptionValue;

        Audio = _storage.LoadAudio();
        AudioMixer = _storage.LoadAudioMixer();
        Chat = _storage.LoadChat();
        _defaultCharacter = _storage.LoadCharacter(DefaultToonKey);
        Character = _defaultCharacter;
        ResolvedQuality = _resolveQuality(EffectiveDisplay.Quality);
        Startup = new RuntimeSettingsSnapshot(
            EffectiveDisplay,
            Audio,
            Chat,
            Character,
            ResolvedQuality);
    }

    public RuntimeSettingsSnapshot Startup { get; }

    public SettingsStore? LayoutStore => _storage.LayoutStore;

    public string ActiveToonKey { get; private set; } = DefaultToonKey;

    private DisplaySettings _display = null!;
    private DisplaySettings _effectiveDisplay = null!;
    private bool _windowFocused = true;

    public DisplaySettings Display
    {
        get => _display;
        private set
        {
            _display = value;
            RecomputeEffectiveDisplay();
        }
    }

    /// <summary>True while the window has keyboard focus; a background client with the option on runs UI-only.</summary>
    public bool WindowFocused => _windowFocused;

    private void RecomputeEffectiveDisplay()
    {
        DisplaySettings effective = _display.Effective;
        if (!_windowFocused && _display.UiOnlyWhenUnfocused && !effective.UiOnly)
            effective = effective with { UiOnly = true };
        _effectiveDisplay = effective;
    }

    /// <summary>
    /// The window gained or lost focus. With "UI Only in Background" on, losing
    /// focus switches the effective display to UI-only (world pass skipped,
    /// window shrunk, nothing unowned kept) and gaining it switches back; the
    /// stored settings are untouched.
    /// </summary>
    public void SetWindowFocused(bool focused)
    {
        if (_windowFocused == focused)
            return;
        _windowFocused = focused;
        bool wasUiOnly = _effectiveDisplay.UiOnly;
        RecomputeEffectiveDisplay();
        if (wasUiOnly == _effectiveDisplay.UiOnly)
            return;
        _log($"[QUALITY] Window {(focused ? "focused" : "in background")}: UI-only {(_effectiveDisplay.UiOnly ? "on" : "off")}");
        ReapplyQualityPreset(_effectiveDisplay.Quality);
        _runtimeTargets?.SetUnownedContentRetained(!_effectiveDisplay.UiOnly);
    }

    /// <summary>
    /// What the runtime runs with: <see cref="Display"/> with Potato Mode
    /// applied while it is on. The stored settings stay the user's own, so
    /// the Options panel binds to <see cref="Display"/> and the renderer,
    /// streaming, particles and render pack read this.
    /// </summary>
    public DisplaySettings EffectiveDisplay => _effectiveDisplay;

    public AudioSettings Audio { get; private set; }

    /// <summary>
    /// The mixer settings: how many sounds can be audible at once and what
    /// happens when they all are. Client settings, not game options.
    /// </summary>
    public AudioMixerOptions AudioMixer { get; private set; }

    public ChatSettings Chat { get; private set; }

    public CharacterSettings Character { get; private set; }

    public QualitySettings ResolvedQuality { get; private set; }

    public event Action<DisplaySettings>? DisplayChanged;

    public bool HasDraftPreview => false;

    public DisplaySettings DisplayPreview => EffectiveDisplay;

    public AudioSettings AudioPreview => Audio;

    public void ApplyStartup(IRuntimeSettingsStartupTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_startupApplied)
            throw new InvalidOperationException("Runtime settings startup was already applied.");

        if (!_startupDisplayApplied)
        {
            RuntimeDisplayApplyResult result = target.ApplyDisplay(Startup.Display);
            // Reconcile the STORED record: the startup snapshot is the Potato
            // overlay when the switch is on, and the reconciled fullscreen flag
            // must never carry the overlay's values into the file.
            DisplaySettings applied = ReconcileDisplayResult(Display, result);
            if (!ReferenceEquals(applied, Display))
            {
                _storage.SaveDisplay(applied);
                Display = applied;
                _log(
                    $"settings: startup fullscreen request reconciled to "
                    + $"native state {applied.Fullscreen}");
            }
            _startupDisplayApplied = true;
        }
        if (!_startupAudioApplied)
        {
            target.ApplyAudio(Startup.Audio);
            _startupAudioApplied = true;
        }
        _startupApplied = true;

        QualitySettings baseQuality = QualitySettings.From(Startup.Display.Quality);
        _log(Startup.Quality.Equals(baseQuality)
            ? $"[QUALITY] Preset {Startup.Display.Quality} -> {Startup.Quality}"
            : $"[QUALITY] Preset {Startup.Display.Quality} overridden by env vars: {Startup.Quality}");
    }

    public void BindRuntimeTargets(IRuntimeSettingsTargets targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (_runtimeTargets is not null)
            throw new InvalidOperationException("Runtime settings targets are already bound.");
        _runtimeTargets = targets;
        targets.SetUnownedContentRetained(!EffectiveDisplay.UiOnly);
        targets.ApplyCameraTurning(LoadCameraTurning());
    }

    public IDisposable BindRuntimeTargetsOwned(IRuntimeSettingsTargets targets)
    {
        BindRuntimeTargets(targets);
        return new RuntimeTargetBinding(this, targets);
    }

    private void UnbindRuntimeTargets(IRuntimeSettingsTargets expected)
    {
        if (ReferenceEquals(_runtimeTargets, expected))
            _runtimeTargets = null;
    }

    public void UnbindRuntimeTargets() => _runtimeTargets = null;

    private sealed class RuntimeTargetBinding : IDisposable
    {
        private RuntimeSettingsController? _owner;
        private readonly IRuntimeSettingsTargets _expected;

        public RuntimeTargetBinding(
            RuntimeSettingsController owner,
            IRuntimeSettingsTargets expected)
        {
            _owner = owner;
            _expected = expected;
        }

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?
                .UnbindRuntimeTargets(_expected);
    }

    public void SetUiLocked(bool locked)
    {
        if (_lastAppliedUiLocked == locked)
            return;

        _runtimeTargets?.ApplyUiLock(locked);
        _lastAppliedUiLocked = locked;
    }

    public void RequestUiLocked(bool locked)
    {
        IRuntimeSettingsTargets? targets = _runtimeTargets;
        if (targets is null || _characterOptionValue is null)
            return;

        targets.SetSingleCharacterOption(
            (uint)CharacterOptionId.LockUI,
            locked);

        // LiveSessionCommandRouter applies RuntimeCharacterOptionsState's
        // local write synchronously before the autosave send. If the route is
        // inactive/displaced, Publish is intentionally dropped and the bit
        // remains unchanged; do not split root presentation from authority.
        if (_characterOptionValue((uint)CharacterOptionId.LockUI) != locked)
            return;

        SetUiLocked(locked);
    }

    public void ToggleFrameRate()
    {
        Display = Display with { ShowFps = !Display.ShowFps };

        try
        {
            _storage.SaveDisplay(Display);
        }
        catch (Exception ex)
        {
            _log($"settings: framerate display save failed: {ex.Message}");
        }
    }

    public CameraTurningSettings LoadCameraTurning() => _storage.LoadCameraTurning();

    public void SaveCameraTurning(CameraTurningSettings cameraTurning)
    {
        ArgumentNullException.ThrowIfNull(cameraTurning);
        try
        {
            _storage.SaveCameraTurning(cameraTurning);
            _runtimeTargets?.ApplyCameraTurning(cameraTurning);
        }
        catch (Exception ex)
        {
            _log($"settings: camera-turning save failed: {ex.Message}");
        }
    }

    public void SetActiveCharacter(string characterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterName);
        ActiveToonKey = characterName;
    }

    public void LoadCharacterContext(string characterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterName);
        ActiveToonKey = characterName;
        Character = _storage.LoadCharacter(characterName);
        _log($"settings: loaded character[{characterName}] preferences");
    }

    public void RestoreDefaultCharacterContext()
    {
        Character = _defaultCharacter;
    }

    public void ResetActiveCharacterKey() => ActiveToonKey = DefaultToonKey;

    public void ReapplyQualityPreset(QualityPreset preset)
    {
        QualitySettings resolved = _resolveQuality(preset);
        _log($"[QUALITY] ReapplyQualityPreset: {preset} -> {resolved}");

        if (resolved.MsaaSamples != ResolvedQuality.MsaaSamples)
        {
            _log(
                $"[QUALITY] MSAA samples change ({ResolvedQuality.MsaaSamples} -> " +
                $"{resolved.MsaaSamples}) requires a restart - skipped for this session.");
        }

        ResolvedQuality = resolved;
        _runtimeTargets?.ApplyQuality(resolved);
    }

    public void SaveDisplay(DisplaySettings display)
    {
        try
        {
            _storage.SaveDisplay(display);
            _log($"settings: display saved to {_storage.Location}");
            RuntimeDisplayApplyResult result = _runtimeTargets is null
                ? new RuntimeDisplayApplyResult(display.Fullscreen)
                : _runtimeTargets.ApplyDisplayWindowState(display);
            DisplaySettings applied = ReconcileDisplayResult(display, result);
            if (!ReferenceEquals(applied, display))
            {
                _storage.SaveDisplay(applied);
                _log(
                    $"settings: fullscreen request reconciled to native state "
                    + $"{applied.Fullscreen}");
            }
            Display = applied;
            ReapplyQualityPreset(applied.Effective.Quality);
            _runtimeTargets?.SetUnownedContentRetained(!EffectiveDisplay.UiOnly);
        }
        catch (Exception ex)
        {
            _log($"settings: display save failed: {ex.Message}");
            return;
        }

        try
        {
            DisplayChanged?.Invoke(EffectiveDisplay);
        }
        catch (Exception ex)
        {
            _log($"settings: display observer failed: {ex.Message}");
        }
    }

    private static DisplaySettings ReconcileDisplayResult(
        DisplaySettings requested,
        RuntimeDisplayApplyResult result) =>
        requested.Fullscreen == result.Fullscreen
            ? requested
            : requested with { Fullscreen = result.Fullscreen };

    public void SaveAudio(AudioSettings audio)
    {
        try
        {
            _storage.SaveAudio(audio);
            Audio = audio;
            _runtimeTargets?.ApplyAudio(audio);
            _log($"settings: audio saved to {_storage.Location}");
        }
        catch (Exception ex)
        {
            _log($"settings: audio save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Write the mixer settings down. Applying them to the running mixer is the
    /// audio engine's job; this owns only what survives the session.
    /// </summary>
    /// <returns>
    /// Whether they were written. A failed save leaves the remembered settings
    /// alone, so the caller must not go on to change the running mixer: it
    /// would then be running settings nothing remembers.
    /// </returns>
    public bool SaveAudioMixer(AudioMixerOptions mixer)
    {
        ArgumentNullException.ThrowIfNull(mixer);
        try
        {
            _storage.SaveAudioMixer(mixer);
            AudioMixer = mixer;
            _log($"settings: audio mixer saved to {_storage.Location}");
            return true;
        }
        catch (Exception ex)
        {
            _log($"settings: audio mixer save failed: {ex.Message}");
            return false;
        }
    }

    public void SaveChat(ChatSettings chat)
    {
        ChatSettings previous = Chat;
        try
        {
            _storage.SaveChat(chat);
            Chat = chat;
            _log($"settings: chat saved to {_storage.Location}");
        }
        catch (Exception ex)
        {
            _log($"settings: chat save failed: {ex.Message}");
            return;
        }

        PublishHearOptionChange(
            previous.HearGeneralChat, chat.HearGeneralChat,
            (uint)CharacterOptionId.ListenToGeneralChat);
        PublishHearOptionChange(
            previous.HearTradeChat, chat.HearTradeChat,
            (uint)CharacterOptionId.ListenToTradeChat);
        PublishHearOptionChange(
            previous.HearLFGChat, chat.HearLFGChat,
            (uint)CharacterOptionId.ListenToLFGChat);
        PublishHearOptionChange(
            previous.HearRoleplayChat, chat.HearRoleplayChat,
            (uint)CharacterOptionId.ListenToRoleplayChat);
        PublishHearOptionChange(
            previous.HearSocietyChat, chat.HearSocietyChat,
            (uint)CharacterOptionId.ListenToSocietyChat);

        _runtimeTargets?.SetChatOpacity(chat.DefaultOpacity, chat.ActiveOpacity);
    }

    private void PublishHearOptionChange(bool previous, bool current, uint optionId)
    {
        if (previous == current)
            return;
        _runtimeTargets?.SetSingleCharacterOption(optionId, current);
    }

    public void SyncChatFromServerOptions(uint options2)
    {
        ChatSettings Reseed(ChatSettings current) => current with
        {
            HearGeneralChat = (options2
                & (uint)PlayerDescriptionParser.CharacterOptions2.HearGeneralChat) != 0u,
            HearTradeChat = (options2
                & (uint)PlayerDescriptionParser.CharacterOptions2.HearTradeChat) != 0u,
            HearLFGChat = (options2
                & (uint)PlayerDescriptionParser.CharacterOptions2.HearLFGChat) != 0u,
            HearRoleplayChat = (options2
                & (uint)PlayerDescriptionParser.CharacterOptions2.HearRoleplayChat) != 0u,
            HearSocietyChat = (options2
                & (uint)PlayerDescriptionParser.CharacterOptions2.HearSocietyChat) != 0u,
        };

        ChatSettings synced = Reseed(Chat);
        if (synced == Chat)
            return;

        Chat = synced;
        try
        {
            _storage.SaveChat(synced);
            _log($"settings: chat synced from server options2=0x{options2:X8}");
        }
        catch (Exception ex)
        {
            _log($"settings: chat sync save failed: {ex.Message}");
        }
    }

    public Action? ServerOptionsSeeded { get; set; }

    public void NotifyServerOptionsSeeded() => ServerOptionsSeeded?.Invoke();

    private static QualitySettings ResolveQuality(
        QualityPreset preset,
        int landscapeDrawDistance,
        bool uiOnly = false)
    {
        QualitySettings quality = ApplyLandscapeDrawDistance(
            QualitySettings.From(preset),
            landscapeDrawDistance);
        return ApplyUiOnly(QualitySettings.WithEnvOverrides(quality), uiOnly);
    }

    /// <summary>The smallest window that still keeps the player's own landblock and its neighbours resident for collision and movement.</summary>
    internal const int UiOnlyStreamingRadius = 1;

    /// <summary>
    /// With the world not drawn, the streaming window shrinks to the 3x3 around
    /// the player: enough for collision, movement and the objects the server
    /// keeps talking about, and nothing rendered is kept for the eye. Turning
    /// the switch off restores the preset's window and the world streams back.
    /// </summary>
    internal static QualitySettings ApplyUiOnly(QualitySettings quality, bool uiOnly) =>
        uiOnly
            ? quality with
            {
                NearRadius = Math.Min(quality.NearRadius, UiOnlyStreamingRadius),
                FarRadius = UiOnlyStreamingRadius,
            }
            : quality;

    internal static QualitySettings ApplyLandscapeDrawDistance(
        QualitySettings quality,
        int landscapeDrawDistance)
    {
        if (landscapeDrawDistance is >= 3 and <= 25)
        {
            quality = quality with
            {
                NearRadius = Math.Min(
                    quality.NearRadius,
                    landscapeDrawDistance),
                FarRadius = landscapeDrawDistance,
            };
        }

        return quality;
    }
}
