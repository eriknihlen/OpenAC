using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.App.UI;
using AcDream.Core.Net.Messages;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.UI.Layout;

public static class CharacterOptionsPageController
{
    public const uint RootElementId = 0x100001F9u;

    /// <summary>The row ListBox (dat Type 5) — <c>m_pOptionBox</c>.</summary>
    public const uint ListBoxElementId = 0x100001FAu;

    /// <summary>The ListBox's linked scrollbar.</summary>
    public const uint ScrollbarElementId = 0x100001FBu;

    /// <summary>Template-list index of the header row (Type 12 text).</summary>
    private const int HeaderTemplateIndex = 0;

    /// <summary>Template-list index of the separator row (Type 3 image).</summary>
    private const int SeparatorTemplateIndex = 1;

    /// <summary>Template-list index of the toggle-option row.</summary>
    private const int ToggleTemplateIndex = 2;

    private const uint StringTableId = 0x23000003u;

    public readonly record struct RowSpec(CharacterOptionId Id, string RetailName, bool StoreOnly);

    /// <summary>One authored header group: its section string key plus
    /// authored-order rows.</summary>
    public readonly record struct GroupSpec(string HeaderKey, RowSpec[] Rows);

    private const bool Live = false;
    private const bool StoreOnly = true;

    public static readonly GroupSpec[] Groups =
    {
        new("ID_CharacterOption_UIBehavior_Section", new RowSpec[]
        {
            new(CharacterOptionId.ViewCombatTarget, "ViewCombatTarget", Live), // Group C
            new(CharacterOptionId.SalvageMultiple, "SalvageMultiple", StoreOnly), // Group D
            new(CharacterOptionId.MainPackPreferred, "MainPackPreferred", StoreOnly), // Group B, unbound
        }),
        new("ID_CharacterOption_UIDisplay_Section", new RowSpec[]
        {
            new(CharacterOptionId.VividTargetingIndicator, "VividTargetingIndicator", Live), // Group C
            new(CharacterOptionId.ShowTooltips, "ShowTooltips", StoreOnly), // Group B, unbound
            new(CharacterOptionId.CoordinatesOnRadar, "CoordinatesOnRadar", Live), // Group C
            new(CharacterOptionId.SideBySideVitals, "SideBySideVitals", Live),
            new(CharacterOptionId.SpellDuration, "SpellDuration", StoreOnly), // Group B, unbound
            new(CharacterOptionId.DisableMostWeatherEffects, "DisableMostWeatherEffects", StoreOnly), // Group B, unbound
            new(CharacterOptionId.DisableDistanceFog, "DisableDistanceFog", Live), // Group B, bound (GameWindow.cs:657)
            new(CharacterOptionId.PersistentAtDay, "PersistentAtDay", StoreOnly), // Group B, unbound
            new(CharacterOptionId.DisableHouseRestrictionEffects, "DisableHouseRestrictionEffects", StoreOnly), // Group D
            new(CharacterOptionId.UseCraftSuccessDialog, "UseCraftSuccessDialog", StoreOnly), // Group A
            new(CharacterOptionId.ConfirmVolatileRareUse, "ConfirmVolatileRareUse", StoreOnly), // Group A
            new(CharacterOptionId.DisplayTimeStamps, "DisplayTimeStamps", Live), // Group B, bound (GameWindow.cs:664)
            new(CharacterOptionId.FilterLanguage, "FilterLanguage", StoreOnly), // Group B, unbound
            new(CharacterOptionId.ShowHelm, "ShowHelm", StoreOnly), // Group A
            new(CharacterOptionId.ShowCloak, "ShowCloak", StoreOnly), // Group A
        }),
        new("ID_CharacterOption_Grouping_Section", new RowSpec[]
        {
            new(CharacterOptionId.IgnoreAllegianceRequests, "IgnoreAllegianceRequests", StoreOnly),
            new(CharacterOptionId.IgnoreFellowshipRequests, "IgnoreFellowshipRequests", StoreOnly),
            new(CharacterOptionId.DisplayAllegianceLogonNotifications, "DisplayAllegianceLogonNotifications", StoreOnly),
            new(CharacterOptionId.FellowshipShareXP, "FellowshipShareXP", Live),
            new(CharacterOptionId.FellowshipShareLoot, "FellowshipShareLoot", StoreOnly),
            new(CharacterOptionId.FellowshipAutoAcceptRequests, "FellowshipAutoAcceptRequests", StoreOnly),
        }),
        new("ID_CharacterOption_OtherPlayers_Section", new RowSpec[]
        {
            new(CharacterOptionId.AcceptLootPermits, "AcceptLootPermits", StoreOnly), // Group A (see ambiguity note)
            new(CharacterOptionId.UseDeception, "UseDeception", StoreOnly), // Group A
            new(CharacterOptionId.AllowGive, "AllowGive", StoreOnly), // Group A
            new(CharacterOptionId.IgnoreTradeRequests, "IgnoreTradeRequests", StoreOnly), // Group A
            new(CharacterOptionId.DragItemOnPlayerOpensSecureTrade, "DragItemOnPlayerOpensSecureTrade", Live),
            new(CharacterOptionId.DisplayDateOfBirth, "DisplayDateOfBirth", StoreOnly), // Group A
            new(CharacterOptionId.DisplayAge, "DisplayAge", StoreOnly), // Group A
            new(CharacterOptionId.DisplayChessRank, "DisplayChessRank", StoreOnly), // Group A
            new(CharacterOptionId.DisplayFishingSkill, "DisplayFishingSkill", StoreOnly), // Group A
            new(CharacterOptionId.DisplayNumberDeaths, "DisplayNumberDeaths", StoreOnly), // Group A
            new(CharacterOptionId.DisplayNumberCharacterTitles, "DisplayNumberCharacterTitles", StoreOnly), // Group A
        }),
        new("ID_CharacterOption_CharacterBehavior_Section", new RowSpec[]
        {
            new(CharacterOptionId.ToggleRun, "ToggleRun", Live), // Group B, bound (GameWindow.cs:680)
            new(CharacterOptionId.AdvancedCombatUI, "AdvancedCombatUI", StoreOnly), // Group B, unbound
            new(CharacterOptionId.AutoTarget, "AutoTarget", Live), // Group C
            new(CharacterOptionId.AutoRepeatAttack, "AutoRepeatAttack", Live), // Group C
            new(CharacterOptionId.UseChargeAttack, "UseChargeAttack", StoreOnly), // Group A
            new(CharacterOptionId.LeadMissileTargets, "LeadMissileTargets", StoreOnly), // Group A
            new(CharacterOptionId.UseFastMissiles, "UseFastMissiles", StoreOnly), // Group A
        }),
        new("ID_CharacterOption_Chat_Section", new RowSpec[]
        {
            new(CharacterOptionId.StayInChatMode, "StayInChatMode", StoreOnly), // Group B, unbound
            new(CharacterOptionId.ListenToAllegianceChat, "HearAllegianceChat", Live), // TurbineChatMembershipGate.cs:107-110
            new(CharacterOptionId.ListenToGeneralChat, "HearGeneralChat", Live), // TurbineChatMembershipGate.cs:111-114
            new(CharacterOptionId.ListenToTradeChat, "HearTradeChat", Live), // TurbineChatMembershipGate.cs:115-118
            new(CharacterOptionId.ListenToLFGChat, "HearLFGChat", Live), // TurbineChatMembershipGate.cs:119-122
            new(CharacterOptionId.ListenToRoleplayChat, "HearRoleplayChat", Live), // TurbineChatMembershipGate.cs:123-126
            new(CharacterOptionId.ListenToSocietyChat, "HearSocietyChat", Live), // TurbineChatMembershipGate.cs:127-130
            new(CharacterOptionId.HearPkDeathMessages, "HearPKDeaths", StoreOnly), // Group D
        }),
    };

    public static int TotalRowCount
    {
        get
        {
            int count = 0;
            foreach (GroupSpec group in Groups) count += group.Rows.Length;
            return count;
        }
    }

    public sealed record Bindings(
        Func<CharacterOptionId, bool> CurrentValue,
        Action<CharacterOptionId, bool> SetOption);

    public static bool Bind(
        ImportedLayout layout,
        OptionPage page,
        Func<uint, uint, UiElement?> templateResolver,
        Func<uint, uint, string?> resolveString,
        Bindings bindings)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(templateResolver);
        ArgumentNullException.ThrowIfNull(resolveString);
        ArgumentNullException.ThrowIfNull(bindings);

        if (layout.FindElement(ListBoxElementId) is not UiTemplateListBox listBox)
        {
            Console.WriteLine(
                $"[D.2b] CharacterOptionsPageController: ListBox 0x{ListBoxElementId:X8} "
                + "not found (or not a UiTemplateListBox) in the built Options panel tree — "
                + "the Character tab will have no rows.");
            return false;
        }

        listBox.TemplateResolver = templateResolver;

        if (layout.FindElement(ScrollbarElementId) is UiScrollbar scrollbar)
            scrollbar.Model = listBox.Scroll;
        else
            Console.WriteLine(
                $"[D.2b] CharacterOptionsPageController: scrollbar 0x{ScrollbarElementId:X8} "
                + "not found — the Character tab's row list will not scroll.");

        for (int groupIndex = 0; groupIndex < Groups.Length; groupIndex++)
        {
            GroupSpec group = Groups[groupIndex];
            BuildHeaderRow(listBox, group.HeaderKey, resolveString);

            foreach (RowSpec spec in group.Rows)
                BuildToggleRow(listBox, spec, page, resolveString, bindings);

            // Structure doc §7: "6 headers, 5 interior separators + 1
            // trailing" — one separator after EVERY group, including the
            // last (the trailing separator before the ListBox's own
            // bottom padding; Apply/Reset/Defaults are separate elements
            // outside the ListBox, not part of this row sequence).
            BuildSeparatorRow(listBox);
        }

        return true;
    }

    private static void BuildHeaderRow(
        UiTemplateListBox listBox, string headerKey, Func<uint, uint, string?> resolveString)
    {
        if (listBox.AddItemFromTemplateList(HeaderTemplateIndex) is not UiText header)
        {
            Console.WriteLine(
                $"[D.2b] CharacterOptionsPageController: header template did not build as "
                + $"UiText for '{headerKey}'.");
            return;
        }

        string? label = resolveString(StringTableId, DatStringResolver.ComputeHash(headerKey));
        if (label is null)
        {
            Console.WriteLine(
                $"[D.2b] CharacterOptionsPageController: header string '{headerKey}' did not "
                + "resolve from the DAT string table — the row renders with no text rather "
                + "than an invented label.");
            return;
        }

        header.LinesProvider = () => new[] { new UiText.Line(label, header.DefaultColor) };
    }

    private static void BuildSeparatorRow(UiTemplateListBox listBox)
    {
        if (listBox.AddItemFromTemplateList(SeparatorTemplateIndex) is null)
        {
            Console.WriteLine(
                "[D.2b] CharacterOptionsPageController: separator template did not build.");
        }
    }

    private static void BuildToggleRow(
        UiTemplateListBox listBox,
        RowSpec spec,
        OptionPage page,
        Func<uint, uint, string?> resolveString,
        Bindings bindings)
    {
        UiElement? row = listBox.AddItemFromTemplateList(ToggleTemplateIndex);
        if (row is null)
        {
            Console.WriteLine(
                $"[D.2b] CharacterOptionsPageController: toggle template did not build for "
                + $"{spec.RetailName} (0x{(uint)spec.Id:X2}).");
            return;
        }

        UiButton? checkbox = FindCheckbox(row);
        if (checkbox is null)
        {
            Console.WriteLine(
                $"[D.2b] CharacterOptionsPageController: no checkbox child found in the "
                + $"toggle row for {spec.RetailName} (0x{(uint)spec.Id:X2}).");
            return;
        }

        string labelKey = $"ID_PlayerOption_{spec.RetailName}";
        string? label = resolveString(StringTableId, DatStringResolver.ComputeHash(labelKey));
        if (label is not null)
            checkbox.Label = label;
        else
            Console.WriteLine(
                $"[D.2b] CharacterOptionsPageController: label '{labelKey}' did not resolve — "
                + "row renders with no caption rather than invented English.");

        checkbox.LabelColor = spec.StoreOnly
            ? UiRenderContext.StoreOnlyCaptionColor
            : Vector4.One;

        string? tooltip = resolveString(
            StringTableId, DatStringResolver.ComputeHash(labelKey + "_Help"));
        if (tooltip is not null)
            checkbox.TooltipText = tooltip;

        if (!CharacterOptionTable.TryGet(spec.Id, out CharacterOptionTableEntry entry))
        {
            Console.WriteLine(
                $"[D.2b] CharacterOptionsPageController: {spec.RetailName} "
                + $"(0x{(uint)spec.Id:X2}) is not in CharacterOptionTable.");
            return;
        }

        bool initial = bindings.CurrentValue(spec.Id);
        checkbox.Selected = initial;

        var row_ = new BoolOptionRow(
            initial,
            entry.ClientDefault,
            apply: value =>
            {
                checkbox.Selected = value;
                bindings.SetOption(spec.Id, value);
            },
            read: () => bindings.CurrentValue(spec.Id),
            refresh: value => checkbox.Selected = value);
        page.Register(row_);

        checkbox.OnClick = () => row_.SetCurrentValue(checkbox.Selected);
    }

    private static UiButton? FindCheckbox(UiElement root)
    {
        if (root is UiButton direct) return direct;
        foreach (UiElement child in root.Children)
            if (child is UiButton button)
                return button;
        return null;
    }
}
