using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using AcDream.App.UI;

namespace AcDream.App.UI.Layout;

public static class CharacterStatController
{
    public const uint NameId         = 0x10000231u;   // m_pNameText
    public const uint HeritageId     = 0x10000232u;   // m_pHeritageText
    public const uint PkStatusId     = 0x10000233u;   // m_pPKStatusText
    public const uint LevelCaptionId = 0x1000023Au;
    public const uint LevelId        = 0x1000023Bu;   // m_pLevelText  (right-side level area)
    public const uint TotalXpLabelId = 0x10000234u;
    public const uint TotalXpId      = 0x10000235u;   // m_pTotalXPText
    public const uint XpMeterId      = 0x10000236u;   // m_pXPToLevelMeter (UiMeter)
    public const uint XpNextLabelId  = 0x10000237u;
    public const uint XpNextValueId  = 0x10000238u;
    public const uint ListBoxId       = 0x1000023Du;
    public const uint ListScrollbarId = 0x1000023Eu;   // m_pListBox vertical scrollbar gutter
    public const uint ListDividerId   = 0x1000023Fu;   // bottom divider above footer

    public const uint LuminanceLabelId = 0x100005C5u;
    public const uint LuminanceValueId = 0x100005C6u;

    private const string LuminanceCaption = "Luminance:";

    public const uint FooterStateAId  = 0x10000240u;
    public const uint FooterStateBId  = 0x10000241u;
    public const uint FooterStateCId  = 0x10000247u;

    public const uint TabAttribId = 0x10000228u;  // Attributes tab group
    public const uint TabSkillsId = 0x10000229u;  // Skills tab group
    public const uint TabTitlesId = 0x10000538u;  // Titles tab group

    public const uint AttributesPageId = 0x1000022Bu;
    public const uint SkillsPageId     = 0x1000022Cu;
    public const uint TitlesPageId     = 0x10000539u;

    public const uint FooterTitleId    = 0x1000024eu;  // GetFooterTitleLabel
    public const uint FooterLine1Label = 0x10000242u;  // GetFooterLineOneLabel
    public const uint FooterLine1Value = 0x10000243u;  // GetFooterLineOneValue
    public const uint FooterLine2Label = 0x10000244u;  // GetFooterLineTwoLabel
    public const uint FooterLine2Value = 0x10000245u;  // GetFooterLineTwoValue

    public const uint RaiseOneId = 0x10000246u;   // raise × 1
    public const uint RaiseTenId = 0x100005EBu;   // raise × 10

    private static readonly Vector4 Body = new(0.92f, 0.90f, 0.82f, 1f);   // parchment-white body text

    private static readonly Vector4 HighlightBg = new(1f, 0.75f, 0.2f, 0.25f);
    private static readonly Vector4 RetailBuffGreen = new(0f, 1f, 0f, 1f);
    private static readonly Vector4 RetailDebuffRed = new(1f, 0f, 0f, 1f);
    private static readonly Vector4 RetailVitaeBlue = new(127f / 255f, 1f, 1f, 1f);

    private const float RowHeight   = 20f;
    private const float RowIconX    = 0f;
    private const float RowIconSize = 20f;
    private const float RowNameX    = 25f;
    private const float RowNameW    = 150f;
    private const float RowValueX   = 175f;
    private const float RowValueW   = 100f;

    private const float RowPadX    = 4f;

    private const float SkillHeaderHeight = 20f;
    private const float SkillContentWidth = 282f;

    private const uint SkillHeaderSpecializedSprite = 0x06000F90u;
    private const uint SkillHeaderTrainedSprite     = 0x06000F86u;
    private const uint SkillHeaderUntrainedSprite   = 0x06000F98u;
    private const uint SkillHeaderUnusableSprite    = 0x06000F89u;

    private const uint RowHighlightSprite = 0x06000F93u;

    private const uint RowNormalSprite = 0x06004CC2u;

    private const uint AttributeIconCategory = 0x10000002u;
    private const uint VitalIconCategory     = 0x10000003u;


    public enum CharacterStatTab
    {
        Attributes,
        Skills,
        Titles,
    }

    public sealed record Binding(
        Action Refresh,
        Action<CharacterStatTab> ShowTab,
        Func<CharacterStatTab> CurrentTab);

    public enum RaiseTargetKind
    {
        Attribute,
        Vital,
        Skill,
        TrainSkill,
    }

    public readonly record struct RaiseRequest(
        RaiseTargetKind Kind,
        uint StatId,
        long Cost,
        int Amount);

    public delegate void RaiseRequestHandler(RaiseRequest request, Action completed);

    private sealed record SkillRowBinding(UiClickablePanel Panel, CharacterSkill Skill);

    internal static readonly (string name, uint iconDid, uint statId)[] AttrRows = new[]
    {
        ("Strength",     0x060002C8u, 1u),
        ("Endurance",    0x060002C4u, 2u),
        ("Coordination", 0x060002C9u, 4u),
        ("Quickness",    0x060002C6u, 3u),
        ("Focus",        0x060002C5u, 5u),
        ("Self",         0x060002C7u, 6u),
    };

    // Internal for the same reason as AttrRows above.
    internal static readonly (string name, uint iconDid, uint maxStatId)[] VitalRows = new[]
    {
        ("Health",  0x06004C3Bu, 1u),
        ("Stamina", 0x06004C3Cu, 3u),
        ("Mana",    0x06004C3Du, 5u),
    };

    private static readonly IReadOnlyDictionary<uint, string> AttributeDescriptions =
        new Dictionary<uint, string>
        {
            [1u] = "Measures your character's muscular power.",       // Strength
            [2u] = "Measures how healthy your character is.",         // Endurance
            [3u] = "Measures how fast your character is.",            // Quickness
            [4u] = "Measures your character's reflexes",
            [5u] = "Measures your character's mind and senses.",      // Focus
            [6u] = "Measures your character's willpower.",            // Self
        };

    private static readonly IReadOnlyDictionary<uint, string> Attribute2ndDescriptions =
        new Dictionary<uint, string>
        {
            [1u] = "(Endurance/2)\nIf you run out of health, you will die!",   // Health
            [3u] = "(Endurance)\nAffects your actions and movement.",          // Stamina
            [5u] = "(Self)\nAffects how much magic you can cast.",             // Mana
        };

    public static Binding Bind(
        ImportedLayout layout,
        Func<CharacterSheet> data,
        UiDatFont? datFont = null,
        UiDatFont? rowDatFont = null,
        Func<uint, (uint handle, int w, int h)>? spriteResolve = null,
        RaiseRequestHandler? onRaiseRequest = null,
        Action? onClose = null,
        Func<uint, uint, uint>? iconDidResolve = null)
    {
        // rowDatFont: larger font for attribute row name/value text (18px vs 16px default).
        // Falls back to datFont when null (tests, or dat missing).
        rowDatFont ??= datFont;
        WindowChromeController.BindCloseButton(layout, onClose);

        var activeTab = new[] { CharacterStatTab.Attributes };
        var attrSel   = new[] { -1 };
        var skillSel  = new[] { -1 };
        var activeListEntries = new List<UiElement>();
        var currentAttributeRows = new List<UiClickablePanel>();
        var currentSkillRows = new List<SkillRowBinding>();
        UiElement? attributesTab = layout.FindElement(TabAttribId);
        UiElement? skillsTab = layout.FindElement(TabSkillsId);
        UiElement? titlesTab = layout.FindElement(TabTitlesId);
        UiElement? contentPage = FindDirectChildById(layout.Root, AttributesPageId);
        UiElement? titlesPage = FindDirectChildById(layout.Root, TitlesPageId);

        LabelAuthoredColor(layout, contentPage, NameId,     null, () => data().Name);
        LabelAuthoredColor(layout, contentPage, HeritageId, null, () => CharacterIdentityText.StatHeaderLine(data()));
        LabelAuthoredColor(layout, contentPage, PkStatusId, null, () => data().PkStatus ?? string.Empty);

        LabelTwoLine(layout, contentPage, LevelCaptionId, null, Body, "Character", "Level");

        LabelAuthoredColor(layout, contentPage, LevelId, null,
            () => data().Level is int lvl ? lvl.ToString(CultureInfo.InvariantCulture) : "???");

        // TotalXpLabel (16px from dat) + TotalXp (16px from dat): pass null → keep dat font.
        LabelLeft(layout, contentPage, TotalXpLabelId, null, Body, static () => "Total Experience (XP):");
        LabelRight(layout, contentPage, TotalXpId, null, Body, () => FormatXp(data().TotalXp));

        if (FindElementByDatId(layout, contentPage, XpMeterId) is UiMeter meter)
        {
            meter.Fill = () => data().XpFraction;

            if (FindTextByDatId(layout, contentPage, XpNextLabelId) is UiText xpLabel)
            {
                if (datFont is not null) xpLabel.DatFont = datFont;
                xpLabel.ClickThrough = true;
                xpLabel.Centered     = false;
                xpLabel.RightAligned = false;
                xpLabel.Padding      = 0f;

                if (FindElementByDatId(layout, contentPage, TotalXpLabelId) is { } totalXpLbl)
                {
                    float xpNextLeft = totalXpLbl.Left - meter.Left;
                    xpLabel.Left = xpNextLeft >= 0f ? xpNextLeft : 0f;
                }

                xpLabel.LinesProvider = static () => new[] { new UiText.Line("XP for next level:", Body) };
            }
            if (FindTextByDatId(layout, contentPage, XpNextValueId) is UiText xpValue)
            {
                if (datFont is not null) xpValue.DatFont = datFont;
                xpValue.ClickThrough = true;
                xpValue.RightAligned = true;
                xpValue.OneLine      = true;
                xpValue.Padding      = 0f;
                xpValue.LinesProvider = () => new[] { new UiText.Line(FormatXp(data().XpToNextLevel), Body) };
            }
        }

        bool LuminanceVisible(CharacterSheet sheet) =>
            sheet.Level is int lvl && lvl >= 200 && sheet.MaximumLuminance != 0;

        if (FindTextByDatId(layout, contentPage, LuminanceLabelId) is UiText luminanceLabel)
        {
            luminanceLabel.LinesProvider = () => LuminanceVisible(data())
                ? new[] { new UiText.Line(LuminanceCaption, luminanceLabel.DefaultColor) }
                : Array.Empty<UiText.Line>();
        }
        if (FindTextByDatId(layout, contentPage, LuminanceValueId) is UiText luminanceValue)
        {
            luminanceValue.LinesProvider = () =>
            {
                var sheet = data();
                if (!LuminanceVisible(sheet)) return Array.Empty<UiText.Line>();
                string text = $"{FormatXp(sheet.AvailableLuminance)} / {FormatXp(sheet.MaximumLuminance)}";
                return new[] { new UiText.Line(text, luminanceValue.DefaultColor) };
            };
        }



        var allRaise1  = new List<UiButton>();
        var allRaise10 = new List<UiButton>();
        if (layout.Root is { } r)
        {
            CollectButtonsById(r, RaiseOneId,  allRaise1,  layout);
            CollectButtonsById(r, RaiseTenId,  allRaise10, layout);
        }
        if (allRaise1.Count  == 0 && layout.FindElement(RaiseOneId)  is UiButton b1)  allRaise1.Add(b1);
        if (allRaise10.Count == 0 && layout.FindElement(RaiseTenId)  is UiButton b10) allRaise10.Add(b10);

        var footerDefaultGroups = new List<UiElement>();
        var footerSelectedGroups = new List<UiElement>();
        var footerInactiveGroups = new List<UiElement>();
        if (contentPage is not null)
        {
            CollectElementsByDatId(contentPage, FooterStateAId, footerDefaultGroups);
            CollectElementsByDatId(contentPage, FooterStateBId, footerSelectedGroups);
            CollectElementsByDatId(contentPage, FooterStateCId, footerInactiveGroups);
        }
        else if (layout.Root is not null)
        {
            CollectElementsByDatId(layout.Root, FooterStateAId, footerDefaultGroups);
            CollectElementsByDatId(layout.Root, FooterStateBId, footerSelectedGroups);
            CollectElementsByDatId(layout.Root, FooterStateCId, footerInactiveGroups);
        }

        void SetFooterSelected(bool selected)
        {
            foreach (var g in footerDefaultGroups) g.Visible = !selected;
            foreach (var g in footerSelectedGroups) g.Visible = selected;
            foreach (var g in footerInactiveGroups) g.Visible = false;
        }

        // Initial state: raise buttons hidden until a row is selected.
        foreach (var b in allRaise1)  b.Visible = false;
        foreach (var b in allRaise10) b.Visible = false;

        SetFooterSelected(false);

        BindFooterDynamic(layout, datFont, data, activeTab, attrSel, skillSel, contentPage);
        SetFooterSelected(false);

        UiElement? statList =
            (contentPage is not null
                ? FindInSubtree(contentPage, static el => HasDatElementId(el, ListBoxId))
                : null)
            ?? layout.FindElement(ListBoxId);
        if (statList is not null && statList.LayoutPolicy is null)
            statList.Anchors = AnchorEdges.Left | AnchorEdges.Top | AnchorEdges.Bottom;

        if (layout.Root is { } stretchRoot)
        {
            SetCompatibilityAnchorsAllById(stretchRoot, ListScrollbarId, AnchorEdges.Left | AnchorEdges.Top | AnchorEdges.Bottom);
            SetCompatibilityAnchorsAllById(stretchRoot, ListDividerId, AnchorEdges.Left | AnchorEdges.Bottom);
            SetCompatibilityAnchorsAllById(stretchRoot, FooterStateAId, AnchorEdges.Left | AnchorEdges.Bottom);
            SetCompatibilityAnchorsAllById(stretchRoot, FooterStateBId, AnchorEdges.Left | AnchorEdges.Bottom);
            SetCompatibilityAnchorsAllById(stretchRoot, FooterStateCId, AnchorEdges.Left | AnchorEdges.Bottom);
        }

        UiScrollbar? skillScrollbar = PrepareSkillScrollbar(layout, contentPage, statList, spriteResolve);
        WireRaiseButtonClicks(allRaise1, allRaise10, data, activeTab, attrSel, skillSel,
            () => currentSkillRows, onRaiseRequest, RefreshAfterRaise);
        RebuildActiveList();

        RetailTabBinding.SetClick(attributesTab, () => SwitchTab(CharacterStatTab.Attributes));
        RetailTabBinding.SetClick(skillsTab, () => SwitchTab(CharacterStatTab.Skills));
        RetailTabBinding.SetClick(titlesTab, () => SwitchTab(CharacterStatTab.Titles));
        UpdateTabStates();

        if (layout.Root is { } root)
        {
            foreach (var page in root.Children)
            {
                uint id = DatElementId(page);
                if (id is AttributesPageId or SkillsPageId or TitlesPageId)
                    page.Visible = id == AttributesPageId;
            }
        }

        void SwitchTab(CharacterStatTab tab)
        {
            if (activeTab[0] == tab) return;
            activeTab[0] = tab;
            attrSel[0] = -1;
            skillSel[0] = -1;
            SetFooterSelected(false);

            bool showTitles = tab == CharacterStatTab.Titles;
            if (titlesPage is not null) titlesPage.Visible = showTitles;
            if (contentPage is not null) contentPage.Visible = !showTitles;

            if (showTitles)
            {
                foreach (var b in allRaise1)  b.Visible = false;
                foreach (var b in allRaise10) b.Visible = false;
            }
            else
            {
                RebuildActiveList();
                RefreshActiveRaiseButtons();
            }

            UpdateTabStates();
            Console.WriteLine($"[CharacterStat] Tab click: {tab}");
        }

        void UpdateTabStates()
        {
            RetailTabBinding.SetOpen(attributesTab, activeTab[0] == CharacterStatTab.Attributes);
            RetailTabBinding.SetOpen(skillsTab, activeTab[0] == CharacterStatTab.Skills);
            RetailTabBinding.SetOpen(titlesTab, activeTab[0] == CharacterStatTab.Titles);
        }

        void RebuildActiveList()
        {
            if (statList is null) return;

            foreach (var entry in activeListEntries)
                statList.RemoveChild(entry);
            activeListEntries.Clear();
            currentAttributeRows.Clear();
            currentSkillRows.Clear();

            bool isSkills = activeTab[0] == CharacterStatTab.Skills;
            float contentW = isSkills
                ? SkillViewportWidth(statList, skillScrollbar)
                : RowContentWidth(statList);
            var viewport = new UiScrollablePanel
            {
                Left = 0f,
                Top = 0f,
                Width = contentW,
                Height = statList.Height,
                LineHeight = (int)RowHeight,
                Anchors = AnchorEdges.Left | AnchorEdges.Top | AnchorEdges.Bottom,
            };
            statList.AddChild(viewport);
            viewport.CaptureCurrentAnchorBaseline();
            activeListEntries.Add(viewport);

            if (isSkills)
            {
                BuildSkillRows(viewport, rowDatFont, spriteResolve, data, skillSel,
                    allRaise1, allRaise10, SetFooterSelected, out currentSkillRows);
            }
            else
            {
                currentAttributeRows = BuildAttributeRows(viewport, rowDatFont, spriteResolve, data, attrSel,
                    allRaise1, allRaise10, SetFooterSelected, iconDidResolve);
            }

            if (skillScrollbar is not null)
            {
                skillScrollbar.Model = viewport.Scroll;
                skillScrollbar.Visible = true;
            }
        }

        void RefreshActiveRaiseButtons()
        {
            if (activeTab[0] == CharacterStatTab.Attributes)
            {
                RefreshRaiseButtons(attrSel[0], data, allRaise1, allRaise10);
                return;
            }

            CharacterSkill? selectedSkill =
                skillSel[0] >= 0 && skillSel[0] < currentSkillRows.Count
                    ? currentSkillRows[skillSel[0]].Skill
                    : null;
            RefreshSkillRaiseButtons(selectedSkill, data(), allRaise1, allRaise10);
        }

        void RefreshAfterRaise(uint? selectedSkillId)
        {
            if (activeTab[0] == CharacterStatTab.Skills)
            {
                if (selectedSkillId is null
                    && skillSel[0] >= 0
                    && skillSel[0] < currentSkillRows.Count)
                {
                    selectedSkillId = currentSkillRows[skillSel[0]].Skill.Id;
                }

                RebuildActiveList();

                skillSel[0] = -1;
                if (selectedSkillId is uint id)
                {
                    for (int i = 0; i < currentSkillRows.Count; i++)
                    {
                        if (currentSkillRows[i].Skill.Id == id)
                        {
                            skillSel[0] = i;
                            break;
                        }
                    }
                }

                ApplySkillSelectionVisuals(skillSel[0], currentSkillRows, spriteResolve);
                SetFooterSelected(skillSel[0] >= 0);
            }

            RefreshActiveRaiseButtons();
        }

        return new Binding(
            () => RefreshAfterRaise(null),
            SwitchTab,
            () => activeTab[0]);
    }

    private static UiScrollbar? PrepareSkillScrollbar(
        ImportedLayout layout,
        UiElement? contentPage,
        UiElement? statList,
        Func<uint, (uint handle, int w, int h)>? spriteResolve)
    {
        if (spriteResolve is null)
            return null;

        UiElement? source = statList?.Parent?.Children.FirstOrDefault(
                static el => HasDatElementId(el, ListScrollbarId))
            ?? (contentPage is not null
                ? FindInSubtree(contentPage, static el => HasDatElementId(el, ListScrollbarId))
                : null)
            ?? layout.FindElement(ListScrollbarId);

        if (source is UiScrollbar existingBar)
        {
            existingBar.SpriteResolve ??= id =>
            {
                var (handle, width, height) = spriteResolve(id);
                return (handle, width, height);
            };
            existingBar.Visible = false;
            return existingBar;
        }

        UiElement? parent = source?.Parent ?? statList?.Parent;
        if (parent is null || statList is null)
            return null;

        float left = source is not null
            ? source.Left
            : statList.Left + MathF.Min(statList.Width, SkillContentWidth);
        float top = source?.Top ?? statList.Top;
        float width = source?.Width > 0f ? source.Width : 16f;
        float height = source?.Height > 0f ? source.Height : statList.Height;
        int z = (source?.ZOrder ?? statList.ZOrder) + 1;

        if (source is not null)
            source.Visible = false;

        var bar = new UiScrollbar
        {
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            ZOrder = z,
            Anchors = AnchorEdges.Left | AnchorEdges.Top | AnchorEdges.Bottom,
        };
        ConfigureSkillScrollbar(bar, spriteResolve);
        bar.Visible = false;
        parent.AddChild(bar);
        return bar;
    }

    private static void ConfigureSkillScrollbar(
        UiScrollbar bar,
        Func<uint, (uint handle, int w, int h)> spriteResolve)
    {
        bar.SpriteResolve = id => { var (h, w, ht) = spriteResolve(id); return (h, w, ht); };
        RetailScrollbarChrome.ApplyVertical(bar);
    }

    private static float SkillViewportWidth(UiElement statList, UiScrollbar? bar)
    {
        if (bar is not null && ReferenceEquals(bar.Parent, statList.Parent))
        {
            float widthToGutter = bar.Left - statList.Left;
            if (widthToGutter > 32f)
                return widthToGutter;
        }

        return statList.Width > 0f
            ? MathF.Min(statList.Width, SkillContentWidth)
            : SkillContentWidth;
    }

    private static float RowContentWidth(UiElement list)
        => list.Width > 0f ? MathF.Min(list.Width, SkillContentWidth) : SkillContentWidth;

    private static uint ResolveIconDid(
        Func<uint, uint, uint>? resolver,
        uint enumValue,
        uint category,
        uint fallback)
    {
        if (resolver is null) return fallback;
        uint resolved = resolver(enumValue, category);
        return resolved != 0u ? resolved : fallback;
    }

    // ── 9-row attribute list ─────────────────────────────────────────────────

    private static List<UiClickablePanel> BuildAttributeRows(
        UiElement list,
        UiDatFont? datFont,
        Func<uint, (uint handle, int w, int h)>? spriteResolve,
        Func<CharacterSheet> data,
        int[] sel,
        List<UiButton> allRaise1,
        List<UiButton> allRaise10,
        Action<bool> setFooterSelected,
        Func<uint, uint, uint>? iconDidResolve)
    {
        float listW = RowContentWidth(list);
        float y     = 0f;
        var rows    = new List<UiClickablePanel>();

        for (int i = 0; i < AttrRows.Length; i++)
        {
            var (rowName, iconDid, statId) = AttrRows[i];
            int rowIndex = i;

            var row = AddRow(list, datFont, spriteResolve,
                left: 0f, top: y, width: listW, height: RowHeight,
                iconDid: ResolveIconDid(iconDidResolve, statId, AttributeIconCategory, iconDid),
                nameText: rowName,
                valueProvider: () =>
                {
                    var s = data();
                    int v = rowIndex switch
                    {
                        0 => s.Strength,
                        1 => s.Endurance,
                        2 => s.Coordination,
                        3 => s.Quickness,
                        4 => s.Focus,
                        5 => s.Self,
                        _ => 0,
                    };
                    return v.ToString();
                },
                valueColorProvider: () => AttributeValueColor(data(), rowIndex));
            row.TooltipText = AttributeDescriptions.GetValueOrDefault(statId);

            row.OnClick = () =>
            {
                HandleRowClick(rowIndex, sel, rows, spriteResolve, data, allRaise1, allRaise10);
                setFooterSelected(sel[0] >= 0);
            };
            rows.Add(row);
            y += RowHeight;
        }

        for (int i = 0; i < VitalRows.Length; i++)
        {
            var (rowName, iconDid, maxStatId) = VitalRows[i];
            int rowIndex = i;
            int absIndex = AttrRows.Length + i;

            var row = AddRow(list, datFont, spriteResolve,
                left: 0f, top: y, width: listW, height: RowHeight,
                iconDid: ResolveIconDid(iconDidResolve, maxStatId, VitalIconCategory, iconDid),
                nameText: rowName,
                valueProvider: () =>
                {
                    var s = data();
                    return rowIndex switch
                    {
                        0 => $"{s.HealthCurrent}/{s.HealthMax}",
                        1 => $"{s.StaminaCurrent}/{s.StaminaMax}",
                        2 => $"{s.ManaCurrent}/{s.ManaMax}",
                        _ => string.Empty,
                    };
                },
                valueColorProvider: () => VitalValueColor(data(), rowIndex));
            row.TooltipText = Attribute2ndDescriptions.GetValueOrDefault(maxStatId);

            row.OnClick = () =>
            {
                HandleRowClick(absIndex, sel, rows, spriteResolve, data, allRaise1, allRaise10);
                setFooterSelected(sel[0] >= 0);
            };
            rows.Add(row);
            y += RowHeight;
        }

        return rows;
    }

    private static List<UiElement> BuildSkillRows(
        UiElement list,
        UiDatFont? datFont,
        Func<uint, (uint handle, int w, int h)>? spriteResolve,
        Func<CharacterSheet> data,
        int[] sel,
        List<UiButton> allRaise1,
        List<UiButton> allRaise10,
        Action<bool> setFooterSelected,
        out List<SkillRowBinding> skillRows)
    {
        float listW = RowContentWidth(list);
        float y = 0f;
        var entries = new List<UiElement>();
        var bindings = new List<SkillRowBinding>();

        AddBucket("Specialized Skills", SkillHeaderSpecializedSprite,
            OrderedSkills(data(), CharacterSkillAdvancementClass.Specialized, usableUntrained: null));
        AddBucket("Trained Skills", SkillHeaderTrainedSprite,
            OrderedSkills(data(), CharacterSkillAdvancementClass.Trained, usableUntrained: null));
        AddBucket("Untrained Skills", SkillHeaderUntrainedSprite,
            OrderedSkills(data(), CharacterSkillAdvancementClass.Untrained, usableUntrained: true));
        AddBucket("Unusable Skills", SkillHeaderUnusableSprite,
            OrderedSkills(data(), CharacterSkillAdvancementClass.Untrained, usableUntrained: false));

        skillRows = bindings;
        return entries;

        void AddBucket(string title, uint spriteId, IReadOnlyList<CharacterSkill> skills)
        {
            var header = AddSkillHeader(list, datFont, spriteResolve, 0f, y, listW, title, spriteId);
            entries.Add(header);
            y += SkillHeaderHeight;

            foreach (var skill in skills)
            {
                int rowIndex = bindings.Count;
                CharacterSkill LiveSkill() =>
                    FindSkill(data(), skill.Id) ?? skill;
                var row = AddRow(list, datFont, spriteResolve,
                    left: 0f, top: y, width: listW, height: RowHeight,
                    iconDid: skill.IconDid,
                    nameText: skill.Name,
                    valueProvider: () => LiveSkill().CurrentLevel.ToString(),
                    valueColorProvider: () => SkillValueColor(LiveSkill()),
                    nameColor: Vector4.One);
                row.TooltipText = skill.TooltipText;
                row.OnClick = () =>
                {
                    HandleSkillRowClick(rowIndex, sel, bindings, spriteResolve, data, allRaise1, allRaise10);
                    setFooterSelected(sel[0] >= 0);
                };
                bindings.Add(new SkillRowBinding(row, skill));
                entries.Add(row);
                y += RowHeight;
            }
        }
    }

    private static UiPanel AddSkillHeader(
        UiElement list,
        UiDatFont? datFont,
        Func<uint, (uint handle, int w, int h)>? spriteResolve,
        float left,
        float top,
        float width,
        string title,
        uint spriteId)
    {
        var header = new UiPanel
        {
            Left = left,
            Top = top,
            Width = width,
            Height = SkillHeaderHeight,
            BackgroundColor = spriteResolve is null ? new Vector4(0.12f, 0.12f, 0.14f, 0.65f) : Vector4.Zero,
            BackgroundSprite = spriteResolve is not null ? spriteId : 0u,
            SpriteResolve = spriteResolve is not null
                ? id => { var (h, w, ht) = spriteResolve(id); return (h, w, ht); }
                : null,
            BorderColor = Vector4.Zero,
            Anchors = AnchorEdges.Left | AnchorEdges.Top,
            ClickThrough = true,
        };

        var label = new UiText
        {
            Left = RowPadX,
            Top = 0f,
            Width = MathF.Max(1f, width - RowPadX * 2f),
            Height = SkillHeaderHeight,
            DatFont = datFont,
            ClickThrough = true,
            Centered = false,
            RightAligned = false,
            Padding = 1f,
            Anchors = AnchorEdges.Left | AnchorEdges.Top,
        };
        string captured = title;
        label.LinesProvider = () => new[] { new UiText.Line(captured, Vector4.One) };
        header.AddChild(label);
        list.AddChild(header);
        return header;
    }

    private static IReadOnlyList<CharacterSkill> OrderedSkills(
        CharacterSheet sheet,
        CharacterSkillAdvancementClass advancement,
        bool? usableUntrained)
    {
        var result = new List<CharacterSkill>();
        foreach (var skill in sheet.Skills)
        {
            if (skill.AdvancementClass != advancement) continue;
            if (usableUntrained is not null && skill.UsableUntrained != usableUntrained.Value) continue;
            result.Add(skill);
        }
        result.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        return result;
    }

    private static CharacterSkill? FindSkill(CharacterSheet sheet, uint skillId)
    {
        IReadOnlyList<CharacterSkill> skills = sheet.Skills;
        for (int i = 0; i < skills.Count; i++)
        {
            CharacterSkill skill = skills[i];
            if (skill.Id == skillId)
                return skill;
        }
        return null;
    }

    private static CharacterSkill? SkillAtDisplayIndex(CharacterSheet sheet, int index)
    {
        if (index < 0) return null;
        int n = 0;
        foreach (var bucket in new[]
        {
            OrderedSkills(sheet, CharacterSkillAdvancementClass.Specialized, usableUntrained: null),
            OrderedSkills(sheet, CharacterSkillAdvancementClass.Trained, usableUntrained: null),
            OrderedSkills(sheet, CharacterSkillAdvancementClass.Untrained, usableUntrained: true),
            OrderedSkills(sheet, CharacterSkillAdvancementClass.Untrained, usableUntrained: false),
        })
        {
            foreach (var skill in bucket)
            {
                if (n == index) return skill;
                n++;
            }
        }
        return null;
    }

    internal static Vector4 SkillValueColor(CharacterSkill skill)
    {
        int withoutVitae = skill.CurrentLevel - skill.VitaeModifier;
        return withoutVitae > skill.BaseLevel ? RetailBuffGreen
         : withoutVitae < skill.BaseLevel ? RetailDebuffRed
         : Vector4.One;
    }

    internal static Vector4 AttributeValueColor(
        CharacterSheet sheet,
        int rowIndex)
    {
        int delta = GetAttributeDelta(sheet, rowIndex);
        return delta > 0 ? RetailBuffGreen
            : delta < 0 ? RetailDebuffRed
            : Vector4.One;
    }

    internal static Vector4 VitalValueColor(
        CharacterSheet sheet,
        int vitalIndex)
    {
        if ((uint)vitalIndex >= 3u
            || vitalIndex >= sheet.VitalBaseMaxValues.Length
            || vitalIndex >= sheet.VitalVitaeModifiers.Length)
        {
            return Vector4.One;
        }

        int effective = vitalIndex switch
        {
            0 => sheet.HealthMax,
            1 => sheet.StaminaMax,
            2 => sheet.ManaMax,
            _ => 0,
        };
        int withoutVitae = effective - sheet.VitalVitaeModifiers[vitalIndex];
        int baseline = sheet.VitalBaseMaxValues[vitalIndex];
        return withoutVitae > baseline ? RetailBuffGreen
            : withoutVitae < baseline ? RetailDebuffRed
            : Vector4.One;
    }

    private static void HandleRowClick(
        int clickedIndex,
        int[] sel,
        List<UiClickablePanel> rows,
        Func<uint, (uint handle, int w, int h)>? spriteResolve,
        Func<CharacterSheet> data,
        List<UiButton> allRaise1,
        List<UiButton> allRaise10)
    {
        int newSel = (sel[0] == clickedIndex) ? -1 : clickedIndex;
        sel[0] = newSel;

        string rowName = GetRowName(newSel);
        Console.WriteLine($"[CharacterStat] Row click: index={clickedIndex} → selected={newSel} ({rowName})");

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (i == newSel)
            {
                if (spriteResolve is not null)
                {
                    row.BackgroundColor  = Vector4.Zero;
                    row.BackgroundSprite = RowHighlightSprite;
                    row.SpriteResolve    = spriteResolve;
                }
                else
                {
                    row.BackgroundColor  = HighlightBg;
                    row.BackgroundSprite = 0u;
                    row.SpriteResolve    = null;
                }
            }
            else
            {
                row.BackgroundColor  = Vector4.Zero;
                row.BackgroundSprite = spriteResolve is not null ? RowNormalSprite : 0u;
                row.SpriteResolve    = spriteResolve;
            }
        }

        // Update raise buttons.
        RefreshRaiseButtons(newSel, data, allRaise1, allRaise10);
    }

    private static void HandleSkillRowClick(
        int clickedIndex,
        int[] sel,
        List<SkillRowBinding> rows,
        Func<uint, (uint handle, int w, int h)>? spriteResolve,
        Func<CharacterSheet> data,
        List<UiButton> allRaise1,
        List<UiButton> allRaise10)
    {
        int newSel = (sel[0] == clickedIndex) ? -1 : clickedIndex;
        sel[0] = newSel;

        string rowName = newSel >= 0 && newSel < rows.Count ? rows[newSel].Skill.Name : string.Empty;
        Console.WriteLine($"[CharacterStat] Skill row click: index={clickedIndex} -> selected={newSel} ({rowName})");

        ApplySkillSelectionVisuals(newSel, rows, spriteResolve);

        CharacterSkill? selectedSkill = newSel >= 0 && newSel < rows.Count ? rows[newSel].Skill : null;
        RefreshSkillRaiseButtons(selectedSkill, data(), allRaise1, allRaise10);
    }

    private static void ApplySkillSelectionVisuals(
        int selectedIndex,
        IReadOnlyList<SkillRowBinding> rows,
        Func<uint, (uint handle, int w, int h)>? spriteResolve)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i].Panel;
            if (i == selectedIndex)
            {
                if (spriteResolve is not null)
                {
                    row.BackgroundColor = Vector4.Zero;
                    row.BackgroundSprite = RowHighlightSprite;
                    row.SpriteResolve = spriteResolve;
                }
                else
                {
                    row.BackgroundColor = HighlightBg;
                    row.BackgroundSprite = 0u;
                    row.SpriteResolve = null;
                }
            }
            else
            {
                row.BackgroundColor = Vector4.Zero;
                row.BackgroundSprite = spriteResolve is not null ? RowNormalSprite : 0u;
                row.SpriteResolve = spriteResolve;
            }
        }
    }

    private static void RefreshRaiseButtons(
        int selectedIndex,
        Func<CharacterSheet> data,
        List<UiButton> allRaise1,
        List<UiButton> allRaise10)
    {
        if (allRaise1.Count == 0 && allRaise10.Count == 0) return;

        if (selectedIndex < 0)
        {
            // Nothing selected: hide all raise buttons.
            foreach (var b in allRaise1)  b.Visible = false;
            foreach (var b in allRaise10) b.Visible = false;
            return;
        }

        var sheet = data();
        long cost1 = GetRaiseCost(sheet, selectedIndex, amount: 1);
        long cost10 = GetRaiseCost(sheet, selectedIndex, amount: 10);
        bool affordable1 = !sheet.AwaitingRaise && cost1 > 0 && sheet.UnassignedXp >= cost1;
        bool affordable10 = !sheet.AwaitingRaise && cost10 > 0 && sheet.UnassignedXp >= cost10;

        foreach (var b in allRaise1)
        {
            b.Visible = true;
            b.TrySetRetailState(affordable1
                ? UiButtonStateMachine.Normal
                : UiButtonStateMachine.Ghosted);
        }
        foreach (var b in allRaise10)
        {
            b.Visible = true;
            b.TrySetRetailState(affordable10
                ? UiButtonStateMachine.Normal
                : UiButtonStateMachine.Ghosted);
        }
    }

    private static void RefreshSkillRaiseButtons(
        CharacterSkill? selectedSkill,
        CharacterSheet sheet,
        List<UiButton> allRaise1,
        List<UiButton> allRaise10)
    {
        if (selectedSkill is null)
        {
            foreach (var b in allRaise1)  b.Visible = false;
            foreach (var b in allRaise10) b.Visible = false;
            return;
        }

        bool trained = selectedSkill.AdvancementClass >= CharacterSkillAdvancementClass.Trained;
        long cost = trained ? selectedSkill.RaiseCost : selectedSkill.TrainedCost;
        bool affordable = !sheet.AwaitingRaise && (trained
            ? cost > 0 && sheet.UnassignedXp >= cost
            : cost > 0 && sheet.SkillCredits >= cost);
        foreach (var b in allRaise1)
        {
            b.Visible = true;
            b.TrySetRetailState(affordable
                ? UiButtonStateMachine.Normal
                : UiButtonStateMachine.Ghosted);
        }

        foreach (var b in allRaise10)
        {
            b.Visible = trained;
            if (trained)
            {
                long cost10 = selectedSkill.Raise10Cost;
                bool affordable10 = !sheet.AwaitingRaise && cost10 > 0 && sheet.UnassignedXp >= cost10;
                b.TrySetRetailState(affordable10
                    ? UiButtonStateMachine.Normal
                    : UiButtonStateMachine.Ghosted);
            }
        }
    }

    private static void WireRaiseButtonClicks(
        List<UiButton> allRaise1,
        List<UiButton> allRaise10,
        Func<CharacterSheet> data,
        CharacterStatTab[] activeTab,
        int[] attrSel,
        int[] skillSel,
        Func<IReadOnlyList<SkillRowBinding>> skillRows,
        RaiseRequestHandler? onRaiseRequest,
        Action<uint?>? afterRaiseRequest)
    {
        foreach (var button in allRaise1)
        {
            UiButton captured = button;
            captured.OnClick = () => HandleRaiseButtonClick(
                amount: 1, data, activeTab, attrSel, skillSel, skillRows, onRaiseRequest, afterRaiseRequest);
        }

        foreach (var button in allRaise10)
        {
            UiButton captured = button;
            captured.OnClick = () => HandleRaiseButtonClick(
                amount: 10, data, activeTab, attrSel, skillSel, skillRows, onRaiseRequest, afterRaiseRequest);
        }
    }

    private static void HandleRaiseButtonClick(
        int amount,
        Func<CharacterSheet> data,
        CharacterStatTab[] activeTab,
        int[] attrSel,
        int[] skillSel,
        Func<IReadOnlyList<SkillRowBinding>> skillRows,
        RaiseRequestHandler? onRaiseRequest,
        Action<uint?>? afterRaiseRequest)
    {
        if (onRaiseRequest is null) return;

        var sheet = data();
        RaiseRequest? request;
        uint? selectedSkillId = null;

        if (activeTab[0] == CharacterStatTab.Attributes)
        {
            request = TryBuildAttributeRaiseRequest(sheet, attrSel[0], amount);
        }
        else
        {
            var rows = skillRows();
            CharacterSkill? selectedSkill =
                skillSel[0] >= 0 && skillSel[0] < rows.Count
                    ? rows[skillSel[0]].Skill
                    : null;
            selectedSkillId = selectedSkill?.Id;
            request = TryBuildSkillRaiseRequest(sheet, selectedSkill, amount);
        }

        if (request is not { } value) return;

        onRaiseRequest(value, () => afterRaiseRequest?.Invoke(selectedSkillId));
    }

    private static RaiseRequest? TryBuildAttributeRaiseRequest(
        CharacterSheet sheet,
        int selectedIndex,
        int amount)
    {
        if (selectedIndex < 0) return null;

        long cost = GetRaiseCost(sheet, selectedIndex, amount);
        if (cost <= 0 || sheet.UnassignedXp < cost) return null;

        if (selectedIndex < AttrRows.Length)
            return new RaiseRequest(RaiseTargetKind.Attribute, AttrRows[selectedIndex].statId, cost, amount);

        int vitalIndex = selectedIndex - AttrRows.Length;
        if (vitalIndex >= 0 && vitalIndex < VitalRows.Length)
            return new RaiseRequest(RaiseTargetKind.Vital, VitalRows[vitalIndex].maxStatId, cost, amount);

        return null;
    }

    private static RaiseRequest? TryBuildSkillRaiseRequest(
        CharacterSheet sheet,
        CharacterSkill? selectedSkill,
        int amount)
    {
        if (selectedSkill is null) return null;

        bool trained = selectedSkill.AdvancementClass >= CharacterSkillAdvancementClass.Trained;
        if (!trained)
        {
            if (amount != 1) return null;
            long trainCost = selectedSkill.TrainedCost;
            return trainCost > 0 && sheet.SkillCredits >= trainCost
                ? new RaiseRequest(RaiseTargetKind.TrainSkill, selectedSkill.Id, trainCost, amount)
                : null;
        }

        long raiseCost = amount == 10 ? selectedSkill.Raise10Cost : selectedSkill.RaiseCost;
        return raiseCost > 0 && sheet.UnassignedXp >= raiseCost
            ? new RaiseRequest(RaiseTargetKind.Skill, selectedSkill.Id, raiseCost, amount == 10 ? 10 : 1)
            : null;
    }

    internal static long GetRaiseCost(CharacterSheet sheet, int rowIndex)
        => GetRaiseCost(sheet, rowIndex, amount: 1);

    internal static long GetRaiseCost(CharacterSheet sheet, int rowIndex, int amount)
    {
        var costs = amount == 10 ? sheet.AttributeRaise10Costs : sheet.AttributeRaiseCosts;
        if (costs is null || rowIndex < 0 || rowIndex >= costs.Length)
            return 0L;
        return costs[rowIndex];
    }

    /// <summary>Return the display name for the row at <paramref name="index"/>,
    /// or an empty string if the index is out of range.</summary>
    internal static string GetRowName(int index)
    {
        if (index < 0) return string.Empty;
        if (index < AttrRows.Length)  return AttrRows[index].name;
        int vi = index - AttrRows.Length;
        if (vi < VitalRows.Length) return VitalRows[vi].name;
        return string.Empty;
    }

    /// <summary>Return the numeric value for the row at <paramref name="index"/>.</summary>
    internal static string GetRowValueString(CharacterSheet sheet, int index)
    {
        return index switch
        {
            0 => sheet.Strength.ToString(),
            1 => sheet.Endurance.ToString(),
            2 => sheet.Coordination.ToString(),
            3 => sheet.Quickness.ToString(),
            4 => sheet.Focus.ToString(),
            5 => sheet.Self.ToString(),
            6 => $"{sheet.HealthCurrent}/{sheet.HealthMax}",
            7 => $"{sheet.StaminaCurrent}/{sheet.StaminaMax}",
            8 => $"{sheet.ManaCurrent}/{sheet.ManaMax}",
            _ => string.Empty,
        };
    }


    private static int GetRowEffectiveAttributeValue(CharacterSheet sheet, int index) => index switch
    {
        0 => sheet.Strength,
        1 => sheet.Endurance,
        2 => sheet.Coordination,
        3 => sheet.Quickness,
        4 => sheet.Focus,
        5 => sheet.Self,
        _ => 0,
    };

    internal static int GetAttributeDelta(CharacterSheet sheet, int index)
    {
        if ((uint)index >= (uint)AttrRows.Length) return 0;
        int[] baseValues = sheet.AttributeBaseValues;
        if (baseValues is null || index >= baseValues.Length) return 0;
        return GetRowEffectiveAttributeValue(sheet, index) - baseValues[index];
    }

    internal static int GetSkillBuffOnlyDelta(CharacterSkill skill) =>
        (skill.CurrentLevel - skill.VitaeModifier) - skill.BaseLevel;

    private static string FormatBuffDelta(int delta) => delta switch
    {
        0 => string.Empty,
        > 0 => string.Create(CultureInfo.InvariantCulture, $" (+{delta})"),
        _ => string.Create(CultureInfo.InvariantCulture, $" ({delta})"),
    };

    private static string FormatVitaeDelta(int vitaeModifier) =>
        vitaeModifier < 0
            ? string.Create(CultureInfo.InvariantCulture, $" ({vitaeModifier})")
            : string.Empty;

    private static string BuildSelectedTitleText(
        CharacterStatTab tab,
        Func<CharacterSheet> data,
        int[] attrSel,
        int[] skillSel)
    {
        if (tab == CharacterStatTab.Skills)
        {
            CharacterSkill? skill = SkillAtDisplayIndex(data(), skillSel[0]);
            if (skill is null) return "Select a Skill to Improve";
            if (skill.AdvancementClass < CharacterSkillAdvancementClass.Trained)
                return skill.Name;

            string vitaeSuffix = FormatVitaeDelta(skill.VitaeModifier);
            string buffSuffix = FormatBuffDelta(GetSkillBuffOnlyDelta(skill));
            return $"{skill.Name}: {skill.CurrentLevel}{vitaeSuffix}{buffSuffix}";
        }

        if (attrSel[0] < 0) return "Select an Attribute to Improve";
        CharacterSheet sheet = data();
        string name = GetRowName(attrSel[0]);
        string value = GetRowValueString(sheet, attrSel[0]);
        string delta = FormatBuffDelta(GetAttributeDelta(sheet, attrSel[0]));
        return $"{name}: {value}{delta}";
    }

    private static IReadOnlyList<UiText.TextRun> BuildSelectedTitleRuns(
        UiText target,
        CharacterStatTab tab,
        Func<CharacterSheet> data,
        int[] attrSel,
        int[] skillSel)
    {
        Vector4 Color(int index) =>
            index >= 0 && index < target.FontColorPalette.Count
                ? target.FontColorPalette[index]
                : index switch
                {
                    1 => RetailBuffGreen,
                    2 => RetailDebuffRed,
                    3 => RetailVitaeBlue,
                    _ => Vector4.One,
                };

        if (tab == CharacterStatTab.Skills)
        {
            CharacterSkill? skill = SkillAtDisplayIndex(data(), skillSel[0]);
            if (skill is null)
                return [new("Select a Skill to Improve", Body)];
            if (skill.AdvancementClass < CharacterSkillAdvancementClass.Trained)
                return [new(skill.Name, Color(0))];

            var runs = new List<UiText.TextRun>
            {
                new($"{skill.Name}: {skill.CurrentLevel}", Color(0)),
            };
            if (skill.VitaeModifier < 0)
                runs.Add(new(FormatVitaeDelta(skill.VitaeModifier), Color(3)));
            int buffDelta = GetSkillBuffOnlyDelta(skill);
            if (buffDelta != 0)
                runs.Add(new(
                    FormatBuffDelta(buffDelta),
                    Color(buffDelta > 0 ? 1 : 2)));
            return runs;
        }

        if (attrSel[0] < 0)
            return [new("Select an Attribute to Improve", Body)];

        CharacterSheet sheet = data();
        var attributeRuns = new List<UiText.TextRun>
        {
            new(
                $"{GetRowName(attrSel[0])}: {GetRowValueString(sheet, attrSel[0])}",
                Color(0)),
        };
        int delta = GetAttributeDelta(sheet, attrSel[0]);
        if (delta != 0)
            attributeRuns.Add(new(
                FormatBuffDelta(delta),
                Color(delta > 0 ? 1 : 2)));
        return attributeRuns;
    }

    private static UiClickablePanel AddRow(
        UiElement list,
        UiDatFont? datFont,
        Func<uint, (uint handle, int w, int h)>? spriteResolve,
        float left, float top, float width, float height,
        uint iconDid,
        string nameText,
        Func<string> valueProvider,
        Func<Vector4>? valueColorProvider = null,
        Vector4? nameColor = null)
    {
        var row = new UiClickablePanel
        {
            AuthoredTooltipRootElementId =
                RetailTooltipPresenter.SharedPopupSkinRootElementId,
            AuthoredTooltipLayoutDid =
                RetailTooltipPresenter.SharedPopupSkinLayoutDid,
            Left             = left,
            Top              = top,
            Width            = width,
            Height           = height,
            BackgroundColor  = Vector4.Zero,
            BackgroundSprite = spriteResolve is not null ? RowNormalSprite : 0u,
            SpriteResolve    = spriteResolve,
            BorderColor      = Vector4.Zero,
            Anchors          = AnchorEdges.Left | AnchorEdges.Top,
        };

        var iconEl = new UiText
        {
            Left            = RowIconX,
            Top             = 0f,
            Width           = RowIconSize,
            Height          = RowIconSize,
            ClickThrough    = true,
            DatFont         = null,
            BackgroundSprite = spriteResolve is not null ? iconDid : 0u,
            SpriteResolve   = spriteResolve is not null
                ? id => { var (h, w, ht) = spriteResolve(id); return (h, w, ht); }
                : null,
            LinesProvider   = static () => Array.Empty<UiText.Line>(),
            Anchors         = AnchorEdges.Left | AnchorEdges.Top,
        };

        string capturedName = nameText;
        Vector4 capturedNameColor = nameColor ?? Body;
        var nameEl = new UiText
        {
            Left         = RowNameX,
            Top          = 0f,
            Width        = RowNameW,
            Height       = height,
            DatFont      = datFont,
            ClickThrough = true,
            Centered     = false,
            RightAligned = false,
            Padding      = 0f,
            OneLine      = true,
            Anchors      = AnchorEdges.Left | AnchorEdges.Top,
        };
        nameEl.LinesProvider = () => new[] { new UiText.Line(capturedName, capturedNameColor) };

        var valueEl = new UiText
        {
            Left         = RowValueX,
            Top          = 0f,
            Width        = RowValueW,
            Height       = height,
            DatFont      = datFont,
            ClickThrough = true,
            RightAligned = true,
            OneLine      = true,
            Anchors      = AnchorEdges.Left | AnchorEdges.Top,
        };
        var capturedProvider = valueProvider;
        valueEl.LinesProvider = () => new[] { new UiText.Line(capturedProvider(), valueColorProvider?.Invoke() ?? Body) };

        row.AddChild(iconEl);
        row.AddChild(nameEl);
        row.AddChild(valueEl);
        list.AddChild(row);
        return row;
    }

    // ── Footer — dynamic (State A + State B via sel[]) ────────────────────────

    private static void BindFooterDynamic(
        ImportedLayout layout,
        UiDatFont? datFont,
        Func<CharacterSheet> data,
        CharacterStatTab[] activeTab,
        int[] attrSel,
        int[] skillSel,
        UiElement? contentPage = null)
    {
        UiElement? stateA = contentPage is not null
            ? FindInSubtree(contentPage, static el => el is UiDatElement d && d.ElementId == FooterStateAId)
            : null;
        UiElement? stateB = contentPage is not null
            ? FindInSubtree(contentPage, static el => el is UiDatElement d && d.ElementId == FooterStateBId)
            : null;
        // Fallback: layout._byId (test layouts with a single page).
        stateA ??= layout.FindElement(FooterStateAId);
        stateB ??= layout.FindElement(FooterStateBId);

        UiText? ByPos(float top, float left, uint fallbackId)
        {
            if (stateA is not null)
            {
                foreach (var c in stateA.Children)
                    if (c is UiText t
                        && Math.Abs(c.Top  - top)  < 1f
                        && Math.Abs(c.Left - left) < 1f)
                        return t;
            }
            return layout.FindElement(fallbackId) as UiText;
        }

        var titleEl = ByPos(0f, 0f, FooterTitleId);
        if (titleEl is not null)
        {
            titleEl.BackgroundSprite = 0;
            titleEl.VerticalJustify  = VJustify.Top;
            titleEl.OneLine          = true;
        }
        if (titleEl is not null)
        {
            titleEl.ClickThrough = true;
            titleEl.RunsProvider = () => BuildSelectedTitleRuns(
                titleEl,
                activeTab[0],
                data,
                attrSel,
                skillSel);
            titleEl.LinesProvider = () =>
            {
                string title = BuildSelectedTitleText(activeTab[0], data, attrSel, skillSel);
                bool nothingSelected = activeTab[0] == CharacterStatTab.Skills
                    ? SkillAtDisplayIndex(data(), skillSel[0]) is null
                    : attrSel[0] < 0;
                return new[] { new UiText.Line(title, nothingSelected ? Body : Vector4.One) };
            };
        }

        // Footer lines (all dat-origin with their own font sizes): pass null → keep dat font.
        var l1L = ByPos(20f, 5f, FooterLine1Label);
        LabelProvider(l1L, null, Body, () =>
        {
            if (activeTab[0] == CharacterStatTab.Skills)
            {
                var skill = SkillAtDisplayIndex(data(), skillSel[0]);
                if (skill is null) return "Skill Credits Available:";
                return skill.AdvancementClass >= CharacterSkillAdvancementClass.Trained
                    ? "Experience To Raise:"
                    : "Skill Credits To Raise:";
            }

            return attrSel[0] < 0 ? "Skill Credits Available:" : "Experience To Raise:";
        });

        var l1V = ByPos(20f, 200f, FooterLine1Value);
        LabelProvider(l1V, null, Body, () =>
        {
            var sheet = data();
            if (activeTab[0] == CharacterStatTab.Skills)
            {
                var skill = SkillAtDisplayIndex(sheet, skillSel[0]);
                if (skill is null) return sheet.SkillCredits.ToString();
                long skillCost = skill.AdvancementClass >= CharacterSkillAdvancementClass.Trained
                    ? skill.RaiseCost
                    : skill.TrainedCost;
                return skillCost > 0 ? FormatXp(skillCost) : "Infinity!";
            }

            if (attrSel[0] < 0) return sheet.SkillCredits.ToString();
            long cost = GetRaiseCost(sheet, attrSel[0]);
            return cost > 0 ? FormatXp(cost) : "Infinity!";
        });

        // Line-2 elements: pass null → keep dat font.
        var l2L = ByPos(37f, 5f, FooterLine2Label);
        LabelProvider(l2L, null, Body, () =>
        {
            if (activeTab[0] == CharacterStatTab.Skills)
            {
                var skill = SkillAtDisplayIndex(data(), skillSel[0]);
                if (skill is not null && skill.AdvancementClass < CharacterSkillAdvancementClass.Trained)
                    return "Skill Credits Available:";
            }
            return "Unassigned Experience:";
        });

        var l2V = ByPos(37f, 200f, FooterLine2Value);
        LabelProvider(l2V, null, Body, () =>
        {
            var sheet = data();
            if (activeTab[0] == CharacterStatTab.Skills)
            {
                var skill = SkillAtDisplayIndex(sheet, skillSel[0]);
                if (skill is not null && skill.AdvancementClass < CharacterSkillAdvancementClass.Trained)
                    return sheet.SkillCredits.ToString();
            }
            return FormatXp(sheet.UnassignedXp);
        });

        BindSelectedFooterState(stateB);

        UiText? TextById(UiElement? state, uint id)
            => state is null
                ? null
                : FindInSubtree(state, el => el is UiText t && t.ElementId == id) as UiText;

        void BindSelectedFooterState(UiElement? state)
        {
            var title = TextById(state, FooterTitleId);
            if (title is not null)
            {
                title.BackgroundSprite = 0;
                title.VerticalJustify = VJustify.Top;
                title.ClickThrough = true;
                title.LinesProvider = () =>
                {
                    string titleText = BuildSelectedTitleText(activeTab[0], data, attrSel, skillSel);
                    bool nothingSelected = activeTab[0] == CharacterStatTab.Skills
                        ? SkillAtDisplayIndex(data(), skillSel[0]) is null
                        : attrSel[0] < 0;
                    return new[] { new UiText.Line(titleText, nothingSelected ? Body : Vector4.One) };
                };
            }

            LabelProvider(TextById(state, FooterLine1Label), null, Body, () =>
            {
                if (activeTab[0] == CharacterStatTab.Skills)
                {
                    var skill = SkillAtDisplayIndex(data(), skillSel[0]);
                    if (skill is null) return "Skill Credits Available:";
                    return skill.AdvancementClass >= CharacterSkillAdvancementClass.Trained
                        ? "Experience To Raise:"
                        : "Skill Credits To Raise:";
                }

                return attrSel[0] < 0 ? "Skill Credits Available:" : "Experience To Raise:";
            });

            LabelProvider(TextById(state, FooterLine1Value), null, Body, () =>
            {
                var sheet = data();
                if (activeTab[0] == CharacterStatTab.Skills)
                {
                    var skill = SkillAtDisplayIndex(sheet, skillSel[0]);
                    if (skill is null) return sheet.SkillCredits.ToString();
                    long skillCost = skill.AdvancementClass >= CharacterSkillAdvancementClass.Trained
                        ? skill.RaiseCost
                        : skill.TrainedCost;
                    return skillCost > 0 ? FormatXp(skillCost) : "Infinity!";
                }

                if (attrSel[0] < 0) return sheet.SkillCredits.ToString();
                long cost = GetRaiseCost(sheet, attrSel[0]);
                return cost > 0 ? FormatXp(cost) : "Infinity!";
            });

            LabelProvider(TextById(state, FooterLine2Label), null, Body, () =>
            {
                if (activeTab[0] == CharacterStatTab.Skills)
                {
                    var skill = SkillAtDisplayIndex(data(), skillSel[0]);
                    if (skill is not null && skill.AdvancementClass < CharacterSkillAdvancementClass.Trained)
                        return "Skill Credits Available:";
                }
                return "Unassigned Experience:";
            });

            LabelProvider(TextById(state, FooterLine2Value), null, Body, () =>
            {
                var sheet = data();
                if (activeTab[0] == CharacterStatTab.Skills)
                {
                    var skill = SkillAtDisplayIndex(sheet, skillSel[0]);
                    if (skill is not null && skill.AdvancementClass < CharacterSkillAdvancementClass.Trained)
                        return sheet.SkillCredits.ToString();
                }
                return FormatXp(sheet.UnassignedXp);
            });
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void SetCompatibilityAnchorsAllById(
        UiElement node,
        uint targetId,
        AnchorEdges anchors)
    {
        if (node is UiDatElement d
            && d.ElementId == targetId
            && node.LayoutPolicy is null)
        {
            node.Anchors = anchors;
        }
        foreach (var child in node.Children)
            SetCompatibilityAnchorsAllById(child, targetId, anchors);
    }

    /// <summary>Depth-first search of <paramref name="node"/> and its descendants.
    /// Returns the first element for which <paramref name="predicate"/> returns true,
    /// or null if none found.</summary>
    private static UiElement? FindInSubtree(UiElement node, Func<UiElement, bool> predicate)
    {
        if (predicate(node)) return node;
        foreach (var child in node.Children)
        {
            var found = FindInSubtree(child, predicate);
            if (found is not null) return found;
        }
        return null;
    }

    private static bool HasDatElementId(UiElement element, uint id)
        => DatElementId(element) == id;

    private static uint DatElementId(UiElement element)
    {
        if (element.DatElementId != 0u)
            return element.DatElementId;

        return element switch
        {
            UiDatElement datElement => datElement.ElementId,
            UiButton button => button.ElementId,
            UiMeter meter => meter.ElementId,
            UiText text => text.ElementId,
            _ => 0u,
        };
    }

    private static UiElement? FindElementByDatId(ImportedLayout layout, UiElement? scope, uint id)
    {
        if (scope is not null)
        {
            var scoped = FindInSubtree(scope, el => HasDatElementId(el, id));
            if (scoped is not null)
                return scoped;
        }

        return layout.FindElement(id);
    }

    private static UiText? FindTextByDatId(ImportedLayout layout, UiElement? scope, uint id)
        => FindElementByDatId(layout, scope, id) as UiText;

    private static UiElement? FindDirectChildById(UiElement? root, uint id)
    {
        if (root is null) return null;
        foreach (var child in root.Children)
        {
            if (DatElementId(child) == id)
                return child;
        }
        return null;
    }

    private static void CollectElementsByDatId(UiElement node, uint id, List<UiElement> result)
    {
        if (DatElementId(node) == id)
            result.Add(node);
        foreach (var child in node.Children)
            CollectElementsByDatId(child, id, result);
    }

    private static void Label(ImportedLayout layout, uint id, UiDatFont? datFont, Vector4 color, Func<string> text)
        => Label(layout, null, id, datFont, color, text);

    private static void Label(ImportedLayout layout, UiElement? scope, uint id, UiDatFont? datFont, Vector4 color, Func<string> text)
    {
        if (FindTextByDatId(layout, scope, id) is UiText t)
        {
            if (datFont is not null) t.DatFont = datFont;
            t.Centered      = true;
            t.OneLine       = true;
            t.ClickThrough  = true;
            t.LinesProvider = () => new[] { new UiText.Line(text(), color) };
        }
    }

    private static string FormatXp(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static void LabelAuthoredColor(ImportedLayout layout, UiElement? scope, uint id, UiDatFont? datFont, Func<string> text)
    {
        if (FindTextByDatId(layout, scope, id) is UiText t)
        {
            if (datFont is not null) t.DatFont = datFont;
            t.Centered      = true;
            t.OneLine       = true;
            t.ClickThrough  = true;
            t.LinesProvider = () => new[] { new UiText.Line(text(), t.DefaultColor) };
        }
    }

    private static void LabelTwoLine(ImportedLayout layout, uint id, UiDatFont? datFont, Vector4 color,
        string line1, string line2)
        => LabelTwoLine(layout, null, id, datFont, color, line1, line2);

    private static void LabelTwoLine(ImportedLayout layout, UiElement? scope, uint id, UiDatFont? datFont, Vector4 color,
        string line1, string line2)
    {
        if (FindTextByDatId(layout, scope, id) is UiText t)
        {
            // Null = keep whatever the importer (dat FontDid resolver) set at build time.
            if (datFont is not null) t.DatFont = datFont;
            t.Centered      = false;
            t.RightAligned  = false;
            t.ClickThrough  = true;
            t.Padding       = 1f;
            t.LinesProvider = () => new[]
            {
                new UiText.Line(line1, color),
                new UiText.Line(line2, color),
            };
        }
    }

    private static void LabelLeft(ImportedLayout layout, uint id, UiDatFont? datFont, Vector4 color, Func<string> text)
        => LabelLeft(layout, null, id, datFont, color, text);

    private static void LabelLeft(ImportedLayout layout, UiElement? scope, uint id, UiDatFont? datFont, Vector4 color, Func<string> text)
    {
        if (FindTextByDatId(layout, scope, id) is UiText t)
        {
            // Null = keep whatever the importer (dat FontDid resolver) set at build time.
            if (datFont is not null) t.DatFont = datFont;
            t.Centered      = false;
            t.RightAligned  = false;
            t.ClickThrough  = true;
            t.Padding       = 0f;
            t.LinesProvider = () => new[] { new UiText.Line(text(), color) };
        }
    }

    private static void LabelRight(
        ImportedLayout layout,
        UiElement? scope,
        uint id,
        UiDatFont? datFont,
        Vector4 color,
        Func<string> text)
    {
        if (FindTextByDatId(layout, scope, id) is UiText t)
        {
            if (datFont is not null) t.DatFont = datFont;
            t.Centered = false;
            t.RightAligned = true;
            t.OneLine = true;
            t.ClickThrough = true;
            t.Padding = 0f;
            t.LinesProvider = () => new[] { new UiText.Line(text(), color) };
        }
    }

    private static void LabelProvider(UiText? t, UiDatFont? datFont, Vector4 color, Func<string> text)
    {
        if (t is null) return;
        // Null = keep whatever the importer (dat FontDid resolver) set at build time.
        if (datFont is not null) t.DatFont = datFont;
        t.Centered      = false;
        t.RightAligned  = false;
        t.ClickThrough  = true;
        t.Padding       = 0f;
        t.LinesProvider = () => new[] { new UiText.Line(text(), color) };
    }

    // Match by ElementId, not geometry: the x1 and x10 raise buttons are both 30x26.
    private static void CollectButtonsById(
        UiElement node,
        uint targetId,
        List<UiButton> result,
        ImportedLayout layout)
    {
        _ = layout;

        var seen = new HashSet<UiButton>(ReferenceEqualityComparer.Instance);
        CollectMatchingButtons(node, targetId, seen, result);
    }

    private static void CollectMatchingButtons(
        UiElement node,
        uint targetId,
        HashSet<UiButton> seen,
        List<UiButton> result)
    {
        if (node is UiButton btn && btn.ElementId == targetId && seen.Add(btn))
        {
            result.Add(btn);
        }
        foreach (var child in node.Children)
            CollectMatchingButtons(child, targetId, seen, result);
    }

}
