using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AcDream.App.Audio;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Audio;
using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions.Rendering;
using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.App.Tests.UI.Layout;

public sealed class ConfigOptionsPageControllerTests
{

    [Fact]
    public void SixSectionHeaders_AreDistinct()
    {
        string[] headers =
        [
            "ID_Sound_SoundSection",
            "ID_Camera_CameraSection",
            "ID_Graphics_GraphicsSection",
            "ID_Graphics_TextureSection",
            "ID_Input_InputSection",
            "ID_UI_UISection",
        ];
        Assert.Equal(6, headers.Distinct().Count());
    }

    [Fact]
    public void AudioSettings_Default_MatchesRetailByteVerifiedTrioDefaults()
    {
        AudioSettings d = AudioSettings.Default;
        Assert.Equal(0, d.SoundFeatures);
        Assert.True(d.SfxEnabled);
        Assert.True(d.AmbientEnabled);
        Assert.True(d.InterfaceEnabled);
        Assert.Equal(1.0f, d.InterfaceVolume);
        Assert.True(d.PlaySoundOnlyWhenActive);
        Assert.Equal(1.0f, d.Sfx);
        Assert.Equal(1.0f, d.Ambient);
    }

    [Fact]
    public void CameraTurningSettings_Default_MatchesRetailByteVerifiedConfigDefaults()
    {
        CameraTurningSettings d = CameraTurningSettings.Default;
        Assert.Equal(0.45f, d.Stiffness);
        Assert.Equal(40.0f, d.AdjustmentSpeed);
        Assert.Equal(0.55f, d.MouseLookSensitivity);
        Assert.True(d.AlignToSlope);
        Assert.False(d.InvertMouseLookYAxis);
        Assert.False(d.UseMouseTurning);
    }

    [Fact]
    public void DisplaySettings_Default_MatchesRetailByteVerifiedConfigDefaults()
    {
        DisplaySettings d = DisplaySettings.Default;
        // OP6 rework (review S2): ScreenBrightness is its own field now.
        Assert.Equal(0f, d.ScreenBrightness);
        Assert.False(d.AutomaticDegrades);
        Assert.Equal(0f, d.GraphicsPerformance);
        Assert.Equal(50f, d.DegradeDistance);
        Assert.Equal(2, d.LandscapeTextureDetail);
        Assert.Equal(1, d.EnvironmentTextureDetail);
        Assert.Equal(1, d.TextureFiltering);
        Assert.Equal(8, d.LandscapeDrawDistance);
        Assert.True(d.BuildingDetailTextures);
        Assert.False(d.MultiPassAlpha);
    }

    [Fact]
    public void ChatSettings_Default_MatchesRetailByteVerifiedConfigDefaults()
    {
        ChatSettings d = ChatSettings.Default;
        Assert.Equal(2, d.ChatFontFace);
        Assert.Equal(1, d.ChatFontSizeIndex);
    }


    [Fact]
    public void SettingsStore_AudioRoundTrip_PreservesTheSixOP6Fields()
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"acdream-op6-audio-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            var written = AudioSettings.Default with
            {
                SoundFeatures = 1,
                SfxEnabled = false,
                AmbientEnabled = false,
                InterfaceEnabled = false,
                InterfaceVolume = 0.4f,
                PlaySoundOnlyWhenActive = false,
            };
            store.SaveAudio(written);
            AudioSettings read = store.LoadAudio();
            Assert.Equal(written, read);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void SettingsStore_ExistingProfileMissingTheEnabledKeys_LoadsWithSoundOn()
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"acdream-op6-audio-legacy-{Guid.NewGuid():N}.json");
        try
        {
            System.IO.File.WriteAllText(path, """
                {
                  "version": 2,
                  "display": { "resolution": "1920x1080" }
                }
                """);
            var store = new SettingsStore(path);
            AudioSettings read = store.LoadAudio();

            Assert.True(read.SfxEnabled);
            Assert.True(read.AmbientEnabled);
            Assert.True(read.InterfaceEnabled);
            Assert.Equal(1.0f, read.Sfx);
            Assert.Equal(1.0f, read.Ambient);

            (float sfx, float ambient) =
                AcDream.App.Settings.RuntimeSettingsStartupTargets.ComputeEffectiveCategoryVolumes(read);
            Assert.Equal(1.0f, sfx);
            Assert.Equal(1.0f, ambient);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void SettingsStore_DisplayRoundTrip_PreservesTheTenOP6Fields()
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"acdream-op6-display-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            var written = DisplaySettings.Default with
            {
                // OP6 rework (review S2): ScreenBrightness's own field.
                ScreenBrightness = 0.25f,
                AutomaticDegrades = true,
                GraphicsPerformance = 0.5f,
                DegradeDistance = 75f,
                LandscapeTextureDetail = 4,
                EnvironmentTextureDetail = 3,
                TextureFiltering = 2,
                LandscapeDrawDistance = 3,
                BuildingDetailTextures = false,
                MultiPassAlpha = true,
            };
            store.SaveDisplay(written);
            DisplaySettings read = store.LoadDisplay();
            Assert.Equal(written, read);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void SettingsStore_CameraTurningRoundTrip_PreservesUseMouseTurning()
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"acdream-op6-camera-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            var written = CameraTurningSettings.Default with { UseMouseTurning = true };
            store.SaveCameraTurning(written);
            CameraTurningSettings read = store.LoadCameraTurning();
            Assert.Equal(written, read);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void SettingsStore_ChatRoundTrip_PreservesFontFaceAndSize()
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"acdream-op6-chat-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            var written = ChatSettings.Default with { ChatFontFace = 0, ChatFontSizeIndex = 3 };
            store.SaveChat(written);
            ChatSettings read = store.LoadChat();
            Assert.Equal(written.ChatFontFace, read.ChatFontFace);
            Assert.Equal(written.ChatFontSizeIndex, read.ChatFontSizeIndex);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }


    private static (uint, int, int) NoTex(uint _) => (0, 0, 0);

    private static ElementInfo? Find(ElementInfo n, uint id)
    {
        if (n.Id == id) return n;
        foreach (ElementInfo c in n.Children)
        {
            ElementInfo? f = Find(c, id);
            if (f is not null) return f;
        }
        return null;
    }

    private static Func<uint, uint, UiElement?> MakeTemplateResolver()
    {
        ElementInfo panelRoot = FixtureLoader.LoadOptionsPanelInfos();
        return (layoutId, elementId) =>
        {
            if (layoutId != 0x2100002Bu) return null;
            ElementInfo? templateInfo = Find(panelRoot, elementId);
            return templateInfo is null ? null : LayoutImporter.Build(templateInfo, NoTex, null).Root;
        };
    }

    private sealed class FakeBindings
    {
        public DisplaySettings Display = DisplaySettings.Default;
        public AudioSettings Audio = AudioSettings.Default;
        public CameraTurningSettings CameraTurning = CameraTurningSettings.Default;
        public ChatSettings Chat = ChatSettings.Default;

        public List<DisplaySettings> DisplaySaves { get; } = new();
        public List<AudioSettings> AudioSaves { get; } = new();
        public List<CameraTurningSettings> CameraTurningSaves { get; } = new();
        public List<ChatSettings> ChatSaves { get; } = new();

        public ConfigOptionsPageController.Bindings ToBindings(
            ConfigOptionsPageController.RenderPackBindings? renderPacks = null,
            ConfigOptionsPageController.AudioMixerBindings? audioMixer = null) => new(
            LoadDisplay: () => Display,
            SaveDisplay: value => { Display = value; DisplaySaves.Add(value); },
            LoadAudio: () => Audio,
            SaveAudio: value => { Audio = value; AudioSaves.Add(value); },
            LoadCameraTurning: () => CameraTurning,
            SaveCameraTurning: value => { CameraTurning = value; CameraTurningSaves.Add(value); },
            LoadChat: () => Chat,
            SaveChat: value => { Chat = value; ChatSaves.Add(value); })
        {
            RenderPacks = renderPacks,
            AudioMixer = audioMixer,
        };
    }

    private static (OptionsPanelController Panel, FakeBindings Bindings, bool Bound) BindReal(
        Func<uint, uint, string?>? resolveString = null)
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        OptionsPanelController controller = OptionsPanelController.Bind(
            layout,
            new OptionsPanelController.Callbacks(
                Toggle: () => { },
                RequestExitToCharacterSelection: () => { },
                ExitGame: () => { },
                UseMouseTurningSettings: () => { },
                DisplaySystemMessage: _ => { }))!;

        var fakeBindings = new FakeBindings();
        bool bound = ConfigOptionsPageController.Bind(
            layout,
            controller.ConfigPage,
            MakeTemplateResolver(),
            resolveString ?? ((_, _) => null),
            fakeBindings.ToBindings());

        return (controller, fakeBindings, bound);
    }

    private static List<UiMenu> CollectMenus(UiElement root)
    {
        var found = new List<UiMenu>();
        Walk(root, found);
        return found;

        static void Walk(UiElement node, List<UiMenu> acc)
        {
            if (node is UiMenu menu) acc.Add(menu);
            foreach (UiElement child in node.Children) Walk(child, acc);
        }
    }

    [Fact]
    public void Bind_Succeeds_AndRegistersExactly30Rows()
    {
        (OptionsPanelController controller, _, bool bound) = BindReal();

        Assert.True(bound);
        Assert.Equal(30, controller.ConfigPage.Rows.Count);
    }

    [Fact]
    public void Bind_RowTypeSequence_MatchesAuthoredSectionOrder()
    {
        (OptionsPanelController controller, _, bool bound) = BindReal();
        Assert.True(bound);

        Type[] expected =
        [
            typeof(IntOptionRow),                              // Sound Features menu
            typeof(BoolOptionRow), typeof(FloatOptionRow),      // Sound trio
            typeof(BoolOptionRow), typeof(FloatOptionRow),      // Ambient trio
            typeof(BoolOptionRow), typeof(FloatOptionRow),      // Interface trio
            typeof(BoolOptionRow),                              // Play sound only when active

            typeof(FloatOptionRow),
            typeof(FloatOptionRow),
            typeof(FloatOptionRow),                             // Field of View
            typeof(BoolOptionRow),                              // Align To Slope

            typeof(StringOptionRow),                            // Resolution
            typeof(BoolOptionRow),                              // Full Screen
            typeof(BoolOptionRow),                              // Sync To Refresh
            typeof(FloatOptionRow),                             // Screen Brightness
            typeof(BoolOptionRow),                              // Automatic Degrades
            typeof(FloatOptionRow),                             // Graphics Performance
            typeof(FloatOptionRow),                             // Degrade Distance

            typeof(IntOptionRow),                               // Landscape Texture Detail
            typeof(IntOptionRow),                               // Environment Texture Detail
            typeof(IntOptionRow),                               // Texture Filtering
            typeof(IntOptionRow),                               // Landscape Draw Distance
            typeof(BoolOptionRow),                              // Building Detail Textures
            typeof(BoolOptionRow),                              // Multi-Pass Alpha

            typeof(FloatOptionRow),                             // Mouse Look Sensitivity
            typeof(BoolOptionRow),                              // Invert Mouselook Y Axis
            typeof(BoolOptionRow),                              // Use Mouse Turning

            typeof(IntOptionRow),
            typeof(IntOptionRow),
        ];

        Type[] actual = controller.ConfigPage.Rows.Select(r => r.GetType()).ToArray();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Bind_ListBoxStacks39Items_SixHeaders_SixSeparators_27OptionRows()
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        OptionsPanelController controller = OptionsPanelController.Bind(
            layout,
            new OptionsPanelController.Callbacks(
                Toggle: () => { },
                RequestExitToCharacterSelection: () => { },
                ExitGame: () => { },
                UseMouseTurningSettings: () => { },
                DisplaySystemMessage: _ => { }))!;
        var fakeBindings = new FakeBindings();
        bool bound = ConfigOptionsPageController.Bind(
            layout, controller.ConfigPage, MakeTemplateResolver(), (_, _) => null,
            fakeBindings.ToBindings());
        Assert.True(bound);

        var configSlot = UiElement.FindDescendant(controller.TabPanel, ConfigPageSlotId)!;
        var listBox = Assert.IsType<UiTemplateListBox>(
            UiElement.FindDescendant(configSlot, ConfigOptionsPageController.ListBoxElementId));

        UiElement viewport = Assert.Single(listBox.Children);
        Assert.Equal(39, viewport.Children.Count);
    }

    [Fact]
    public void OptInRenderPackBindings_append_two_menus_without_changing_retail_only_fixture()
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        OptionsPanelController controller = OptionsPanelController.Bind(
            layout,
            new OptionsPanelController.Callbacks(
                Toggle: () => { },
                RequestExitToCharacterSelection: () => { },
                ExitGame: () => { },
                UseMouseTurningSettings: () => { },
                DisplaySystemMessage: _ => { }))!;
        var fake = new FakeBindings();
        string? activationFailure = "Last activation failed: shader interface mismatch.";
        var renderPacks = new ConfigOptionsPageController.RenderPackBindings(() =>
        [
            new ConfigOptionsPageController.RenderPackChoice(
                "acdream.atmospheric",
                "Atmospheric Rendering",
                "1.0.0",
                true,
                null,
                [
                    new ConfigOptionsPageController.RenderPackPresetChoice(
                        "low", "Low", true, null)
                    {
                        MaxResidentGpuBytes = 64L * 1024 * 1024,
                        MaxIncrementalGpuMillisecondsP50 = 2.0,
                        MaxIncrementalGpuMillisecondsP99 = 3.0,
                        MaxIncrementalCpuMillisecondsP50 = 0.15,
                        MaxIncrementalCpuMillisecondsP99 = 0.50,
                    },
                    new("medium", "Medium", true, null),
                    new("high", "High", false, "High needs more GPU memory."),
                ])
            {
                FeatureSummary = "Filmic atmosphere and moving-sun shadows.",
            },
            new ConfigOptionsPageController.RenderPackChoice(
                "test.unsupported",
                "Unsupported Test Pack",
                "2.0.0",
                false,
                "Directional depth sampling is unavailable.",
                [new("low", "Low", false, "Directional depth sampling is unavailable.")]),
        ])
        {
            LoadFailureNotice = () => activationFailure,
        };

        bool bound = ConfigOptionsPageController.Bind(
            layout,
            controller.ConfigPage,
            MakeTemplateResolver(),
            (_, _) => null,
            fake.ToBindings(renderPacks));

        Assert.True(bound);
        var configSlot = UiElement.FindDescendant(controller.TabPanel, ConfigPageSlotId)!;
        var listBox = Assert.IsType<UiTemplateListBox>(
            UiElement.FindDescendant(configSlot, ConfigOptionsPageController.ListBoxElementId));
        Assert.Equal(43, Assert.Single(listBox.Children).Children.Count);
        Assert.Equal(32, controller.ConfigPage.Rows.Count);

        List<UiMenu> menus = CollectMenus(configSlot);
        UiMenu packMenu = menus[^2];
        UiMenu presetMenu = menus[^1];
        Assert.Equal("acdream default (retail-faithful)", packMenu.Items[0].Label);
        Assert.Contains(packMenu.Items, value => Equals(value.Payload, "acdream.atmospheric"));
        Assert.False(packMenu.EnabledProvider!("test.unsupported"));
        Assert.Equal(
            "Last activation failed: shader interface mismatch.",
            packMenu.GetTooltipText()!.Split(Environment.NewLine)[0]);
        packMenu.OnSelect!("test.unsupported");
        Assert.True(fake.Display.RenderPack.IsRetail);

        packMenu.OnSelect!("acdream.atmospheric");
        Assert.Equal("acdream.atmospheric", fake.Display.RenderPack.PackId);
        Assert.Equal("1.0.0", fake.Display.RenderPack.PackVersion);
        Assert.Equal("low", fake.Display.RenderPack.PresetId);
        activationFailure = null;
        Assert.Equal(
            "Filmic atmosphere and moving-sun shadows.",
            packMenu.GetTooltipText());
        presetMenu = CollectMenus(configSlot)[^1];
        Assert.Equal(3, presetMenu.Items.Count);
        Assert.False(presetMenu.EnabledProvider!("high"));
        Assert.Contains(
            "GPU p50/p99 ≤ 2/3 ms",
            presetMenu.GetTooltipText(),
            StringComparison.Ordinal);
        Assert.Contains("pack VRAM ≤ 64 MiB", presetMenu.GetTooltipText(), StringComparison.Ordinal);

        presetMenu.OnSelect!("medium");
        Assert.Equal("medium", fake.Display.RenderPack.PresetId);
        Assert.True(fake.DisplaySaves.Count >= 2);
    }

    [Fact]
    public void RenderPackMenu_revision_refresh_removes_withdrawn_schema_and_discovers_reregistration()
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        OptionsPanelController controller = OptionsPanelController.Bind(
            layout,
            new OptionsPanelController.Callbacks(
                Toggle: () => { },
                RequestExitToCharacterSelection: () => { },
                ExitGame: () => { },
                UseMouseTurningSettings: () => { },
                DisplaySystemMessage: _ => { }))!;
        var alpha = new ConfigOptionsPageController.RenderPackChoice(
            "pack.alpha", "Alpha", "1.0.0", true, null,
            [new("low", "Low", true, null)])
        {
            Settings =
            [
                new RenderSettingDeclaration(
                    "alpha-toggle", "Alpha toggle", RenderSettingKind.Boolean,
                    "true", null, null, null, []),
            ],
        };
        var beta = new ConfigOptionsPageController.RenderPackChoice(
            "pack.beta", "Beta", "2.0.0", true, null,
            [new("medium", "Medium", true, null)]);
        IReadOnlyList<ConfigOptionsPageController.RenderPackChoice> discovered = [alpha];
        long revision = 1;
        var fake = new FakeBindings
        {
            Display = DisplaySettings.Default with
            {
                RenderPack = new RenderPackSelectionSettings(
                    "pack.alpha", "1.0.0", "low"),
            },
        };
        var renderPacks = new ConfigOptionsPageController.RenderPackBindings(
            () => discovered)
        {
            LoadRevision = () => revision,
        };

        Assert.True(ConfigOptionsPageController.Bind(
            layout,
            controller.ConfigPage,
            MakeTemplateResolver(),
            (_, _) => null,
            fake.ToBindings(renderPacks)));

        var configSlot = UiElement.FindDescendant(controller.TabPanel, ConfigPageSlotId)!;
        var listBox = Assert.IsType<UiTemplateListBox>(
            UiElement.FindDescendant(configSlot, ConfigOptionsPageController.ListBoxElementId));
        UiMenu packMenu = CollectMenus(configSlot)[^2];
        Assert.Equal(44, listBox.ItemCount);
        Assert.Contains(packMenu.Items, item => Equals(item.Payload, "pack.alpha"));

        discovered = [beta];
        revision++;
        packMenu.BeforeOpen!();

        Assert.DoesNotContain(packMenu.Items, item => Equals(item.Payload, "pack.alpha"));
        Assert.Contains(packMenu.Items, item => Equals(item.Payload, "pack.beta"));
        Assert.Equal(RenderPackSelectionSettings.RetailPackId, packMenu.Selected);
        Assert.Equal(43, listBox.ItemCount);
        Assert.Equal(32, controller.ConfigPage.Rows.Count);
        controller.ConfigPage.Reset();
        Assert.Equal(RenderPackSelectionSettings.RetailPackId, packMenu.Selected);
        Assert.DoesNotContain(packMenu.Items, item => Equals(item.Payload, "pack.alpha"));

        fake.Display = fake.Display with
        {
            RenderPack = new RenderPackSelectionSettings(
                "pack.beta", "2.0.0", "medium"),
        };
        revision++;
        packMenu.BeforeOpen!();

        Assert.Equal("pack.beta", packMenu.Selected);
        Assert.Equal(43, listBox.ItemCount);
        Assert.Equal("Medium", CollectMenus(configSlot)[^1].Items.Single().Label);
    }

    [Fact]
    public void RenderPackSettings_live_schema_swap_replaces_only_the_optional_tail()
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        OptionsPanelController controller = OptionsPanelController.Bind(
            layout,
            new OptionsPanelController.Callbacks(
                Toggle: () => { },
                RequestExitToCharacterSelection: () => { },
                ExitGame: () => { },
                UseMouseTurningSettings: () => { },
                DisplaySystemMessage: _ => { }))!;
        var fake = new FakeBindings
        {
            Display = DisplaySettings.Default with
            {
                RenderPack = new RenderPackSelectionSettings("pack.alpha", "1.0.0", "low"),
            },
        };
        RenderSettingDeclaration[] alphaSettings =
        [
            new("enabled", "Enabled", RenderSettingKind.Boolean, "false", null, null, null, []),
            new("strength", "Strength", RenderSettingKind.Float, "0.5", 0, 1, 0.25, []),
            new("samples", "Samples", RenderSettingKind.Integer, "2", 0, 10, 2, []),
            new("mode", "Mode", RenderSettingKind.Choice, "low", null, null, null,
                ["low", "high"]),
        ];
        var alpha = new ConfigOptionsPageController.RenderPackChoice(
            "pack.alpha", "Alpha", "1.0.0", true, null,
            [
                new ConfigOptionsPageController.RenderPackPresetChoice(
                    "low", "Low", true, null)
                {
                    SettingOverrides =
                    [
                        new RenderQualitySettingOverride("strength", "0.75"),
                    ],
                },
                new ConfigOptionsPageController.RenderPackPresetChoice(
                    "high", "High", true, null),
            ])
        {
            Settings = alphaSettings,
        };
        var beta = new ConfigOptionsPageController.RenderPackChoice(
            "pack.beta", "Beta", "2.0.0", true, null,
            [new("default", "Default", true, null)])
        {
            Settings =
            [
                new RenderSettingDeclaration(
                    "beta-enabled", "Beta enabled", RenderSettingKind.Boolean,
                    "true", null, null, null, []),
            ],
        };
        var renderPacks = new ConfigOptionsPageController.RenderPackBindings(() =>
            [alpha, beta]);

        Assert.True(ConfigOptionsPageController.Bind(
            layout,
            controller.ConfigPage,
            MakeTemplateResolver(),
            (_, _) => null,
            fake.ToBindings(renderPacks)));

        var configSlot = UiElement.FindDescendant(controller.TabPanel, ConfigPageSlotId)!;
        var listBox = Assert.IsType<UiTemplateListBox>(
            UiElement.FindDescendant(configSlot, ConfigOptionsPageController.ListBoxElementId));
        Assert.Equal(47, listBox.ItemCount);
        Assert.Equal(36, controller.ConfigPage.Rows.Count);

        IReadOnlyList<UiElement> items = listBox.ViewportForTest!.Children;
        var enabled = Assert.IsType<UiButton>(
            UiElement.FindDescendant(items[42], 0x10000219u));
        var strength = Assert.IsType<UiScrollbar>(
            UiElement.FindDescendant(items[43], 0x1000021Cu));
        var samples = Assert.IsType<UiScrollbar>(
            UiElement.FindDescendant(items[44], 0x1000021Cu));
        List<UiMenu> menus = CollectMenus(configSlot);
        UiMenu packMenu = menus[^3];
        UiMenu presetMenu = menus[^2];
        UiMenu oldModeMenu = menus[^1];
        Assert.Equal(0.75f, strength.ScalarPosition, 3); // preset wins declaration default
        Assert.Equal(["low", "high"], oldModeMenu.Items.Select(value => value.Label));

        enabled.Selected = true;
        enabled.OnClick!();
        strength.ScalarChanged!(0.62f); // 0.62 snaps to 0.5 on the declared 0.25 step
        samples.ScalarChanged!(0.33f);  // 3.3 snaps to integer step 4
        oldModeMenu.OnSelect!("high");

        Assert.Equal("true", fake.Display.RenderPack.SettingOverrides["enabled"]);
        Assert.Equal("0.5", fake.Display.RenderPack.SettingOverrides["strength"]);
        Assert.Equal("4", fake.Display.RenderPack.SettingOverrides["samples"]);
        Assert.Equal("high", fake.Display.RenderPack.SettingOverrides["mode"]);

        presetMenu.OnSelect!("high");
        Assert.Equal("high", fake.Display.RenderPack.PresetId);
        Assert.Equal(4, fake.Display.RenderPack.SettingOverrides.Count);
        Assert.Equal(47, listBox.ItemCount);
        Assert.Equal(36, controller.ConfigPage.Rows.Count);
        int savesBeforeStaleWidget = fake.DisplaySaves.Count;
        oldModeMenu.OnSelect!("low");
        Assert.Equal(savesBeforeStaleWidget, fake.DisplaySaves.Count);

        packMenu.OnSelect!("pack.beta");
        Assert.Equal("pack.beta", fake.Display.RenderPack.PackId);
        Assert.Equal("2.0.0", fake.Display.RenderPack.PackVersion);
        Assert.Equal("default", fake.Display.RenderPack.PresetId);
        Assert.Empty(fake.Display.RenderPack.SettingOverrides);
        Assert.Equal(44, listBox.ItemCount); // header + pack + preset + one setting + separator
        Assert.Equal(33, controller.ConfigPage.Rows.Count);

        oldModeMenu.OnSelect!("high");
        Assert.Empty(fake.Display.RenderPack.SettingOverrides);

        controller.ConfigPage.Reset();
        Assert.Equal("pack.alpha", fake.Display.RenderPack.PackId);
        Assert.Equal(47, listBox.ItemCount);
        Assert.Equal(36, controller.ConfigPage.Rows.Count);

        controller.ConfigPage.Defaults();
        Assert.True(fake.Display.RenderPack.IsRetail);
        Assert.Equal(43, listBox.ItemCount);
        Assert.Equal(32, controller.ConfigPage.Rows.Count);
    }

    [Fact]
    public void RenderPackSettingEdit_sanitizes_unknown_and_invalid_persisted_values()
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        OptionsPanelController controller = OptionsPanelController.Bind(
            layout,
            new OptionsPanelController.Callbacks(
                Toggle: () => { },
                RequestExitToCharacterSelection: () => { },
                ExitGame: () => { },
                UseMouseTurningSettings: () => { },
                DisplaySystemMessage: _ => { }))!;
        var fake = new FakeBindings
        {
            Display = DisplaySettings.Default with
            {
                RenderPack = new RenderPackSelectionSettings("pack.alpha", "1.0.0", "low")
                {
                    SettingOverrides = new RenderPackSettingOverrides(
                        new Dictionary<string, string>
                        {
                            ["removed"] = "1",
                            ["strength"] = "0.6", // not aligned to 0.25
                        }),
                },
            },
        };
        var pack = new ConfigOptionsPageController.RenderPackChoice(
            "pack.alpha", "Alpha", "1.0.0", true, null,
            [new("low", "Low", true, null)])
        {
            Settings =
            [
                new RenderSettingDeclaration(
                    "enabled", "Enabled", RenderSettingKind.Boolean,
                    "false", null, null, null, []),
                new RenderSettingDeclaration(
                    "strength", "Strength", RenderSettingKind.Float,
                    "0.5", 0, 1, 0.25, []),
            ],
        };

        Assert.True(ConfigOptionsPageController.Bind(
            layout,
            controller.ConfigPage,
            MakeTemplateResolver(),
            (_, _) => null,
            fake.ToBindings(new ConfigOptionsPageController.RenderPackBindings(() => [pack]))));

        var configSlot = UiElement.FindDescendant(controller.TabPanel, ConfigPageSlotId)!;
        var listBox = Assert.IsType<UiTemplateListBox>(
            UiElement.FindDescendant(configSlot, ConfigOptionsPageController.ListBoxElementId));
        UiElement enabledRow = listBox.ViewportForTest!.Children[42];
        var enabled = Assert.IsType<UiButton>(
            UiElement.FindDescendant(enabledRow, 0x10000219u));
        enabled.Selected = true;
        enabled.OnClick!();

        Assert.Single(fake.Display.RenderPack.SettingOverrides);
        Assert.Equal("true", fake.Display.RenderPack.SettingOverrides["enabled"]);
        Assert.False(fake.Display.RenderPack.SettingOverrides.ContainsKey("removed"));
        Assert.False(fake.Display.RenderPack.SettingOverrides.ContainsKey("strength"));
    }

    [Fact]
    public void ToggleRow_SfxEnabled_WritesThroughAudioBindings()
    {
        (OptionsPanelController controller, FakeBindings bindings, _) = BindReal();
        var row = (BoolOptionRow)controller.ConfigPage.Rows[1]; // Sound trio toggle

        row.SetCurrentValue(!AudioSettings.Default.SfxEnabled);

        Assert.Single(bindings.AudioSaves);
        Assert.Equal(!AudioSettings.Default.SfxEnabled, bindings.AudioSaves[0].SfxEnabled);
        // The slider half's own field must be untouched by the toggle write.
        Assert.Equal(AudioSettings.Default.Sfx, bindings.AudioSaves[0].Sfx);
    }

    [Fact]
    public void SliderRow_SoundVolume_ConvertsScalarToRealUnitRange()
    {
        (OptionsPanelController controller, FakeBindings bindings, _) = BindReal();
        var row = (FloatOptionRow)controller.ConfigPage.Rows[2]; // Sound trio slider, [0,1]

        row.SetCurrentValue(0.25f);

        Assert.Equal(0.25f, bindings.AudioSaves[^1].Sfx);
    }

    [Fact]
    public void SliderRow_CameraAdjustmentSpeed_ConvertsRealUnitOutOf0To1Range()
    {
        (OptionsPanelController controller, FakeBindings bindings, _) = BindReal();
        var row = (FloatOptionRow)controller.ConfigPage.Rows[9]; // AdjustmentSpeed

        row.SetCurrentValue(62.5f);

        Assert.Equal(62.5f, bindings.CameraTurningSaves[^1].AdjustmentSpeed);
    }

    [Fact]
    public void MenuRow_SoundFeatures_WritesThroughAudioBindings()
    {
        (OptionsPanelController controller, FakeBindings bindings, _) = BindReal();
        var row = (IntOptionRow)controller.ConfigPage.Rows[0]; // Sound Features

        row.SetCurrentValue(1);

        Assert.Equal(1, bindings.AudioSaves[^1].SoundFeatures);
    }

    [Fact]
    public void MenuRow_SoundFeatures_OpensAndSelectsThroughRealHitPath_UsingAuthoredPopupGeometry()
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        OptionsPanelController controller = OptionsPanelController.Bind(
            layout,
            new OptionsPanelController.Callbacks(
                Toggle: () => { },
                RequestExitToCharacterSelection: () => { },
                ExitGame: () => { },
                UseMouseTurningSettings: () => { },
                DisplaySystemMessage: _ => { }))!;
        var fakeBindings = new FakeBindings();
        bool bound = ConfigOptionsPageController.Bind(
            layout,
            controller.ConfigPage,
            MakeTemplateResolver(),
            (_, _) => null,
            fakeBindings.ToBindings(),
            resolveSprite: _ => (1u, 8, 8));
        Assert.True(bound);

        var listBox = Assert.IsType<UiTemplateListBox>(
            layout.FindElement(ConfigOptionsPageController.ListBoxElementId));
        List<UiMenu> menus = CollectMenus(listBox);
        Assert.Equal(8, menus.Count);
        UiMenu soundFeatures = menus[0]; // build order matches Rows order — Sound Features first

        Assert.NotNull(soundFeatures.SpriteResolve);
        Assert.NotEqual(0u, soundFeatures.NormalSprite);
        Assert.NotEqual(0u, soundFeatures.PressedSprite);
        Assert.NotEqual(0u, soundFeatures.ItemNormalSprite);
        Assert.NotEqual(0u, soundFeatures.ItemHighlightSprite);
        Assert.NotEqual(0u, soundFeatures.ArrowCapClosedSprite);
        Assert.NotEqual(0u, soundFeatures.ArrowCapOpenSprite);

        Assert.True(soundFeatures.Scrollable);
        Assert.Equal(6, soundFeatures.RowsPerColumn);
        Assert.Equal(18f, soundFeatures.RowHeight);
        Assert.Equal(100f, soundFeatures.ColumnWidth);
        Assert.False(soundFeatures.OpenUpward); // no authored attribute 5 — absent defaults false

        Assert.False(soundFeatures.IsOpen);

        Assert.True(soundFeatures.OnEvent(new UiEvent(0, soundFeatures, UiEventType.MouseDown, 0, 10, 5)));
        Assert.True(soundFeatures.IsOpen);

        const int border = 5;
        const int targetRow = 1;
        float iy = targetRow * soundFeatures.RowHeight + soundFeatures.RowHeight / 2f;
        float ly = soundFeatures.Height + iy + border;

        Assert.True(soundFeatures.OnEvent(new UiEvent(0, soundFeatures, UiEventType.MouseDown, 0, 10, (int)ly)));

        Assert.False(soundFeatures.IsOpen);
        Assert.Equal(1, fakeBindings.Audio.SoundFeatures);
        Assert.Equal(1, fakeBindings.AudioSaves[^1].SoundFeatures);
    }

    [Fact]
    public void MenuRows_All8_UseTheAuthoredTextStyleAndSizeToContent()
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        OptionsPanelController controller = OptionsPanelController.Bind(
            layout,
            new OptionsPanelController.Callbacks(
                Toggle: () => { },
                RequestExitToCharacterSelection: () => { },
                ExitGame: () => { },
                UseMouseTurningSettings: () => { },
                DisplaySystemMessage: _ => { }))!;
        var fakeBindings = new FakeBindings();
        Assert.True(ConfigOptionsPageController.Bind(
            layout,
            controller.ConfigPage,
            MakeTemplateResolver(),
            (_, _) => null,
            fakeBindings.ToBindings(),
            resolveSprite: _ => (1u, 8, 8)));

        var listBox = Assert.IsType<UiTemplateListBox>(
            layout.FindElement(ConfigOptionsPageController.ListBoxElementId));
        List<UiMenu> menus = CollectMenus(listBox);
        Assert.Equal(8, menus.Count);
        foreach (UiMenu menu in menus)
        {
            Assert.Equal(System.Numerics.Vector4.One, menu.TextColor);
            Assert.True(menu.ButtonTextCentered);
            Assert.True(menu.ItemTextCentered);
            Assert.True(menu.PopupSizeToContent);
        }
    }

    [Fact]
    public void MenuRow_Resolution_IsStringBacked_AndWritesThroughDisplayBindings()
    {
        (OptionsPanelController controller, FakeBindings bindings, _) = BindReal();
        var row = (StringOptionRow)controller.ConfigPage.Rows[12]; // Resolution

        row.SetCurrentValue("1920x1080");

        Assert.Equal("1920x1080", bindings.DisplaySaves[^1].Resolution);
    }

    [Fact]
    public void ToggleRow_UseMouseTurning_WritesTheConfigTabOwnPreference_NotTheWireBit()
    {
        (OptionsPanelController controller, FakeBindings bindings, _) = BindReal();
        var row = (BoolOptionRow)controller.ConfigPage.Rows[27]; // Use Mouse Turning

        row.SetCurrentValue(true);

        Assert.True(bindings.CameraTurningSaves[^1].UseMouseTurning);
    }

    [Fact]
    public void MenuRow_ChatFontSize_WritesThroughChatBindings_WithoutTouchingHearFlags()
    {
        (OptionsPanelController controller, FakeBindings bindings, _) = BindReal();
        var row = (IntOptionRow)controller.ConfigPage.Rows[29];

        row.SetCurrentValue(3);

        Assert.Equal(3, bindings.ChatSaves[^1].ChatFontSizeIndex);
        Assert.Equal(ChatSettings.Default.HearGeneralChat, bindings.ChatSaves[^1].HearGeneralChat);
    }

    [Fact]
    public void Apply_CommitsBaseline_ForAMixOfRowTypes()
    {
        (OptionsPanelController controller, _, _) = BindReal();
        var toggle = (BoolOptionRow)controller.ConfigPage.Rows[1];
        var slider = (FloatOptionRow)controller.ConfigPage.Rows[2];
        var menu = (IntOptionRow)controller.ConfigPage.Rows[0];

        toggle.SetCurrentValue(!toggle.Current);
        slider.SetCurrentValue(0.1f);
        menu.SetCurrentValue(1);
        Assert.True(controller.ConfigPage.Changed);

        controller.ConfigPage.Apply();

        Assert.False(controller.ConfigPage.Changed);
        Assert.Equal(toggle.Current, toggle.Saved);
        Assert.Equal(slider.Current, slider.Saved);
        Assert.Equal(menu.Current, menu.Saved);
    }

    [Fact]
    public void Defaults_RestoresRetailDefaultValue_ForEveryRow_WithoutCommitting()
    {
        (OptionsPanelController controller, _, _) = BindReal();
        foreach (IOptionRow r in controller.ConfigPage.Rows)
        {
            switch (r)
            {
                case BoolOptionRow b: b.SetCurrentValue(!b.DefaultValue); break;
                case FloatOptionRow f: f.SetCurrentValue(f.DefaultValue + 1000f); break;
                case IntOptionRow i: i.SetCurrentValue(i.DefaultValue + 1); break;
                case StringOptionRow s: s.SetCurrentValue(s.DefaultValue + "-x"); break;
            }
        }

        controller.ConfigPage.Defaults();

        foreach (IOptionRow r in controller.ConfigPage.Rows)
        {
            switch (r)
            {
                case BoolOptionRow b: Assert.Equal(b.DefaultValue, b.Current); break;
                case FloatOptionRow f: Assert.Equal(f.DefaultValue, f.Current); break;
                case IntOptionRow i: Assert.Equal(i.DefaultValue, i.Current); break;
                case StringOptionRow s: Assert.Equal(s.DefaultValue, s.Current); break;
            }
        }
    }

    [Fact]
    public void EveryRow_DefaultValue_MatchesRetailLiteral_NotJustSelfConsistency()
    {
        (OptionsPanelController controller, _, _) = BindReal();
        IReadOnlyList<IOptionRow> rows = controller.ConfigPage.Rows;
        Assert.Equal(30, rows.Count);

        object[] expected =
        [
            0,                  // 0  Sound Features menu (Stereo=0)
            true, 1.0f,         // 1-2  Sound trio (Enabled, Sfx)
            true, 1.0f,         // 3-4  Ambient trio
            true, 1.0f,         // 5-6  Interface trio
            true,               // 7  Play Sound Only When Active

            0.45f,
            40.0f,
            90.0f,              // 10 Field Of View
            true,               // 11 Align To Slope

            "1280x720",
                                //    DisplaySettings.Default.Resolution; production
                                //    passes the desktop mode via DisplayModeCatalog)
            true,               // 13 Full Screen
            false,              // 14 Sync To Refresh
            0f,                 // 15 Screen Brightness
            false,              // 16 Automatic Degrades
            0f,                 // 17 Graphics Performance
            50.0f,              // 18 Degrade Distance

            2,                  // 19 Landscape Texture Detail
            1,                  // 20 Environment Texture Detail
            1,                  // 21 Texture Filtering
            8,
            true,               // 23 Building Detail Textures
            false,              // 24 Multi-Pass Alpha

            0.55f,              // 25 Mouse Look Sensitivity
            false,              // 26 Invert Mouselook Y Axis
            false,              // 27 Use Mouse Turning

            2,
            1,
        ];

        for (int i = 0; i < rows.Count; i++)
        {
            object actual = rows[i] switch
            {
                BoolOptionRow b => b.DefaultValue,
                FloatOptionRow f => f.DefaultValue,
                IntOptionRow n => n.DefaultValue,
                StringOptionRow s => s.DefaultValue,
                _ => throw new InvalidOperationException(
                    $"row {i} has unexpected type {rows[i].GetType()}"),
            };
            Assert.True(
                Equals(expected[i], actual),
                $"row {i}: expected retail default {expected[i]} but row.DefaultValue was {actual}.");
        }
    }

    [Fact]
    public void AdaptiveDegradeRowsApplyToTheSharedDisplaySettingsSnapshot()
    {
        (OptionsPanelController controller, FakeBindings bindings, bool bound) = BindReal();
        Assert.True(bound);
        var automatic = Assert.IsType<BoolOptionRow>(controller.ConfigPage.Rows[16]);
        var bias = Assert.IsType<FloatOptionRow>(controller.ConfigPage.Rows[17]);
        var distance = Assert.IsType<FloatOptionRow>(controller.ConfigPage.Rows[18]);

        automatic.SetCurrentValue(true);
        bias.SetCurrentValue(-0.35f);
        distance.SetCurrentValue(77f);
        controller.ConfigPage.Apply();

        Assert.True(bindings.Display.AutomaticDegrades);
        Assert.Equal(-0.35f, bindings.Display.GraphicsPerformance);
        Assert.Equal(77f, bindings.Display.DegradeDistance);
    }

    [Fact]
    public void Bind_ResolvesOnlyTheAuthoredStringKeys_NoInventedOrDroppedKey()
    {
        string[] expectedKeys =
        [
            // Headers
            "ID_Sound_SoundSection", "ID_Camera_CameraSection",
            "ID_Graphics_GraphicsSection", "ID_Graphics_TextureSection",
            "ID_Input_InputSection", "ID_UI_UISection",

            // Sound section
            "ID_Sound_SoundFeatures", "ID_Sound_SoundFeatures_Help",
            "ID_Sound_Stereo", "ID_Sound_Mono",
            "ID_Sound_DisableSound", "ID_Sound_DisableSound_Help",
            "ID_Sound_EffectVolume_Help",
            "ID_Sound_DisableAmbientSound", "ID_Sound_DisableAmbientSound_Help",
            "ID_Sound_AmbientVolume_Help",
            "ID_Sound_DisableInterfaceSound", "ID_Sound_DisableInterfaceSound_Help",
            "ID_Sound_InterfaceVolume_Help",
            "ID_Sound_NoFocusNoSound", "ID_Sound_NoFocusNoSound_Help",

            "ID_Camera_Stiffness", "ID_Camera_Stiffness_Help",
            "ID_Graphics_Value_Soft", "ID_Graphics_Value_Hard",
            "ID_Camera_AdjustmentSpeed", "ID_Camera_AdjustmentSpeed_Help",
            "ID_Graphics_Value_Slow", "ID_Graphics_Value_Fast",
            "ID_Graphics_FieldOfView", "ID_Graphics_FieldOfView_Help",
            "ID_Graphics_Value_Narrow", "ID_Graphics_Value_Wide",
            "ID_Camera_AlignToSlope", "ID_Camera_AlignToSlope_Help",

            // Graphics section
            "ID_Rendering_DisplayResolution", "ID_Rendering_DisplayResolution_Help",
            "ID_Rendering_FullScreen", "ID_Rendering_FullScreen_Help",
            "ID_Rendering_SyncToDisplayRefresh", "ID_Rendering_SyncToDisplayRefresh_Help",
            "ID_Graphics_ScreenBrightness", "ID_Graphics_ScreenBrightness_Help",
            "ID_Graphics_Value_Dark", "ID_Graphics_Value_Bright",
            "ID_Graphics_AdaptiveDegrade", "ID_Graphics_AdaptiveDegrade_Help",
            "ID_Graphics_AdaptiveDegradeBias", "ID_Graphics_AdaptiveDegradeBias_Help",
            "ID_Graphics_Value_Speed", "ID_Graphics_Value_Detail",
            "ID_Graphics_DegradeDistance", "ID_Graphics_DegradeDistance_Help",
            "ID_Graphics_Value_Close", "ID_Graphics_Value_Far",

            // Rendering Quality section
            "ID_Graphics_LandscapeTextureDetail", "ID_Graphics_LandscapeTextureDetail_Help",
            "ID_Graphics_EnvironmentTextureDetail", "ID_Graphics_EnvironmentTextureDetail_Help",
            "ID_Graphics_Value_VeryLow", "ID_Graphics_Value_Low", "ID_Graphics_Value_Medium",
            "ID_Graphics_Value_High", "ID_Graphics_Value_VeryHigh",
            "ID_Graphics_TextureFiltering", "ID_Graphics_TextureFiltering_Help",
            "ID_Graphics_TextureFiltering_Bilinear", "ID_Graphics_TextureFiltering_Trilinear",
            "ID_Graphics_TextureFiltering_Sharp", "ID_Graphics_TextureFiltering_Anisotropic",
            "ID_Graphics_LandscapeDrawDistance", "ID_Graphics_LandscapeDrawDistance_Help",
            "ID_Graphics_Value_Extreme",
            "ID_Graphics_BuildingDetailTextures", "ID_Graphics_BuildingDetailTextures_Help",
            "ID_Graphics_MultiPassAlpha", "ID_Graphics_MultiPassAlpha_Help",

            // Input section
            "ID_Input_MouseLookSensitivity", "ID_Input_MouseLookSensitivity_Help",
            "ID_Input_InvertMouseLookYAxis", "ID_Input_InvertMouseLookYAxis_Help",
            "ID_Input_UseMouseTurning", "ID_Input_UseMouseTurning_Help",

            // UI section
            "ID_UI_ChatFontFace", "ID_UI_ChatFontFace_Help",
            "ID_UI_Value_Arial", "ID_UI_Value_CourierNew", "ID_UI_Value_PalatinoLinotype",
            "ID_UI_Value_Tahoma", "ID_UI_Value_TimesNewRoman",
            "ID_UI_ChatFontSize", "ID_UI_ChatFontSize_Help",
            "ID_UI_Value_Tiny", "ID_UI_Value_Small", "ID_UI_Value_Medium",
            "ID_UI_Value_Large", "ID_UI_Value_XLarge",
        ];

        var expectedByHash = new Dictionary<uint, string>();
        foreach (string key in expectedKeys)
            expectedByHash[DatStringResolver.ComputeHash(key)] = key;

        var seen = new HashSet<string>();
        Func<uint, uint, string?> recordingResolver = (table, hash) =>
        {
            Assert.Equal(0x23000003u, table);
            Assert.True(
                expectedByHash.TryGetValue(hash, out string? key),
                $"resolveString queried hash 0x{hash:X8} — no key in the expected table hashes "
                + "to this value. Either an invented/typo'd key was added, or this test's "
                + "expected-key table is stale.");
            seen.Add(key!);
            return "x"; // any non-null value keeps the row-build path fully populated.
        };

        (_, _, bool bound) = BindReal(recordingResolver);
        Assert.True(bound);

        IEnumerable<string> missing = expectedByHash.Values.Except(seen);
        Assert.True(
            !missing.Any(),
            "Bind never queried these expected keys (a dropped row/key): "
            + string.Join(", ", missing));
    }

    private const uint ConfigPageSlotId = 0x10000213u;
    private const uint ChatPageSlotId = 0x1000050Cu;

    [Fact]
    public void ScrollbarLinkage_ModelPointsAtTheConfigListBoxScroll()
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        OptionsPanelController controller = OptionsPanelController.Bind(
            layout,
            new OptionsPanelController.Callbacks(
                Toggle: () => { },
                RequestExitToCharacterSelection: () => { },
                ExitGame: () => { },
                UseMouseTurningSettings: () => { },
                DisplaySystemMessage: _ => { }))!;
        var fakeBindings = new FakeBindings();
        bool bound = ConfigOptionsPageController.Bind(
            layout, controller.ConfigPage, MakeTemplateResolver(), (_, _) => null,
            fakeBindings.ToBindings());
        Assert.True(bound);

        var configSlot = UiElement.FindDescendant(controller.TabPanel, ConfigPageSlotId)!;
        var listBox = Assert.IsType<UiTemplateListBox>(
            UiElement.FindDescendant(configSlot, ConfigOptionsPageController.ListBoxElementId));
        var scrollbar = Assert.IsType<UiScrollbar>(
            UiElement.FindDescendant(configSlot, ConfigOptionsPageController.ScrollbarElementId));

        Assert.Same(listBox.Scroll, scrollbar.Model);
    }

    [Fact]
    public void SharedScrollbarId_ChatAndConfigBoundTogether_EachOwnsItsOwnScrollbar()
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        OptionsPanelController controller = OptionsPanelController.Bind(
            layout,
            new OptionsPanelController.Callbacks(
                Toggle: () => { },
                RequestExitToCharacterSelection: () => { },
                ExitGame: () => { },
                UseMouseTurningSettings: () => { },
                DisplaySystemMessage: _ => { }))!;

        var chatBindings = new ChatOptionsPageControllerFakeBindings();
        bool chatBound = ChatOptionsPageController.Bind(
            layout, controller.ChatPage, MakeTemplateResolver(), (_, _) => null,
            chatBindings.ToBindings());
        var configBindings = new FakeBindings();
        bool configBound = ConfigOptionsPageController.Bind(
            layout, controller.ConfigPage, MakeTemplateResolver(), (_, _) => null,
            configBindings.ToBindings());

        Assert.True(chatBound);
        Assert.True(configBound);

        var chatSlot = UiElement.FindDescendant(controller.TabPanel, ChatPageSlotId)!;
        var chatListBox = Assert.IsType<UiTemplateListBox>(
            UiElement.FindDescendant(chatSlot, ChatOptionsPageController.ListBoxElementId));
        var chatScrollbar = Assert.IsType<UiScrollbar>(
            UiElement.FindDescendant(chatSlot, ChatOptionsPageController.ScrollbarElementId));

        var configSlot = UiElement.FindDescendant(controller.TabPanel, ConfigPageSlotId)!;
        var configListBox = Assert.IsType<UiTemplateListBox>(
            UiElement.FindDescendant(configSlot, ConfigOptionsPageController.ListBoxElementId));
        var configScrollbar = Assert.IsType<UiScrollbar>(
            UiElement.FindDescendant(configSlot, ConfigOptionsPageController.ScrollbarElementId));

        Assert.Same(chatListBox.Scroll, chatScrollbar.Model);
        Assert.Same(configListBox.Scroll, configScrollbar.Model);
        Assert.NotSame(chatScrollbar, configScrollbar);
        Assert.NotSame(chatListBox.Scroll, configListBox.Scroll);
    }

    private sealed class ChatOptionsPageControllerFakeBindings
    {
        public float DefaultOpacity = 0.5f;
        public float ActiveOpacity = 1.0f;

        public ChatOptionsPageController.Bindings ToBindings() => new(
            CurrentDefaultOpacity: () => DefaultOpacity,
            CurrentActiveOpacity: () => ActiveOpacity,
            SetDefaultOpacity: value => DefaultOpacity = value,
            SetActiveOpacity: value => ActiveOpacity = value,
            FlushOpacity: () => { },
            DefaultOpacityDatDefault: 0.5f,
            ActiveOpacityDatDefault: 1.0f,
            CurrentFilter: _ => 0xFBFFFFFFul,
            SetFilter: (_, _) => { },
            DefaultOpacityCaption: new ChatOptionsDatCaptions.Caption("Inactive Opacity", null),
            ActiveOpacityCaption: new ChatOptionsDatCaptions.Caption("Active Opacity", null));
    }

    [Fact]
    public void Bind_MissingListBox_ReturnsFalse_AndDoesNotThrow()
    {
        var emptyRoot = new ElementInfo { Id = 0, Type = 3 };
        ImportedLayout emptyLayout = LayoutImporter.Build(emptyRoot, NoTex, null);
        var page = new OptionPage();
        var fakeBindings = new FakeBindings();

        bool bound = ConfigOptionsPageController.Bind(
            emptyLayout, page, MakeTemplateResolver(), (_, _) => null,
            fakeBindings.ToBindings());

        Assert.False(bound);
        Assert.Empty(page.Rows);
    }

    [Fact]
    public void LabelResolutionFailure_LeavesLabelsNull_NeverInventsEnglish_AndStillRegistersAllRows()
    {
        (OptionsPanelController controller, _, bool bound) = BindReal(resolveString: (_, _) => null);

        Assert.True(bound);
        Assert.Equal(30, controller.ConfigPage.Rows.Count);
    }


    private enum RowKind { Toggle, TrioToggle, Slider, Menu }

    private static readonly (int ViewportIndex, RowKind Kind, bool StoreOnly, string Label)[]
        DimmingExpectations =
    {
        (1, RowKind.Menu, true, "Sound Features"),
        (2, RowKind.TrioToggle, false, "Disable Sound Effects"),      // LIVE
        (3, RowKind.TrioToggle, false, "Disable Ambient Sound"),      // LIVE
        (4, RowKind.TrioToggle, true, "Disable Interface Sound"),
        (5, RowKind.Toggle, true, "Play Sound Only When Active"),
        (8, RowKind.Slider, true, "Camera Stiffness"),
        (9, RowKind.Slider, true, "Camera Adjustment Speed"),
        (10, RowKind.Slider, false, "Field Of View"),                 // NEXT-LAUNCH
        (11, RowKind.Toggle, true, "Align To Slope"),
        (14, RowKind.Menu, false, "Resolution"),                      // LIVE
        (15, RowKind.Toggle, false, "Full Screen"),                   // LIVE
        (16, RowKind.Toggle, false, "Sync To Refresh"),               // NEXT-LAUNCH
        (17, RowKind.Slider, true, "Screen Brightness"),              // review S2
        (18, RowKind.Toggle, false, "Automatic Degrades"),            // LIVE
        (19, RowKind.Slider, false, "Graphics Performance"),          // LIVE
        (20, RowKind.Slider, false, "Degrade Distance"),              // LIVE
        (23, RowKind.Menu, true, "Landscape Texture Detail"),
        (24, RowKind.Menu, true, "Environment Texture Detail"),
        (25, RowKind.Menu, true, "Texture Filtering"),
        (26, RowKind.Menu, false, "Landscape Draw Distance"),
        (27, RowKind.Toggle, false, "Building Detail Textures"),
        (28, RowKind.Toggle, true, "Multi-Pass Alpha"),
        (31, RowKind.Slider, true, "Mouse Look Sensitivity"),
        (32, RowKind.Toggle, true, "Invert Mouselook Y Axis"),
        (33, RowKind.Toggle, true, "Use Mouse Turning"),
        (36, RowKind.Menu, true, "Chat Font Face"),
        (37, RowKind.Menu, true, "Chat Font Size"),
    };

    private static Vector4? FindTextLineColor(UiElement root, uint elementId)
    {
        if (UiElement.FindDescendant(root, elementId) is not UiText text)
            return null;
        IReadOnlyList<UiText.Line> lines = text.LinesProvider();
        return lines.Count == 0 ? null : lines[0].Color;
    }

    [Fact]
    public void CaptionDimming_MatchesTheStoreOnlySetExactly()
    {
        (OptionsPanelController controller, _, bool bound) = BindReal(resolveString: (_, _) => "x");
        Assert.True(bound);
        Assert.Equal(16, DimmingExpectations.Count(expectation => expectation.StoreOnly));

        var configSlot = UiElement.FindDescendant(controller.TabPanel, ConfigPageSlotId)!;
        var listBox = Assert.IsType<UiTemplateListBox>(
            UiElement.FindDescendant(configSlot, ConfigOptionsPageController.ListBoxElementId));
        UiElement viewport = Assert.Single(listBox.Children);
        IReadOnlyList<UiElement> items = viewport.Children.ToList();
        Assert.Equal(39, items.Count);

        const uint ToggleCheckboxElementId = 0x10000219u;
        const uint SliderLabelElementId = 0x1000021Bu;
        const uint MenuLabelElementId = 0x10000223u;

        foreach ((int index, RowKind kind, bool storeOnly, string label) in DimmingExpectations)
        {
            Vector4 expected = storeOnly
                ? UiRenderContext.StoreOnlyCaptionColor
                : Vector4.One;
            Vector4? actual = kind switch
            {
                RowKind.Toggle =>
                    (UiElement.FindDescendant(items[index], ToggleCheckboxElementId) as UiButton)?.LabelColor,
                RowKind.TrioToggle =>
                    Assert.IsType<UiOptionToggleSlider>(items[index]).Toggle?.LabelColor,
                RowKind.Slider => FindTextLineColor(items[index], SliderLabelElementId),
                RowKind.Menu => FindTextLineColor(items[index], MenuLabelElementId),
                _ => throw new InvalidOperationException($"unhandled row kind {kind}"),
            };
            Assert.True(
                actual.HasValue,
                $"{label} (viewport index {index}, kind {kind}): could not locate the "
                + "caption widget/line to check its color.");
            Assert.True(
                expected == actual.Value,
                $"{label} (viewport index {index}): expected "
                + $"{(storeOnly ? "DIMMED" : "LIVE")} caption color {expected} but the "
                + $"built widget rendered {actual.Value}.");
        }
    }

    [Fact]
    public void LandscapeDrawDistance_UsesRetailRadiusPayloads_AndAppliesSelection()
    {
        (OptionsPanelController controller, FakeBindings bindings, bool bound) = BindReal();
        Assert.True(bound);

        var configSlot = UiElement.FindDescendant(controller.TabPanel, ConfigPageSlotId)!;
        List<UiMenu> menus = CollectMenus(configSlot);
        UiMenu drawDistance = menus[5];

        Assert.Equal(8, drawDistance.Selected);
        Assert.Equal(
            [3, 5, 8, 11, 15, 25],
            drawDistance.Items.Select(item => Assert.IsType<int>(item.Payload)).ToArray());

        drawDistance.OnSelect!(25);
        controller.ConfigPage.Apply();

        Assert.Equal(25, bindings.Display.LandscapeDrawDistance);
    }


    [Fact]
    public void ConfigSlot_MatchesItsAuthoredOversizedDesign_BeforeAnyLayoutPass()
    {
        (OptionsPanelController controller, _, bool bound) = BindReal();
        Assert.True(bound);

        Assert.Equal(300f, controller.TabPanel.Width);
        Assert.Equal(362f, controller.TabPanel.Height);

        var configSlot = UiElement.FindDescendant(controller.TabPanel, ConfigPageSlotId)!;
        Assert.Equal(575f, configSlot.Height);
        Assert.NotNull(configSlot.LayoutPolicy);
    }

    [Fact]
    public void ConfigTab_ContentFitsInsideItsMountedWindow_AfterOneDrawFramesLayoutPass()
    {
        (OptionsPanelController controller, _, bool bound) = BindReal();
        Assert.True(bound);

        var configSlot = UiElement.FindDescendant(controller.TabPanel, ConfigPageSlotId)!;
        var listBox = Assert.IsType<UiTemplateListBox>(
            UiElement.FindDescendant(configSlot, ConfigOptionsPageController.ListBoxElementId));
        UiElement viewport = Assert.Single(listBox.Children);

        for (int frame = 0; frame < 2; frame++)
            ApplyAnchorRecursive(controller.TabPanel);

        // The viewport must track the REAL (shrunk) ListBox extent, not stay
        // locked at its original oversized 560px design height.
        Assert.True(
            viewport.Height <= listBox.Height + 0.5f,
            $"viewport height {viewport.Height} exceeds its ListBox's actual "
                + $"height {listBox.Height} — rows will draw/cull past where "
                + "the window actually is (the wrong-window-position bug).");

        float slotBottom = configSlot.Top + configSlot.Height;
        Assert.True(
            slotBottom <= controller.TabPanel.Height + 0.5f,
            $"Config slot bottom {slotBottom} exceeds the mounted window's "
                + $"own height {controller.TabPanel.Height}.");

        foreach (uint footerId in new[]
                 {
                     ConfigOptionsPageController_ApplyButtonId,
                     ConfigOptionsPageController_ResetButtonId,
                     ConfigOptionsPageController_DefaultsButtonId,
                 })
        {
            UiElement? btn = UiElement.FindDescendant(configSlot, footerId);
            Assert.NotNull(btn);
            float bottom = btn!.Top + btn.Height;
            Assert.True(
                bottom <= slotBottom + 0.5f,
                $"footer 0x{footerId:X8} bottom {bottom} exceeds the Config "
                    + $"slot's own bottom {slotBottom}.");
        }
    }

    private const uint ConfigOptionsPageController_ApplyButtonId = 0x100001FCu;
    private const uint ConfigOptionsPageController_ResetButtonId = 0x100001FDu;
    private const uint ConfigOptionsPageController_DefaultsButtonId = 0x100001FEu;

    private static void ApplyAnchorRecursive(UiElement e)
    {
        foreach (UiElement child in e.Children)
        {
            child.ApplyAnchor(e.Width, e.Height);
            ApplyAnchorRecursive(child);
        }
    }

    // ── OpenAC #42 follow-up: the mixer knobs in the Sound block ────────────
    // The four acdream-only mixer rows sit after "Play Sound Only When Active"
    // and change the mixer through the same save-then-apply owner the /mixer
    // command uses.

    private const uint ToggleCheckboxId = 0x10000219u;
    private const uint SliderCaptionId = 0x1000021Bu;
    private const uint SliderLeafId = 0x1000021Cu;
    private const uint SliderRangeLowId = 0x1000021Eu;
    private const uint SliderRangeHighId = 0x1000021Fu;

    private const int RetailMixerItem = 6;
    private const int VoicesItem = 7;
    private const int PriorityItem = 8;
    private const int VoicesPerSoundItem = 9;

    private const int RetailMixerRow = 8;
    private const int VoicesRow = 9;
    private const int PriorityRow = 10;
    private const int VoicesPerSoundRow = 11;

    private sealed class FakeMixer
    {
        internal AudioMixerOptions Stored { get; private set; } = AudioMixerOptions.Default;

        internal List<AudioMixerOptions> Saves { get; } = new();

        internal bool SavesFail { get; set; }

        internal ConfigOptionsPageController.AudioMixerBindings Bindings =>
            new(() => Stored, Save);

        internal bool Save(AudioMixerOptions options)
        {
            if (SavesFail)
                return false;
            Stored = options;
            Saves.Add(options);
            return true;
        }
    }

    private static (OptionsPanelController Panel, IReadOnlyList<UiElement> Items) BindWithMixer(
        ConfigOptionsPageController.AudioMixerBindings mixer)
    {
        ImportedLayout layout = FixtureLoader.LoadOptionsPanelHost();
        OptionsPanelController controller = OptionsPanelController.Bind(
            layout,
            new OptionsPanelController.Callbacks(
                Toggle: () => { },
                RequestExitToCharacterSelection: () => { },
                ExitGame: () => { },
                UseMouseTurningSettings: () => { },
                DisplaySystemMessage: _ => { }))!;

        bool bound = ConfigOptionsPageController.Bind(
            layout,
            controller.ConfigPage,
            MakeTemplateResolver(),
            (_, _) => "x",
            new FakeBindings().ToBindings(audioMixer: mixer));
        Assert.True(bound);

        var configSlot = UiElement.FindDescendant(controller.TabPanel, ConfigPageSlotId)!;
        var listBox = Assert.IsType<UiTemplateListBox>(
            UiElement.FindDescendant(configSlot, ConfigOptionsPageController.ListBoxElementId));
        UiElement viewport = Assert.Single(listBox.Children);
        return (controller, viewport.Children.ToList());
    }

    private static UiButton Checkbox(UiElement item) =>
        Assert.IsType<UiButton>(UiElement.FindDescendant(item, ToggleCheckboxId));

    private static UiScrollbar Slider(UiElement item) =>
        Assert.IsType<UiScrollbar>(UiElement.FindDescendant(item, SliderLeafId));

    private static UiText.Line TextLine(UiElement item, uint elementId)
    {
        var text = Assert.IsType<UiText>(UiElement.FindDescendant(item, elementId));
        return Assert.Single(text.LinesProvider());
    }

    [Fact]
    public void TheFourMixerRows_AreBuiltInTheSoundBlock_WithTheirCaptionsAndRanges()
    {
        var mixer = new FakeMixer();
        (OptionsPanelController panel, IReadOnlyList<UiElement> items) =
            BindWithMixer(mixer.Bindings);

        // Four rows more than the authored retail page has on its own.
        Assert.Equal(43, items.Count);
        Assert.Equal(34, panel.ConfigPage.Rows.Count);

        Assert.Equal("Retail Mixer", Checkbox(items[RetailMixerItem]).Label);
        Assert.Equal("Voices", TextLine(items[VoicesItem], SliderCaptionId).Text);
        Assert.Equal("Priority", Checkbox(items[PriorityItem]).Label);
        Assert.Equal(
            "Voices Per Sound",
            TextLine(items[VoicesPerSoundItem], SliderCaptionId).Text);

        Assert.Equal("16", TextLine(items[VoicesItem], SliderRangeLowId).Text);
        Assert.Equal("64", TextLine(items[VoicesItem], SliderRangeHighId).Text);
        Assert.Equal("Off", TextLine(items[VoicesPerSoundItem], SliderRangeLowId).Text);
        Assert.Equal("8", TextLine(items[VoicesPerSoundItem], SliderRangeHighId).Text);

        Assert.IsType<BoolOptionRow>(panel.ConfigPage.Rows[RetailMixerRow]);
        Assert.IsType<FloatOptionRow>(panel.ConfigPage.Rows[VoicesRow]);
        Assert.IsType<BoolOptionRow>(panel.ConfigPage.Rows[PriorityRow]);
        Assert.IsType<FloatOptionRow>(panel.ConfigPage.Rows[VoicesPerSoundRow]);

        // Every row says what it does when hovered.
        Assert.Contains("16 voices", Checkbox(items[RetailMixerItem]).TooltipText);
        Assert.Contains("at once", Slider(items[VoicesItem]).TooltipText);
        Assert.Contains("footstep", Checkbox(items[PriorityItem]).TooltipText);
        Assert.Contains("oldest", Slider(items[VoicesPerSoundItem]).TooltipText);

        // The rows start on the remembered values, not on invented ones.
        Assert.False(Checkbox(items[RetailMixerItem]).Selected);
        Assert.True(Checkbox(items[PriorityItem]).Selected);
        Assert.Equal(
            AudioMixerOptions.DefaultVoiceCount,
            ((FloatOptionRow)panel.ConfigPage.Rows[VoicesRow]).Current);
        Assert.Equal(
            AudioMixerOptions.DefaultMaxVoicesPerWave,
            ((FloatOptionRow)panel.ConfigPage.Rows[VoicesPerSoundRow]).Current);
        Assert.Empty(mixer.Saves);
    }

    [Fact]
    public void TurningTheRetailMixerOn_SavesIt_AndDimsTheThreeSettingsItOverrides()
    {
        var mixer = new FakeMixer();
        (_, IReadOnlyList<UiElement> items) = BindWithMixer(mixer.Bindings);

        Assert.NotEqual(
            UiRenderContext.StoreOnlyCaptionColor,
            TextLine(items[VoicesItem], SliderCaptionId).Color);
        Assert.Equal(Vector4.One, Checkbox(items[PriorityItem]).LabelColorProvider!());
        Assert.NotEqual(
            UiRenderContext.StoreOnlyCaptionColor,
            TextLine(items[VoicesPerSoundItem], SliderCaptionId).Color);

        UiButton retailMixer = Checkbox(items[RetailMixerItem]);
        retailMixer.Selected = true;
        retailMixer.OnClick!();

        Assert.True(Assert.Single(mixer.Saves).RetailMixer);
        Assert.Equal(
            UiRenderContext.StoreOnlyCaptionColor,
            TextLine(items[VoicesItem], SliderCaptionId).Color);
        Assert.Equal(
            UiRenderContext.StoreOnlyCaptionColor,
            Checkbox(items[PriorityItem]).LabelColorProvider!());
        Assert.Equal(
            UiRenderContext.StoreOnlyCaptionColor,
            TextLine(items[VoicesPerSoundItem], SliderCaptionId).Color);

        // Dimmed, not rewritten: the three keep their values for when the
        // retail mixer goes off again.
        Assert.Equal(AudioMixerOptions.DefaultVoiceCount, mixer.Stored.VoiceCount);
        Assert.True(mixer.Stored.UseAuthoredPriority);
        Assert.Equal(
            AudioMixerOptions.DefaultMaxVoicesPerWave,
            mixer.Stored.MaxVoicesPerWave);
    }

    [Fact]
    public void TheVoicesSlider_WritesWholeNumbersInsideSixteenToSixtyFour()
    {
        var mixer = new FakeMixer();
        (OptionsPanelController panel, IReadOnlyList<UiElement> items) =
            BindWithMixer(mixer.Bindings);
        UiScrollbar voices = Slider(items[VoicesItem]);
        var row = (FloatOptionRow)panel.ConfigPage.Rows[VoicesRow];

        List<float> rowValues = new();
        foreach (float normalized in new[] { 0f, 0.013f, 0.5f, 0.77f, 1f })
        {
            voices.ScalarChanged!(normalized);
            rowValues.Add(row.Current);
        }

        // The row itself steps in whole voices — a slider that ran free would
        // show 16.6 voices and save 17.
        Assert.Equal([16f, 17f, 40f, 53f, 64f], rowValues);
        Assert.Equal(
            [16, 17, 40, 53, 64],
            mixer.Saves.Select(static saved => saved.VoiceCount).ToArray());
        Assert.All(
            mixer.Saves,
            static saved => Assert.InRange(
                saved.VoiceCount,
                AudioMixerOptions.MinimumVoiceCount,
                AudioMixerOptions.MaximumVoiceCount));

        Slider(items[VoicesPerSoundItem]).ScalarChanged!(0.5f);
        Assert.Equal(4, mixer.Stored.MaxVoicesPerWave);
    }

    [Fact]
    public void ASaveThatFails_LeavesTheRowOnTheStoredValue()
    {
        var mixer = new FakeMixer { SavesFail = true };
        (OptionsPanelController panel, IReadOnlyList<UiElement> items) =
            BindWithMixer(mixer.Bindings);

        UiScrollbar voices = Slider(items[VoicesItem]);
        float storedPosition = voices.ScalarPosition;
        voices.ScalarChanged!(1f);

        Assert.Empty(mixer.Saves);
        Assert.Equal(AudioMixerOptions.DefaultVoiceCount, mixer.Stored.VoiceCount);
        Assert.Equal(storedPosition, voices.ScalarPosition);
        Assert.Equal(
            AudioMixerOptions.DefaultVoiceCount,
            ((FloatOptionRow)panel.ConfigPage.Rows[VoicesRow]).Current);

        UiButton priority = Checkbox(items[PriorityItem]);
        priority.Selected = false;
        priority.OnClick!();

        Assert.Empty(mixer.Saves);
        Assert.True(mixer.Stored.UseAuthoredPriority);
        Assert.True(priority.Selected);
        Assert.True(((BoolOptionRow)panel.ConfigPage.Rows[PriorityRow]).Current);
    }

    [Fact]
    public void TheCommandAndThePanel_ChangeTheMixerThroughOneSaveAndApplyPath()
    {
        AudioMixerOptions stored = AudioMixerOptions.Default;
        List<AudioMixerOptions> persisted = new();
        List<AudioMixerOptions> applied = new();
        var owner = new AudioMixerSettings(
            () => stored,
            options =>
            {
                stored = options;
                persisted.Add(options);
                return true;
            },
            options =>
            {
                applied.Add(options);
                return true;
            });

        var commands = new PluginCommandRegistry();
        using AudioMixerCommandBinding binding = Assert.IsType<AudioMixerCommandBinding>(
            AudioMixerCommandBinding.TryRegister(commands, owner, _ => { }));

        (OptionsPanelController panel, IReadOnlyList<UiElement> items) = BindWithMixer(
            new ConfigOptionsPageController.AudioMixerBindings(
                () => owner.Current,
                options => owner.Change(options).Saved));

        Assert.True(commands.TryHandle("/mixer voices 40"));

        Assert.Equal(40, Assert.Single(persisted).VoiceCount);
        Assert.Equal(40, Assert.Single(applied).VoiceCount);

        // The panel reads the live settings, so the command's change is what
        // the row shows the next time it is read.
        panel.ConfigPage.ReloadFromLive();
        Assert.Equal(40f, ((FloatOptionRow)panel.ConfigPage.Rows[VoicesRow]).Current);
        // 40 of 16..64 sits the slider thumb halfway along its travel.
        Assert.InRange(Slider(items[VoicesItem]).ScalarPosition, 0.499f, 0.501f);

        UiButton priority = Checkbox(items[PriorityItem]);
        priority.Selected = false;
        priority.OnClick!();

        // Two changes, two saves, two applies — one path, not two.
        Assert.Equal(2, persisted.Count);
        Assert.Equal(2, applied.Count);
        Assert.False(persisted[1].UseAuthoredPriority);
        Assert.False(applied[1].UseAuthoredPriority);
        Assert.Equal(40, persisted[1].VoiceCount);
    }

}
