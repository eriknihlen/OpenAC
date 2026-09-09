using System.Globalization;
using System.Numerics;
using AcDream.Content;
using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using AcDream.Core.Properties;
using AcDream.Core.Ui;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.App.UI.Layout;

public enum CreatureAppraisalValueStyle
{
    Normal,
    Positive,
    Negative,
    Incomplete,
}

public readonly record struct CreatureAppraisalRow(
    string Label,
    string Value,
    CreatureAppraisalValueStyle Style);

public enum CreatureAppraisalRowLayer
{
    Combined,
    Background,
    Foreground,
}

public static class CreatureAppraisalRows
{
    private const string Unknown = "???";
    private const uint DamageRating = 0x133u;
    private const uint DamageResistRating = 0x134u;
    private const uint CritRating = 0x139u;
    private const uint CritDamageRating = 0x13Au;
    private const uint CritResistRating = 0x13Bu;
    private const uint CritDamageResistRating = 0x13Cu;
    private const uint HealingBoostRating = 0x143u;
    private const uint DotResistRating = 0x15Eu;
    private const uint LifeResistRating = 0x15Fu;

    private const uint Faction1BitsProperty = (uint)PropertyInt.Faction1Bits; // 281
    private const uint SocietyRankCelestialHandProperty =
        (uint)PropertyInt.SocietyRankCelhan; // 287
    private const uint SocietyRankEldrytchWebProperty =
        (uint)PropertyInt.SocietyRankEldweb; // 288
    private const uint SocietyRankRadiantBloodProperty =
        (uint)PropertyInt.SocietyRankRadblo; // 289
    private const int CelestialHandBit = 0x1;
    private const int EldrytchWebBit = 0x2;
    private const int RadiantBloodBit = 0x4;
    private const int SocietyBitsMask =
        CelestialHandBit | EldrytchWebBit | RadiantBloodBit;

    private const uint AllegianceRankProperty = (uint)PropertyInt.AllegianceRank; // 30
    private const uint AllegianceFollowersProperty =
        (uint)PropertyInt.AllegianceFollowers; // 35 (Int table)
    private const uint MonarchsTitleProperty =
        (uint)PropertyString.MonarchsTitle; // 21 (String table)
    private const uint PatronsTitleProperty =
        (uint)PropertyString.PatronsTitle; // 35 (String table)

    private const uint FellowshipProperty = (uint)PropertyString.Fellowship; // 10
    private const uint DateOfBirthProperty = (uint)PropertyString.DateOfBirth; // 43
    private const uint AgeProperty = (uint)PropertyInt.Age; // 125
    private const uint ChessRankProperty = (uint)PropertyInt.ChessRank; // 181
    private const uint FishingSkillProperty =
        (uint)PropertyInt.FakeFishingSkill; // 192
    private const uint NumDeathsProperty = (uint)PropertyInt.NumDeaths; // 43 (Int table)
    private const uint NumCharacterTitlesProperty =
        (uint)PropertyInt.NumCharacterTitles; // 262

    public static IReadOnlyList<CreatureAppraisalRow> Build(
        AppraiseInfoParser.CreatureProfile profile,
        bool success)
    {
        return
        [
            Primary("Strength", profile.Strength, 0, profile, success),
            Primary("Endurance", profile.Endurance, 1, profile, success),
            Primary("Coordination", profile.Coordination, 3, profile, success),
            Primary("Quickness", profile.Quickness, 2, profile, success),
            Primary("Focus", profile.Focus, 4, profile, success),
            Primary("Self", profile.Self, 5, profile, success),
            Secondary(
                "Health",
                profile.Health,
                profile.HealthMax,
                showPercent: true,
                enchantmentBit: 6,
                profile,
                success),
            Secondary(
                "Stamina",
                profile.Stamina,
                profile.StaminaMax,
                showPercent: false,
                enchantmentBit: 7,
                profile,
                success),
            Secondary(
                "Mana",
                profile.Mana,
                profile.ManaMax,
                showPercent: false,
                enchantmentBit: 8,
                profile,
                success),
        ];
    }

    private const int UnenchantableArmorLevel = 9999;

    public static IReadOnlyList<CreatureAppraisalRow> BuildExtra(
        PropertyBundle properties,
        AppraiseInfoParser.ArmorLevel? armorLevels,
        bool character,
        int localFactionBits = 0)
    {
        ArgumentNullException.ThrowIfNull(properties);

        var rows = new List<CreatureAppraisalRow>();

        if (character)
        {
            AddSocietyRow(rows, properties, localFactionBits);
            if (Get(properties, AllegianceRankProperty) >= 1)
                AddAllegianceCascade(rows, properties);
        }

        if (character && armorLevels is { } levels && HasAnyArmorLevel(levels))
        {
            rows.Add(Blank());
            rows.Add(ArmorLevelRow(
                "Head/Chest/Groin", levels.Head, levels.Chest, levels.Abdomen));
            rows.Add(ArmorLevelRow(
                "Bicep/Wrist/Hand", levels.UpperArm, levels.LowerArm, levels.Hand));
            rows.Add(ArmorLevelRow(
                "Thigh/Shin/Foot", levels.UpperLeg, levels.LowerLeg, levels.Foot));
        }

        int damage = Get(properties, DamageRating);
        int damageResist = Get(properties, DamageResistRating);
        int crit = Get(properties, CritRating);
        int critDamage = Get(properties, CritDamageRating);
        int critResist = Get(properties, CritResistRating);
        int critDamageResist = Get(properties, CritDamageResistRating);
        _ = Get(properties, HealingBoostRating);
        int dotResist = Get(properties, DotResistRating);
        int lifeResist = Get(properties, LifeResistRating);

        bool showRating = damage > 0 || crit > 0 || critDamage > 0;
        bool showResist =
            damageResist > 0 || critResist > 0 || critDamageResist > 0;
        bool showDotLife = dotResist > 0 || lifeResist > 0;
        if (showRating || showResist || showDotLife)
        {
            rows.Add(Blank());
            if (showRating)
            {
                rows.Add(new CreatureAppraisalRow(
                    "Dmg/CritDmg",
                    $"Rating: {Number(damage)}/{Number(critDamage)}",
                    CreatureAppraisalValueStyle.Normal));
            }
            if (showResist)
            {
                rows.Add(new CreatureAppraisalRow(
                    "Dmg/CritDmg",
                    $"Resist: {Number(damageResist)}/{Number(critDamageResist)}",
                    CreatureAppraisalValueStyle.Normal));
            }
            if (showDotLife)
            {
                rows.Add(new CreatureAppraisalRow(
                    "DoT/Life:",
                    $"Resist: {Number(dotResist)}/{Number(lifeResist)}",
                    CreatureAppraisalValueStyle.Normal));
            }
            rows.Add(Blank());
        }

        if (character)
        {
            AddConfigurableExtras(rows, properties);

            rows.Add(new CreatureAppraisalRow(
                "* = Unenchantable",
                string.Empty,
                CreatureAppraisalValueStyle.Normal));
        }

        return rows;
    }

    private static void AddSocietyRow(
        List<CreatureAppraisalRow> rows,
        PropertyBundle properties,
        int localFactionBits)
    {
        if (!properties.Ints.TryGetValue(Faction1BitsProperty, out int targetBits))
            return;

        string name;
        int rank;
        int targetBit;
        if ((targetBits & CelestialHandBit) != 0)
        {
            name = "Celestial Hand";
            rank = Get(properties, SocietyRankCelestialHandProperty);
            targetBit = CelestialHandBit;
        }
        else if ((targetBits & EldrytchWebBit) != 0)
        {
            name = "Eldrytch Web";
            rank = Get(properties, SocietyRankEldrytchWebProperty);
            targetBit = EldrytchWebBit;
        }
        else if ((targetBits & RadiantBloodBit) == 0)
        {
            rows.Add(new CreatureAppraisalRow(
                "Society:", Unknown, CreatureAppraisalValueStyle.Normal));
            return;
        }
        else
        {
            name = "Radiant Blood";
            rank = Get(properties, SocietyRankRadiantBloodProperty);
            targetBit = RadiantBloodBit;
        }

        rows.Add(new CreatureAppraisalRow(
            "Society:",
            name + SocietyRankSuffix(rank),
            SocietyColor(targetBit, localFactionBits)));
    }

    private static string SocietyRankSuffix(int rank) => rank switch
    {
        >= 1 and <= 100 => " ~ Initiate",
        >= 101 and <= 300 => " ~ Adept",
        >= 301 and <= 600 => " ~ Knight",
        >= 601 and <= 1000 => " ~ Lord",
        >= 1001 and <= 1500 => " ~ Master",
        _ => string.Empty,
    };

    private static CreatureAppraisalValueStyle SocietyColor(
        int targetBit, int localFactionBits)
    {
        if ((localFactionBits & targetBit) != 0)
            return CreatureAppraisalValueStyle.Positive;
        if ((localFactionBits & (SocietyBitsMask & ~targetBit)) != 0)
            return CreatureAppraisalValueStyle.Negative;
        return CreatureAppraisalValueStyle.Normal;
    }

    private static void AddAllegianceCascade(
        List<CreatureAppraisalRow> rows, PropertyBundle properties)
    {
        if (!properties.Strings.TryGetValue(
                MonarchsTitleProperty, out string? monarchsTitle))
        {
            int followers = Get(properties, AllegianceFollowersProperty);
            if (followers < 0)
                followers = 0;
            string unit = followers == 1 ? "Follower" : "Followers";
            rows.Add(new CreatureAppraisalRow(
                "Alleg. Monarch:",
                $"{Number(followers)} {unit}",
                CreatureAppraisalValueStyle.Normal));
            return;
        }

        if (!properties.Strings.TryGetValue(
                PatronsTitleProperty, out string? patronsTitle))
        {
            rows.Add(new CreatureAppraisalRow(
                "Monarch:", monarchsTitle, CreatureAppraisalValueStyle.Normal));
            return;
        }

        if (string.Equals(monarchsTitle, patronsTitle, StringComparison.Ordinal))
        {
            rows.Add(new CreatureAppraisalRow(
                "Monarch/Patron:",
                monarchsTitle,
                CreatureAppraisalValueStyle.Normal));
            return;
        }

        rows.Add(new CreatureAppraisalRow(
            "Monarch:", monarchsTitle, CreatureAppraisalValueStyle.Normal));
        rows.Add(new CreatureAppraisalRow(
            "Patron:", patronsTitle, CreatureAppraisalValueStyle.Normal));
    }

    private static void AddConfigurableExtras(
        List<CreatureAppraisalRow> rows, PropertyBundle properties)
    {
        if (properties.Strings.TryGetValue(
                FellowshipProperty, out string? fellowship))
        {
            rows.Add(new CreatureAppraisalRow(
                "Fellowship:", fellowship, CreatureAppraisalValueStyle.Normal));
        }

        if (properties.Strings.TryGetValue(
                DateOfBirthProperty, out string? arrived))
        {
            rows.Add(new CreatureAppraisalRow(
                "Arrived in Dereth:",
                arrived,
                CreatureAppraisalValueStyle.Normal));
        }

        if (properties.Ints.TryGetValue(AgeProperty, out int ageSeconds))
        {
            rows.Add(new CreatureAppraisalRow(
                "Time in Dereth:",
                RetailDurationText.Format(ageSeconds),
                CreatureAppraisalValueStyle.Normal));
        }

        if (properties.Ints.TryGetValue(ChessRankProperty, out int chessRank))
        {
            rows.Add(new CreatureAppraisalRow(
                "Chess Rank:", Number(chessRank), CreatureAppraisalValueStyle.Normal));
        }

        if (properties.Ints.TryGetValue(FishingSkillProperty, out int fishingSkill))
        {
            rows.Add(new CreatureAppraisalRow(
                "Fishing Skill:",
                Number(fishingSkill),
                CreatureAppraisalValueStyle.Normal));
        }

        if (properties.Ints.TryGetValue(NumDeathsProperty, out int deaths))
        {
            rows.Add(new CreatureAppraisalRow(
                "Deaths:",
                deaths <= 0 ? "Has never died" : Number(deaths),
                CreatureAppraisalValueStyle.Normal));
        }

        if (properties.Ints.TryGetValue(
                NumCharacterTitlesProperty, out int titles))
        {
            rows.Add(new CreatureAppraisalRow(
                "Titles Earned:", Number(titles), CreatureAppraisalValueStyle.Normal));
        }
    }

    private static bool HasAnyArmorLevel(AppraiseInfoParser.ArmorLevel levels)
        => levels.Head > 0 || levels.Chest > 0 || levels.Abdomen > 0
        || levels.UpperArm > 0 || levels.LowerArm > 0 || levels.Hand > 0
        || levels.UpperLeg > 0 || levels.LowerLeg > 0 || levels.Foot > 0;

    private static CreatureAppraisalRow ArmorLevelRow(
        string label, int a, int b, int c)
        => new(
            label,
            $"AL: {ArmorLevelPart(a)}/{ArmorLevelPart(b)}/{ArmorLevelPart(c)}",
            CreatureAppraisalValueStyle.Normal);

    private static string ArmorLevelPart(int value)
        => value >= UnenchantableArmorLevel
            ? $"*{Number(value - UnenchantableArmorLevel)}"
            : Number(value);

    private static CreatureAppraisalRow Blank() =>
        new(string.Empty, string.Empty, CreatureAppraisalValueStyle.Normal);

    private static int Get(PropertyBundle properties, uint id) =>
        properties.Ints.TryGetValue(id, out int value) ? value : 0;

    private static string Number(int value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static CreatureAppraisalRow Primary(
        string label,
        uint? value,
        int enchantmentBit,
        AppraiseInfoParser.CreatureProfile profile,
        bool success)
        => new(
            label,
            value is > 0
                ? value.Value.ToString(CultureInfo.InvariantCulture)
                : Unknown,
            Style(enchantmentBit, profile, success));

    private static CreatureAppraisalRow Secondary(
        string label,
        uint? current,
        uint? maximum,
        bool showPercent,
        int enchantmentBit,
        AppraiseInfoParser.CreatureProfile profile,
        bool success)
    {
        string value = Unknown;
        if (current is > 0 && maximum is > 0)
        {
            int percent = RoundedPercent(current.Value, maximum.Value);
            if (success)
            {
                value = showPercent
                    ? $"{current.Value.ToString(CultureInfo.InvariantCulture)}/{maximum.Value.ToString(CultureInfo.InvariantCulture)} ({percent.ToString(CultureInfo.InvariantCulture)} %)"
                    : $"{current.Value.ToString(CultureInfo.InvariantCulture)}/{maximum.Value.ToString(CultureInfo.InvariantCulture)}";
            }
            else if (showPercent)
            {
                value = $"{percent.ToString(CultureInfo.InvariantCulture)} %";
            }
        }

        return new CreatureAppraisalRow(
            label,
            value,
            Style(enchantmentBit, profile, success));
    }

    private static int RoundedPercent(uint numerator, uint denominator)
        => denominator == 0u
            ? 0
            : (int)Math.Min(
                int.MaxValue,
                ((100L * numerator) + denominator / 2L) / denominator);

    private static CreatureAppraisalValueStyle Style(
        int bit,
        AppraiseInfoParser.CreatureProfile profile,
        bool success)
    {
        if (!success)
            return CreatureAppraisalValueStyle.Incomplete;

        ushort highlight = profile.AttributeHighlights ?? 0;
        if ((highlight & (1 << bit)) == 0)
            return CreatureAppraisalValueStyle.Normal;

        ushort color = profile.AttributeColors ?? 0;
        return (color & (1 << bit)) != 0
            ? CreatureAppraisalValueStyle.Positive
            : CreatureAppraisalValueStyle.Negative;
    }
}

public sealed class CreatureAppraisalRowTemplateFactory
{
    public const uint TemplateId = 0x10000166u;
    public const uint LabelId = 0x1000012Au;
    public const uint ValueId = 0x1000012Bu;

    private readonly ElementInfo _template;
    private readonly Func<uint, (uint Texture, int Width, int Height)> _resolveSprite;
    private readonly UiDatFont? _defaultFont;
    private readonly IReadOnlyDictionary<uint, UiDatFont?> _fonts;

    public CreatureAppraisalRowTemplateFactory(
        ElementInfo template,
        Func<uint, (uint Texture, int Width, int Height)> resolveSprite,
        UiDatFont? defaultFont,
        IReadOnlyDictionary<uint, UiDatFont?>? fonts = null)
    {
        _template = template ?? throw new ArgumentNullException(nameof(template));
        _resolveSprite = resolveSprite ?? throw new ArgumentNullException(nameof(resolveSprite));
        _defaultFont = defaultFont;
        _fonts = fonts ?? new Dictionary<uint, UiDatFont?>();
    }

    public float Width => _template.Width;
    public float Height => _template.Height;

    public static CreatureAppraisalRowTemplateFactory? TryLoad(
        IDatReaderWriter dats,
        Func<uint, (uint Texture, int Width, int Height)> resolveSprite,
        UiDatFont? defaultFont,
        Func<uint, UiDatFont?>? resolveFont)
    {
        ElementInfo? template = LayoutImporter.ImportInfos(
            dats,
            AppraisalUiController.LayoutId,
            TemplateId);
        if (template is null
            || Find(template, LabelId) is null
            || Find(template, ValueId) is null
            || template.Width <= 0f
            || template.Height <= 0f)
        {
            return null;
        }

        var fonts = new Dictionary<uint, UiDatFont?>();
        CaptureFonts(template, resolveFont, fonts);
        return new CreatureAppraisalRowTemplateFactory(
            template,
            resolveSprite,
            defaultFont,
            fonts);
    }

    public UiTemplateListSlot Create(
        CreatureAppraisalRow row,
        CreatureAppraisalRowLayer layer = CreatureAppraisalRowLayer.Combined)
    {
        ImportedLayout content = LayoutImporter.Build(
            _template,
            _resolveSprite,
            _defaultFont,
            did => _fonts.TryGetValue(did, out UiDatFont? font)
                ? font
                : _defaultFont);
        UiText label = Required<UiText>(content, LabelId);
        UiText value = Required<UiText>(content, ValueId);
        if (layer == CreatureAppraisalRowLayer.Background)
        {
            label.LinesProvider = static () => Array.Empty<UiText.Line>();
            value.LinesProvider = static () => Array.Empty<UiText.Line>();
        }
        else
        {
            label.LinesProvider = () =>
                [new UiText.Line(row.Label, label.DefaultColor)];
            value.LinesProvider = () =>
                [new UiText.Line(row.Value, ResolveColor(row.Style, value.DefaultColor))];
        }

        if (layer == CreatureAppraisalRowLayer.Foreground
            && content.Root is UiDatElement root)
        {
            root.MediaVisible = false;
        }

        return new UiTemplateListSlot(
            content,
            entryId: 0u,
            content.Root is IUiDatStateful stateful
                ? stateful.ActiveRetailStateId
                : UiStateInfo.DirectStateId,
            selectedState: null);
    }

    private static Vector4 ResolveColor(
        CreatureAppraisalValueStyle style,
        Vector4 normal)
    {
        _ = style;
        return normal;
    }

    private static T Required<T>(ImportedLayout content, uint id)
        where T : UiElement
        => content.FindElement(id) as T
            ?? throw new InvalidOperationException(
                $"Retail creature appraisal template element 0x{id:X8} did not resolve to {typeof(T).Name}.");

    private static ElementInfo? Find(ElementInfo root, uint id)
    {
        if (root.Id == id)
            return root;
        foreach (ElementInfo child in root.Children)
            if (Find(child, id) is { } found)
                return found;
        return null;
    }

    private static void CaptureFonts(
        ElementInfo info,
        Func<uint, UiDatFont?>? resolveFont,
        Dictionary<uint, UiDatFont?> fonts)
    {
        if (info.FontDid != 0u && !fonts.ContainsKey(info.FontDid))
            fonts[info.FontDid] = resolveFont?.Invoke(info.FontDid);
        foreach (ElementInfo child in info.Children)
            CaptureFonts(child, resolveFont, fonts);
    }
}

public sealed class CreatureAppraisalLayeredList
{
    public const float TextInset = 8f;

    private readonly CreatureAppraisalRowTemplateFactory _templates;

    private CreatureAppraisalLayeredList(
        CreatureAppraisalRowTemplateFactory templates,
        UiItemList background,
        UiItemList foreground)
    {
        _templates = templates;
        Background = background;
        Foreground = foreground;
    }

    public UiItemList Background { get; }
    public UiItemList Foreground { get; }

    public static CreatureAppraisalLayeredList Create(
        UiElement panel,
        UiElement backgroundHost,
        UiViewport viewport,
        CreatureAppraisalRowTemplateFactory templates,
        int backgroundZOrder)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(backgroundHost);
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(templates);

        int foregroundZOrder = backgroundHost.ZOrder;
        backgroundHost.ZOrder = backgroundZOrder;
        var scroll = new UiScrollable();
        var background = NewList(
            left: 0f,
            top: 0f,
            width: templates.Width,
            height: backgroundHost.Height,
            zOrder: 0,
            scroll: scroll);
        background.ClickThrough = true;
        backgroundHost.AddChild(background);

        var foreground = NewList(
            left: backgroundHost.Left + TextInset,
            top: backgroundHost.Top,
            width: templates.Width,
            height: backgroundHost.Height,
            zOrder: foregroundZOrder,
            scroll: scroll);
        foreground.Anchors = AnchorEdges.Left | AnchorEdges.Top
            | AnchorEdges.Right | AnchorEdges.Bottom;
        panel.AddChild(foreground);

        return new CreatureAppraisalLayeredList(
            templates,
            background,
            foreground);
    }

    public void Rebuild(IReadOnlyList<CreatureAppraisalRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        using (Background.DeferLayout())
        using (Foreground.DeferLayout())
        {
            Background.Flush();
            Foreground.Flush();
            foreach (CreatureAppraisalRow row in rows)
            {
                Background.AddItem(_templates.Create(
                    row,
                    CreatureAppraisalRowLayer.Background));
                Foreground.AddItem(_templates.Create(
                    row,
                    CreatureAppraisalRowLayer.Foreground));
            }
        }
    }

    public void Flush()
    {
        Background.Flush();
        Foreground.Flush();
    }

    public void ResetScroll() => Foreground.Scroll.SetScrollY(0);

    private static UiItemList NewList(
        float left,
        float top,
        float width,
        float height,
        int zOrder,
        UiScrollable scroll)
    {
        return new UiItemList(scroll: scroll)
        {
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            ZOrder = zOrder,
            Columns = 1,
            CellWidth = width,
            CellHeight = 20f,
            Anchors = AnchorEdges.Left | AnchorEdges.Top,
        };
    }
}

public sealed class CreatureDisplayNameResolver
{
    public const uint MapperDid = 0x2200000Eu;
    private readonly IReadOnlyDictionary<uint, string> _names;

    public CreatureDisplayNameResolver(
        IReadOnlyDictionary<uint, string> names)
    {
        _names = names ?? throw new ArgumentNullException(nameof(names));
    }

    public static CreatureDisplayNameResolver Load(IDatReaderWriter dats)
    {
        ArgumentNullException.ThrowIfNull(dats);
        var names = new Dictionary<uint, string>();
        var visited = new HashSet<uint>();
        uint did = MapperDid;
        while (did != 0u && visited.Add(did))
        {
            EnumMapper? mapper = dats.Get<EnumMapper>(did);
            if (mapper is null)
                break;
            foreach ((uint id, PStringBase<byte> text) in mapper.IdToStringMap)
                names.TryAdd(id, text.Value.Replace('_', ' '));
            did = mapper.BaseEnumMap;
        }
        return new CreatureDisplayNameResolver(names);
    }

    public string Resolve(int creatureType)
        => creatureType > 0
            && _names.TryGetValue((uint)creatureType, out string? name)
                ? name
                : string.Empty;
}
