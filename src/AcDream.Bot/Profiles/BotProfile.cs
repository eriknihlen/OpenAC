using System.Text.Json;
using System.Text.Json.Serialization;
using AcDream.Bot.Loot;
using AcDream.Bot.Navigation;

namespace AcDream.Bot.Profiles;

/// <summary>
/// Everything the operator tunes. Spells are named the way the game names
/// them; the selector resolves names to whatever tier the character knows.
/// </summary>
public sealed record BotProfile
{
    public static BotProfile Default { get; } = new();

    public string Name { get; init; } = "default";

    public VitalSettings Vitals { get; init; } = new();

    public BuffSettings Buffs { get; init; } = new();

    public CombatSettings Combat { get; init; } = new();

    public LootSettings Loot { get; init; } = new();

    public NavigationSettings Navigation { get; init; } = new();

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static BotProfile FromJson(string json) =>
        JsonSerializer.Deserialize<BotProfile>(json, JsonOptions)
            ?? throw new JsonException("profile is empty");
}

public sealed record VitalSettings
{
    /// <summary>Cast the heal spell when health drops below this fraction.</summary>
    public double HealBelow { get; init; } = 0.6;

    public double StaminaBelow { get; init; } = 0.3;

    public double ManaBelow { get; init; } = 0.25;

    public string HealSpell { get; init; } = "Heal Self";

    public string StaminaSpell { get; init; } = "Revitalize Self";

    public string ManaSpell { get; init; } = "Stamina to Mana Self";

    /// <summary>Fall back to a healing kit when no heal spell is castable.</summary>
    public bool UseHealingKits { get; init; } = true;
}

public sealed record BuffSettings
{
    public bool Enabled { get; init; } = true;

    /// <summary>Recast a buff when less than this many seconds remain.</summary>
    public double RebuffWhenRemainingSeconds { get; init; } = 60d;

    /// <summary>Spell families to keep up, by game name without tier.</summary>
    public IReadOnlyList<string> Spells { get; init; } =
    [
        "Strength Self",
        "Endurance Self",
        "Coordination Self",
        "Quickness Self",
        "Focus Self",
        "Willpower Self",
        "Armor Self",
        "Blade Protection Self",
        "Pierce Protection Self",
        "Bludgeon Protection Self",
        "Flame Protection Self",
        "Frost Protection Self",
        "Acid Protection Self",
        "Lightning Protection Self",
    ];
}

public enum CombatStyle
{
    Melee,
    Missile,
    Magic,
}

public sealed record CombatSettings
{
    public bool Enabled { get; init; } = true;

    public CombatStyle Style { get; init; } = CombatStyle.Melee;

    /// <summary>Hostiles farther than this are left alone.</summary>
    public float EngageDistance { get; init; } = 25f;

    /// <summary>Restrict war spells to one element by name; empty means any.</summary>
    public string ElementKeyword { get; init; } = string.Empty;

    public AttackHeight Height { get; init; } = AttackHeight.Medium;

    /// <summary>Physical attack power bar level, 0..1.</summary>
    public float Power { get; init; } = 1f;

    /// <summary>Names attacked first, in order, when several hostiles are in range.</summary>
    public IReadOnlyList<string> PriorityNames { get; init; } = [];

    /// <summary>Names never attacked.</summary>
    public IReadOnlyList<string> IgnoreNames { get; init; } = [];

    /// <summary>Return to peace mode once nothing is left to fight.</summary>
    public bool LeaveCombatWhenIdle { get; init; } = true;
}

public enum AttackHeight
{
    High = 1,
    Medium = 2,
    Low = 3,
}

public sealed record LootSettings
{
    public bool Enabled { get; init; } = true;

    public float ScanDistance { get; init; } = 15f;

    /// <summary>Seconds to wait for the server before giving up on one corpse.</summary>
    public double StepTimeoutSeconds { get; init; } = 6d;

    public LootRuleSet Rules { get; init; } = LootRuleSet.Default;
}

public sealed record NavigationSettings
{
    public bool Enabled { get; init; } = true;

    public RouteMode Mode { get; init; } = RouteMode.Loop;

    /// <summary>A waypoint counts as reached inside this many meters.</summary>
    public double ArrivalDistanceMeters { get; init; } = 1.5;

    /// <summary>Turn in place before walking when off by more than this.</summary>
    public float TurnToleranceDegrees { get; init; } = 12f;
}
