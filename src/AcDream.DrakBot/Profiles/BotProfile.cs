using System.Text.Json;
using System.Text.Json.Serialization;
using AcDream.DrakBot.Combat;
using AcDream.DrakBot.Loot;
using AcDream.DrakBot.Navigation;

namespace AcDream.DrakBot.Profiles;

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

    public MetaOptions Meta { get; init; } = new();

    public PetSettings Pets { get; init; } = new();

    public DoorSettings Doors { get; init; } = new();

    public ManaStoneSettings ManaStones { get; init; } = new();

    public InventorySettings Inventory { get; init; } = new();

    public SalvageSettings Salvage { get; init; } = new();

    public SpellTierSettings SpellTiers { get; init; } = new();

    public PrioritySettings Priorities { get; init; } = new();

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

    /// <summary>
    /// With no hostile in range, top vitals up to these higher fractions
    /// too, so a fight starts full; zero leaves a vital to the fight
    /// thresholds above.
    /// </summary>
    public double IdleHealthBelow { get; init; } = 0.95;

    public double IdleStaminaBelow { get; init; } = 0.9;

    public double IdleManaBelow { get; init; } = 0.9;

    /// <summary>Heal fellows in range whose health falls under this fraction; zero never does.</summary>
    public double HealFellowsBelow { get; init; }

    public string HealOtherSpell { get; init; } = "Heal Other";

    /// <summary>How far a fellow may be to be healed.</summary>
    public float HealFellowsRangeMeters { get; init; } = 20f;
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

    /// <summary>Cast the weapon auras below on the wielded weapon.</summary>
    public bool BuffWeapon { get; init; }

    /// <summary>Self-cast auras that land on the wielded weapon.</summary>
    public IReadOnlyList<string> WeaponSpells { get; init; } =
    [
        "Blood Drinker Self",
        "Heart Seeker Self",
        "Defender Self",
        "Swift Killer Self",
    ];

    /// <summary>Cast the armor spells below on every equipped piece of armor.</summary>
    public bool BuffArmor { get; init; }

    /// <summary>Spells cast on each equipped armor piece.</summary>
    public IReadOnlyList<string> ArmorSpells { get; init; } =
    [
        "Impenetrability",
        "Acid Bane",
        "Blade Bane",
        "Bludgeoning Bane",
        "Flame Bane",
        "Frost Bane",
        "Lightning Bane",
        "Piercing Bane",
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

    /// <summary>
    /// The VTank-style monster list: per kind of monster, whether to fight
    /// it and how. Empty means every hostile is fought the same way with
    /// the element keyword above; with rules, a monster no rule matches
    /// falls to the rule named Default, and is left alone without one.
    /// </summary>
    public IReadOnlyList<MonsterRule> Monsters { get; init; } = [];

    /// <summary>Rules that allow a ring cast one when this many hostiles stand within ring range.</summary>
    public int MinRingTargets { get; init; } = 3;

    /// <summary>
    /// With a ranged style, walk away from a hostile that gets this close
    /// before the next shot; zero never backs off.
    /// </summary>
    public float BackOffWhenWithinMeters { get; init; }

    /// <summary>Back off until the nearest hostile is this far.</summary>
    public float BackOffToMeters { get; init; } = 6f;

    /// <summary>Give up backing off after this long and fight where the character stands.</summary>
    public double BackOffTimeoutSeconds { get; init; } = 4d;

    /// <summary>The weapon to wield for the melee style, by name; empty leaves the hands alone.</summary>
    public string MeleeWeapon { get; init; } = string.Empty;

    public string MissileWeapon { get; init; } = string.Empty;

    public string Wand { get; init; } = string.Empty;

    /// <summary>With the missile style, keep a stack of the wielded bow's ammunition wielded.</summary>
    public bool KeepAmmunition { get; init; } = true;

    /// <summary>How far a ring reaches; zero never rings.</summary>
    public float RingRangeMeters { get; init; } = 8f;

    /// <summary>Return to peace mode once nothing is left to fight.</summary>
    public bool LeaveCombatWhenIdle { get; init; } = true;

    /// <summary>A melee target farther than this is walked up to before the swing.</summary>
    public float MeleeRangeMeters { get; init; } = 2.5f;

    /// <summary>
    /// A ranged target with no line of sight is walked toward until it is
    /// this close or the path clears; inside this range a blocked target
    /// earns blacklist strikes instead.
    /// </summary>
    public float ApproachRangeMeters { get; init; } = 6f;

    /// <summary>Give up on walking toward one target after this long.</summary>
    public double ApproachTimeoutSeconds { get; init; } = 12d;

    public LineOfSightSettings LineOfSight { get; init; } = new();
}

/// <summary>How a war spell is modelled when checking whether it can reach.</summary>
public enum WarSpellPath
{
    /// <summary>Bolts and streaks: a flat shot.</summary>
    Straight,
    /// <summary>Arc spells: a lob under gravity.</summary>
    Arc,
}

/// <summary>
/// Line-of-sight checks before ranged attacks. They run the client's own
/// projectile collision, so nothing here describes geometry; the options
/// pick the trajectory to test and decide what to do with a blocked target.
/// </summary>
public sealed record LineOfSightSettings
{
    public bool Enabled { get; init; } = true;

    public WarSpellPath WarSpellPath { get; init; } = WarSpellPath.Straight;

    /// <summary>Indoors an arc meets the ceiling; test the flat path there instead.</summary>
    public bool StraightPathIndoors { get; init; } = true;

    /// <summary>Horizontal launch speed used to model an arc spell; zero takes the client default.</summary>
    public float ArcLaunchSpeed { get; init; }

    /// <summary>Horizontal launch speed used to model an arrow or bolt; zero takes the client default.</summary>
    public float MissileLaunchSpeed { get; init; }

    /// <summary>A target found blocked this many times in a row is skipped for a while.</summary>
    public int BlacklistStrikes { get; init; } = 3;

    /// <summary>How long a blacklisted target is skipped.</summary>
    public double BlacklistSeconds { get; init; } = 30d;

    /// <summary>A verdict is reused for this long before the path is swept again.</summary>
    public double CacheSeconds { get; init; } = 0.75d;

    public float ProjectileRadius { get; init; } = 0.25f;

    public float StepDistanceMeters { get; init; } = 1.5f;

    public int MaximumCollisionChecks { get; init; } = 128;

    /// <summary>Ask the client to draw the swept path.</summary>
    public bool ShowDebugSamples { get; init; }

    /// <summary>
    /// Walk the character's own collision toward a target before and while
    /// approaching it, steering around what blocks the way; a target no
    /// heading reaches is struck like a ranged-blocked one.
    /// </summary>
    public bool CheckWalkPath { get; init; } = true;

    /// <summary>How far ahead a steering heading is walked to call it open.</summary>
    public float WalkLookaheadMeters { get; init; } = 4f;
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

    /// <summary>
    /// A VTank <c>.utl</c> loot profile by name, from the VTank profiles
    /// folder; when set it decides instead of <see cref="Rules"/>.
    /// </summary>
    public string UtlProfile { get; init; } = string.Empty;
}

/// <summary>Doors met while walking a route.</summary>
public sealed record DoorSettings
{
    public bool Enabled { get; init; } = true;

    /// <summary>A closed door this close is opened before the walk goes on.</summary>
    public float RangeMeters { get; init; } = 4f;

    /// <summary>A door that will not open is picked with a lockpick from the pack.</summary>
    public bool UseLockpicks { get; init; }
}

/// <summary>
/// Mana stones: recharge worn items from charged stones in the pack and,
/// when tapping is on, fill empty stones from loot carrying enough mana.
/// </summary>
public sealed record ManaStoneSettings
{
    public bool Enabled { get; init; }

    /// <summary>
    /// An unworn item with at least this much mana is drained into an empty
    /// stone; zero never drains anything and only charged stones are used.
    /// </summary>
    public int TapThresholdMana { get; init; } = 2500;

    /// <summary>Stones kept in the pack; corpses' stones past this are left. Applied by the looter.</summary>
    public int KeepCount { get; init; } = 5;

    /// <summary>Only stones whose name contains one of these are used; empty means any mana stone.</summary>
    public IReadOnlyList<string> StoneNames { get; init; } = [];
}

/// <summary>
/// The buffed skill a school needs before each spell tier is cast, tier I
/// to VIII. RynthAi's defaults: the combat ladder sits where casts stop
/// fizzling; the buff ladder at the game's minimums.
/// </summary>
public sealed record SpellTierSettings
{
    public IReadOnlyList<int> CombatMinimums { get; init; } = [0, 85, 135, 185, 235, 285, 335, 435];

    public IReadOnlyList<int> BuffMinimums { get; init; } = [0, 85, 135, 185, 235, 285, 335, 435];
}

/// <summary>
/// User overrides of the behavior order: navigation or looting lifted
/// above combat. Survival and buffing stay on top whatever is set, and a
/// fight already under way is not left for a corpse.
/// </summary>
public sealed record PrioritySettings
{
    public bool BoostNavigation { get; init; }

    public bool BoostLooting { get; init; }
}

/// <summary>Salvaging looted items with the Ust, and merging the bags.</summary>
public sealed record SalvageSettings
{
    public bool Enabled { get; init; } = true;

    /// <summary>Merge under-full bags of one material and workmanship band now and then.</summary>
    public bool CombineBags { get; init; } = true;
}

/// <summary>Pack housekeeping done beside whatever the bot is doing.</summary>
public sealed record InventorySettings
{
    /// <summary>Move loose items out of the main pack into a side pack with room.</summary>
    public bool AutoCram { get; init; }

    /// <summary>Merge partial stacks of the same item.</summary>
    public bool AutoStack { get; init; } = true;
}

/// <summary>Combat pets: which essences to summon from, and when.</summary>
public sealed record PetSettings
{
    public bool Enabled { get; init; }

    /// <summary>Essence devices by name, tried in order.</summary>
    public IReadOnlyList<string> Devices { get; init; } = [];

    /// <summary>Summon once this many hostiles are within range.</summary>
    public int MinimumHostiles { get; init; } = 1;

    public float RangeMeters { get; init; } = 20f;

    /// <summary>Refill an empty essence from an Encapsulated Spirit in the pack.</summary>
    public bool RefillFromSpirits { get; init; } = true;
}

/// <summary>The VTank-style meta: which one to load with the profile, and whether it runs.</summary>
public sealed record MetaOptions
{
    public bool Enabled { get; init; }

    /// <summary>A meta (<c>.af</c> or <c>.met</c>) in the VTank profiles folder, loaded when the profile is; empty for none.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Echo every rule that fires to chat.</summary>
    public bool Debug { get; init; }
}

public sealed record NavigationSettings
{
    public bool Enabled { get; init; } = true;

    public RouteMode Mode { get; init; } = RouteMode.Loop;

    /// <summary>A waypoint counts as reached inside this many meters.</summary>
    public double ArrivalDistanceMeters { get; init; } = 1.5;

    /// <summary>
    /// Stop and turn in place when the waypoint is off by more than this;
    /// smaller errors are steered out while running. The run resumes at
    /// half this angle.
    /// </summary>
    public float TurnToleranceDegrees { get; init; } = Navigation.Walker.TurnInPlaceDegrees;

    /// <summary>
    /// Inside this many meters of a waypoint the aim point blends toward
    /// the next one so corners are cut smoothly; zero aims straight at each.
    /// </summary>
    public double LookaheadMeters { get; init; } = 4d;

    /// <summary>
    /// How long to stand still after a portal or recall lands before the
    /// route carries on, so the world around the new spot has arrived.
    /// </summary>
    public double PostPortalDelaySeconds { get; init; } = 4d;

    /// <summary>
    /// Follow a player instead of walking the route: a name, or
    /// <c>leader</c> for the fellowship's leader; empty walks the route.
    /// </summary>
    public string Follow { get; init; } = string.Empty;

    /// <summary>Stop this close to the followed player and set off again a little beyond it.</summary>
    public float FollowStopMeters { get; init; } = 5f;

    public float FollowResumeMeters { get; init; } = 8f;
}
