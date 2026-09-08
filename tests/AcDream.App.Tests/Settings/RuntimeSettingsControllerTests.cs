using AcDream.App.Diagnostics;
using AcDream.App.Net;
using AcDream.App.Rendering;
using AcDream.App.Settings;
using AcDream.Core.Net.Messages;
using AcDream.UI.Abstractions;
using AcDream.UI.Abstractions.Panels.Settings;
using AcDream.UI.Abstractions.Settings;

namespace AcDream.App.Tests.Settings;

public sealed class RuntimeSettingsControllerTests
{
    [Theory]
    [InlineData(3, 3, 3)]
    [InlineData(5, 4, 5)]
    [InlineData(8, 4, 8)]
    [InlineData(12, 4, 12)]
    [InlineData(25, 4, 25)]
    public void LandscapeDrawDistance_IsTheRetailFarRadiusValue(
        int value,
        int expectedNear,
        int expectedFar)
    {
        QualitySettings result = RuntimeSettingsController
            .ApplyLandscapeDrawDistance(
                QualitySettings.From(QualityPreset.High),
                value);

        Assert.Equal(expectedNear, result.NearRadius);
        Assert.Equal(expectedFar, result.FarRadius);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(26)]
    public void InvalidStoredLandscapeDistance_PreservesThePreset(int value)
    {
        QualitySettings original = QualitySettings.From(QualityPreset.High);

        QualitySettings result = RuntimeSettingsController
            .ApplyLandscapeDrawDistance(original, value);

        Assert.Equal(original, result);
    }

    [Fact]
    public void ConstructionLoadsEachBagOnceAndPublishesOneStartupSnapshot()
    {
        var storage = new FakeStorage
        {
            DisplayValue = DisplaySettings.Default with
            {
                VSync = false,
                Quality = QualityPreset.Ultra,
            },
        };
        var resolved = new QualitySettings(7, 18, 8, 16, true, 9);
        int resolveCount = 0;

        var controller = new RuntimeSettingsController(
            storage,
            preset =>
            {
                resolveCount++;
                Assert.Equal(QualityPreset.Ultra, preset);
                return resolved;
            },
            static _ => { });

        Assert.Equal(1, storage.DisplayLoads);
        Assert.Equal(1, storage.AudioLoads);
        Assert.Equal(1, storage.ChatLoads);
        Assert.Equal(1, storage.CharacterLoads);
        Assert.Equal(1, resolveCount);
        Assert.Equal("default", storage.LastLoadedCharacter);
        Assert.Same(storage.DisplayValue, controller.Startup.Display);
        Assert.Same(storage.AudioValue, controller.Startup.Audio);
        Assert.Same(storage.ChatValue, controller.Startup.Chat);
        Assert.Same(storage.DefaultCharacterValue, controller.Startup.Character);
        Assert.Equal(resolved, controller.Startup.Quality);
        Assert.Equal(resolved, controller.ResolvedQuality);
        Assert.Equal("default", controller.ActiveToonKey);
    }

    [Fact]
    public void StartupApplyIsOrderedExactlyOnceAndRuntimeBindingDoesNotReplay()
    {
        var events = new List<string>();
        var controller = CreateController(events: events);
        var startup = new FakeStartupTarget(events);

        controller.ApplyStartup(startup);

        Assert.Equal(["startup-display", "startup-audio"], events);
        Assert.Throws<InvalidOperationException>(() => controller.ApplyStartup(startup));

        events.Clear();
        var runtime = new FakeRuntimeTargets(events);
        controller.BindRuntimeTargets(runtime);

        Assert.Empty(events);
        Assert.Throws<InvalidOperationException>(() =>
            controller.BindRuntimeTargets(new FakeRuntimeTargets(events)));
    }

    [Fact]
    public void StartupRetryResumesAfterLastSuccessfulStage()
    {
        var displayEvents = new List<string>();
        var displayController = CreateController();
        var displayTarget = new FakeStartupTarget(displayEvents)
        {
            RemainingDisplayFailures = 1,
        };

        Assert.Throws<InvalidOperationException>(() =>
            displayController.ApplyStartup(displayTarget));
        displayController.ApplyStartup(displayTarget);

        Assert.Equal(
            ["startup-display", "startup-display", "startup-audio"],
            displayEvents);

        var audioEvents = new List<string>();
        var audioController = CreateController();
        var audioTarget = new FakeStartupTarget(audioEvents)
        {
            RemainingAudioFailures = 1,
        };

        Assert.Throws<InvalidOperationException>(() =>
            audioController.ApplyStartup(audioTarget));
        audioController.ApplyStartup(audioTarget);

        Assert.Equal(
            ["startup-display", "startup-audio", "startup-audio"],
            audioEvents);
    }

    [Fact]
    public void ConcreteStartupTargetAppliesPacingThenWindowThenPersistedFov()
    {
        using var profiler = new FrameProfiler();
        using var pacing = new DisplayFramePacingController(
            uncappedRendering: false,
            profiler,
            new FramePacingController(new FakeClock(), new NullWaiter()));
        var surface = new FakePacingSurface
        {
            VSync = true,
            ActiveMonitorRefreshHz = 144,
        };
        pacing.InitializeStartup(requestedVSync: true);
        pacing.BindSurface(surface);
        var cameras = new CameraController(new OrbitCamera(), new FlyCamera());
        float originalFov = cameras.Orbit.FovY;
        var displayWindow = new InspectingDisplayWindowTarget(display =>
        {
            Assert.False(pacing.RequestedVSync);
            Assert.Equal(originalFov, cameras.Orbit.FovY);
            Assert.Equal("1600x900", display.Resolution);
        });
        var target = new RuntimeSettingsStartupTargets(
            displayWindow,
            pacing,
            cameras,
            audio: null);

        target.ApplyDisplay(DisplaySettings.Default with
        {
            Resolution = "1600x900",
            VSync = false,
            FieldOfView = 83f,
        });

        Assert.Equal(1, displayWindow.ApplyCount);
        Assert.Equal(1, surface.RefreshReadCount);
        Assert.Equal(new FramePacingPolicy(false, 144d), pacing.Policy);
        Assert.Equal(83f * (MathF.PI / 180f), cameras.GameFovRadians, precision: 5);
        Assert.True(RetailFieldOfView.TryAppliedVerticalFov(
            83f * (MathF.PI / 180f), 16f / 9f, out float expectedFov));
        Assert.Equal(expectedFov, cameras.Orbit.FovY, precision: 5);
        Assert.Equal(expectedFov, cameras.Fly.FovY, precision: 5);
    }


    private sealed class FakeSizeSurface : IWindowedSizeSurface
    {
        public Silk.NET.Maths.Vector2D<int> Size { get; set; } = new(1280, 720);
        public int Writes { get; private set; }
        public bool IsMaximized { get; set; }
        public int Restores { get; private set; }

        Silk.NET.Maths.Vector2D<int> IWindowedSizeSurface.Size
        {
            get => Size;
            set { Size = value; Writes++; }
        }

        public void Restore()
        {
            IsMaximized = false;
            Restores++;
        }
    }

    private sealed class FakeModeSwitcher : IDisplayModeSwitcher
    {
        public bool IsFullscreen { get; set; }
        public (int Width, int Height)? CurrentFullscreenMode { get; set; }
        public bool EnterSucceeds { get; set; } = true;
        public List<string> Calls { get; } = [];

        public bool TryEnterFullscreen(int width, int height, out string? error)
        {
            Calls.Add($"enter:{width}x{height}");
            error = EnterSucceeds ? null : "injected failure";
            if (EnterSucceeds)
            {
                IsFullscreen = true;
                CurrentFullscreenMode = (width, height);
            }
            return EnterSucceeds;
        }

        public bool TryLeaveFullscreen(int width, int height, out string? error)
        {
            Calls.Add($"leave:{width}x{height}");
            error = null;
            IsFullscreen = false;
            CurrentFullscreenMode = null;
            return true;
        }
    }

    [Fact]
    public void DisplayApply_SameFullscreenMode_IsANoOp_BeforeAnyNativeWork()
    {
        var surface = new FakeSizeSurface();
        var switcher = new FakeModeSwitcher
        {
            IsFullscreen = true,
            CurrentFullscreenMode = (1920, 1080),
        };
        var target = new SilkRuntimeDisplayWindowTarget(
            surface, switcher, _ => true);

        target.Apply(DisplaySettings.Default with
        {
            Fullscreen = true,
            Resolution = "1920x1080",
        });

        Assert.Empty(switcher.Calls);
        Assert.Equal(0, surface.Writes);
    }

    [Fact]
    public void DisplayApply_UnparseableResolutionWhileFullscreen_RefusesWithoutCalls()
    {
        var surface = new FakeSizeSurface();
        var switcher = new FakeModeSwitcher();
        var target = new SilkRuntimeDisplayWindowTarget(
            surface, switcher, _ => true);

        target.Apply(DisplaySettings.Default with
        {
            Fullscreen = true,
            Resolution = "garbage",
        });

        Assert.Empty(switcher.Calls);
        Assert.Equal(0, surface.Writes);
    }

    [Fact]
    public void DisplayApply_MaximizedWindowedPick_RestoresBeforeTheSizeWrite()
    {
        var surface = new FakeSizeSurface { IsMaximized = true };
        var switcher = new FakeModeSwitcher();
        var target = new SilkRuntimeDisplayWindowTarget(
            surface, switcher, _ => true);

        target.Apply(DisplaySettings.Default with
        {
            Fullscreen = false,
            Resolution = "1600x900",
        });

        Assert.Equal(1, surface.Restores);
        Assert.Equal(1, surface.Writes);
        Assert.False(surface.IsMaximized);
    }

    [Fact]
    public void DisplayApply_FullscreenPick_IsAValidatedModeSwitch_NeverASizeWrite()
    {
        var surface = new FakeSizeSurface();
        var switcher = new FakeModeSwitcher();
        var target = new SilkRuntimeDisplayWindowTarget(
            surface, switcher, spec => spec == "1920x1080");

        target.Apply(DisplaySettings.Default with
        {
            Fullscreen = true,
            Resolution = "1920x1080",
        });

        Assert.Equal(["enter:1920x1080"], switcher.Calls);
        Assert.Equal(0, surface.Writes);
    }

    [Fact]
    public void DisplayApply_UnofferedFullscreenMode_IsRefused_NotAttempted()
    {
        var surface = new FakeSizeSurface();
        var switcher = new FakeModeSwitcher();
        var target = new SilkRuntimeDisplayWindowTarget(
            surface, switcher, _ => false);

        RuntimeDisplayApplyResult result = target.Apply(DisplaySettings.Default with
        {
            Fullscreen = true,
            Resolution = "1234x777",
        });

        Assert.False(result.Fullscreen);
        Assert.Empty(switcher.Calls);
        Assert.Equal(0, surface.Writes);
    }

    [Fact]
    public void DisplayApply_FailedModeSwitch_LeavesTheWindowUsable()
    {
        var surface = new FakeSizeSurface();
        var switcher = new FakeModeSwitcher { EnterSucceeds = false };
        var target = new SilkRuntimeDisplayWindowTarget(
            surface, switcher, _ => true);

        RuntimeDisplayApplyResult result = target.Apply(DisplaySettings.Default with
        {
            Fullscreen = true,
            Resolution = "1920x1080",
        });

        Assert.False(result.Fullscreen);
        Assert.False(switcher.IsFullscreen);
        Assert.Equal(0, surface.Writes);
    }

    [Fact]
    public void DisplayApply_WindowedWhileFullscreen_LeavesViaTheSwitcher()
    {
        var surface = new FakeSizeSurface();
        var switcher = new FakeModeSwitcher { IsFullscreen = true };
        var target = new SilkRuntimeDisplayWindowTarget(
            surface, switcher, _ => true);

        target.Apply(DisplaySettings.Default with
        {
            Fullscreen = false,
            Resolution = "1600x900",
        });

        Assert.Equal(["leave:1600x900"], switcher.Calls);
        Assert.Equal(0, surface.Writes);   // the native exit sets the size itself
    }

    [Fact]
    public void DisplayApply_PlainWindowedPick_IsTheProvenSizeWrite()
    {
        var surface = new FakeSizeSurface();
        var switcher = new FakeModeSwitcher();
        var target = new SilkRuntimeDisplayWindowTarget(
            surface, switcher, _ => true);

        target.Apply(DisplaySettings.Default with
        {
            Fullscreen = false,
            Resolution = "1600x900",
        });

        Assert.Empty(switcher.Calls);
        Assert.Equal(1, surface.Writes);
        Assert.Equal(new Silk.NET.Maths.Vector2D<int>(1600, 900), surface.Size);
    }

    [Fact]
    public void RuntimeTarget_ApplyDisplayWindowState_AppliesFieldOfViewLive()
    {
        var cameras = new CameraController(new OrbitCamera(), new FlyCamera());
        var target = new RuntimeSettingsTargets(
            new InspectingDisplayWindowTarget(static _ => { }),
            new RecordingQualityApplicationTarget([]),
            new RecordingUiLockTarget([]),
            NullCommandBus.Instance,
            static _ => { },
            cameras: cameras);

        target.ApplyDisplayWindowState(
            DisplaySettings.Default with { FieldOfView = 120f });

        Assert.Equal(120f * (MathF.PI / 180f), cameras.GameFovRadians, precision: 5);
        Assert.True(RetailFieldOfView.TryAppliedVerticalFov(
            120f * (MathF.PI / 180f), 16f / 9f, out float expectedFov));
        Assert.Equal(expectedFov, cameras.Orbit.FovY, precision: 5);
    }

    [Fact]
    public void ConcreteRuntimeTargetAppliesEveryQualityDimensionInOrder()
    {
        var events = new List<string>();
        var qualityTarget = new RecordingQualityApplicationTarget(events);
        var displayTarget = new InspectingDisplayWindowTarget(
            _ => events.Add("display"));
        var uiTarget = new RecordingUiLockTarget(events);
        var target = new RuntimeSettingsTargets(
            displayTarget,
            qualityTarget,
            uiTarget,
            NullCommandBus.Instance,
            static _ => { });
        var quality = new QualitySettings(6, 17, 4, 12, true, 7);

        target.ApplyQuality(quality);

        Assert.Equal(
        [
            "a2c:True",
            "aniso:12",
            "range:6:17",
            "stream:6:17",
            "budget:7",
        ],
            events);
        Assert.Equal(quality, qualityTarget.Observed);
    }

    [Fact]
    public void ConcreteRuntimeQualityTargetStopsAtTheThrowingStep()
    {
        string[] allSteps =
        [
            "a2c",
            "aniso",
            "range",
            "stream",
            "budget",
        ];

        for (int failureIndex = 0; failureIndex < allSteps.Length; failureIndex++)
        {
            var events = new List<string>();
            var target = new RuntimeSettingsTargets(
                new InspectingDisplayWindowTarget(static _ => { }),
                new FailingQualityApplicationTarget(events, failureIndex),
                new RecordingUiLockTarget(events),
                NullCommandBus.Instance,
                static _ => { });

            Assert.Throws<InvalidOperationException>(() =>
                target.ApplyQuality(QualitySettings.From(QualityPreset.High)));
            Assert.Equal(allSteps[..(failureIndex + 1)], events);
        }
    }

    [Fact]
    public void ConcreteRuntimeTargetPublishesSetSingleCharacterOptionOntoTheBus()
    {
        var bus = new CaptureCommandBus();
        var target = new RuntimeSettingsTargets(
            new InspectingDisplayWindowTarget(static _ => { }),
            new RecordingQualityApplicationTarget([]),
            new RecordingUiLockTarget([]),
            bus,
            static _ => { });

        target.SetSingleCharacterOption(
            (uint)CharacterOptionId.ListenToRoleplayChat, value: false);

        var cmd = Assert.IsType<SetSingleCharacterOptionRuntimeCmd>(
            Assert.Single(bus.Published));
        Assert.Equal((uint)CharacterOptionId.ListenToRoleplayChat, cmd.OptionId);
        Assert.False(cmd.Value);
    }

    [Fact]
    public void SaveAudioPersistsThenPushesLiveApplyAudioWithTheSavedSnapshot()
    {
        var events = new List<string>();
        var controller = CreateController(events: events);
        var targets = new FakeRuntimeTargets(events);
        controller.BindRuntimeTargets(targets);
        events.Clear();

        AudioSettings updated = AudioSettings.Default with { Sfx = 0.35f };
        controller.SaveAudio(updated);

        Assert.Equal(["save-audio", "target-audio"], events);
        Assert.Equal(updated, Assert.Single(targets.AudioCalls));
        Assert.Equal(updated, controller.Audio);
    }

    [Fact]
    public void SaveAudioSkipsTheLivePushWhenPersistenceFails()
    {
        var events = new List<string>();
        var storage = new FakeStorage(events) { ThrowOnAudioSave = true };
        var controller = CreateController(storage);
        var targets = new FakeRuntimeTargets(events);
        controller.BindRuntimeTargets(targets);
        events.Clear();

        AudioSettings before = controller.Audio;
        controller.SaveAudio(AudioSettings.Default with { Sfx = 0.35f });

        Assert.Equal(["save-audio"], events);
        Assert.Empty(targets.AudioCalls);
        Assert.Equal(before, controller.Audio);
    }

    [Theory]
    [InlineData(true, true, 0.6f, 0.9f, 0.6f, 0.9f)]     // enabled: slider value passes through
    [InlineData(false, false, 0.6f, 0.9f, 0f, 0f)]       // disabled: forced to zero regardless of slider
    [InlineData(true, false, 1.0f, 1.0f, 1.0f, 0f)]
    public void ComputeEffectiveCategoryVolumes_GatesSliderValueOnEnabledFlag(
        bool sfxEnabled, bool ambientEnabled,
        float sfxSlider, float ambientSlider,
        float expectedSfx, float expectedAmbient)
    {
        AudioSettings audio = AudioSettings.Default with
        {
            SfxEnabled = sfxEnabled,
            AmbientEnabled = ambientEnabled,
            Sfx = sfxSlider,
            Ambient = ambientSlider,
        };

        (float sfx, float ambient) = RuntimeSettingsStartupTargets.ComputeEffectiveCategoryVolumes(audio);

        Assert.Equal(expectedSfx, sfx);
        Assert.Equal(expectedAmbient, ambient);
    }

    [Fact]
    public void ComputeEffectiveCategoryVolumes_DefaultProfile_IsAudible_NotMuted()
    {
        (float sfx, float ambient) =
            RuntimeSettingsStartupTargets.ComputeEffectiveCategoryVolumes(AudioSettings.Default);

        Assert.Equal(1.0f, sfx);
        Assert.Equal(1.0f, ambient);
    }

    [Fact]
    public void SaveChatPublishesSetSingleCharacterOptionOnlyForChangedBits()
    {
        var storage = new FakeStorage();
        var controller = new RuntimeSettingsController(
            storage,
            static preset => QualitySettings.From(preset),
            static _ => { });
        var targets = new FakeRuntimeTargets([]);
        controller.BindRuntimeTargets(targets);

        Assert.False(controller.Chat.HearRoleplayChat);
        controller.SaveChat(controller.Chat with { HearRoleplayChat = true });

        Assert.Equal(
            [((uint)CharacterOptionId.ListenToRoleplayChat, true)],
            targets.SingleOptionCalls);

        targets.SingleOptionCalls.Clear();
        controller.SaveChat(controller.Chat with
        {
            HearRoleplayChat = false,
            HearSocietyChat = true,
        });

        Assert.Equal(
        [
            ((uint)CharacterOptionId.ListenToRoleplayChat, false),
            ((uint)CharacterOptionId.ListenToSocietyChat, true),
        ],
            targets.SingleOptionCalls);
    }

    [Fact]
    public void SaveChatWithNoHearOptionChangePublishesNothing()
    {
        var controller = CreateController();
        var targets = new FakeRuntimeTargets([]);
        controller.BindRuntimeTargets(targets);

        controller.SaveChat(controller.Chat with { ShowTimestamps = false });

        Assert.Empty(targets.SingleOptionCalls);
    }

    [Fact]
    public void SaveChat_PushesOpacityToRuntimeTargets_LiveApply_NoRestart()
    {
        var controller = CreateController();
        var targets = new FakeRuntimeTargets([]);
        controller.BindRuntimeTargets(targets);

        controller.SaveChat(controller.Chat with
        {
            DefaultOpacity = 0.3f,
            ActiveOpacity = 0.6f,
        });

        Assert.Equal([(0.3f, 0.6f)], targets.ChatOpacityCalls);
        Assert.Equal(0.3f, controller.Chat.DefaultOpacity);
        Assert.Equal(0.6f, controller.Chat.ActiveOpacity);
    }

    [Fact]
    public void ConcreteRuntimeTargetForwardsChatOpacityToTheLiveController()
    {
        var recording = new RecordingChatOpacityTarget();
        var target = new RuntimeSettingsTargets(
            new InspectingDisplayWindowTarget(static _ => { }),
            new RecordingQualityApplicationTarget([]),
            new RecordingUiLockTarget([]),
            NullCommandBus.Instance,
            log: static _ => { },
            chatOpacity: recording);

        target.SetChatOpacity(0.25f, 0.75f);

        Assert.Equal((0.25f, 0.75f), Assert.Single(recording.Calls));
    }

    private sealed class RecordingChatOpacityTarget : IRuntimeChatOpacityTarget
    {
        public List<(float DefaultOpacity, float ActiveOpacity)> Calls { get; } = [];

        public void Apply(float defaultOpacity, float activeOpacity) =>
            Calls.Add((defaultOpacity, activeOpacity));
    }

    [Fact]
    public void SyncChatFromServerOptionsReseedsPersisted()
    {
        var storage = new FakeStorage
        {
            ChatValue = ChatSettings.Default with
            {
                HearRoleplayChat = true,
                HearSocietyChat = true,
            },
        };
        var controller = new RuntimeSettingsController(
            storage,
            static preset => QualitySettings.From(preset),
            static _ => { });
        Assert.True(controller.Chat.HearRoleplayChat);

        controller.SyncChatFromServerOptions(0x00948700u);

        Assert.True(controller.Chat.HearGeneralChat);
        Assert.True(controller.Chat.HearTradeChat);
        Assert.True(controller.Chat.HearLFGChat);
        Assert.False(controller.Chat.HearRoleplayChat);
        Assert.False(controller.Chat.HearSocietyChat);
        Assert.Same(controller.Chat, storage.ChatValue);
    }

    [Fact]
    public void SyncChatFromServerOptionsIsANoOpWhenUnchanged()
    {
        var storage = new FakeStorage();
        var controller = new RuntimeSettingsController(
            storage,
            static preset => QualitySettings.From(preset),
            static _ => { });
        storage.ClearEvents();

        const uint aceDefaultHearBits =
            (uint)PlayerDescriptionParser.CharacterOptions2.HearGeneralChat
            | (uint)PlayerDescriptionParser.CharacterOptions2.HearTradeChat
            | (uint)PlayerDescriptionParser.CharacterOptions2.HearLFGChat;
        controller.SyncChatFromServerOptions(aceDefaultHearBits);

        Assert.Equal(0, storage.ChatSaves);
    }

    [Fact]
    public void DraftPreviewAlwaysMirrorsCommittedState()
    {
        var controller = CreateController();

        Assert.False(controller.HasDraftPreview);
        Assert.Equal(controller.Display, controller.DisplayPreview);
        Assert.Equal(controller.Audio, controller.AudioPreview);

        controller.SaveDisplay(controller.Display with { FieldOfView = 91f });
        controller.SaveAudio(controller.Audio with { Sfx = 0.33f });

        Assert.False(controller.HasDraftPreview);
        Assert.Equal(91f, controller.DisplayPreview.FieldOfView);
        Assert.Equal(0.33f, controller.AudioPreview.Sfx);
    }

    [Fact]
    public void CharacterContextSwitchesActiveToonAndReloadsSettings()
    {
        var storage = new FakeStorage();
        storage.Characters["Alice"] = CharacterSettings.Default with
        {
            DefaultChatChannel = "Trade",
        };
        storage.Characters["Bob"] = CharacterSettings.Default with
        {
            ConfirmSalvage = false,
        };
        var controller = CreateController(storage);

        controller.SetActiveCharacter("Alice");
        controller.LoadCharacterContext("Alice");

        Assert.Equal("Alice", controller.ActiveToonKey);
        Assert.Equal("Trade", controller.Character.DefaultChatChannel);

        controller.LoadCharacterContext("Bob");

        Assert.Equal("Bob", controller.ActiveToonKey);
        Assert.False(controller.Character.ConfirmSalvage);

        controller.RestoreDefaultCharacterContext();
        controller.ResetActiveCharacterKey();

        Assert.Equal("default", controller.ActiveToonKey);
        Assert.Same(storage.DefaultCharacterValue, controller.Character);
    }

    [Fact]
    public void RuntimeTargetLoansCanBeWithdrawnAndReboundPrecisely()
    {
        var events = new List<string>();
        var controller = CreateController(events: events);

        var first = new FakeRuntimeTargets(events);
        controller.BindRuntimeTargets(first);
        controller.UnbindRuntimeTargets();
        var second = new FakeRuntimeTargets(events);
        controller.BindRuntimeTargets(second);
        controller.SetUiLocked(true);

        Assert.Equal(0, first.UiLockCalls);
        Assert.Equal(1, second.UiLockCalls);
    }

    [Fact]
    public void OwnedRuntimeTargetsReleaseExactlyAndAllowRebind()
    {
        var events = new List<string>();
        RuntimeSettingsController controller = CreateController(events: events);
        var first = new FakeRuntimeTargets(events);
        var second = new FakeRuntimeTargets(events);

        IDisposable firstBinding = controller.BindRuntimeTargetsOwned(first);
        Assert.Throws<InvalidOperationException>(() =>
            controller.BindRuntimeTargetsOwned(second));

        firstBinding.Dispose();
        using IDisposable secondBinding = controller.BindRuntimeTargetsOwned(second);
        firstBinding.Dispose();
        controller.SetUiLocked(true);

        Assert.Equal(0, first.UiLockCalls);
        Assert.Equal(1, second.UiLockCalls);
    }

    [Fact]
    public void DisplayPersistenceFailureDoesNotPublishStateOrTargets()
    {
        var events = new List<string>();
        var storage = new FakeStorage(events) { ThrowOnDisplaySave = true };
        var logs = new List<string>();
        var controller = new RuntimeSettingsController(
            storage,
            static preset => QualitySettings.From(preset),
            logs.Add);
        storage.ClearEvents();
        controller.BindRuntimeTargets(new FakeRuntimeTargets(events));
        DisplaySettings original = controller.Display;

        controller.SaveDisplay(controller.Display with
        {
            Resolution = "2560x1440",
            Quality = QualityPreset.Ultra,
        });

        Assert.Same(original, controller.Display);
        Assert.DoesNotContain("target-display", events);
        Assert.DoesNotContain("target-quality", events);
        Assert.Contains(logs, line => line.Contains("display save failed", StringComparison.Ordinal));
    }

    [Fact]
    public void DisplayTargetFailurePreservesEstablishedStoreThenPublishBoundary()
    {
        var events = new List<string>();
        var storage = new FakeStorage(events);
        var logs = new List<string>();
        var controller = new RuntimeSettingsController(
            storage,
            static preset => QualitySettings.From(preset),
            logs.Add);
        storage.ClearEvents();
        controller.BindRuntimeTargets(new FakeRuntimeTargets(events)
        {
            ThrowOnDisplay = true,
        });
        DisplaySettings original = controller.Display;

        controller.SaveDisplay(controller.Display with
        {
            Resolution = "3840x2160",
            Quality = QualityPreset.Ultra,
        });

        Assert.Equal(1, storage.DisplaySaves);
        Assert.Same(original, controller.Display);
        Assert.DoesNotContain("target-quality", events);
        Assert.Contains(logs, line => line.Contains("display save failed", StringComparison.Ordinal));
    }

    [Fact]
    public void RefusedFullscreenRequest_ReconcilesPersistedAndPublishedState()
    {
        var events = new List<string>();
        var storage = new FakeStorage(events);
        var controller = new RuntimeSettingsController(
            storage,
            static preset => QualitySettings.From(preset),
            static _ => { });
        storage.ClearEvents();
        var targets = new FakeRuntimeTargets(events)
        {
            DisplayResult = new RuntimeDisplayApplyResult(Fullscreen: false),
        };
        controller.BindRuntimeTargets(targets);
        var observed = new List<DisplaySettings>();
        controller.DisplayChanged += observed.Add;

        controller.SaveDisplay(controller.Display with
        {
            Fullscreen = true,
            Resolution = "2056x1290",
        });

        Assert.Equal(2, storage.DisplaySaves);
        Assert.False(storage.DisplayValue.Fullscreen);
        Assert.False(controller.Display.Fullscreen);
        Assert.False(Assert.Single(observed).Fullscreen);
        Assert.Equal(
            ["save-display", "target-display", "save-display", "target-quality"],
            events);
    }

    [Fact]
    public void Successful_display_commit_publishes_render_pack_selection_once()
    {
        var storage = new FakeStorage();
        RuntimeSettingsController controller = CreateController(storage);
        var observed = new List<DisplaySettings>();
        controller.DisplayChanged += observed.Add;
        DisplaySettings selected = controller.Display with
        {
            RenderPack = new RenderPackSelectionSettings(
                "acdream.atmospheric",
                "1.0.0",
                "medium"),
        };

        controller.SaveDisplay(selected);

        Assert.Same(selected, controller.Display);
        Assert.Equal([selected], observed);
    }

    [Fact]
    public void Failed_display_commit_does_not_publish_render_pack_selection()
    {
        var storage = new FakeStorage { ThrowOnDisplaySave = true };
        RuntimeSettingsController controller = CreateController(storage);
        int observed = 0;
        controller.DisplayChanged += _ => observed++;

        controller.SaveDisplay(controller.Display with
        {
            RenderPack = new RenderPackSelectionSettings(
                "acdream.atmospheric",
                "1.0.0",
                "high"),
        });

        Assert.Equal(0, observed);
        Assert.True(controller.Display.RenderPack.IsRetail);
    }

    [Fact]
    public void NonDisplayPersistenceFailuresContinueAndPreserveControllerState()
    {
        var storage = new FakeStorage
        {
            ThrowOnAudioSave = true,
            ThrowOnChatSave = true,
        };
        var logs = new List<string>();
        var controller = new RuntimeSettingsController(
            storage,
            static preset => QualitySettings.From(preset),
            logs.Add);
        AudioSettings originalAudio = controller.Audio;
        ChatSettings originalChat = controller.Chat;

        controller.SaveAudio(controller.Audio with { Master = 0.1f });
        controller.SaveChat(controller.Chat with { ShowTimestamps = true });

        Assert.Same(originalAudio, controller.Audio);
        Assert.Same(originalChat, controller.Chat);
        Assert.Contains(logs, line => line.Contains("audio save failed", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("chat save failed", StringComparison.Ordinal));
    }

    [Fact]
    public void MsaaChangeIsRestartRequiredWhileOtherQualityStateAdvances()
    {
        var logs = new List<string>();
        var events = new List<string>();
        var controller = new RuntimeSettingsController(
            new FakeStorage(),
            static preset => QualitySettings.From(preset),
            logs.Add);
        controller.BindRuntimeTargets(new FakeRuntimeTargets(events));

        controller.ReapplyQualityPreset(QualityPreset.Low);

        Assert.Equal(QualitySettings.From(QualityPreset.Low), controller.ResolvedQuality);
        Assert.Equal(["target-quality"], events);
        Assert.Contains(logs, line =>
            line.Contains("MSAA samples change (4 -> 0) requires a restart", StringComparison.Ordinal));
    }

    [Fact]
    public void QualityTargetFailurePublishesResolvedQualityThenPropagates()
    {
        var events = new List<string>();
        var controller = CreateController(events: events);
        controller.BindRuntimeTargets(new FakeRuntimeTargets(events)
        {
            ThrowOnQuality = true,
        });
        QualitySettings requested = QualitySettings.From(QualityPreset.Ultra);

        Assert.Throws<InvalidOperationException>(() =>
            controller.ReapplyQualityPreset(QualityPreset.Ultra));

        Assert.Equal(requested, controller.ResolvedQuality);
        Assert.Equal(["target-quality"], events);
    }

    [Fact]
    public void RequestUiLocked_PublishesAuthoritativeOptionBeforePresentation()
    {
        var events = new List<string>();
        bool authoritativeLock = false;
        var controller = new RuntimeSettingsController(
            new FakeStorage(),
            log: events.Add,
            characterOptionValue: optionId =>
                optionId == (uint)CharacterOptionId.LockUI && authoritativeLock);
        var targets = new FakeRuntimeTargets(events);
        targets.SingleOptionApplied = (optionId, value) =>
        {
            if (optionId == (uint)CharacterOptionId.LockUI)
                authoritativeLock = value;
        };
        controller.BindRuntimeTargets(targets);

        controller.RequestUiLocked(true);

        Assert.Equal(
        [
            $"target-single-option:0x{(uint)CharacterOptionId.LockUI:X}:True",
            "target-ui-lock:True",
        ], events);
        Assert.Equal(
            [((uint)CharacterOptionId.LockUI, true)],
            targets.SingleOptionCalls);
        Assert.Equal(1, targets.UiLockCalls);

        events.Clear();
        controller.RequestUiLocked(false);

        Assert.Equal(
        [
            $"target-single-option:0x{(uint)CharacterOptionId.LockUI:X}:False",
            "target-ui-lock:False",
        ], events);
        Assert.Equal(
        [
            ((uint)CharacterOptionId.LockUI, true),
            ((uint)CharacterOptionId.LockUI, false),
        ], targets.SingleOptionCalls);
        Assert.Equal(2, targets.UiLockCalls);
    }

    [Fact]
    public void RequestUiLocked_InactiveRouteDoesNotSplitPresentation_AndServerSeedDoesNotEcho()
    {
        var events = new List<string>();
        bool authoritativeLock = false;
        var controller = new RuntimeSettingsController(
            new FakeStorage(),
            log: events.Add,
            characterOptionValue: optionId =>
                optionId == (uint)CharacterOptionId.LockUI && authoritativeLock);
        var targets = new FakeRuntimeTargets(events);
        controller.BindRuntimeTargets(targets);

        controller.RequestUiLocked(true);

        Assert.Equal(
            [$"target-single-option:0x{(uint)CharacterOptionId.LockUI:X}:True"],
            events);
        Assert.Equal(0, targets.UiLockCalls);

        authoritativeLock = true;
        events.Clear();
        controller.SetUiLocked(true);

        Assert.Equal(["target-ui-lock:True"], events);
        Assert.Single(targets.SingleOptionCalls);
        Assert.Equal(1, targets.UiLockCalls);
    }

    [Fact]
    public void SetUiLocked_AppliesOnFirstCallThenNoOpsOnRepeatedSameValue()
    {
        var events = new List<string>();
        var controller = CreateController(events: events);
        var targets = new FakeRuntimeTargets(events);
        controller.BindRuntimeTargets(targets);

        controller.SetUiLocked(true);
        Assert.Equal(1, targets.UiLockCalls);

        controller.SetUiLocked(true);
        Assert.Equal(1, targets.UiLockCalls);

        controller.SetUiLocked(false);
        Assert.Equal(2, targets.UiLockCalls);
    }

    [Fact]
    public void UiLockTargetFailureCanRetryTheSameRequestedValue()
    {
        var events = new List<string>();
        var controller = CreateController(events: events);
        var targets = new FakeRuntimeTargets(events)
        {
            RemainingUiLockFailures = 1,
        };
        controller.BindRuntimeTargets(targets);

        Assert.Throws<InvalidOperationException>(() => controller.SetUiLocked(true));
        Assert.Equal(["target-ui-lock:True"], events);

        events.Clear();
        controller.SetUiLocked(true);

        Assert.Equal(["target-ui-lock:True"], events);
        Assert.Equal(2, targets.UiLockCalls);
    }

    [Fact]
    public void UnboundRuntimeTargetsConsumeUiLockCallsSilently()
    {
        var events = new List<string>();
        var controller = CreateController(events: events);
        controller.BindRuntimeTargets(new FakeRuntimeTargets(events));
        controller.UnbindRuntimeTargets();

        controller.SetUiLocked(true);
        controller.ReapplyQualityPreset(QualityPreset.Ultra);

        Assert.Equal(
            QualitySettings.From(QualityPreset.Ultra),
            controller.ResolvedQuality);
        Assert.DoesNotContain(events, value => value.StartsWith("target-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("1920x1080", true, 1920, 1080)]
    [InlineData(" 800x600 ", true, 800, 600)]
    [InlineData("", false, 0, 0)]
    [InlineData("1920", false, 0, 0)]
    [InlineData("0x1080", false, 0, 1080)]
    [InlineData("1920x-1", false, 1920, -1)]
    public void ResolutionParserMatchesWindowTargetPolicy(
        string spec,
        bool expected,
        int expectedWidth,
        int expectedHeight)
    {
        bool parsed = SilkRuntimeDisplayWindowTarget.TryParseResolution(
            spec,
            out int width,
            out int height);

        Assert.Equal(expected, parsed);
        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedHeight, height);
    }

    private static RuntimeSettingsController CreateController(
        FakeStorage? storage = null,
        List<string>? events = null)
    {
        storage ??= new FakeStorage(events);
        return new RuntimeSettingsController(
            storage,
            static preset => QualitySettings.From(preset),
            static _ => { });
    }

    private sealed class FakeStartupTarget(List<string> events)
        : IRuntimeSettingsStartupTarget
    {
        public int RemainingDisplayFailures { get; set; }

        public int RemainingAudioFailures { get; set; }

        public RuntimeDisplayApplyResult ApplyDisplay(DisplaySettings display)
        {
            events.Add("startup-display");

            if (RemainingDisplayFailures > 0)
            {
                RemainingDisplayFailures--;
                throw new InvalidOperationException("display startup failed");
            }
            return new RuntimeDisplayApplyResult(display.Fullscreen);
        }

        public void ApplyAudio(AudioSettings audio)
        {
            events.Add("startup-audio");
            if (RemainingAudioFailures > 0)
            {
                RemainingAudioFailures--;
                throw new InvalidOperationException("audio startup failed");
            }
        }
    }

    private sealed class FakeRuntimeTargets(List<string> events)
        : IRuntimeSettingsTargets
    {
        public bool ThrowOnDisplay { get; init; }

        public bool ThrowOnQuality { get; init; }

        public RuntimeDisplayApplyResult? DisplayResult { get; init; }

        public int RemainingUiLockFailures { get; set; }

        public int UiLockCalls { get; private set; }

        public RuntimeDisplayApplyResult ApplyDisplayWindowState(DisplaySettings display)
        {
            events.Add("target-display");
            if (ThrowOnDisplay)
                throw new InvalidOperationException("display target failed");
            return DisplayResult ?? new RuntimeDisplayApplyResult(display.Fullscreen);
        }

        public void ApplyQuality(QualitySettings quality)
        {
            events.Add("target-quality");
            if (ThrowOnQuality)
                throw new InvalidOperationException("quality target failed");
        }

        public List<AudioSettings> AudioCalls { get; } = [];

        public void ApplyAudio(AudioSettings audio)
        {
            AudioCalls.Add(audio);
            events.Add("target-audio");
        }

        public void ApplyUiLock(bool locked)
        {
            UiLockCalls++;
            events.Add($"target-ui-lock:{locked}");
            if (RemainingUiLockFailures > 0)
            {
                RemainingUiLockFailures--;
                throw new InvalidOperationException("UI-lock target failed");
            }
        }

        public List<(uint OptionId, bool Value)> SingleOptionCalls { get; } = [];

        public Action<uint, bool>? SingleOptionApplied { get; set; }

        public void SetSingleCharacterOption(uint optionId, bool value)
        {
            SingleOptionCalls.Add((optionId, value));
            events.Add($"target-single-option:0x{optionId:X}:{value}");
            SingleOptionApplied?.Invoke(optionId, value);
        }

        public List<(float DefaultOpacity, float ActiveOpacity)> ChatOpacityCalls { get; } = [];

        public void SetChatOpacity(float defaultOpacity, float activeOpacity)
        {
            ChatOpacityCalls.Add((defaultOpacity, activeOpacity));
            events.Add($"target-chat-opacity:{defaultOpacity}:{activeOpacity}");
        }
    }

    private sealed class CaptureCommandBus : ICommandBus
    {
        public readonly List<object> Published = new();

        public void Publish<T>(T command) where T : notnull =>
            Published.Add(command!);
    }

    private sealed class InspectingDisplayWindowTarget(
        Action<DisplaySettings> apply)
        : IRuntimeDisplayWindowTarget
    {
        public int ApplyCount { get; private set; }

        public RuntimeDisplayApplyResult Apply(DisplaySettings display)
        {
            ApplyCount++;
            apply(display);
            return new RuntimeDisplayApplyResult(display.Fullscreen);
        }
    }

    private sealed class RecordingQualityApplicationTarget(List<string> events)
        : IRuntimeQualityApplicationTarget
    {
        private bool _alphaToCoverage;
        private int _anisotropic;
        private int _near;
        private int _far;
        private int _streamNear;
        private int _streamFar;
        private int _budget;

        public QualitySettings Observed => new(
            _near,
            _far,
            4,
            _anisotropic,
            _alphaToCoverage,
            _budget);

        public void SetAlphaToCoverage(bool enabled)
        {
            _alphaToCoverage = enabled;
            events.Add($"a2c:{enabled}");
        }

        public void SetAnisotropic(int level)
        {
            _anisotropic = level;
            events.Add($"aniso:{level}");
        }

        public void PublishRenderRange(int nearRadius, int farRadius)
        {
            _near = nearRadius;
            _far = farRadius;
            events.Add($"range:{nearRadius}:{farRadius}");
        }

        public void ReconfigureStreamingRadii(int nearRadius, int farRadius)
        {
            _streamNear = nearRadius;
            _streamFar = farRadius;
            events.Add($"stream:{nearRadius}:{farRadius}");
        }

        public void SetCompletionBudget(int maxCompletionsPerFrame)
        {
            Assert.Equal(_near, _streamNear);
            Assert.Equal(_far, _streamFar);
            _budget = maxCompletionsPerFrame;
            events.Add($"budget:{maxCompletionsPerFrame}");
        }
    }

    private sealed class FailingQualityApplicationTarget(
        List<string> events,
        int failureIndex)
        : IRuntimeQualityApplicationTarget
    {
        private int _step;

        public void SetAlphaToCoverage(bool enabled) => Record("a2c");

        public void SetAnisotropic(int level) => Record("aniso");

        public void PublishRenderRange(int nearRadius, int farRadius) => Record("range");

        public void ReconfigureStreamingRadii(int nearRadius, int farRadius) =>
            Record("stream");

        public void SetCompletionBudget(int maxCompletionsPerFrame) => Record("budget");

        private void Record(string step)
        {
            events.Add(step);
            if (_step++ == failureIndex)
                throw new InvalidOperationException($"{step} failed");
        }
    }

    private sealed class RecordingUiLockTarget(List<string> events)
        : IRuntimeUiLockTarget
    {
        public void Apply(bool locked) => events.Add($"ui:{locked}");
    }

    private sealed class FakeStorage(List<string>? events = null)
        : IRuntimeSettingsStorage
    {
        private readonly List<string> _events = events ?? [];

        public DisplaySettings DisplayValue { get; set; } = DisplaySettings.Default;

        public AudioSettings AudioValue { get; set; } = AudioSettings.Default;

        public ChatSettings ChatValue { get; set; } = ChatSettings.Default;

        public CharacterSettings DefaultCharacterValue { get; set; } =
            CharacterSettings.Default;

        public CameraTurningSettings CameraTurningValue { get; set; } =
            CameraTurningSettings.Default;

        public Dictionary<string, CharacterSettings> Characters { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public SettingsStore? LayoutStore => null;

        public string Location => "memory://settings";

        public int DisplayLoads { get; private set; }

        public int AudioLoads { get; private set; }

        public int ChatLoads { get; private set; }

        public int CharacterLoads { get; private set; }

        public int DisplaySaves { get; private set; }

        public int ChatSaves { get; private set; }

        public string? LastLoadedCharacter { get; private set; }

        public bool ThrowOnDisplaySave { get; init; }

        public bool ThrowOnAudioSave { get; init; }

        public bool ThrowOnChatSave { get; init; }

        public DisplaySettings LoadDisplay()
        {
            DisplayLoads++;
            return DisplayValue;
        }

        public AudioSettings LoadAudio()
        {
            AudioLoads++;
            return AudioValue;
        }

        public ChatSettings LoadChat()
        {
            ChatLoads++;
            return ChatValue;
        }

        public CharacterSettings LoadCharacter(string toonKey)
        {
            CharacterLoads++;
            LastLoadedCharacter = toonKey;
            return Characters.TryGetValue(toonKey, out CharacterSettings? value)
                ? value
                : DefaultCharacterValue;
        }

        public void SaveDisplay(DisplaySettings display)
        {
            DisplaySaves++;
            _events.Add("save-display");
            if (ThrowOnDisplaySave)
                throw new IOException("display persistence failed");
            DisplayValue = display;
        }

        public void SaveAudio(AudioSettings audio)
        {
            _events.Add("save-audio");
            if (ThrowOnAudioSave)
                throw new IOException("audio persistence failed");
            AudioValue = audio;
        }

        public void SaveChat(ChatSettings chat)
        {
            ChatSaves++;
            _events.Add("save-chat");
            if (ThrowOnChatSave)
                throw new IOException("chat persistence failed");
            ChatValue = chat;
        }

        public CameraTurningSettings LoadCameraTurning() => CameraTurningValue;

        public void SaveCameraTurning(CameraTurningSettings cameraTurning)
        {
            _events.Add("save-camera-turning");
            CameraTurningValue = cameraTurning;
        }

        public void ClearEvents() => _events.Clear();
    }

    private sealed class FakeClock : IFramePacingClock
    {
        public long Frequency => 1_000;

        public long GetTimestamp() => 0;
    }

    private sealed class NullWaiter : IFramePacingWaiter
    {
        public void Wait(long durationTicks, long clockFrequency)
        {
        }
    }

    private sealed class FakePacingSurface : IDisplayFramePacingSurface
    {
        private int? _refreshRate;

        public bool VSync { get; set; }

        public int RefreshReadCount { get; private set; }

        public int? ActiveMonitorRefreshHz
        {
            get => _refreshRate;
            set => _refreshRate = value;
        }

        public bool TryGetActiveMonitorRefreshHz(out int refreshHz)
        {
            RefreshReadCount++;
            refreshHz = _refreshRate.GetValueOrDefault();
            return _refreshRate is > 0;
        }
    }
}
