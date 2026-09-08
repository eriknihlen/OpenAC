using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.UI;
using AcDream.Plugin.Abstractions.Rendering;
using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.App.UI.Layout;

public static class ConfigOptionsPageController
{
    public const uint RootElementId = 0x100001FFu;

    private const uint PageSlotElementId = 0x10000213u;

    /// <summary>The row ListBox (dat Type 5) — <c>m_pOptionBox</c>.</summary>
    public const uint ListBoxElementId = 0x10000200u;

    public const uint ScrollbarElementId = 0x10000201u;

    private const int HeaderTemplateIndex = 0;
    private const int SeparatorTemplateIndex = 1;
    private const int ToggleTemplateIndex = 2;
    private const int SimpleSliderTemplateIndex = 3;
    private const int MenuTemplateIndex = 4;
    private const int TrioTemplateIndex = 5;
    private const int RangedSliderTemplateIndex = 6;

    private const uint StringTableId = 0x23000003u;

    private const uint SliderLabelElementId = 0x1000021Bu;

    private const uint SliderElementId = 0x1000021Cu;

    private const uint SliderRangeMinElementId = 0x1000021Eu;
    private const uint SliderRangeMaxElementId = 0x1000021Fu;

    private const uint MenuLabelElementId = 0x10000223u;

    private const uint MenuElementId = 0x10000224u;

    private const uint ToggleCheckboxElementId = 0x10000219u;

    private static class MenuChromeSprites
    {
        public const uint Normal = 0x060012B3u;
        public const uint Pressed = 0x060012B4u;
        public const uint ItemNormal = 0x060012B3u;
        public const uint ItemHighlight = 0x060012B4u;
        public const uint ArrowCapClosed = 0x060012B1u;
        public const uint ArrowCapOpen = 0x060012B2u;

        public const int RowsPerColumn = 6;
        public const float RowHeight = 18f;
        public const float ColumnWidth = 100f;

        public const float ScrollbarWidth = 16f;
        public const float ScrollButtonExtent = 16f;
        public const uint ScrollTrack = 0x06004C5Fu;
        public const uint ScrollThumbTop = 0x06004C60u;
        public const uint ScrollThumb = 0x06004C63u;
        public const uint ScrollThumbBottom = 0x06004C66u;
        public const uint ScrollUp = RetailScrollbarChrome.UpNormal;
        public const uint ScrollDown = RetailScrollbarChrome.DownNormal;
    }

    private static void ApplyMenuChrome(
        UiMenu menu,
        Func<uint, (uint tex, int w, int h)>? resolveSprite,
        UiDatFont? datFont,
        BitmapFont? debugFont)
    {
        menu.SpriteResolve = resolveSprite;
        menu.DatFont = datFont;
        menu.Font = debugFont;
        menu.NormalSprite = MenuChromeSprites.Normal;
        menu.PressedSprite = MenuChromeSprites.Pressed;
        menu.ItemNormalSprite = MenuChromeSprites.ItemNormal;
        menu.ItemHighlightSprite = MenuChromeSprites.ItemHighlight;
        menu.RowsPerColumn = MenuChromeSprites.RowsPerColumn;
        menu.RowHeight = MenuChromeSprites.RowHeight;
        menu.ColumnWidth = MenuChromeSprites.ColumnWidth;
        menu.Scrollable = true;
        menu.ScrollbarWidth = MenuChromeSprites.ScrollbarWidth;
        menu.ScrollButtonExtent = MenuChromeSprites.ScrollButtonExtent;
        menu.ScrollTrackSprite = MenuChromeSprites.ScrollTrack;
        menu.ScrollThumbTopSprite = MenuChromeSprites.ScrollThumbTop;
        menu.ScrollThumbSprite = MenuChromeSprites.ScrollThumb;
        menu.ScrollThumbBottomSprite = MenuChromeSprites.ScrollThumbBottom;
        menu.ScrollUpSprite = MenuChromeSprites.ScrollUp;
        menu.ScrollDownSprite = MenuChromeSprites.ScrollDown;
        menu.ArrowCapClosedSprite = MenuChromeSprites.ArrowCapClosed;
        menu.ArrowCapOpenSprite = MenuChromeSprites.ArrowCapOpen;
        menu.OpenUpward = false;
        menu.TextIndent = 0f;
        menu.ButtonTextIndent = 0f;

        menu.TextColor = Vector4.One;
        menu.ButtonTextCentered = true;
        menu.ItemTextCentered = true;
        menu.PopupSizeToContent = true;
    }

    public sealed record Bindings(
        Func<DisplaySettings> LoadDisplay,
        Action<DisplaySettings> SaveDisplay,
        Func<AudioSettings> LoadAudio,
        Action<AudioSettings> SaveAudio,
        Func<CameraTurningSettings> LoadCameraTurning,
        Action<CameraTurningSettings> SaveCameraTurning,
        Func<ChatSettings> LoadChat,
        Action<ChatSettings> SaveChat)
    {
        public RenderPackBindings? RenderPacks { get; init; }
    }

    public sealed record RenderPackBindings(
        Func<IReadOnlyList<RenderPackChoice>> LoadChoices)
    {
        public Func<long>? LoadRevision { get; init; }
        public Func<string?>? LoadFailureNotice { get; init; }
    }

    public sealed record RenderPackChoice(
        string Id,
        string DisplayName,
        string? Version,
        bool Selectable,
        string? UnavailableReason,
        IReadOnlyList<RenderPackPresetChoice> Presets)
    {
        public string FeatureSummary { get; init; } = string.Empty;
        public IReadOnlyList<RenderSettingDeclaration> Settings { get; init; } = [];
    }

    public sealed record RenderPackPresetChoice(
        string Id,
        string DisplayName,
        bool Selectable,
        string? UnavailableReason)
    {
        public IReadOnlyList<RenderQualitySettingOverride> SettingOverrides { get; init; } = [];
        public long MaxResidentGpuBytes { get; init; }
        public double MaxIncrementalGpuMillisecondsP50 { get; init; }
        public double MaxIncrementalGpuMillisecondsP99 { get; init; }
        public double MaxIncrementalCpuMillisecondsP50 { get; init; }
        public double MaxIncrementalCpuMillisecondsP99 { get; init; }
    }

    public static bool Bind(
        ImportedLayout layout,
        OptionPage page,
        Func<uint, uint, UiElement?> templateResolver,
        Func<uint, uint, string?> resolveString,
        Bindings bindings,
        Func<uint, (uint tex, int w, int h)>? resolveSprite = null,
        UiDatFont? datFont = null,
        BitmapFont? debugFont = null,
        IReadOnlyList<string>? availableResolutions = null,
        string? resolutionDefault = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(templateResolver);
        ArgumentNullException.ThrowIfNull(resolveString);
        ArgumentNullException.ThrowIfNull(bindings);

        if (layout.FindElement(ListBoxElementId) is not UiTemplateListBox listBox)
        {
            Console.WriteLine(
                $"[D.2b] ConfigOptionsPageController: ListBox 0x{ListBoxElementId:X8} "
                + "not found (or not a UiTemplateListBox) in the built Options panel tree — "
                + "the Config tab will have no rows.");
            return false;
        }

        listBox.TemplateResolver = templateResolver;

        uint scrollbarElementId = listBox.ScrollbarElementId;
        UiElement? configPageSlot = layout.FindElement(PageSlotElementId);
        UiElement? scrollbarElement = configPageSlot is null || scrollbarElementId == 0
            ? null
            : UiElement.FindDescendant(configPageSlot, scrollbarElementId);
        if (scrollbarElement is UiScrollbar scrollbar)
            scrollbar.Model = listBox.Scroll;
        else
            Console.WriteLine(
                $"[D.2b] ConfigOptionsPageController: scrollbar 0x{scrollbarElementId:X8} "
                + $"not found under Config page slot 0x{PageSlotElementId:X8} — the Config "
                + "tab's row list will not scroll.");

        DisplaySettings display = bindings.LoadDisplay();
        AudioSettings audio = bindings.LoadAudio();
        CameraTurningSettings cameraTurning = bindings.LoadCameraTurning();
        ChatSettings chat = bindings.LoadChat();

        BindSoundSection(listBox, page, resolveString, bindings, ref audio, resolveSprite, datFont, debugFont);
        BuildSeparatorRow(listBox);
        BindCameraSection(listBox, page, resolveString, bindings, ref cameraTurning);
        BuildSeparatorRow(listBox);
        BindGraphicsSection(
            listBox, page, resolveString, bindings, ref display,
            resolveSprite, datFont, debugFont,
            availableResolutions, resolutionDefault);
        BuildSeparatorRow(listBox);
        BindRenderingQualitySection(listBox, page, resolveString, bindings, ref display, resolveSprite, datFont, debugFont);
        BuildSeparatorRow(listBox);
        BindInputSection(listBox, page, resolveString, bindings, ref cameraTurning);
        BuildSeparatorRow(listBox);
        BindUiSection(listBox, page, resolveString, bindings, ref chat, resolveSprite, datFont, debugFont);
        BuildSeparatorRow(listBox);

        if (bindings.RenderPacks is not null)
            BindRenderPackSection(
                listBox,
                page,
                bindings,
                bindings.RenderPacks,
                resolveSprite,
                datFont,
                debugFont);

        return true;
    }

    private static void BindRenderPackSection(
        UiTemplateListBox listBox,
        OptionPage page,
        Bindings bindings,
        RenderPackBindings renderPacks,
        Func<uint, (uint tex, int w, int h)>? resolveSprite,
        UiDatFont? datFont,
        BitmapFont? debugFont)
    {
        List<RenderPackChoice> choices = LoadPackChoices(renderPacks);
        long observedRevision = renderPacks.LoadRevision?.Invoke() ?? 0;
        var menuChoices = new ExplicitMenuChoiceSource(
            choices.Select(static value =>
                new ExplicitMenuChoice(
                    value.Id,
                    value.DisplayName,
                    value.Selectable,
                    PackTooltip(value))).ToArray());

        BuildExplicitHeaderRow(listBox, "Graphics Enhancements");
        RenderPackTailOwner? tail = null;
        StringOptionRow? packOption = null;
        UiMenu? packMenu = BuildExplicitStringMenuRow(
            listBox,
            "Shader pack",
            menuChoices.Choices,
            page,
            read: () => NormalizePackId(bindings.LoadDisplay().RenderPack, choices),
            apply: selectedId =>
            {
                RenderPackChoice? selected = choices.FirstOrDefault(value =>
                    value.Selectable && string.Equals(
                        value.Id,
                        selectedId,
                        StringComparison.OrdinalIgnoreCase));
                RenderPackPresetChoice? preset = selected?.Presets.FirstOrDefault(
                    static value => value.Selectable);
                if (selected is null || preset is null)
                    return;
                bindings.SaveDisplay(bindings.LoadDisplay() with
                {
                    RenderPack = new RenderPackSelectionSettings(
                        selected.Id,
                        selected.Version,
                        preset.Id),
                });
                tail?.RebuildPackTail(selected);
            },
            defaultValue: RenderPackSelectionSettings.RetailPackId,
            resolveSprite,
            datFont,
            debugFont,
            menuChoices,
            option => packOption = option);

        string initialPackId = NormalizePackId(bindings.LoadDisplay().RenderPack, choices);
        RenderPackChoice initialPack = choices.First(value =>
            string.Equals(value.Id, initialPackId, StringComparison.OrdinalIgnoreCase));
        tail = new RenderPackTailOwner(
            listBox,
            page,
            bindings,
            retainedItemCount: listBox.ItemCount,
            retainedOptionCount: page.Rows.Count,
            resolveSprite,
            datFont,
            debugFont);
        tail.RebuildPackTail(initialPack);
        if (packMenu is not null)
        {
            packMenu.BeforeOpen = () =>
            {
                if (renderPacks.LoadRevision is not { } loadRevision)
                    return;
                long revision = loadRevision();
                if (revision == observedRevision)
                    return;
                observedRevision = revision;
                choices = LoadPackChoices(renderPacks);
                menuChoices.Choices = choices.Select(static value =>
                    new ExplicitMenuChoice(
                        value.Id,
                        value.DisplayName,
                        value.Selectable,
                        PackTooltip(value))).ToArray();
                packMenu.Items = menuChoices.Choices
                    .Select(static choice => new UiMenu.MenuItem(
                        choice.Label,
                        choice.Id))
                    .ToArray();
                string selectedId = NormalizePackId(
                    bindings.LoadDisplay().RenderPack,
                    choices);
                packMenu.Selected = selectedId;
                packMenu.TooltipText = menuChoices.Choices.FirstOrDefault(choice =>
                        string.Equals(
                            choice.Id,
                            selectedId,
                            StringComparison.OrdinalIgnoreCase))
                    .Tooltip;
                RenderPackChoice selected = choices.First(value => string.Equals(
                    value.Id,
                    selectedId,
                    StringComparison.OrdinalIgnoreCase));
                tail.RebuildPackTail(selected);
                packOption?.SaveCurrentValue();
            };
            packMenu.TooltipTextProvider = () => CombineTooltip(
                renderPacks.LoadFailureNotice?.Invoke(),
                PackTooltip(choices.FirstOrDefault(value => string.Equals(
                    value.Id,
                    packMenu.Selected as string,
                    StringComparison.OrdinalIgnoreCase))));
        }
    }

    private static List<RenderPackChoice> LoadPackChoices(
        RenderPackBindings renderPacks)
    {
        IReadOnlyList<RenderPackChoice> discovered = renderPacks.LoadChoices();
        var choices = new List<RenderPackChoice>(discovered.Count + 1)
        {
            new(
                RenderPackSelectionSettings.RetailPackId,
                "acdream default (retail-faithful)",
                Version: null,
                Selectable: true,
                UnavailableReason: null,
                [new RenderPackPresetChoice(
                    RenderPackSelectionSettings.RetailPresetId,
                    "Off",
                    Selectable: true,
                    UnavailableReason: null)]),
        };
        choices.AddRange(discovered.Where(static choice =>
            !string.Equals(
                choice.Id,
                RenderPackSelectionSettings.RetailPackId,
                StringComparison.OrdinalIgnoreCase)));
        return choices;
    }

    private static string NormalizePackId(
        RenderPackSelectionSettings selection,
        IReadOnlyList<RenderPackChoice> choices) =>
        choices.Any(value => string.Equals(
            value.Id,
            selection.PackId,
            StringComparison.OrdinalIgnoreCase) && value.Selectable)
            ? selection.PackId
            : RenderPackSelectionSettings.RetailPackId;

    private static string NormalizePresetId(
        string presetId,
        RenderPackChoice pack) =>
        pack.Presets.Any(value => value.Selectable && string.Equals(
            value.Id,
            presetId,
            StringComparison.OrdinalIgnoreCase))
            ? presetId
            : pack.Presets.FirstOrDefault(static value => value.Selectable)?.Id
                ?? RenderPackSelectionSettings.RetailPresetId;

    private static string PackTooltip(RenderPackChoice? pack)
    {
        if (pack is null)
            return "acdream's default retail-faithful renderer remains authoritative unless an enhancement pack is explicitly selected.";
        string summary = string.IsNullOrWhiteSpace(pack.FeatureSummary)
            ? "acdream's default retail-faithful renderer remains authoritative unless an enhancement pack is explicitly selected."
            : pack.FeatureSummary.Trim();
        return CombineTooltip(pack.UnavailableReason, summary) ?? summary;
    }

    private static string PresetTooltip(RenderPackPresetChoice preset)
    {
        double residentMiB = preset.MaxResidentGpuBytes / (1024d * 1024d);
        string estimate = string.Format(
            CultureInfo.InvariantCulture,
            "Estimated ceiling — GPU p50/p99 ≤ {0:0.###}/{1:0.###} ms; "
                + "render CPU p50/p99 ≤ {2:0.###}/{3:0.###} ms; pack VRAM ≤ {4:0.##} MiB.",
            preset.MaxIncrementalGpuMillisecondsP50,
            preset.MaxIncrementalGpuMillisecondsP99,
            preset.MaxIncrementalCpuMillisecondsP50,
            preset.MaxIncrementalCpuMillisecondsP99,
            residentMiB);
        return CombineTooltip(preset.UnavailableReason, estimate) ?? estimate;
    }

    private static string? CombineTooltip(string? first, string? second)
    {
        bool hasFirst = !string.IsNullOrWhiteSpace(first);
        bool hasSecond = !string.IsNullOrWhiteSpace(second);
        if (!hasFirst)
            return hasSecond ? second!.Trim() : null;
        if (!hasSecond)
            return first!.Trim();
        return first!.Trim() + Environment.NewLine + second!.Trim();
    }

    private readonly record struct ExplicitMenuChoice(
        string Id,
        string Label,
        bool Enabled,
        string? Tooltip);

    private sealed class ExplicitMenuChoiceSource(
        IReadOnlyList<ExplicitMenuChoice> choices)
    {
        internal IReadOnlyList<ExplicitMenuChoice> Choices { get; set; } = choices;
    }

    private sealed class RenderPackTailOwner(
        UiTemplateListBox listBox,
        OptionPage page,
        Bindings bindings,
        int retainedItemCount,
        int retainedOptionCount,
        Func<uint, (uint tex, int w, int h)>? resolveSprite,
        UiDatFont? datFont,
        BitmapFont? debugFont)
    {
        private readonly int _retainedItemCount = retainedItemCount;
        private readonly int _retainedOptionCount = retainedOptionCount;
        private int _packGeneration;
        private int _settingsGeneration;
        private int _settingsItemCount;
        private int _settingsOptionCount;

        internal void RebuildPackTail(RenderPackChoice pack)
        {
            int generation = ++_packGeneration;
            ++_settingsGeneration;
            listBox.RemoveTail(_retainedItemCount);
            page.RemoveTail(_retainedOptionCount);

            DisplaySettings display = bindings.LoadDisplay();
            string presetId = NormalizePresetId(display.RenderPack.PresetId, pack);
            RenderPackPresetChoice preset = pack.Presets.First(value =>
                string.Equals(value.Id, presetId, StringComparison.OrdinalIgnoreCase));
            BuildExplicitStringMenuRow(
                listBox,
                "Quality preset",
                pack.Presets.Select(static value =>
                    new ExplicitMenuChoice(
                        value.Id,
                        value.DisplayName,
                        value.Selectable,
                        PresetTooltip(value))).ToArray(),
                page,
                read: () => NormalizePresetId(
                    bindings.LoadDisplay().RenderPack.PresetId,
                    pack),
                apply: selectedPresetId =>
                {
                    if (generation != _packGeneration)
                        return;
                    RenderPackPresetChoice? selected = pack.Presets.FirstOrDefault(value =>
                        value.Selectable && string.Equals(
                            value.Id,
                            selectedPresetId,
                            StringComparison.OrdinalIgnoreCase));
                    DisplaySettings current = bindings.LoadDisplay();
                    if (selected is null || !SamePack(current.RenderPack, pack))
                        return;
                    bindings.SaveDisplay(current with
                    {
                        RenderPack = current.RenderPack with
                        {
                            PresetId = selected.Id,
                            SettingOverrides = SanitizeOverrides(
                                pack,
                                current.RenderPack.SettingOverrides),
                        },
                    });
                    RebuildSettingsTail(pack, selected);
                },
                defaultValue: preset.Id,
                resolveSprite,
                datFont,
                debugFont);

            _settingsItemCount = listBox.ItemCount;
            _settingsOptionCount = page.Rows.Count;
            RebuildSettingsTail(pack, preset);
        }

        private void RebuildSettingsTail(
            RenderPackChoice pack,
            RenderPackPresetChoice preset)
        {
            int generation = ++_settingsGeneration;
            listBox.RemoveTail(_settingsItemCount);
            page.RemoveTail(_settingsOptionCount);

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RenderSettingDeclaration setting in pack.Settings)
            {
                if (!ids.Add(setting.Id) || !CanBuildSetting(setting))
                    continue;
                BuildSetting(pack, preset, setting, generation);
            }
            BuildSeparatorRow(listBox);
        }

        private void BuildSetting(
            RenderPackChoice pack,
            RenderPackPresetChoice preset,
            RenderSettingDeclaration setting,
            int generation)
        {
            bool IsCurrent() => generation == _settingsGeneration
                && SamePack(bindings.LoadDisplay().RenderPack, pack)
                && string.Equals(
                    bindings.LoadDisplay().RenderPack.PresetId,
                    preset.Id,
                    StringComparison.OrdinalIgnoreCase);
            string Read() => ResolveSettingValue(
                pack,
                preset,
                setting,
                bindings.LoadDisplay().RenderPack.SettingOverrides);
            string defaultValue = ResolveSettingValue(
                pack,
                preset,
                setting,
                RenderPackSettingOverrides.Empty);
            void Apply(string value)
            {
                if (!IsCurrent()
                    || !RenderPackSettingValueCodec.TryEncode(setting, value, out _))
                    return;
                DisplaySettings current = bindings.LoadDisplay();
                RenderPackSettingOverrides clean = SanitizeOverrides(
                    pack,
                    current.RenderPack.SettingOverrides);
                bindings.SaveDisplay(current with
                {
                    RenderPack = current.RenderPack with
                    {
                        SettingOverrides = clean.Set(setting.Id, value),
                    },
                });
            }

            switch (setting.Kind)
            {
                case RenderSettingKind.Boolean:
                    BuildExplicitToggleRow(
                        listBox,
                        setting.DisplayName,
                        bool.Parse(defaultValue),
                        page,
                        read: () => bool.Parse(Read()),
                        apply: value => Apply(value ? "true" : "false"),
                        IsCurrent);
                    break;

                case RenderSettingKind.Float:
                case RenderSettingKind.Integer:
                    double min = setting.Minimum!.Value;
                    double max = setting.Maximum!.Value;
                    double step = setting.Step
                        ?? (setting.Kind == RenderSettingKind.Integer ? 1d : 0d);
                    BuildExplicitNumericSliderRow(
                        listBox,
                        setting.DisplayName,
                        min,
                        max,
                        step,
                        setting.Kind == RenderSettingKind.Integer,
                        double.Parse(defaultValue, CultureInfo.InvariantCulture),
                        page,
                        read: () => double.Parse(Read(), CultureInfo.InvariantCulture),
                        apply: value => Apply(FormatNumeric(value, setting.Kind)),
                        IsCurrent);
                    break;

                case RenderSettingKind.Choice:
                    BuildExplicitStringMenuRow(
                        listBox,
                        setting.DisplayName,
                        setting.Choices.Select(static value =>
                            new ExplicitMenuChoice(value, value, true, null)).ToArray(),
                        page,
                        read: Read,
                        apply: value =>
                        {
                            if (IsCurrent()) Apply(value);
                        },
                        defaultValue,
                        resolveSprite,
                        datFont,
                        debugFont);
                    break;
            }
        }

        private static bool CanBuildSetting(RenderSettingDeclaration setting)
        {
            if (string.IsNullOrWhiteSpace(setting.Id)
                || string.IsNullOrWhiteSpace(setting.DisplayName)
                || !RenderPackSettingValueCodec.TryEncode(
                    setting,
                    setting.DefaultValue,
                    out _))
                return false;
            if (setting.Kind is RenderSettingKind.Float or RenderSettingKind.Integer)
            {
                return setting.Minimum is { } min
                    && setting.Maximum is { } max
                    && double.IsFinite(min)
                    && double.IsFinite(max)
                    && min >= -float.MaxValue
                    && max <= float.MaxValue
                    && max > min
                    && (setting.Step is null
                        || double.IsFinite(setting.Step.Value) && setting.Step.Value > 0);
            }
            return setting.Kind is RenderSettingKind.Boolean
                || setting.Kind == RenderSettingKind.Choice && setting.Choices.Count > 0;
        }

        private static string ResolveSettingValue(
            RenderPackChoice pack,
            RenderPackPresetChoice preset,
            RenderSettingDeclaration setting,
            IReadOnlyDictionary<string, string> userOverrides)
        {
            if (TryGet(userOverrides, setting.Id, out string? user)
                && RenderPackSettingValueCodec.TryEncode(setting, user, out _))
                return user;
            RenderQualitySettingOverride? presetValue = preset.SettingOverrides
                .FirstOrDefault(value => string.Equals(
                    value.SettingId,
                    setting.Id,
                    StringComparison.OrdinalIgnoreCase));
            if (presetValue is not null
                && RenderPackSettingValueCodec.TryEncode(setting, presetValue.Value, out _))
                return presetValue.Value;
            return setting.DefaultValue;
        }

        private static RenderPackSettingOverrides SanitizeOverrides(
            RenderPackChoice pack,
            IReadOnlyDictionary<string, string> overrides)
        {
            var valid = new List<KeyValuePair<string, string>>();
            foreach ((string id, string value) in overrides)
            {
                RenderSettingDeclaration? setting = pack.Settings.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
                if (setting is not null
                    && RenderPackSettingValueCodec.TryEncode(setting, value, out _))
                    valid.Add(new KeyValuePair<string, string>(setting.Id, value));
            }
            return new RenderPackSettingOverrides(valid);
        }

        private static bool TryGet(
            IReadOnlyDictionary<string, string> values,
            string id,
            out string value)
        {
            if (values.TryGetValue(id, out value!))
                return true;
            foreach ((string key, string candidate) in values)
            {
                if (string.Equals(key, id, StringComparison.OrdinalIgnoreCase))
                {
                    value = candidate;
                    return true;
                }
            }
            value = string.Empty;
            return false;
        }

        private static bool SamePack(
            RenderPackSelectionSettings selection,
            RenderPackChoice pack) =>
            string.Equals(selection.PackId, pack.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(selection.PackVersion, pack.Version, StringComparison.Ordinal);

        private static string FormatNumeric(double value, RenderSettingKind kind) =>
            kind == RenderSettingKind.Integer
                ? checked((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture)
                : value.ToString("R", CultureInfo.InvariantCulture);
    }

    // ── Section 1: Sound Options ────────────────────────────────────────

    private static void BindSoundSection(
        UiTemplateListBox listBox,
        OptionPage page,
        Func<uint, uint, string?> resolveString,
        Bindings bindings,
        ref AudioSettings audio,
        Func<uint, (uint tex, int w, int h)>? resolveSprite,
        UiDatFont? datFont,
        BitmapFont? debugFont)
    {
        BuildHeaderRow(listBox, "ID_Sound_SoundSection", resolveString);

        BuildMenuRow(
            listBox, "ID_Sound_SoundFeatures",
            new[] { "ID_Sound_Stereo", "ID_Sound_Mono" },
            page, resolveString,
            read: () => bindings.LoadAudio().SoundFeatures,
            apply: value =>
            {
                AudioSettings updated = bindings.LoadAudio() with { SoundFeatures = value };
                bindings.SaveAudio(updated);
            },
            defaultValue: 0,
            storeOnly: true,
            resolveSprite, datFont, debugFont);

        BuildTrioRow(
            listBox, "ID_Sound_DisableSound", sliderTooltipKey: "ID_Sound_EffectVolume",
            toggleDefault: true,
            sliderMin: 0f, sliderMax: 1f, sliderDefault: 1.0f,
            page, resolveString,
            toggleRead: () => bindings.LoadAudio().SfxEnabled,
            toggleApply: value => bindings.SaveAudio(bindings.LoadAudio() with { SfxEnabled = value }),
            sliderRead: () => bindings.LoadAudio().Sfx,
            sliderApply: value => bindings.SaveAudio(bindings.LoadAudio() with { Sfx = value }),
            storeOnly: false); // LIVE

        BuildTrioRow(
            listBox, "ID_Sound_DisableAmbientSound", sliderTooltipKey: "ID_Sound_AmbientVolume",
            toggleDefault: true,
            sliderMin: 0f, sliderMax: 1f, sliderDefault: 1.0f,
            page, resolveString,
            toggleRead: () => bindings.LoadAudio().AmbientEnabled,
            toggleApply: value => bindings.SaveAudio(bindings.LoadAudio() with { AmbientEnabled = value }),
            sliderRead: () => bindings.LoadAudio().Ambient,
            sliderApply: value => bindings.SaveAudio(bindings.LoadAudio() with { Ambient = value }),
            storeOnly: false); // LIVE

        BuildTrioRow(
            listBox, "ID_Sound_DisableInterfaceSound", sliderTooltipKey: "ID_Sound_InterfaceVolume",
            toggleDefault: true,
            sliderMin: 0f, sliderMax: 1f, sliderDefault: 1.0f,
            page, resolveString,
            toggleRead: () => bindings.LoadAudio().InterfaceEnabled,
            toggleApply: value => bindings.SaveAudio(bindings.LoadAudio() with { InterfaceEnabled = value }),
            sliderRead: () => bindings.LoadAudio().InterfaceVolume,
            sliderApply: value => bindings.SaveAudio(bindings.LoadAudio() with { InterfaceVolume = value }),
            storeOnly: true);

        BuildToggleRow(
            listBox, "ID_Sound_NoFocusNoSound", defaultValue: true, page, resolveString,
            read: () => bindings.LoadAudio().PlaySoundOnlyWhenActive,
            apply: value => bindings.SaveAudio(bindings.LoadAudio() with { PlaySoundOnlyWhenActive = value }),
            storeOnly: true);

        audio = bindings.LoadAudio();
    }


    private static void BindCameraSection(
        UiTemplateListBox listBox,
        OptionPage page,
        Func<uint, uint, string?> resolveString,
        Bindings bindings,
        ref CameraTurningSettings cameraTurning)
    {
        BuildHeaderRow(listBox, "ID_Camera_CameraSection", resolveString);

        BuildSliderRow(
            listBox, RangedSliderTemplateIndex, "ID_Camera_Stiffness",
            min: 0.285714298f, max: 1f, defaultValue: 0.45f, page, resolveString,
            read: () => bindings.LoadCameraTurning().Stiffness,
            apply: value => bindings.SaveCameraTurning(bindings.LoadCameraTurning() with { Stiffness = value }),
            storeOnly: true,
            rangeLowKey: "ID_Graphics_Value_Soft", rangeHighKey: "ID_Graphics_Value_Hard");

        BuildSliderRow(
            listBox, RangedSliderTemplateIndex, "ID_Camera_AdjustmentSpeed",
            min: 5f, max: 80f, defaultValue: 40.0f, page, resolveString,
            read: () => bindings.LoadCameraTurning().AdjustmentSpeed,
            apply: value => bindings.SaveCameraTurning(bindings.LoadCameraTurning() with { AdjustmentSpeed = value }),
            storeOnly: true,
            rangeLowKey: "ID_Graphics_Value_Slow", rangeHighKey: "ID_Graphics_Value_Fast");

        BuildSliderRow(
            listBox, RangedSliderTemplateIndex, "ID_Graphics_FieldOfView",
            min: 10f, max: 160f, defaultValue: 90.0f, page, resolveString,
            read: () => bindings.LoadDisplay().FieldOfView,
            apply: value => bindings.SaveDisplay(bindings.LoadDisplay() with { FieldOfView = value }),
            storeOnly: false, // NEXT-LAUNCH, not store-only
            rangeLowKey: "ID_Graphics_Value_Narrow", rangeHighKey: "ID_Graphics_Value_Wide");

        BuildToggleRow(
            listBox, "ID_Camera_AlignToSlope", defaultValue: true, page, resolveString,
            read: () => bindings.LoadCameraTurning().AlignToSlope,
            apply: value => bindings.SaveCameraTurning(bindings.LoadCameraTurning() with { AlignToSlope = value }),
            storeOnly: true);

        cameraTurning = bindings.LoadCameraTurning();
    }

    // ── Section 3: Graphics Options ─────────────────────────────────────

    private static void BindGraphicsSection(
        UiTemplateListBox listBox,
        OptionPage page,
        Func<uint, uint, string?> resolveString,
        Bindings bindings,
        ref DisplaySettings display,
        Func<uint, (uint tex, int w, int h)>? resolveSprite,
        UiDatFont? datFont,
        BitmapFont? debugFont,
        IReadOnlyList<string>? availableResolutions,
        string? resolutionDefault)
    {
        BuildHeaderRow(listBox, "ID_Graphics_GraphicsSection", resolveString);

        BuildStringMenuRow(
            listBox, "ID_Rendering_DisplayResolution",
            availableResolutions ?? DisplaySettings.AvailableResolutions, page, resolveString,
            read: () => bindings.LoadDisplay().Resolution,
            apply: value => bindings.SaveDisplay(bindings.LoadDisplay() with { Resolution = value }),
            defaultValue: resolutionDefault ?? DisplaySettings.Default.Resolution,
            storeOnly: false, // LIVE
            resolveSprite, datFont, debugFont);

        BuildToggleRow(
            listBox, "ID_Rendering_FullScreen", defaultValue: true, page, resolveString,
            read: () => bindings.LoadDisplay().Fullscreen,
            apply: value => bindings.SaveDisplay(bindings.LoadDisplay() with { Fullscreen = value }),
            storeOnly: false); // LIVE

        // Sync To Refresh: NEXT-LAUNCH (same DisplaySettings.VSync
        // pre-existing precedent as FieldOfView above).
        BuildToggleRow(
            listBox, "ID_Rendering_SyncToDisplayRefresh", defaultValue: false, page, resolveString,
            read: () => bindings.LoadDisplay().VSync,
            apply: value => bindings.SaveDisplay(bindings.LoadDisplay() with { VSync = value }),
            storeOnly: false); // NEXT-LAUNCH, not store-only

        BuildSliderRow(
            listBox, RangedSliderTemplateIndex, "ID_Graphics_ScreenBrightness",
            min: -1f, max: 1f, defaultValue: 0f, page, resolveString,
            read: () => bindings.LoadDisplay().ScreenBrightness,
            apply: value => bindings.SaveDisplay(bindings.LoadDisplay() with { ScreenBrightness = value }),
            storeOnly: true, // review S2
            rangeLowKey: "ID_Graphics_Value_Dark", rangeHighKey: "ID_Graphics_Value_Bright");

        BuildToggleRow(
            listBox, "ID_Graphics_AdaptiveDegrade", defaultValue: false, page, resolveString,
            read: () => bindings.LoadDisplay().AutomaticDegrades,
            apply: value => bindings.SaveDisplay(bindings.LoadDisplay() with { AutomaticDegrades = value }),
            storeOnly: false);

        BuildSliderRow(
            listBox, RangedSliderTemplateIndex, "ID_Graphics_AdaptiveDegradeBias",
            min: -1f, max: 1f, defaultValue: 0f, page, resolveString,
            read: () => bindings.LoadDisplay().GraphicsPerformance,
            apply: value => bindings.SaveDisplay(bindings.LoadDisplay() with { GraphicsPerformance = value }),
            storeOnly: false,
            rangeLowKey: "ID_Graphics_Value_Speed", rangeHighKey: "ID_Graphics_Value_Detail");

        BuildSliderRow(
            listBox, RangedSliderTemplateIndex, "ID_Graphics_DegradeDistance",
            min: 0f, max: 100f, defaultValue: 50.0f, page, resolveString,
            read: () => bindings.LoadDisplay().DegradeDistance,
            apply: value => bindings.SaveDisplay(bindings.LoadDisplay() with { DegradeDistance = value }),
            storeOnly: false,
            rangeLowKey: "ID_Graphics_Value_Close", rangeHighKey: "ID_Graphics_Value_Far");

        display = bindings.LoadDisplay();
    }

    // ── Section 4: Rendering Quality Options ────────────────────────────

    private static readonly string[] TextureDetailChoices =
    {
        "ID_Graphics_Value_VeryLow", "ID_Graphics_Value_Low", "ID_Graphics_Value_Medium",
        "ID_Graphics_Value_High", "ID_Graphics_Value_VeryHigh",
    };

    private static readonly string[] TextureFilteringChoices =
    {
        "ID_Graphics_TextureFiltering_Bilinear", "ID_Graphics_TextureFiltering_Trilinear",
        "ID_Graphics_TextureFiltering_Sharp", "ID_Graphics_TextureFiltering_Anisotropic",
    };

    private static readonly string[] LandscapeDrawDistanceChoices =
    {
        "ID_Graphics_Value_VeryLow", "ID_Graphics_Value_Low", "ID_Graphics_Value_Medium",
        "ID_Graphics_Value_High", "ID_Graphics_Value_VeryHigh", "ID_Graphics_Value_Extreme",
    };

    private static readonly int[] LandscapeDrawDistanceValues =
    {
        3, 5, 8, 11, 15, 25,
    };

    private static void BindRenderingQualitySection(
        UiTemplateListBox listBox,
        OptionPage page,
        Func<uint, uint, string?> resolveString,
        Bindings bindings,
        ref DisplaySettings display,
        Func<uint, (uint tex, int w, int h)>? resolveSprite,
        UiDatFont? datFont,
        BitmapFont? debugFont)
    {
        BuildHeaderRow(listBox, "ID_Graphics_TextureSection", resolveString);

        BuildMenuRow(
            listBox, "ID_Graphics_LandscapeTextureDetail", TextureDetailChoices, page, resolveString,
            read: () => bindings.LoadDisplay().LandscapeTextureDetail,
            apply: value => bindings.SaveDisplay(bindings.LoadDisplay() with { LandscapeTextureDetail = value }),
            defaultValue: 2,
            storeOnly: true,
            resolveSprite, datFont, debugFont);

        BuildMenuRow(
            listBox, "ID_Graphics_EnvironmentTextureDetail", TextureDetailChoices, page, resolveString,
            read: () => bindings.LoadDisplay().EnvironmentTextureDetail,
            apply: value => bindings.SaveDisplay(bindings.LoadDisplay() with { EnvironmentTextureDetail = value }),
            defaultValue: 1,
            storeOnly: true,
            resolveSprite, datFont, debugFont);

        BuildMenuRow(
            listBox, "ID_Graphics_TextureFiltering", TextureFilteringChoices, page, resolveString,
            read: () => bindings.LoadDisplay().TextureFiltering,
            apply: value => bindings.SaveDisplay(bindings.LoadDisplay() with { TextureFiltering = value }),
            defaultValue: 1,
            storeOnly: true,
            resolveSprite, datFont, debugFont);

        BuildMenuRow(
            listBox, "ID_Graphics_LandscapeDrawDistance", LandscapeDrawDistanceChoices, page, resolveString,
            read: () => bindings.LoadDisplay().LandscapeDrawDistance,
            apply: value => bindings.SaveDisplay(bindings.LoadDisplay() with { LandscapeDrawDistance = value }),
            defaultValue: 8,
            storeOnly: false,
            resolveSprite, datFont, debugFont,
            payloadValues: LandscapeDrawDistanceValues);

        BuildToggleRow(
            listBox, "ID_Graphics_BuildingDetailTextures", defaultValue: true, page, resolveString,
            read: () => bindings.LoadDisplay().BuildingDetailTextures,
            apply: value => bindings.SaveDisplay(bindings.LoadDisplay() with { BuildingDetailTextures = value }),
            storeOnly: false);

        BuildToggleRow(
            listBox, "ID_Graphics_MultiPassAlpha", defaultValue: false, page, resolveString,
            read: () => bindings.LoadDisplay().MultiPassAlpha,
            apply: value => bindings.SaveDisplay(bindings.LoadDisplay() with { MultiPassAlpha = value }),
            storeOnly: true);

        display = bindings.LoadDisplay();
    }

    // ── Section 5: Input Options ────────────────────────────────────────

    private static void BindInputSection(
        UiTemplateListBox listBox,
        OptionPage page,
        Func<uint, uint, string?> resolveString,
        Bindings bindings,
        ref CameraTurningSettings cameraTurning)
    {
        BuildHeaderRow(listBox, "ID_Input_InputSection", resolveString);

        BuildSliderRow(
            listBox, SimpleSliderTemplateIndex, "ID_Input_MouseLookSensitivity",
            min: 0.00999999978f, max: 1f, defaultValue: 0.55f, page, resolveString,
            read: () => bindings.LoadCameraTurning().MouseLookSensitivity,
            apply: value => bindings.SaveCameraTurning(bindings.LoadCameraTurning() with { MouseLookSensitivity = value }),
            storeOnly: true);

        BuildToggleRow(
            listBox, "ID_Input_InvertMouseLookYAxis", defaultValue: false, page, resolveString,
            read: () => bindings.LoadCameraTurning().InvertMouseLookYAxis,
            apply: value => bindings.SaveCameraTurning(bindings.LoadCameraTurning() with { InvertMouseLookYAxis = value }),
            storeOnly: true);

        BuildToggleRow(
            listBox, "ID_Input_UseMouseTurning", defaultValue: false, page, resolveString,
            read: () => bindings.LoadCameraTurning().UseMouseTurning,
            apply: value => bindings.SaveCameraTurning(bindings.LoadCameraTurning() with { UseMouseTurning = value }),
            storeOnly: true);

        cameraTurning = bindings.LoadCameraTurning();
    }

    // ── Section 6: UI Options ───────────────────────────────────────────

    private static readonly string[] ChatFontFaceChoices =
    {
        "ID_UI_Value_Arial", "ID_UI_Value_CourierNew", "ID_UI_Value_PalatinoLinotype",
        "ID_UI_Value_Tahoma", "ID_UI_Value_TimesNewRoman",
    };

    private static readonly string[] ChatFontSizeChoices =
    {
        "ID_UI_Value_Tiny", "ID_UI_Value_Small", "ID_UI_Value_Medium",
        "ID_UI_Value_Large", "ID_UI_Value_XLarge",
    };

    private static void BindUiSection(
        UiTemplateListBox listBox,
        OptionPage page,
        Func<uint, uint, string?> resolveString,
        Bindings bindings,
        ref ChatSettings chat,
        Func<uint, (uint tex, int w, int h)>? resolveSprite,
        UiDatFont? datFont,
        BitmapFont? debugFont)
    {
        BuildHeaderRow(listBox, "ID_UI_UISection", resolveString);

        BuildMenuRow(
            listBox, "ID_UI_ChatFontFace", ChatFontFaceChoices, page, resolveString,
            read: () => bindings.LoadChat().ChatFontFace,
            apply: value => bindings.SaveChat(bindings.LoadChat() with { ChatFontFace = value }),
            defaultValue: 2,
            storeOnly: true,
            resolveSprite, datFont, debugFont);

        BuildMenuRow(
            listBox, "ID_UI_ChatFontSize", ChatFontSizeChoices, page, resolveString,
            read: () => bindings.LoadChat().ChatFontSizeIndex,
            apply: value => bindings.SaveChat(bindings.LoadChat() with { ChatFontSizeIndex = value }),
            defaultValue: 1,
            storeOnly: true,
            resolveSprite, datFont, debugFont);

        chat = bindings.LoadChat();
    }

    // ── Row builders (shared shapes) ────────────────────────────────────

    private static void BuildHeaderRow(
        UiTemplateListBox listBox, string headerKey, Func<uint, uint, string?> resolveString)
    {
        if (listBox.AddItemFromTemplateList(HeaderTemplateIndex) is not UiText header)
        {
            Console.WriteLine(
                $"[D.2b] ConfigOptionsPageController: header template did not build as "
                + $"UiText for '{headerKey}'.");
            return;
        }

        string? label = resolveString(StringTableId, DatStringResolver.ComputeHash(headerKey));
        if (label is null)
        {
            Console.WriteLine(
                $"[D.2b] ConfigOptionsPageController: header string '{headerKey}' did not "
                + "resolve from the DAT string table — the row renders with no text rather "
                + "than an invented label.");
            return;
        }

        header.LinesProvider = () => new[] { new UiText.Line(label, header.DefaultColor) };
    }

    private static void BuildExplicitHeaderRow(
        UiTemplateListBox listBox,
        string label)
    {
        if (listBox.AddItemFromTemplateList(HeaderTemplateIndex) is not UiText header)
        {
            Console.WriteLine(
                "[render-pack] Config header template did not build as UiText.");
            return;
        }

        header.LinesProvider = () => new[]
        {
            new UiText.Line(label, header.DefaultColor),
        };
    }

    private static void BuildSeparatorRow(UiTemplateListBox listBox)
    {
        if (listBox.AddItemFromTemplateList(SeparatorTemplateIndex) is null)
            Console.WriteLine("[D.2b] ConfigOptionsPageController: separator template did not build.");
    }

    private static void BuildToggleRow(
        UiTemplateListBox listBox,
        string labelKey,
        bool defaultValue,
        OptionPage page,
        Func<uint, uint, string?> resolveString,
        Func<bool> read,
        Action<bool> apply,
        bool storeOnly)
    {
        UiElement? row = listBox.AddItemFromTemplateList(ToggleTemplateIndex);
        if (row is null)
        {
            Console.WriteLine(
                $"[D.2b] ConfigOptionsPageController: toggle template did not build for "
                + $"'{labelKey}'.");
            return;
        }

        UiButton? checkbox = FindCheckbox(row);
        if (checkbox is null)
        {
            Console.WriteLine(
                $"[D.2b] ConfigOptionsPageController: no checkbox child found in the "
                + $"toggle row for '{labelKey}'.");
            return;
        }

        ApplyLabelAndTooltip(checkbox, labelKey, resolveString, storeOnly);

        bool initial = read();
        checkbox.Selected = initial;

        var row_ = new BoolOptionRow(
            initial,
            defaultValue,
            apply: value =>
            {
                checkbox.Selected = value;
                apply(value);
            },
            read: read,
            refresh: value => checkbox.Selected = value);
        page.Register(row_);

        checkbox.OnClick = () => row_.SetCurrentValue(checkbox.Selected);
    }

    private static void BuildSliderRow(
        UiTemplateListBox listBox,
        int templateIndex,
        string labelKey,
        float min,
        float max,
        float defaultValue,
        OptionPage page,
        Func<uint, uint, string?> resolveString,
        Func<float> read,
        Action<float> apply,
        bool storeOnly,
        string? rangeLowKey = null,
        string? rangeHighKey = null)
    {
        UiElement? row = listBox.AddItemFromTemplateList(templateIndex);
        if (row is null)
        {
            Console.WriteLine(
                $"[D.2b] ConfigOptionsPageController: slider template did not build for "
                + $"'{labelKey}'.");
            return;
        }

        if (UiElement.FindDescendant(row, SliderLabelElementId) is UiText label)
            SetLabelText(label, labelKey, resolveString, storeOnly);

        if (rangeLowKey is not null)
            SetRangeLabel(row, SliderRangeMinElementId, rangeLowKey, resolveString);
        if (rangeHighKey is not null)
            SetRangeLabel(row, SliderRangeMaxElementId, rangeHighKey, resolveString);

        if (UiElement.FindDescendant(row, SliderElementId) is not UiScrollbar slider)
        {
            Console.WriteLine(
                $"[D.2b] ConfigOptionsPageController: no slider leaf found in the row for "
                + $"'{labelKey}'.");
            return;
        }

        // OP6 rework (review S3): the slider IS the interactive/hoverable
        // widget for this row — the label text has no hit-test surface.
        string? tooltip = ResolveTooltip(labelKey, resolveString);
        if (tooltip is not null)
            slider.TooltipText = tooltip;

        float initial = read();
        slider.SetScalarPosition(ToNormalized(initial, min, max));

        var row_ = new FloatOptionRow(
            initial,
            defaultValue,
            apply: value =>
            {
                slider.SetScalarPosition(ToNormalized(value, min, max));
                apply(value);
            },
            read: read,
            refresh: value => slider.SetScalarPosition(ToNormalized(value, min, max)));
        page.Register(row_);

        slider.ScalarChanged = normalized => row_.SetCurrentValue(FromNormalized(normalized, min, max));
    }

    private static void SetRangeLabel(
        UiElement row, uint elementId, string labelKey, Func<uint, uint, string?> resolveString)
    {
        if (UiElement.FindDescendant(row, elementId) is not UiText text)
            return;
        string? label = resolveString(StringTableId, DatStringResolver.ComputeHash(labelKey));
        if (label is null)
        {
            Console.WriteLine(
                $"[D.2b] ConfigOptionsPageController: range label '{labelKey}' did not "
                + "resolve — rendered with no text rather than invented English.");
            return;
        }
        text.LinesProvider = () => new[] { new UiText.Line(label, text.DefaultColor) };
    }

    private static void BuildTrioRow(
        UiTemplateListBox listBox,
        string toggleLabelKey,
        string sliderTooltipKey,
        bool toggleDefault,
        float sliderMin,
        float sliderMax,
        float sliderDefault,
        OptionPage page,
        Func<uint, uint, string?> resolveString,
        Func<bool> toggleRead,
        Action<bool> toggleApply,
        Func<float> sliderRead,
        Action<float> sliderApply,
        bool storeOnly)
    {
        if (listBox.AddItemFromTemplateList(TrioTemplateIndex) is not UiOptionToggleSlider trio)
        {
            Console.WriteLine(
                $"[D.2b] ConfigOptionsPageController: trio template did not build as "
                + $"UiOptionToggleSlider for '{toggleLabelKey}'.");
            return;
        }

        UiButton? checkbox = trio.Toggle;
        UiScrollbar? slider = trio.Slider;
        if (checkbox is null || slider is null)
        {
            Console.WriteLine(
                $"[D.2b] ConfigOptionsPageController: trio row for '{toggleLabelKey}' is "
                + $"missing its toggle or slider child (toggle={checkbox is not null}, "
                + $"slider={slider is not null}).");
            return;
        }

        ApplyLabelAndTooltip(checkbox, toggleLabelKey, resolveString, storeOnly);

        bool toggleInitial = toggleRead();
        checkbox.Selected = toggleInitial;
        var toggleRow = new BoolOptionRow(
            toggleInitial,
            toggleDefault,
            apply: value =>
            {
                checkbox.Selected = value;
                toggleApply(value);
            },
            read: toggleRead,
            refresh: value => checkbox.Selected = value);
        page.Register(toggleRow);
        checkbox.OnClick = () => toggleRow.SetCurrentValue(checkbox.Selected);

        string? sliderTooltip = ResolveTooltip(sliderTooltipKey, resolveString);
        if (sliderTooltip is not null)
            slider.TooltipText = sliderTooltip;

        float sliderInitial = sliderRead();
        slider.SetScalarPosition(ToNormalized(sliderInitial, sliderMin, sliderMax));
        var sliderRow = new FloatOptionRow(
            sliderInitial,
            sliderDefault,
            apply: value =>
            {
                slider.SetScalarPosition(ToNormalized(value, sliderMin, sliderMax));
                sliderApply(value);
            },
            read: sliderRead,
            refresh: value => slider.SetScalarPosition(ToNormalized(value, sliderMin, sliderMax)));
        page.Register(sliderRow);
        slider.ScalarChanged = normalized =>
            sliderRow.SetCurrentValue(FromNormalized(normalized, sliderMin, sliderMax));
    }

    private static void BuildMenuRow(
        UiTemplateListBox listBox,
        string labelKey,
        string[] choiceKeys,
        OptionPage page,
        Func<uint, uint, string?> resolveString,
        Func<int> read,
        Action<int> apply,
        int defaultValue,
        bool storeOnly,
        Func<uint, (uint tex, int w, int h)>? resolveSprite,
        UiDatFont? datFont,
        BitmapFont? debugFont,
        IReadOnlyList<int>? payloadValues = null)
    {
        UiElement? row = listBox.AddItemFromTemplateList(MenuTemplateIndex);
        if (row is null)
        {
            Console.WriteLine(
                $"[D.2b] ConfigOptionsPageController: menu template did not build for "
                + $"'{labelKey}'.");
            return;
        }

        if (UiElement.FindDescendant(row, MenuLabelElementId) is UiText label)
            SetLabelText(label, labelKey, resolveString, storeOnly);

        if (UiElement.FindDescendant(row, MenuElementId) is not UiMenu menu)
        {
            Console.WriteLine(
                $"[D.2b] ConfigOptionsPageController: no UiMenu leaf found in the row for "
                + $"'{labelKey}'.");
            return;
        }

        ApplyMenuChrome(menu, resolveSprite, datFont, debugFont);

        // OP6 rework (review S3): the menu button IS the interactive/
        // hoverable widget for this row.
        string? tooltip = ResolveTooltip(labelKey, resolveString);
        if (tooltip is not null)
            menu.TooltipText = tooltip;

        if (payloadValues is not null && payloadValues.Count != choiceKeys.Length)
            throw new ArgumentException(
                "Menu payload count must match the choice count.",
                nameof(payloadValues));

        string[] choiceLabels = new string[choiceKeys.Length];
        int[] choiceValues = new int[choiceKeys.Length];
        var items = new UiMenu.MenuItem[choiceKeys.Length];
        for (int i = 0; i < choiceKeys.Length; i++)
        {
            string? choiceLabel = resolveString(StringTableId, DatStringResolver.ComputeHash(choiceKeys[i]));
            choiceLabels[i] = choiceLabel ?? string.Empty;
            if (choiceLabel is null)
                Console.WriteLine(
                    $"[D.2b] ConfigOptionsPageController: menu choice '{choiceKeys[i]}' "
                    + $"(for '{labelKey}') did not resolve — item renders with no caption "
                    + "rather than invented English.");
            choiceValues[i] = payloadValues?[i] ?? i;
            items[i] = new UiMenu.MenuItem(choiceLabels[i], choiceValues[i]);
        }
        menu.Items = items;

        int initial = read();
        menu.Selected = initial;
        menu.ButtonLabelProvider = () =>
        {
            int current = menu.Selected is int selected ? selected : initial;
            int choiceIndex = Array.IndexOf(choiceValues, current);
            return choiceIndex >= 0 ? choiceLabels[choiceIndex] : string.Empty;
        };

        var row_ = new IntOptionRow(
            initial,
            defaultValue,
            apply: value =>
            {
                menu.Selected = value;
                apply(value);
            },
            read: read,
            refresh: value => menu.Selected = value);
        page.Register(row_);

        menu.OnSelect = payload =>
        {
            if (payload is int value)
                row_.SetCurrentValue(value);
        };
    }

    private static void BuildStringMenuRow(
        UiTemplateListBox listBox,
        string labelKey,
        IReadOnlyList<string> choices,
        OptionPage page,
        Func<uint, uint, string?> resolveString,
        Func<string> read,
        Action<string> apply,
        string defaultValue,
        bool storeOnly,
        Func<uint, (uint tex, int w, int h)>? resolveSprite,
        UiDatFont? datFont,
        BitmapFont? debugFont)
    {
        UiElement? row = listBox.AddItemFromTemplateList(MenuTemplateIndex);
        if (row is null)
        {
            Console.WriteLine(
                $"[D.2b] ConfigOptionsPageController: menu template did not build for "
                + $"'{labelKey}'.");
            return;
        }

        if (UiElement.FindDescendant(row, MenuLabelElementId) is UiText label)
            SetLabelText(label, labelKey, resolveString, storeOnly);

        if (UiElement.FindDescendant(row, MenuElementId) is not UiMenu menu)
        {
            Console.WriteLine(
                $"[D.2b] ConfigOptionsPageController: no UiMenu leaf found in the row for "
                + $"'{labelKey}'.");
            return;
        }

        ApplyMenuChrome(menu, resolveSprite, datFont, debugFont);

        // OP6 rework (review S3): the menu button IS the interactive/
        // hoverable widget for this row.
        string? tooltip = ResolveTooltip(labelKey, resolveString);
        if (tooltip is not null)
            menu.TooltipText = tooltip;

        var items = new UiMenu.MenuItem[choices.Count];
        for (int i = 0; i < choices.Count; i++)
            items[i] = new UiMenu.MenuItem(choices[i], choices[i]);
        menu.Items = items;

        string initial = read();
        menu.Selected = initial;
        menu.ButtonLabelProvider = () => menu.Selected as string ?? initial;

        var stringRow = new StringOptionRow(
            initial,
            defaultValue,
            apply: value =>
            {
                menu.Selected = value;
                apply(value);
            },
            read: read,
            refresh: value => menu.Selected = value);
        page.Register(stringRow);

        menu.OnSelect = payload =>
        {
            if (payload is string value)
                stringRow.SetCurrentValue(value);
        };
    }

    private static UiMenu? BuildExplicitStringMenuRow(
        UiTemplateListBox listBox,
        string labelText,
        IReadOnlyList<ExplicitMenuChoice> choices,
        OptionPage page,
        Func<string> read,
        Action<string> apply,
        string defaultValue,
        Func<uint, (uint tex, int w, int h)>? resolveSprite,
        UiDatFont? datFont,
        BitmapFont? debugFont,
        ExplicitMenuChoiceSource? dynamicChoices = null,
        Action<StringOptionRow>? captureOption = null)
    {
        UiElement? row = listBox.AddItemFromTemplateList(MenuTemplateIndex);
        if (row is null)
        {
            Console.WriteLine(
                $"[render-pack] Config menu template did not build for '{labelText}'.");
            return null;
        }

        if (UiElement.FindDescendant(row, MenuLabelElementId) is UiText label)
        {
            label.LinesProvider = () => new[]
            {
                new UiText.Line(labelText, label.DefaultColor),
            };
        }

        if (UiElement.FindDescendant(row, MenuElementId) is not UiMenu menu)
        {
            Console.WriteLine(
                $"[render-pack] No UiMenu leaf found for '{labelText}'.");
            return null;
        }

        var choiceSource = dynamicChoices ?? new ExplicitMenuChoiceSource(choices);
        ApplyMenuChrome(menu, resolveSprite, datFont, debugFont);
        menu.Items = choiceSource.Choices
            .Select(static choice => new UiMenu.MenuItem(choice.Label, choice.Id))
            .ToArray();
        menu.EnabledProvider = payload => choiceSource.Choices.Any(choice =>
            choice.Enabled && Equals(choice.Id, payload));

        string initial = read();
        menu.Selected = initial;
        menu.ButtonLabelProvider = () =>
        {
            string current = menu.Selected as string ?? initial;
            return choiceSource.Choices.FirstOrDefault(choice => string.Equals(
                    choice.Id,
                    current,
                    StringComparison.OrdinalIgnoreCase))
                .Label ?? current;
        };
        menu.TooltipText = choiceSource.Choices.FirstOrDefault(choice => string.Equals(
                choice.Id,
                initial,
                StringComparison.OrdinalIgnoreCase))
            .Tooltip;

        var option = new StringOptionRow(
            initial,
            defaultValue,
            apply: value =>
            {
                menu.Selected = value;
                menu.TooltipText = choiceSource.Choices.FirstOrDefault(choice => string.Equals(
                        choice.Id,
                        value,
                        StringComparison.OrdinalIgnoreCase))
                    .Tooltip;
                apply(value);
            },
            read,
            refresh: value => menu.Selected = value);
        page.Register(option);
        captureOption?.Invoke(option);
        menu.OnSelect = payload =>
        {
            if (payload is string value && choiceSource.Choices.Any(choice =>
                choice.Enabled && string.Equals(
                    choice.Id,
                    value,
                    StringComparison.OrdinalIgnoreCase)))
                option.SetCurrentValue(value);
        };
        return menu;
    }

    private static UiButton? BuildExplicitToggleRow(
        UiTemplateListBox listBox,
        string labelText,
        bool defaultValue,
        OptionPage page,
        Func<bool> read,
        Action<bool> apply,
        Func<bool> isCurrent)
    {
        UiElement? row = listBox.AddItemFromTemplateList(ToggleTemplateIndex);
        UiButton? checkbox = row is null ? null : FindCheckbox(row);
        if (checkbox is null)
        {
            Console.WriteLine(
                $"[render-pack] Toggle template did not build for '{labelText}'.");
            return null;
        }

        checkbox.Label = labelText;
        checkbox.LabelColor = Vector4.One;
        bool initial = read();
        checkbox.Selected = initial;
        var option = new BoolOptionRow(
            initial,
            defaultValue,
            apply: value =>
            {
                checkbox.Selected = value;
                if (isCurrent()) apply(value);
            },
            read: () => isCurrent() ? read() : initial,
            refresh: value => checkbox.Selected = value);
        page.Register(option);
        checkbox.OnClick = () =>
        {
            if (isCurrent()) option.SetCurrentValue(checkbox.Selected);
        };
        return checkbox;
    }

    private static UiScrollbar? BuildExplicitNumericSliderRow(
        UiTemplateListBox listBox,
        string labelText,
        double min,
        double max,
        double step,
        bool integer,
        double defaultValue,
        OptionPage page,
        Func<double> read,
        Action<double> apply,
        Func<bool> isCurrent)
    {
        UiElement? row = listBox.AddItemFromTemplateList(RangedSliderTemplateIndex);
        if (row is null)
        {
            Console.WriteLine(
                $"[render-pack] Slider template did not build for '{labelText}'.");
            return null;
        }
        if (UiElement.FindDescendant(row, SliderLabelElementId) is UiText label)
        {
            label.LinesProvider = () =>
            [
                new UiText.Line(labelText, label.DefaultColor),
            ];
        }
        if (UiElement.FindDescendant(row, SliderRangeMinElementId) is UiText low)
        {
            string text = FormatExplicitNumber(min, integer);
            low.LinesProvider = () => [new UiText.Line(text, low.DefaultColor)];
        }
        if (UiElement.FindDescendant(row, SliderRangeMaxElementId) is UiText high)
        {
            string text = FormatExplicitNumber(max, integer);
            high.LinesProvider = () => [new UiText.Line(text, high.DefaultColor)];
        }
        if (UiElement.FindDescendant(row, SliderElementId) is not UiScrollbar slider)
        {
            Console.WriteLine(
                $"[render-pack] No slider leaf found for '{labelText}'.");
            return null;
        }

        double initialValue = SnapExplicitNumber(read(), min, max, step, integer);
        float initial = (float)initialValue;
        slider.SetScalarPosition((float)((initialValue - min) / (max - min)));
        var option = new FloatOptionRow(
            initial,
            (float)SnapExplicitNumber(defaultValue, min, max, step, integer),
            apply: value =>
            {
                double snapped = SnapExplicitNumber(value, min, max, step, integer);
                slider.SetScalarPosition((float)((snapped - min) / (max - min)));
                if (isCurrent()) apply(snapped);
            },
            read: () => isCurrent()
                ? (float)SnapExplicitNumber(read(), min, max, step, integer)
                : initial,
            refresh: value =>
            {
                double snapped = SnapExplicitNumber(value, min, max, step, integer);
                slider.SetScalarPosition((float)((snapped - min) / (max - min)));
            });
        page.Register(option);
        slider.ScalarChanged = normalized =>
        {
            if (!isCurrent()) return;
            double raw = min + normalized * (max - min);
            option.SetCurrentValue((float)SnapExplicitNumber(raw, min, max, step, integer));
        };
        return slider;
    }

    private static double SnapExplicitNumber(
        double value,
        double min,
        double max,
        double step,
        bool integer)
    {
        double clamped = Math.Clamp(value, min, max);
        if (step > 0)
            clamped = min + Math.Round((clamped - min) / step) * step;
        if (integer)
            clamped = Math.Round(clamped);
        return Math.Clamp(clamped, min, max);
    }

    private static string FormatExplicitNumber(double value, bool integer) =>
        integer
            ? checked((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);

    private static void ApplyLabelAndTooltip(
        UiButton checkbox, string labelKey, Func<uint, uint, string?> resolveString, bool storeOnly)
    {
        string? label = resolveString(StringTableId, DatStringResolver.ComputeHash(labelKey));
        if (label is not null)
            checkbox.Label = label;
        else
            Console.WriteLine(
                $"[D.2b] ConfigOptionsPageController: label '{labelKey}' did not resolve — "
                + "row renders with no caption rather than invented English.");

        checkbox.LabelColor = storeOnly
            ? UiRenderContext.StoreOnlyCaptionColor
            : Vector4.One;

        string? tooltip = ResolveTooltip(labelKey, resolveString);
        if (tooltip is not null)
            checkbox.TooltipText = tooltip;
    }

    private static string? ResolveTooltip(string labelKey, Func<uint, uint, string?> resolveString)
        => resolveString(StringTableId, DatStringResolver.ComputeHash(labelKey + "_Help"));

    private static void SetLabelText(
        UiText label, string labelKey, Func<uint, uint, string?> resolveString, bool storeOnly)
    {
        string? text = resolveString(StringTableId, DatStringResolver.ComputeHash(labelKey));
        if (text is null)
        {
            Console.WriteLine(
                $"[D.2b] ConfigOptionsPageController: label '{labelKey}' did not resolve — "
                + "row renders with no caption rather than invented English.");
            return;
        }
        Vector4 color = storeOnly ? UiRenderContext.StoreOnlyCaptionColor : label.DefaultColor;
        label.LinesProvider = () => new[] { new UiText.Line(text, color) };
    }

    private static UiButton? FindCheckbox(UiElement root)
    {
        if (root is UiButton direct) return direct;
        foreach (UiElement child in root.Children)
            if (child is UiButton button)
                return button;
        return UiElement.FindDescendant(root, ToggleCheckboxElementId) as UiButton;
    }

    private static float ToNormalized(float real, float min, float max)
        => max > min ? (real - min) / (max - min) : 0f;

    private static float FromNormalized(float normalized, float min, float max)
        => min + normalized * (max - min);
}
