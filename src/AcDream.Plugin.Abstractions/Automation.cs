namespace AcDream.Plugin.Abstractions;

/// <summary>How far a character has trained one skill.</summary>
public enum PluginSkillTraining
{
    /// <summary>The client has not been told this skill's training level.</summary>
    Unknown = 0,

    /// <summary>The skill has not been trained and is used at its untrained penalty.</summary>
    Untrained,

    /// <summary>The skill has been trained.</summary>
    Trained,

    /// <summary>The skill has been specialized, the highest training level.</summary>
    Specialized,
}

/// <summary>
/// Everything the client knows about one spell, read from its loaded
/// content. It describes the spell itself, not a particular cast of it.
/// </summary>
/// <param name="SpellId">The spell's id.</param>
/// <param name="Name">The spell's name as shown to the player.</param>
/// <param name="Family">
/// The group of spells that do the same thing at different strengths. Two
/// spells of one family cannot both be in force at once.
/// </param>
/// <param name="Tier">The spell's rank within its family; higher is stronger.</param>
/// <param name="Difficulty">The skill level the spell demands to cast reliably.</param>
/// <param name="ManaCost">Mana one cast costs.</param>
/// <param name="DurationSeconds">
/// How long the effect lasts once it lands, in seconds; 0 for a spell with
/// no lasting effect.
/// </param>
/// <param name="School">
/// The id of the magic skill the spell is cast with, or 0 when the client
/// cannot map the spell's school to one.
/// </param>
/// <param name="Description">The spell's description text.</param>
/// <param name="IsSelfTargeted">Whether the spell can only be cast on the caster.</param>
/// <param name="IsBeneficial">Whether the spell helps rather than harms its target.</param>
public readonly record struct PluginSpellInfo(
    uint SpellId,
    string Name,
    uint Family,
    int Tier,
    int Difficulty,
    int ManaCost,
    float DurationSeconds,
    uint School,
    string Description,
    bool IsSelfTargeted,
    bool IsBeneficial)
{
    /// <summary>Whether the spell weakens its target.</summary>
    public bool IsDebuff { get; init; }

    /// <summary>Whether the spell is cast at an enemy.</summary>
    public bool IsOffensive { get; init; }

    /// <summary>Whether the spell affects the caster's whole fellowship.</summary>
    public bool IsFellowship { get; init; }

    /// <summary>Whether the spell is cast without picking a target.</summary>
    public bool IsUntargeted { get; init; }

    /// <summary>
    /// VTank's spell-facing rule: targeted spells require facing except the
    /// authored family range 222..235.
    /// </summary>
    public bool RequiresTurnTo { get; init; }

    /// <summary>
    /// Whether the spell launches a missile that has to travel to its
    /// target, rather than taking effect where it is aimed.
    /// </summary>
    public bool IsProjectile { get; init; }

    /// <summary>Whether the spell keeps damaging its target over time.</summary>
    public bool IsDamageOverTime { get; init; }

    /// <summary>
    /// The spell's raw flag bits from the content table, for rules this type
    /// does not model as its own property.
    /// </summary>
    public uint RawFlags { get; init; }

    /// <summary>The spell's category number from the content table.</summary>
    public int SpellType { get; init; }

    /// <summary>
    /// Bits describing what kinds of thing the spell may be cast on. 0 means
    /// the content declares no targets for it.
    /// </summary>
    public uint TargetMask { get; init; }

    /// <summary>
    /// The fixed part of the spell's range, in metres. The whole range is
    /// this plus <see cref="BaseRangeModifier"/> times the caster's skill in
    /// the spell's school.
    /// </summary>
    public float BaseRangeConstant { get; init; }

    /// <summary>
    /// The metres of range each point of the caster's school skill adds; see
    /// <see cref="BaseRangeConstant"/>.
    /// </summary>
    public float BaseRangeModifier { get; init; }

    /// <summary>
    /// The components the spell's formula consumes, in casting order. Empty
    /// when the content has no formula for it.
    /// </summary>
    public IReadOnlyList<uint> FormulaComponentIds { get; init; } =
        Array.Empty<uint>();

    /// <summary>
    /// VTank's spell quality. It is the portal spell difficulty unless its
    /// official GameInfoDB override supplies a replacement.
    /// </summary>
    public int? QualityOverride { get; init; }

    /// <summary>
    /// The spell's quality: <see cref="QualityOverride"/> when one is set,
    /// otherwise <see cref="Difficulty"/>.
    /// </summary>
    public int Quality => QualityOverride ?? Difficulty;

    /// <summary>The spell's icon, as a full icon id; 0 when it has none.</summary>
    public uint IconId { get; init; }

    /// <summary>The words spoken while casting; empty when the spell has none.</summary>
    public string Saying { get; init; } = string.Empty;

    /// <summary>The four component slots that identify the spell's formula.</summary>
    public PluginSpellComponentSet ComponentSet { get; init; }

    /// <summary>
    /// The visual effect played on the caster when the spell goes off, as
    /// the spell table's effect-script number; 0 when it names none.
    /// </summary>
    public uint CasterEffect { get; init; }

    /// <summary>
    /// The visual effect played on the target when the spell lands, as the
    /// spell table's effect-script number; 0 when it names none.
    /// </summary>
    public uint TargetEffect { get; init; }

    /// <summary>
    /// The spell table's formula version: the number that, with the
    /// character's name, decides which components this character's version
    /// of the formula uses. Decal-era tools call it the spell's generation.
    /// </summary>
    public uint FormulaVersion { get; init; }

    /// <summary>
    /// Where the spell sits in the spellbook: spells are listed in rising
    /// order of this number. Decal-era tools call it the sort key.
    /// </summary>
    public int DisplayOrder { get; init; }

    /// <summary>
    /// The spell table's component-loss factor, which scales how readily a
    /// cast of this spell consumes its components. Decal-era tools read it
    /// as the spell's speed.
    /// </summary>
    public float ComponentLoss { get; init; }
}

/// <summary>
/// The four component slots that together identify a spell's formula. Each
/// value is a component id, or 0 when the formula fills that slot with
/// nothing.
/// </summary>
/// <param name="Herb">The formula's herb slot.</param>
/// <param name="Powder">The formula's powder slot.</param>
/// <param name="Potion">The formula's potion slot.</param>
/// <param name="Talisman">The formula's talisman slot.</param>
public readonly record struct PluginSpellComponentSet(
    uint Herb,
    uint Powder,
    uint Potion,
    uint Talisman);

/// <summary>One enchantment currently in force.</summary>
/// <param name="SpellId">The spell that placed it.</param>
/// <param name="Family">
/// The spell's family, so a caller can tell which effects would replace each
/// other. 0 when the client has no description of the spell.
/// </param>
/// <param name="Tier">
/// The spell's rank within its family, or 0 when the client has no
/// description of the spell.
/// </param>
/// <param name="SecondsRemaining">
/// The enchantment's duration in seconds, as the client last recorded it.
/// </param>
public readonly record struct PluginActiveEnchantment(
    uint SpellId,
    uint Family,
    int Tier,
    double SecondsRemaining);

/// <summary>One spell component as the client's loaded content describes it.</summary>
/// <param name="ComponentId">The component's id, as spell formulas name it.</param>
/// <param name="WeenieClassId">
/// The item class the component is carried as, so a caller can match it
/// against what is in the pack.
/// </param>
/// <param name="Name">The component's name as shown to the player.</param>
/// <param name="BurnRate">How readily the component is consumed by a failed cast.</param>
/// <param name="GestureId">The gesture animation played for this component.</param>
/// <param name="GestureSpeed">How fast that gesture is played.</param>
/// <param name="IconId">The component's icon, as a full icon id.</param>
/// <param name="SortKey">
/// The component's category number from the content table, which the client
/// orders components by.
/// </param>
/// <param name="Type">The component's type name.</param>
/// <param name="Word">The syllable spoken for this component while casting.</param>
public readonly record struct PluginSpellComponentInfo(
    uint ComponentId,
    uint WeenieClassId,
    string Name,
    double BurnRate,
    uint GestureId,
    double GestureSpeed,
    uint IconId,
    uint SortKey,
    string Type,
    string Word);

/// <summary>One of the character's skills.</summary>
/// <param name="SkillId">The skill's id.</param>
/// <param name="Name">
/// The skill's name as shown to the player; empty when the client has no
/// name for it.
/// </param>
/// <param name="Training">How far the skill has been trained.</param>
/// <param name="Current">
/// The skill level in effect right now, enchantments included.
/// </param>
public readonly record struct PluginSkillInfo(
    uint SkillId,
    string Name,
    PluginSkillTraining Training,
    uint Current)
{
    /// <summary>
    /// The skill level before enchantments. Defaults to
    /// <see cref="Current"/> when the caller sets no separate value.
    /// </summary>
    public uint Base { get; init; } = Current;

    /// <summary>The skill's icon, as a full icon id; 0 when it has none.</summary>
    public uint IconId { get; init; }

    /// <summary>
    /// How many times experience has been spent to raise this skill, which is
    /// the row a cost table is read at to price the next raise. 0 on a host
    /// that cannot tell, and before the server has stated the skill.
    /// </summary>
    public uint Ranks { get; init; }

    /// <summary>
    /// The experience already put into this skill towards the ranks it has.
    /// 0 on a host that cannot tell, and before the server has stated the
    /// skill.
    /// </summary>
    public ulong ExperienceSpent { get; init; }
}

/// <summary>One primary attribute. <paramref name="Kind"/> is 0..5.</summary>
public readonly record struct PluginAttributeInfo(
    int Kind,
    string Name,
    uint Current)
{
    /// <summary>Unenchanted primary-attribute value.</summary>
    public uint Base { get; init; } = Current;

    /// <summary>
    /// The number this attribute is named by when experience is spent on it,
    /// which is what <see cref="ICharacterInfo.RequestAdvancement"/> takes.
    /// It is not <see cref="Kind"/>: kinds count from zero and this does not,
    /// so a zero here means the record was never filled in.
    /// </summary>
    public uint StatId { get; init; } = (uint)(Kind + 1);

    /// <summary>
    /// How many times experience has been spent to raise this attribute,
    /// which is the row a cost table is read at to price the next raise. 0 on
    /// a host that cannot tell, and before the server has stated it.
    /// </summary>
    public uint Ranks { get; init; }

    /// <summary>
    /// The experience already put into this attribute towards the ranks it
    /// has. 0 on a host that cannot tell, and before the server has stated it.
    /// </summary>
    public ulong ExperienceSpent { get; init; }
}

/// <summary>
/// One of the character's three pools -- health, stamina and mana -- as both
/// a reading and something experience can be spent on.
/// </summary>
/// <param name="Kind">
/// Which pool: 0 health, 1 stamina, 2 mana. This is the pool's own kind, the
/// number <see cref="ICharacterInfo.TryGetVital"/> takes; it is neither a
/// position in <see cref="ICharacterInfo.Vitals"/>, which leaves out any
/// pool the server has not stated, nor the number the pool is named by on a
/// request -- see <see cref="StatId"/> for that.
/// </param>
/// <param name="Name">The pool's name as shown to the player.</param>
/// <param name="Current">How much of the pool is left right now.</param>
/// <param name="Maximum">
/// The pool at full, enchantments included -- the same number
/// <see cref="ICharacterInfo.MaxHealth"/> and its two siblings report.
/// </param>
public readonly record struct PluginVitalInfo(
    int Kind,
    string Name,
    uint Current,
    uint Maximum)
{
    /// <summary>
    /// The number this pool is named by when experience is spent on it, which
    /// is what <see cref="ICharacterInfo.RequestAdvancement"/> takes. It is
    /// not <see cref="Kind"/>, and a zero here means the record was never
    /// filled in.
    /// </summary>
    public uint StatId { get; init; } = (uint)((Kind * 2) + 1);

    /// <summary>
    /// The pool at full with every enchantment layer off. A host that does
    /// not track the split reports <see cref="Maximum"/> instead, in which
    /// case the two are the same number.
    /// </summary>
    public uint Base { get; init; } = Maximum;

    /// <summary>
    /// How many times experience has been spent to raise this pool, which is
    /// the row a cost table is read at to price the next raise. 0 on a host
    /// that cannot tell, and before the server has stated it.
    /// </summary>
    public uint Ranks { get; init; }

    /// <summary>
    /// The experience already put into this pool towards the ranks it has. 0
    /// on a host that cannot tell, and before the server has stated it.
    /// </summary>
    public ulong ExperienceSpent { get; init; }
}

/// <summary>Why a cast would or would not be accepted right now.</summary>
public enum PluginCastGate
{
    /// <summary>No live session, or the surface is not bound yet.</summary>
    Unavailable = 0,

    /// <summary>The cast would be accepted.</summary>
    Ready,

    /// <summary>The character has not learned this spell.</summary>
    NotKnown,

    /// <summary>The client is still busy with an earlier request.</summary>
    Busy,

    /// <summary>The spell needs a target and nothing is selected.</summary>
    NoTargetSelected,

    /// <summary>What is selected is not a legal target for this spell.</summary>
    TargetIncompatible,

    /// <summary>The host rejected it for a reason not modelled here.</summary>
    Refused,
}

/// <summary>What happened to a request to cast a spell.</summary>
public enum PluginCastRequestResult
{
    /// <summary>The request went out on the wire.</summary>
    Sent = 0,

    /// <summary>The character has not learned this spell.</summary>
    UnknownSpell,

    /// <summary>The spell needs a selection and there is none.</summary>
    NoTarget,

    /// <summary>The selection is not a legal target for this spell.</summary>
    IncompatibleTarget,

    /// <summary>The character is missing a component the spell's formula needs.</summary>
    MissingComponents,

    /// <summary>
    /// There is no live session to cast in, or the host refused for a reason
    /// not modelled here.
    /// </summary>
    Unavailable,
}

/// <summary>One title the character has earned.</summary>
/// <param name="TitleId">The title's number, as the server names it.</param>
/// <param name="Name">
/// The title as the game displays it, read from the installed data files;
/// empty when the files do not name it or the client has no data files.
/// </param>
public readonly record struct PluginCharacterTitle(uint TitleId, string Name);

/// <summary>
/// The local character: who they are, how they are doing, and what they can
/// do. Values read 0 or empty until the character is in the world.
/// </summary>
public interface ICharacterInfo
{
    /// <summary>Whether the character is in the world right now.</summary>
    bool IsInWorld { get; }

    /// <summary>The character's name; empty before login.</summary>
    string Name => string.Empty;

    /// <summary>Server-advertised world name used to scope global variables.</summary>
    string WorldName => string.Empty;

    /// <summary>
    /// Players the server reported as connected in its login-time world-name
    /// message, or -1 before it has said. This is a one-time snapshot: it
    /// does not update again for the rest of the session.
    /// </summary>
    int ServerPopulation => -1;

    /// <summary>Authenticated account name; expression surfaces expose only its hash.</summary>
    string AccountName => string.Empty;

    /// <summary>
    /// Which slot of the account's character list this character occupies,
    /// or -1 before login.
    /// </summary>
    int CharacterIndex => -1;

    /// <summary>The character's level; 0 before the server has said.</summary>
    int Level => 0;

    /// <summary>Unused ordinary slots in the main pack.</summary>
    int MainPackFreeSlots => 0;

    /// <summary>The character's own object id; 0 before login.</summary>
    uint ObjectId { get; }

    /// <summary>Current health.</summary>
    uint CurrentHealth { get; }

    /// <summary>Health at full.</summary>
    uint MaxHealth { get; }

    /// <summary>Current stamina.</summary>
    uint CurrentStamina { get; }

    /// <summary>Stamina at full.</summary>
    uint MaxStamina { get; }

    /// <summary>Current mana.</summary>
    uint CurrentMana { get; }

    /// <summary>Mana at full.</summary>
    uint MaxMana { get; }

    /// <summary>
    /// The unbuffed maximum health: the maximum with every enchantment layer
    /// off — base attributes, no vital enchantments. A host that does not
    /// track the split reports the buffed maximum instead, in which case this
    /// and <see cref="MaxHealth"/> are the same number.
    /// </summary>
    uint BaseHealth => MaxHealth;

    /// <inheritdoc cref="BaseHealth"/>
    uint BaseStamina => MaxStamina;

    /// <inheritdoc cref="BaseHealth"/>
    uint BaseMana => MaxMana;

    /// <summary>
    /// How many creatures the character may have summoned at once, as the
    /// server reports it; 0 when it has said nothing.
    /// </summary>
    int SummoningMastery => 0;

    /// <summary>
    /// What vitae leaves of the character's attributes, skills and vitals,
    /// as a fraction: 1 for a character with no vitae penalty, 0.95 for a
    /// five percent penalty. It is the product of every vitae effect the
    /// server has placed on the character. 1 before the server has sent the
    /// character's enchantments, and on a host that does not track them,
    /// which is what the default implementation reports.
    /// </summary>
    float VitaeMultiplier => 1f;

    /// <summary>
    /// The title the character has chosen to display, by the server's number
    /// for it; 0 when none is chosen, before the server has sent the
    /// character's titles, and on a host that does not track them, which is
    /// what the default implementation reports. Its text is the matching
    /// entry of <see cref="Titles"/>.
    /// </summary>
    uint CurrentTitleId => 0u;

    /// <summary>
    /// Every title the character has earned, in the order the server listed
    /// them when the character entered the world, with each title earned
    /// since added at the end. Empty before the server has sent the list,
    /// and on a host that does not track titles, which is what the default
    /// implementation returns.
    /// </summary>
    IReadOnlyList<PluginCharacterTitle> Titles =>
        Array.Empty<PluginCharacterTitle>();

    /// <summary>
    /// The vitae penalty in whole percent: 0 for none, 5 for a character
    /// whose stats are held at 95 percent. It is
    /// <see cref="VitaeMultiplier"/> taken from one, times one hundred and
    /// rounded to the nearest whole number, and is always between 0 and 100.
    /// 0 whenever <see cref="VitaeMultiplier"/> reports 1, which includes the
    /// default implementation.
    /// </summary>
    int VitaePenaltyPercent =>
        Math.Clamp((int)(((1d - VitaeMultiplier) * 100d) + 0.5d), 0, 100);

    /// <summary>
    /// Every skill the client can describe, sorted by name. Empty before the
    /// server has sent the character's skills.
    /// </summary>
    IReadOnlyList<PluginSkillInfo> Skills { get; }

    /// <summary>The six primary attributes.</summary>
    IReadOnlyList<PluginAttributeInfo> Attributes { get; }

    /// <summary>
    /// Health, stamina and mana in that order, each with what the server has
    /// said about how it was raised. Empty before the server has stated them,
    /// and on a host that does not project them.
    /// </summary>
    IReadOnlyList<PluginVitalInfo> Vitals => Array.Empty<PluginVitalInfo>();

    /// <summary>
    /// Looks up one pool by its kind: 0 health, 1 stamina, 2 mana. This is
    /// the pool's own number, not an index into <see cref="Vitals"/>, which
    /// leaves out any pool the server has not stated yet and so can be
    /// shorter than three. False when the number names no pool, when the
    /// server has not stated it yet, or on a host that does not project them.
    /// </summary>
    /// <param name="kind">Which pool: 0 health, 1 stamina, 2 mana.</param>
    /// <param name="vital">The pool, or a default record when false.</param>
    bool TryGetVital(int kind, out PluginVitalInfo vital)
    {
        vital = default;
        return false;
    }

    /// <summary>
    /// Asks the client to spend on one stat: raising an attribute, a pool or a
    /// skill with experience, or training a skill with skill credits. The
    /// client checks the request and sends it; whether the spend is allowed is
    /// the server's own decision and arrives later as an updated stat.
    /// </summary>
    /// <param name="kind">Which kind of stat the spend is against.</param>
    /// <param name="statId">
    /// Which stat: <see cref="PluginAttributeInfo.StatId"/> for an attribute,
    /// <see cref="PluginVitalInfo.StatId"/> for a pool, and
    /// <see cref="PluginSkillInfo.SkillId"/> for a skill, trained or raised.
    /// Zero names nothing and is refused.
    /// </param>
    /// <param name="cost">
    /// What to spend: experience for the first three kinds, skill credits for
    /// <see cref="PluginAdvancementKind.TrainSkill"/>. Zero is refused, as is
    /// anything above the bound in <see cref="PluginAdvancement"/>.
    /// </param>
    /// <returns>
    /// What the client did with it, and why not when it refused. A host that
    /// cannot send -- no session, or no such surface -- answers
    /// <see cref="PluginAdvancementStatus.Unavailable"/> rather than throwing.
    /// </returns>
    PluginAdvancementResult RequestAdvancement(
        PluginAdvancementKind kind,
        uint statId,
        ulong cost) =>
        new(PluginAdvancementStatus.Unavailable);

    /// <summary>
    /// Enchantments in force on the local player. Snapshot semantics: the list
    /// is rebuilt by the host, never mutated in place under a reader.
    /// </summary>
    IReadOnlyList<PluginActiveEnchantment> ActiveEnchantments { get; }

    /// <summary>Timed spell buffs, including those temporarily suppressed by
    /// a stronger equipped effect. Equipment enchantments are excluded.</summary>
    IReadOnlyList<PluginActiveEnchantment> TimedEnchantments => ActiveEnchantments;

    /// <summary>
    /// Looks up one skill by id. False when the character has no such skill
    /// or the server has not sent it yet.
    /// </summary>
    bool TryGetSkill(uint skillId, out PluginSkillInfo skill);
}

/// <summary>
/// The spells the character knows and the spell descriptions the client has
/// loaded. Every list is empty before the server has sent the spellbook.
/// </summary>
public interface ISpellCatalog
{
    /// <summary>
    /// Beneficial spells the character can cast on themselves, ordered by
    /// family with the strongest of each family first.
    /// </summary>
    IReadOnlyList<PluginSpellInfo> KnownSelfBuffs { get; }

    /// <summary>
    /// Known spells that attack a target directly, strongest first. Debuffs
    /// and self- or untargeted spells are not in this list.
    /// </summary>
    IReadOnlyList<PluginSpellInfo> KnownAttackSpells =>
        Array.Empty<PluginSpellInfo>();

    /// <summary>
    /// Every known spell used against an enemy, attacks and debuffs alike,
    /// strongest first.
    /// </summary>
    IReadOnlyList<PluginSpellInfo> KnownCombatSpells =>
        Array.Empty<PluginSpellInfo>();

    /// <summary>
    /// Whether the character has learned this spell. The default
    /// implementation always answers false; read the known-spell lists
    /// instead.
    /// </summary>
    bool IsKnown(uint spellId) => false;

    /// <summary>
    /// Looks up one spell's description. False when the loaded content
    /// describes no such spell.
    /// </summary>
    bool TryGet(uint spellId, out PluginSpellInfo info);

    /// <summary>
    /// Every spell the loaded content table describes, not only the ones the
    /// character knows. Built on first use and cached by the host.
    /// </summary>
    IReadOnlyList<PluginSpellInfo> All => Array.Empty<PluginSpellInfo>();

    /// <summary>
    /// Finds a spell in <see cref="All"/> by name, ignoring case. An exact
    /// match wins; <paramref name="partialMatch"/> falls back to the first
    /// name that contains the text.
    /// </summary>
    bool TryFindByName(string name, bool partialMatch, out PluginSpellInfo spell)
    {
        spell = default;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        IReadOnlyList<PluginSpellInfo> all = All;
        for (int index = 0; index < all.Count; index++)
        {
            if (string.Equals(
                    all[index].Name,
                    name,
                    StringComparison.OrdinalIgnoreCase))
            {
                spell = all[index];
                return true;
            }
        }

        if (!partialMatch)
            return false;

        for (int index = 0; index < all.Count; index++)
        {
            if (all[index].Name is { } candidate
                && candidate.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                spell = all[index];
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Looks up one spell component's description. False when the loaded
    /// content describes no such component, and on a host that carries no
    /// component descriptions.
    /// </summary>
    bool TryGetComponent(uint componentId, out PluginSpellComponentInfo info)
    {
        info = default;
        return false;
    }

    /// <summary>
    /// Seconds left before a spell's shared cooldown allows another cast, or
    /// 0 when it is not on cooldown.
    /// </summary>
    double GetCooldownRemaining(uint cooldownId) => 0d;
}

/// <summary>The semantic kind of a link selected in the chat transcript.</summary>
public enum PluginChatLinkKind
{
    /// <summary>A world coordinate.</summary>
    Coordinate,
}

/// <summary>A coordinate carried by a chat link.</summary>
/// <param name="EastWest">The east-west coordinate; west is negative.</param>
/// <param name="NorthSouth">The north-south coordinate; south is negative.</param>
public readonly record struct PluginChatCoordinate(double EastWest, double NorthSouth);

/// <summary>A semantic link selected in the client's chat transcript.</summary>
/// <param name="Kind">The kind of link.</param>
/// <param name="DisplayText">The text displayed for the link.</param>
/// <param name="Coordinate">The coordinate payload, when <paramref name="Kind"/> is <see cref="PluginChatLinkKind.Coordinate"/>.</param>
public readonly record struct PluginChatLinkClicked(
    PluginChatLinkKind Kind,
    string DisplayText,
    PluginChatCoordinate? Coordinate = null);

/// <summary>One line of the client's text, as a plugin sees it.</summary>
/// <param name="Sequence">The host's counter for this line.</param>
/// <param name="SenderObjectId">The object that said it, or 0 without a speaker.</param>
/// <param name="Kind">The line kind.</param>
/// <param name="Sender">The speaker's name.</param>
/// <param name="Text">The line itself.</param>
/// <param name="ChannelName">The channel name, when applicable.</param>
public readonly record struct PluginChatMessage(
    ulong Sequence,
    uint SenderObjectId,
    int Kind,
    string Sender,
    string Text,
    string ChannelName)
{
    /// <summary>
    /// The <see cref="Kind"/> carried by a short status notice shown over the
    /// world instead of being written into the transcript. It sits above every
    /// transcript kind so the two can never collide.
    /// </summary>
    public const int StatusTextKind = 100;

    /// <summary>
    /// The text class the client colours the line by. A plugin printing its
    /// own line passes the same value to <see cref="IPluginChat.PostMessage"/>.
    /// </summary>
    public int LogTextType { get; init; }

    /// <summary>
    /// Sub-kind of a combat line: 0 when the line is not one, 1 for an
    /// ordinary outgoing line, 2 for an incoming one, 3 for a failure.
    /// </summary>
    public int CombatKind { get; init; }

    /// <summary>When the client took delivery of the line.</summary>
    public DateTimeOffset Received { get; init; }

    /// <summary>
    /// The number of the channel a channel line (<see cref="Kind"/> 2) came
    /// on, and 0 on every other line. The fixed channels have no name on the
    /// wire and are told apart by this number alone: <c>0x800</c> fellowship,
    /// <c>0x1000</c> from a patron, <c>0x2000</c> from a vassal,
    /// <c>0x4000</c> from a follower, <c>0x1000000</c> co-vassals,
    /// <c>0x2000000</c> allegiance broadcast, <c>0x4000000</c> a fellowship
    /// broadcast, <c>0x8</c>, <c>0x10</c> and <c>0x20</c> the advocate
    /// channels. A named channel carries its room number here and its name in
    /// <see cref="ChannelName"/>.
    /// </summary>
    public uint ChannelId { get; init; }

    /// <summary>
    /// The whole line as the client's chat window prints it: the channel
    /// prefix, the speaker, the verb and the quoted words, with another
    /// player's name wrapped in the client's tell link
    /// (<c>&lt;Tell:IIDString:id:Name&gt;Name&lt;\Tell&gt;</c>). It carries no
    /// timestamp and no closing line break. Empty on a line the host did not
    /// word. A chat filter is offered the line before the language filter
    /// runs, so there this and <see cref="Text"/> are the words as they
    /// arrived; every reader afterwards gets both as printed, with the
    /// language filter's <c>****</c> in place when that option is on.
    /// </summary>
    public string DisplayText { get; init; } = string.Empty;
}

/// <summary>What an input interceptor decided about a line the player typed.</summary>
public enum PluginChatInputAction
{
    /// <summary>Leave the line alone; the next interceptor, if any, sees it.</summary>
    Pass,

    /// <summary>
    /// Replace the line with <see cref="PluginChatInputDecision.Text"/> and
    /// run the replacement through the chat pipeline from the start.
    /// </summary>
    Rewrite,

    /// <summary>
    /// Drop the line. It is sent nowhere, no command runs for it, and the
    /// player is told nothing unless the plugin says something itself.
    /// </summary>
    Suppress,
}

/// <summary>
/// The answer an input interceptor gives for one typed line. Build it from
/// <see cref="Pass"/>, <see cref="Suppress"/> or <see cref="Rewrite(string)"/>;
/// the default value is <see cref="Pass"/>.
/// </summary>
public readonly record struct PluginChatInputDecision
{
    private PluginChatInputDecision(PluginChatInputAction action, string? text)
    {
        Action = action;
        Text = text;
    }

    /// <summary>What to do with the line.</summary>
    public PluginChatInputAction Action { get; }

    /// <summary>
    /// The replacement line when <see cref="Action"/> is
    /// <see cref="PluginChatInputAction.Rewrite"/>; null otherwise.
    /// </summary>
    public string? Text { get; }

    /// <summary>Leave the line alone.</summary>
    public static PluginChatInputDecision Pass { get; } = default;

    /// <summary>Drop the line without sending it anywhere.</summary>
    public static PluginChatInputDecision Suppress { get; } =
        new(PluginChatInputAction.Suppress, null);

    /// <summary>
    /// Replace the line with <paramref name="text"/>. A blank replacement is
    /// treated as <see cref="Suppress"/> by the host.
    /// </summary>
    /// <param name="text">The line to send instead of what was typed.</param>
    public static PluginChatInputDecision Rewrite(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new(PluginChatInputAction.Rewrite, text);
    }
}

/// <summary>
/// Reading the client's text, printing into it, dropping lines before they
/// are shown, and intercepting lines before they are sent.
/// </summary>
public interface IPluginChat
{
    /// <summary>
    /// The most input interceptors one plugin may have installed at once. A
    /// registration past this is refused with an exception rather than
    /// silently ignored, so a plugin finds out.
    /// </summary>
    const int MaximumInputInterceptors = 16;

    /// <summary>
    /// Every retained line whose <see cref="PluginChatMessage.Sequence"/> is
    /// above the one given. The host keeps only the most recent few hundred
    /// lines, so a plugin that polls rarely loses the overflow; subscribe to
    /// <see cref="Received"/> when nothing may be missed. Empty when there
    /// is nothing newer, and on a host that retains no lines.
    /// </summary>
    IReadOnlyList<PluginChatMessage> CaptureMessages(ulong afterSequence) =>
        Array.Empty<PluginChatMessage>();

    /// <summary>
    /// Raised for every line the client takes delivery of, in order, on the
    /// thread that raises <see cref="IEvents.Tick"/>. Unlike
    /// <see cref="CaptureMessages"/>, nothing is dropped between polls.
    /// </summary>
    event Action<PluginChatLinkClicked> LinkClicked
    {
        add { }
        remove { }
    }

    /// <summary>Raised for each delivered chat line.</summary>
    event Action<PluginChatMessage> Received
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Installs a filter consulted before a line is shown. Returning true
    /// drops the line: it never reaches the transcript, the chat windows,
    /// <see cref="CaptureMessages"/>, <see cref="Received"/>, or the log file.
    /// Filters run in registration order, and one that throws suppresses
    /// nothing. Dispose the result to remove it; the host also removes every
    /// filter a plugin installed when that plugin unloads. A filter sees the
    /// line before the language filter censors it; readers see it censored.
    /// </summary>
    IDisposable RegisterFilter(Func<PluginChatMessage, bool> suppress) =>
        NoOpPluginRegistration.Instance;

    /// <summary>
    /// Installs an interceptor consulted for every line the player sends from
    /// the chat entry, on either front end, and for every line a plugin sends
    /// through <see cref="Submit"/>. The interceptor sees the line trimmed,
    /// otherwise exactly as typed, and answers with a
    /// <see cref="PluginChatInputDecision"/>.
    /// <para>
    /// Ordering: interceptors run AFTER the client's own command catalogue, so
    /// a line the client claims as one of its own commands never reaches an
    /// interceptor and no plugin can shadow or rewrite a client command. They
    /// run BEFORE plugin verbs and before the line is dispatched to a channel
    /// or a tell, so a rewritten line can itself become a plugin verb or a
    /// channel message. Interceptors run in registration order across every
    /// plugin, and the first one that does not pass decides.
    /// </para>
    /// <para>
    /// A rewrite is fed back through the pipeline from the start, so it may be
    /// intercepted again. The host bounds the rewrite passes; once the bound
    /// is reached the last text is sent as it stands. A suppressed line is
    /// sent nowhere and runs no command; the player is told nothing unless
    /// the plugin posts something. An interceptor that throws is logged once
    /// and skipped for that line, and chat carries on without it.
    /// </para>
    /// Dispose the result to remove the interceptor; the host also removes
    /// every interceptor a plugin installed when that plugin unloads. A host
    /// that has no chat pipeline to intercept returns a handle that revokes
    /// nothing and never calls the interceptor, which is what the default
    /// implementation does.
    /// </summary>
    /// <param name="intercept">
    /// Given the typed line, answers what to do with it.
    /// </param>
    /// <returns>A handle that removes the interceptor when disposed.</returns>
    /// <exception cref="InvalidOperationException">
    /// The plugin already has <see cref="MaximumInputInterceptors"/> installed.
    /// </exception>
    IDisposable RegisterInputInterceptor(
        Func<string, PluginChatInputDecision> intercept) =>
        NoOpPluginRegistration.Instance;

    /// <summary>
    /// Writes a line into the client's own text, visible only to this
    /// player. Nothing is sent to the server.
    /// </summary>
    void PostSystemMessage(string text);

    /// <summary>
    /// Writes a line in one of the client's own text classes, so a plugin can
    /// print in the colour that class carries.
    /// </summary>
    void PostMessage(string text, int logTextType) => PostSystemMessage(text);

    /// <summary>
    /// Runs text through the client's chat bar exactly as if the player had
    /// typed and sent it, commands included. Returns false when the text is
    /// blank or the host has no chat bar to submit to.
    /// </summary>
    bool Submit(string text) => false;

    /// <summary>
    /// Puts <paramref name="text"/> into the chat entry and focuses it,
    /// WITHOUT sending it, so the player can finish typing. False when there
    /// is no chat entry to type into or the player is already typing in it.
    /// </summary>
    bool Compose(string text) => false;

    /// <summary>
    /// True while the player is typing into the chat entry, so the keyboard is
    /// going into text rather than driving the character. Automation that
    /// steers by holding keys has to fall back to something else while this is
    /// set. A host with no chat entry answers false.
    /// </summary>
    bool IsInputActive => false;
}

/// <summary>A registration handle from a host that has nothing to revoke.</summary>
public sealed class NoOpPluginRegistration : IDisposable
{
    /// <summary>The shared instance; this type holds no state.</summary>
    public static NoOpPluginRegistration Instance { get; } = new();

    private NoOpPluginRegistration()
    {
    }

    /// <summary>Does nothing; there is no registration to revoke.</summary>
    public void Dispose()
    {
    }
}

/// <summary>
/// Casting spells, and asking beforehand whether a cast would be accepted.
/// </summary>
public interface IMagicCommands
{
    /// <summary>
    /// Whether the client is still working on a request that blocks a new
    /// cast. <see cref="EvaluateGate(uint)"/> reports
    /// <see cref="PluginCastGate.Busy"/> while this is true.
    /// </summary>
    bool IsCasting { get; }

    /// <summary>
    /// How the most recent cast ended. Its revision counter rises each time
    /// a cast completes, so a caller can tell a fresh outcome from a repeat
    /// of the last one; the default value means nothing has completed yet.
    /// </summary>
    PluginCastCompletion LastCompletion => default;

    /// <summary>
    /// Whether casting this spell at the current selection would be
    /// accepted, and if not, why.
    /// </summary>
    PluginCastGate EvaluateGate(uint spellId);

    /// <summary>
    /// Casts the spell at the current selection. True only when the request
    /// went out on the wire; use <see cref="RequestCast(uint)"/> to learn why
    /// it did not.
    /// </summary>
    bool Cast(uint spellId);

    /// <summary>
    /// Whether casting this spell at a particular object would be accepted.
    /// Reports <see cref="PluginCastGate.Refused"/> when that object cannot
    /// be selected at all.
    /// </summary>
    PluginCastGate EvaluateGate(uint spellId, uint targetObjectId) =>
        PluginCastGate.Refused;

    /// <summary>
    /// Selects the given object, then casts the spell at it. False when the
    /// object cannot be selected or the cast was not sent.
    /// </summary>
    bool Cast(uint spellId, uint targetObjectId) => false;

    /// <summary>
    /// Casts the spell at the current selection and reports what happened.
    /// </summary>
    PluginCastRequestResult RequestCast(uint spellId) =>
        Cast(spellId)
            ? PluginCastRequestResult.Sent
            : PluginCastRequestResult.Unavailable;

    /// <summary>
    /// Selects the given object, casts the spell at it, and reports what
    /// happened. Reports
    /// <see cref="PluginCastRequestResult.IncompatibleTarget"/> when the
    /// object cannot be selected.
    /// </summary>
    PluginCastRequestResult RequestCast(uint spellId, uint targetObjectId) =>
        Cast(spellId, targetObjectId)
            ? PluginCastRequestResult.Sent
            : PluginCastRequestResult.Unavailable;

    /// <summary>
    /// Whether the character is carrying everything the spell's formula
    /// consumes. True when the host cannot tell.
    /// </summary>
    bool HasComponents(uint spellId) => true;
}

/// <summary>
/// The whole of live gameplay as a plugin sees it: the character, the
/// spells, the world, and the commands that act on them. Each area is its
/// own interface; an area the host does not provide returns the shared
/// inert implementation instead of null.
/// </summary>
public interface IAutomationSurface
{
    /// <summary>
    /// Whether there is a live in-world session behind this surface. False
    /// before login, after logout, and on a surface the host never bound --
    /// read it before trusting anything else here.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>The local character.</summary>
    ICharacterInfo Character { get; }

    /// <summary>The spells the character knows, and every spell description loaded.</summary>
    ISpellCatalog Spells { get; }

    /// <summary>Casting spells.</summary>
    IMagicCommands Magic { get; }

    /// <summary>Reading and writing the client's text.</summary>
    IPluginChat Chat { get; }

    /// <summary>Answering the server's yes/no confirmation dialogs.</summary>
    IDialogAutomation Dialogs => NoOpAutomationSurface.Instance;

    /// <summary>Combat state, nearby hostiles, and melee/missile attacks.</summary>
    ICombatAutomation Combat => NoOpAutomationSurface.Instance;

    /// <summary>What the character owns to wear or wield, and equipping it.</summary>
    IEquipmentAutomation Equipment => NoOpAutomationSurface.Instance;

    /// <summary>The character's inventory, and using or applying items.</summary>
    IItemAutomation Items => NoOpAutomationSurface.Instance;

    /// <summary>Corpses and containers, and taking things out of them.</summary>
    ILootAutomation Loot => NoOpAutomationSurface.Instance;

    /// <summary>Authoritative fellowship vitals for helper spell policy.</summary>
    IFellowshipAutomation Fellowship => NoOpAutomationSurface.Instance;

    /// <summary>Authoritative allegiance identity and membership state.</summary>
    IAllegianceAutomation Allegiance => NoOpAutomationSurface.Instance;

    /// <summary>Enchantments in force on other objects, as the client tracks them.</summary>
    IEnchantmentAutomation Enchantments => NoOpAutomationSurface.Instance;

    /// <summary>Where things are, and moving and turning the character.</summary>
    INavigationAutomation Navigation => NoOpAutomationSurface.Instance;

    /// <summary>The world objects the client is tracking.</summary>
    IWorldObjectAutomation Objects => NoOpAutomationSurface.Instance;

    /// <summary>Generation-safe recall commands and portal transition state.</summary>
    IRecallAutomation Recalls => NoOpAutomationSurface.Instance;

    /// <summary>The in-game clock and calendar.</summary>
    IWorldTimeAutomation WorldTime => NoOpAutomationSurface.Instance;

    /// <summary>The account's character list, and logging out.</summary>
    ILoginAutomation Login => NoOpAutomationSurface.Instance;

    /// <summary>The other clients on this computer: the local client list, and the casts they report.</summary>
    INetworkAutomation Network => NoOpAutomationSurface.Instance;

    /// <summary>Recovering after death.</summary>
    IRecoveryAutomation Recovery => NoOpAutomationSurface.Instance;

    /// <summary>Whether a missile or spell would reach a target.</summary>
    IProjectileAutomation Projectiles => NoOpAutomationSurface.Instance;

    /// <summary>Text labels hung over objects in the world.</summary>
    IWorldLabelAutomation Labels => NoOpAutomationSurface.Instance;

    /// <summary>The shape of the dungeon the character is in, for drawing a map of it.</summary>
    IDungeonMapAutomation DungeonMap => NoOpAutomationSurface.Instance;

    /// <summary>What the player has selected, and acting on it.</summary>
    ISelectionAutomation Selection => NoOpAutomationSurface.Instance;

    /// <summary>Trading directly with another player.</summary>
    ITradeAutomation Trade => NoOpAutomationSurface.Instance;

    /// <summary>Buying from and selling to a vendor.</summary>
    IVendorAutomation Vendor => NoOpAutomationSurface.Instance;

    /// <summary>The character's own on/off options, as on the character options page.</summary>
    ICharacterOptionsAutomation CharacterOptions => NoOpAutomationSurface.Instance;
}

/// <summary>
/// The automation surface a host hands out when there is no session to act
/// on. It implements every area, reports itself unavailable, returns empty
/// lists, and refuses every command instead of throwing, so a plugin can
/// call into it safely before login.
/// </summary>
public sealed class NoOpAutomationSurface
    : IAutomationSurface, ICharacterInfo, ISpellCatalog, IMagicCommands,
      IPluginChat, ICombatAutomation
      , IEquipmentAutomation, IItemAutomation, ILootAutomation,
      IFellowshipAutomation, IAllegianceAutomation, IEnchantmentAutomation, INavigationAutomation
      , IWorldObjectAutomation, IRecallAutomation, IWorldTimeAutomation, ILoginAutomation,
      INetworkAutomation, IRecoveryAutomation, IProjectileAutomation
      , IWorldLabelAutomation, IDungeonMapAutomation
      , ISelectionAutomation, IDialogAutomation, ITradeAutomation,
      IVendorAutomation, ICharacterOptionsAutomation
{
    /// <summary>The shared instance; this type holds no state.</summary>
    public static NoOpAutomationSurface Instance { get; } = new();

    private NoOpAutomationSurface()
    {
    }

    /// <inheritdoc/>
    public bool IsAvailable => false;

    /// <inheritdoc/>
    public ICharacterInfo Character => this;

    /// <inheritdoc/>
    public ISpellCatalog Spells => this;

    /// <inheritdoc/>
    public IMagicCommands Magic => this;

    /// <inheritdoc/>
    public IPluginChat Chat => this;

    /// <inheritdoc/>
    public IDialogAutomation Dialogs => this;

    /// <inheritdoc/>
    public ICombatAutomation Combat => this;

    /// <inheritdoc/>
    public IEquipmentAutomation Equipment => this;

    /// <inheritdoc/>
    public IItemAutomation Items => this;

    /// <inheritdoc/>
    public ILootAutomation Loot => this;

    /// <inheritdoc/>
    public IFellowshipAutomation Fellowship => this;

    /// <inheritdoc/>
    public IAllegianceAutomation Allegiance => this;

    /// <inheritdoc/>
    public IEnchantmentAutomation Enchantments => this;

    /// <inheritdoc/>
    public INavigationAutomation Navigation => this;

    /// <inheritdoc/>
    public IWorldObjectAutomation Objects => this;

    /// <inheritdoc/>
    public IRecallAutomation Recalls => this;

    /// <inheritdoc/>
    public IWorldTimeAutomation WorldTime => this;

    /// <inheritdoc/>
    public ILoginAutomation Login => this;

    /// <inheritdoc/>
    public INetworkAutomation Network => this;

    /// <inheritdoc/>
    public IRecoveryAutomation Recovery => this;

    /// <inheritdoc/>
    public IProjectileAutomation Projectiles => this;

    /// <inheritdoc/>
    public IWorldLabelAutomation Labels => this;

    /// <inheritdoc/>
    public IDungeonMapAutomation DungeonMap => this;

    /// <inheritdoc/>
    public ISelectionAutomation Selection => this;

    /// <inheritdoc/>
    public ITradeAutomation Trade => this;

    /// <inheritdoc/>
    public IVendorAutomation Vendor => this;

    /// <inheritdoc/>
    public ICharacterOptionsAutomation CharacterOptions => this;

    /// <summary>Discards the text; there is nowhere to print it.</summary>
    public void PostSystemMessage(string text)
    {
    }

    /// <inheritdoc/>
    public bool Submit(string text) => false;

    PluginNavigationSnapshot INavigationAutomation.Snapshot => default;

    /// <inheritdoc/>
    public bool TryGetObject(
        uint objectId,
        out PluginNavigationObject value)
    {
        value = default;
        return false;
    }

    /// <inheritdoc/>
    public PluginNavigationCommandStatus SetMovementIntent(
        in PluginMovementIntent intent) =>
        PluginNavigationCommandStatus.Unavailable;

    /// <inheritdoc/>
    public PluginNavigationCommandStatus ClearMovementIntent() =>
        PluginNavigationCommandStatus.Unavailable;

    /// <inheritdoc/>
    public PluginNavigationCommandStatus FaceHeading(float headingDegrees) =>
        PluginNavigationCommandStatus.Unavailable;

    /// <inheritdoc/>
    public bool IsInWorld => false;

    /// <inheritdoc/>
    public uint ObjectId => 0;

    /// <inheritdoc/>
    public uint CurrentHealth => 0;

    /// <inheritdoc/>
    public uint MaxHealth => 0;

    /// <inheritdoc/>
    public uint CurrentStamina => 0;

    /// <inheritdoc/>
    public uint MaxStamina => 0;

    /// <inheritdoc/>
    public uint CurrentMana => 0;

    /// <inheritdoc/>
    public uint MaxMana => 0;

    /// <inheritdoc/>
    public int SummoningMastery => 0;

    /// <inheritdoc/>
    public IReadOnlyList<PluginSkillInfo> Skills { get; } = Array.Empty<PluginSkillInfo>();

    /// <inheritdoc/>
    public IReadOnlyList<PluginAttributeInfo> Attributes { get; } =
        Array.Empty<PluginAttributeInfo>();

    /// <inheritdoc/>
    public IReadOnlyList<PluginActiveEnchantment> ActiveEnchantments { get; } =
        Array.Empty<PluginActiveEnchantment>();

    /// <inheritdoc/>
    public IReadOnlyList<PluginSpellInfo> KnownSelfBuffs { get; } =
        Array.Empty<PluginSpellInfo>();

    /// <inheritdoc/>
    public IReadOnlyList<PluginSpellInfo> KnownAttackSpells { get; } =
        Array.Empty<PluginSpellInfo>();

    /// <inheritdoc/>
    public IReadOnlyList<PluginSpellInfo> KnownCombatSpells { get; } =
        Array.Empty<PluginSpellInfo>();

    /// <inheritdoc/>
    public bool TryGetSkill(uint skillId, out PluginSkillInfo skill)
    {
        skill = default;
        return false;
    }

    /// <inheritdoc/>
    public bool TryGet(uint spellId, out PluginSpellInfo info)
    {
        info = default;
        return false;
    }

    /// <inheritdoc/>
    public bool IsCasting => false;

    /// <inheritdoc/>
    public PluginCastCompletion LastCompletion => default;

    /// <inheritdoc/>
    public PluginCastGate EvaluateGate(uint spellId) => PluginCastGate.Unavailable;

    /// <inheritdoc/>
    public bool Cast(uint spellId) => false;

    /// <inheritdoc/>
    public PluginCastGate EvaluateGate(uint spellId, uint targetObjectId) =>
        PluginCastGate.Unavailable;

    /// <inheritdoc/>
    public bool Cast(uint spellId, uint targetObjectId) => false;

    /// <inheritdoc/>
    public PluginCombatSnapshot Snapshot => default;

    /// <inheritdoc/>
    public IReadOnlyList<PluginCombatTarget> CaptureHostileTargets(
        float maximumDistance) => Array.Empty<PluginCombatTarget>();

    /// <inheritdoc/>
    public PluginCombatCommandResult EnterDefaultMode() => new(
        PluginCombatCommandStatus.Unavailable);
    bool IEquipmentAutomation.IsAvailable => false;
    bool IEquipmentAutomation.IsBusy => false;

    /// <inheritdoc/>
    public IReadOnlyList<PluginEquipmentItem> CaptureOwnedEquipment() =>
        Array.Empty<PluginEquipmentItem>();

    /// <inheritdoc/>
    public PluginEquipmentCommandResult Equip(
        uint objectId,
        uint requestedLocation = 0u) =>
        new(PluginEquipmentCommandStatus.Unavailable);
    bool IItemAutomation.IsAvailable => false;
    bool IItemAutomation.IsBusy => false;
    int IItemAutomation.ActiveOwnedPetCount => 0;
    PluginItemUseCompletion IItemAutomation.LastCompletion => default;
    PluginItemUseCompletion ILootAutomation.LastItemUseCompletion => default;
    PluginInventoryCompletion ILootAutomation.LastInventoryCompletion => default;
    PluginAppraisalState ILootAutomation.Appraisal => default;

    /// <inheritdoc/>
    public IReadOnlyList<PluginInventoryItem> CaptureOwnedItems() =>
        Array.Empty<PluginInventoryItem>();

    /// <inheritdoc/>
    public PluginItemCommandResult Use(uint objectId) =>
        new(PluginItemCommandStatus.Unavailable);

    /// <inheritdoc/>
    public PluginItemCommandResult Apply(uint objectId, uint targetObjectId) =>
        new(PluginItemCommandStatus.Unavailable);

    /// <inheritdoc/>
    public IReadOnlyList<PluginLootContainer> CaptureCorpses(
        float maximumDistance) => Array.Empty<PluginLootContainer>();

    /// <inheritdoc/>
    public IReadOnlyList<PluginInventoryItem> CaptureCurrentContents() =>
        Array.Empty<PluginInventoryItem>();

    /// <inheritdoc/>
    public PluginItemCommandResult Open(uint containerObjectId) =>
        new(PluginItemCommandStatus.Unavailable);
    /// <inheritdoc/>
    public PluginItemCommandResult Close(uint containerObjectId) =>
        new(PluginItemCommandStatus.Unavailable);

    /// <inheritdoc/>
    public PluginItemCommandResult Identify(uint objectId) =>
        new(PluginItemCommandStatus.Unavailable);

    /// <inheritdoc/>
    public PluginItemCommandResult Pickup(uint objectId, bool mainPack = false) =>
        new(PluginItemCommandStatus.Unavailable);

    /// <inheritdoc/>
    public bool IsInFellowship => false;

    /// <inheritdoc/>
    public IReadOnlyList<PluginFellowMember> CaptureMembers() =>
        Array.Empty<PluginFellowMember>();

    /// <inheritdoc/>
    public IReadOnlyList<PluginTrackedEnchantment> Capture(
        uint targetObjectId) => Array.Empty<PluginTrackedEnchantment>();

    /// <inheritdoc/>
    public bool ReportCast(
        uint targetObjectId,
        uint spellId,
        double durationSeconds) => false;
    PluginWorldTimeSnapshot IWorldTimeAutomation.Snapshot => default;

    /// <inheritdoc/>
    public PluginCombatCommandResult BeginPhysicalAttack(
        uint targetObjectId, PluginAttackHeight height, float power) => new(
            PluginCombatCommandStatus.Unavailable);

    /// <inheritdoc/>
    public PluginCombatCommandResult ReleasePhysicalAttack() => new(
        PluginCombatCommandStatus.Unavailable);

    /// <inheritdoc/>
    public PluginCombatCommandResult AbortPhysicalAttack() => new(
        PluginCombatCommandStatus.Unavailable);
}
