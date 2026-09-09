using System.Globalization;
using System.Text;
using AcDream.Core.Items;

namespace AcDream.App.UI.Layout;

public static class CharacterController
{
    public const uint LayoutId = 0x2100006Eu;
    public const uint RootId = 0x10000183u;
    public const uint MainTextId = 0x1000011Du;
    public const uint ScrollbarId = 0x1000011Eu;
    public const uint CloseId = 0x100000FCu;

    private static readonly AugmentationDescriptor[] Augmentations =
    [
        new(0xDAu, "ID_CharacterInfo_Augmentation_Attribute_Strength", true),
        new(0xDBu, "ID_CharacterInfo_Augmentation_Attribute_Endurance", true),
        new(0xDCu, "ID_CharacterInfo_Augmentation_Attribute_Coordination", true),
        new(0xDDu, "ID_CharacterInfo_Augmentation_Attribute_Quickness", true),
        new(0xDEu, "ID_CharacterInfo_Augmentation_Attribute_Focus", true),
        new(0xDFu, "ID_CharacterInfo_Augmentation_Attribute_Self", true),
        new(0xF0u, "ID_CharacterInfo_Augmentation_Resist_Slash", true),
        new(0xF1u, "ID_CharacterInfo_Augmentation_Resist_Pierce", true),
        new(0xF2u, "ID_CharacterInfo_Augmentation_Resist_Blunt", true),
        new(0xF3u, "ID_CharacterInfo_Augmentation_Resist_Acid", true),
        new(0x147u, "ID_CharacterInfo_Augmentation_Resist_Nether", true),
        new(0xF4u, "ID_CharacterInfo_Augmentation_Resist_Fire", true),
        new(0xF5u, "ID_CharacterInfo_Augmentation_Resist_Frost", true),
        new(0xF6u, "ID_CharacterInfo_Augmentation_Resist_Lightning", true),
        new(0xE0u, "ID_CharacterInfo_Augmentation_Spec_Gearcraft", false),
        new(0xE1u, "ID_CharacterInfo_Augmentation_Spec_WeaponTinkering", false),
        new(0xE2u, "ID_CharacterInfo_Augmentation_Spec_MagicItemTinkering", false),
        new(0xE3u, "ID_CharacterInfo_Augmentation_Spec_ArmorTinkering", false),
        new(0xE4u, "ID_CharacterInfo_Augmentation_Spec_ItemTinkering", false),
        new(0x125u, "ID_CharacterInfo_Augmentation_Spec_Salvaging", false),
        new(0xE5u, "ID_CharacterInfo_Augmentation_ExtraPackSlot", false),
        new(0xE6u, "ID_CharacterInfo_Augmentation_IncreasedCarryingCapacity", true),
        new(0xE7u, "ID_CharacterInfo_Augmentation_LessDeathItemLoss", true),
        new(0xE8u, "ID_CharacterInfo_Augmentation_SpellsRemainPastDeath", false),
        new(0xE9u, "ID_CharacterInfo_Augmentation_CriticalDefense", false),
        new(0xEAu, "ID_CharacterInfo_Augmentation_BonusXP", false),
        new(0xEBu, "ID_CharacterInfo_Augmentation_BonusSalvage", true),
        new(0xECu, "ID_CharacterInfo_Augmentation_BonusImbueChance", false),
        new(0xEDu, "ID_CharacterInfo_Augmentation_FasterRegen", true),
        new(0xEEu, "ID_CharacterInfo_Augmentation_IncreasedSpellDuration", true),
        new(0x126u, "ID_CharacterInfo_Augmentation_Infused_CreatureMagic", true),
        new(0x127u, "ID_CharacterInfo_Augmentation_Infused_ItemMagic", true),
        new(0x128u, "ID_CharacterInfo_Augmentation_Infused_LifeMagic", true),
        new(0x129u, "ID_CharacterInfo_Augmentation_Infused_WarMagic", true),
        new(0x148u, "ID_CharacterInfo_Augmentation_Infused_VoidMagic", true),
        new(0x12Cu, "ID_CharacterInfo_Augmentation_SkilledMelee", true),
        new(0x12Du, "ID_CharacterInfo_Augmentation_SkilledMissile", true),
        new(0x12Eu, "ID_CharacterInfo_Augmentation_SkilledMagic", true),
        new(0x135u, "ID_CharacterInfo_Augmentation_DamageBonus", true),
        new(0x136u, "ID_CharacterInfo_Augmentation_DamageResist", true),
        new(0x12Au, "ID_CharacterInfo_Augmentation_CriticalExpertise", false),
        new(0x12Bu, "ID_CharacterInfo_Augmentation_CriticalPower", false),
        new(0x146u, "ID_CharacterInfo_Augmentation_JackOfAllTrades", false),
    ];

    internal static IReadOnlyList<string> AugmentationStringKeys
        => Augmentations.Select(descriptor => descriptor.StringKey).ToArray();

    public static CharacterInformationUiController Bind(
        ImportedLayout layout,
        Func<CharacterSheet> data,
        UiDatFont? datFont = null,
        Action? close = null,
        CharacterInfoStrings? strings = null,
        Func<Action, IDisposable>? subscribeChanged = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(data);
        UiElement root = layout.Root;
        UiText text = layout.FindElement(MainTextId) as UiText ?? new UiText
        {
            EventId = MainTextId,
            Name = "m_pMainText",
            Left = 12f,
            Top = 44f,
            Width = Math.Max(40f, root.Width - 24f),
            Height = Math.Max(40f, root.Height - 56f),
            ZOrder = 1_000_000,
            DatFont = datFont,
            ClickThrough = false,
        };
        if (text.Parent is null)
            root.AddChild(text);
        else if (text.DatFont is null && datFont is not null)
            text.DatFont = datFont;

        CharacterInfoStrings copy = strings ?? CharacterInfoStrings.English;
        text.PreserveEndOnLayout = false;
        UiScrollbar? scrollbar = layout.FindElement(ScrollbarId) as UiScrollbar;
        if (scrollbar is not null)
            scrollbar.Model = text.Scroll;
        UiButton? closeButton = layout.FindElement(CloseId) as UiButton;
        if (closeButton is not null)
            closeButton.OnClick = close;
        return new CharacterInformationUiController(
            text,
            data,
            copy,
            subscribeChanged,
            scrollbar,
            closeButton);
    }

    internal static string BuildReport(CharacterSheet sheet, CharacterInfoStrings strings)
    {
        var body = new StringBuilder();

        string? birth = sheet.BirthTimestamp is int timestamp
            ? FormatRetailDate(timestamp)
            : sheet.BirthDate;
        if (!string.IsNullOrEmpty(birth))
            AppendTokens(body, strings.Birth, birth);

        string? played = sheet.TotalPlayTimeSeconds is int seconds
            ? FormatRetailDuration(seconds)
            : sheet.PlayTime;
        if (!string.IsNullOrEmpty(played))
            AppendTokens(body, strings.Played, played);

        switch (sheet.Deaths)
        {
            case 0: body.Append(strings.DeathsNone); break;
            case 1: body.Append(strings.DeathsOne); break;
            case 2: body.Append(strings.DeathsTwo); break;
            default: AppendTokens(body, strings.DeathsMany, sheet.Deaths); break;
        }
        AppendSectionBreak(body);

        string resist = ResistanceGrade(sheet.Strength + sheet.Endurance,
            200, 260, 320, 380, 440);
        string regeneration = ResistanceGrade(
            sheet.Strength + (2 * sheet.Endurance),
            200, 346, 470, 580, 690);
        // ID_CharacterInfo_Resists references the named RESIST variable twice
        // (Natural and Drain) and REGEN once.
        body.Append(strings.Resists[0]).Append(resist)
            .Append(strings.Resists[1]).Append(resist)
            .Append(strings.Resists[2]).Append(regeneration)
            .Append(strings.Resists[3]);
        AppendSectionBreak(body);

        AppendTokens(body, strings.Innates,
            sheet.Strength, sheet.Endurance, sheet.Coordination,
            sheet.Quickness, sheet.Focus, sheet.Self);
        AppendSectionBreak(body);

        AppendTokens(body, strings.Chess, sheet.ChessRank);
        AppendTokens(body, strings.Fishing, sheet.FishingSkill);
        AppendSectionBreak(body);

        AppendMastery(body, strings.MeleeMastery,
            Get(sheet, 0x162u), MeleeMasteryName);
        AppendMastery(body, strings.RangedMastery,
            Get(sheet, 0x163u), RangedMasteryName);
        AppendMastery(body, strings.SummoningMastery,
            Get(sheet, 0x16Au), SummoningMasteryName);
        foreach (AugmentationDescriptor descriptor in Augmentations)
        {
            int value = Get(sheet, descriptor.PropertyId);
            if (value <= 0
                || !strings.AugmentationText.TryGetValue(
                    descriptor.StringKey, out string[]? tokens))
                continue;
            if (descriptor.IncludeValue)
                AppendTokens(body, tokens, value);
            else
                body.Append(ResolvePlural(tokens[0], value));
        }
        AppendSectionBreak(body);

        int capacity = BurdenMath.EncumbranceCapacity(
            sheet.Strength, sheet.EncumbranceAugmentations);
        float load = BurdenMath.LoadRatio(capacity, sheet.BurdenCurrent);
        if (load < 1f)
            body.Append(strings.LoadNone);
        else
            AppendTokens(body, strings.LoadBurdened,
                sheet.BurdenCurrent - capacity,
                BurdenMath.LoadPenaltyPercent(load));
        if (sheet.EncumbranceAugmentations > 0)
            AppendTokens(body, strings.LoadAugmentations,
                sheet.EncumbranceAugmentations,
                sheet.EncumbranceAugmentations * 20f);

        return body.ToString().TrimEnd('\r', '\n');
    }

    private static int Get(CharacterSheet sheet, uint id)
        => sheet.CharacterInfoProperties.TryGetValue(id, out int value) ? value : 0;

    private static void AppendMastery(
        StringBuilder body,
        string[] tokens,
        int value,
        Func<int, string> name)
    {
        if (value > 0)
            AppendTokens(body, tokens, name(value));
    }

    private static string MeleeMasteryName(int value) => value switch
    {
        1 => "Unarmed Weapons",
        2 => "Swords",
        3 => "Axes",
        4 => "Maces",
        5 => "Spears",
        6 => "Daggers",
        7 => "Staves",
        11 => "Two Handed Weapons",
        _ => "Unknown",
    };

    private static string RangedMasteryName(int value) => value switch
    {
        8 => "Bows",
        9 => "Crossbows",
        10 => "Thrown Weapons",
        12 => "Magical Spells",
        _ => "Unknown",
    };

    private static string SummoningMasteryName(int value) => value switch
    {
        1 => "Primalist",
        2 => "Necromancer",
        3 => "Naturalist",
        _ => "Unknown",
    };

    internal static string ResistanceGrade(
        int value,
        int none,
        int poor,
        int mediocre,
        int hardy,
        int resilient)
        => value <= none ? "None"
            : value <= poor ? "Poor"
            : value <= mediocre ? "Mediocre"
            : value <= hardy ? "Hardy"
            : value <= resilient ? "Resilient"
            : "Indomitable";

    internal static string FormatRetailDuration(int totalSeconds)
    {
        long remaining = Math.Max(0, totalSeconds);
        (long seconds, string singular, string plural)[] units =
        [
            (31_536_000, "year", "years"),
            (2_592_000, "month", "months"),
            (604_800, "week", "weeks"),
            (86_400, "day", "days"),
            (3_600, "hour", "hours"),
            (60, "minute", "minutes"),
            (1, "second", "seconds"),
        ];
        var parts = new List<string>();
        foreach (var unit in units)
        {
            long count = remaining / unit.seconds;
            remaining %= unit.seconds;
            if (count != 0)
                parts.Add($"{count} {(count == 1 ? unit.singular : unit.plural)}");
        }
        return string.Join(' ', parts);
    }

    private static string FormatRetailDate(int unixSeconds)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds)
                .LocalDateTime.ToString("G", CultureInfo.CurrentCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return unixSeconds.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static void AppendTokens(
        StringBuilder body,
        IReadOnlyList<string> tokens,
        params object[] values)
    {
        for (int i = 0; i < tokens.Count; i++)
        {
            int pluralValue = values.Length == 0 ? 0
                : NumericValue(values[Math.Min(i, values.Length - 1)]);
            body.Append(ResolvePlural(tokens[i], pluralValue));
            if (i < values.Length)
                body.Append(Convert.ToString(values[i], CultureInfo.InvariantCulture));
        }
    }

    private static string ResolvePlural(string text, int value)
        => text.Replace("{time[1]|times}", value == 1 ? "time" : "times",
            StringComparison.Ordinal);

    private static int NumericValue(object value) => value switch
    {
        byte v => v,
        short v => v,
        int v => v,
        long v => checked((int)v),
        float v => (int)v,
        double v => (int)v,
        _ => 0,
    };

    private static void AppendSectionBreak(StringBuilder body)
    {
        if (body.Length != 0 && body[^1] != '\n') body.Append('\n');
        body.Append('\n');
    }

    private readonly record struct AugmentationDescriptor(
        uint PropertyId,
        string StringKey,
        bool IncludeValue);
}

public sealed class CharacterInformationUiController : IRetainedPanelController
{
    private readonly Func<CharacterSheet> _data;
    private readonly CharacterInfoStrings _strings;
    private readonly UiTextLayoutCache<string> _layout;
    private readonly IDisposable? _changeBinding;
    private readonly UiScrollbar? _scrollbar;
    private readonly UiButton? _close;
    private bool _contentDirty = true;
    private bool _disposed;

    internal CharacterInformationUiController(
        UiText text,
        Func<CharacterSheet> data,
        CharacterInfoStrings strings,
        Func<Action, IDisposable>? subscribeChanged,
        UiScrollbar? scrollbar,
        UiButton? close)
    {
        _data = data;
        _strings = strings;
        _scrollbar = scrollbar;
        _close = close;
        _layout = new UiTextLayoutCache<string>(
            text,
            static (target, value) => IndicatorDetailText.Shape(target, value),
            string.Empty,
            StringComparer.Ordinal);
        text.LinesProvider = GetLines;
        _changeBinding = subscribeChanged?.Invoke(InvalidateContent);
    }

    private IReadOnlyList<UiText.Line> GetLines()
    {
        if (_contentDirty)
        {
            _layout.SetValue(CharacterController.BuildReport(_data(), _strings));
            _contentDirty = false;
        }
        return _layout.GetLines();
    }

    private void InvalidateContent() => _contentDirty = true;

    public void OnShown() => InvalidateContent();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _changeBinding?.Dispose();
        if (_scrollbar is not null)
            _scrollbar.Model = null;
        if (_close is not null)
            _close.OnClick = null;
    }
}

public sealed record CharacterInfoStrings(
    string[] Birth,
    string[] Played,
    string DeathsNone,
    string DeathsOne,
    string DeathsTwo,
    string[] DeathsMany,
    string[] Resists,
    string[] Innates,
    string[] Chess,
    string[] Fishing,
    string LoadNone,
    string[] LoadBurdened,
    string[] LoadAugmentations,
    string[] MeleeMastery,
    string[] RangedMastery,
    string[] SummoningMastery,
    IReadOnlyDictionary<string, string[]> AugmentationText)
{
    public static CharacterInfoStrings FromDat(
        Func<string, string[]?> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        CharacterInfoStrings fallback = English;
        string[] Get(string key, string[] defaultValue)
            => resolve(key) is { Length: > 0 } value ? value : defaultValue;
        string GetOne(string key, string defaultValue)
            => Get(key, [defaultValue])[0];

        var augmentations = new Dictionary<string, string[]>();
        foreach (string key in CharacterController.AugmentationStringKeys)
            if (resolve(key) is { Length: > 0 } value)
                augmentations[key] = value;

        return new CharacterInfoStrings(
            Get("ID_CharacterInfo_Birth", fallback.Birth),
            Get("ID_CharacterInfo_Played", fallback.Played),
            GetOne("ID_CharacterInfo_Deaths_None", fallback.DeathsNone),
            GetOne("ID_CharacterInfo_Deaths_One", fallback.DeathsOne),
            GetOne("ID_CharacterInfo_Deaths_Two", fallback.DeathsTwo),
            Get("ID_CharacterInfo_Deaths_Many", fallback.DeathsMany),
            Get("ID_CharacterInfo_Resists", fallback.Resists),
            Get("ID_CharacterInfo_Innates", fallback.Innates),
            Get("ID_CharacterInfo_Chess", fallback.Chess),
            Get("ID_CharacterInfo_Fishing", fallback.Fishing),
            GetOne("ID_CharacterInfo_Load_None", fallback.LoadNone),
            Get("ID_CharacterInfo_Load_Burdened", fallback.LoadBurdened),
            Get("ID_CharacterInfo_Load_Augmentations", fallback.LoadAugmentations),
            Get("ID_CharacterInfo_Mastery_Melee", fallback.MeleeMastery),
            Get("ID_CharacterInfo_Mastery_Ranged", fallback.RangedMastery),
            Get("ID_CharacterInfo_Mastery_Summoning", fallback.SummoningMastery),
            augmentations);
    }

    public static CharacterInfoStrings English { get; } = new(
        ["You were born on ", ".\n"],
        ["You have played for ", ".\n"],
        "You have never died!\n",
        "You have died only once!\n",
        "You have died twice.\n",
        ["You have died ", " times.\n"],
        ["Natural Resistances: ", "\nDrain Resistances: ", "\nRegeneration Bonus: ", "\n"],
        ["Innate Strength: ", "\nInnate Endurance: ", "\nInnate Coordination: ",
            "\nInnate Quickness: ", "\nInnate Focus: ", "\nInnate Self: ", "\n"],
        ["Chess Rank: ", "\n"],
        ["Fishing Skill: ", "\n"],
        "You are not overburdened at this time.\n",
        ["You are currently overburdened by ",
            " burden units. This is reducing your Run, Jump, Melee Defense and Missile Defense skills by ",
            "%.\n"],
        ["You have been augmented ",
            " times with the Might of the Seventh Mule. This has increased your carrying capacity by ",
            "%.\n"],
        ["Your melee mastery is ", ".\n"],
        ["Your ranged mastery is ", ".\n\n"],
        ["Your summoning mastery is ", ".\n\n"],
        new Dictionary<string, string[]>());
}
