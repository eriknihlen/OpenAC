namespace AcDream.Plugin.Abstractions;

/// <summary>The stance the character is in, which decides how it can attack.</summary>
public enum PluginCombatMode
{
    /// <summary>The client has not reported a stance it recognises.</summary>
    Unknown = 0,

    /// <summary>Weapons away; no attacking.</summary>
    Peace,

    /// <summary>Ready to swing a hand weapon.</summary>
    Melee,

    /// <summary>Ready to shoot a bow, crossbow, or thrown weapon.</summary>
    Missile,

    /// <summary>Ready to cast spells.</summary>
    Magic,
}

/// <summary>Where on the target a physical attack aims.</summary>
public enum PluginAttackHeight
{
    /// <summary>Aimed high, at the target's head.</summary>
    High = 1,

    /// <summary>Aimed at the target's middle.</summary>
    Medium = 2,

    /// <summary>Aimed low, at the target's legs.</summary>
    Low = 3,
}

/// <summary>
/// One hostile creature near the player, with everything a plugin needs to
/// pick a target: where it is, how hurt it is, and how fresh that knowledge
/// is.
/// </summary>
/// <param name="ObjectId">The creature's object id.</param>
/// <param name="Name">The creature's display name.</param>
/// <param name="WeenieClassId">The creature's class id.</param>
/// <param name="Distance">
/// Straight-line distance from the player in metres, height included.
/// </param>
/// <param name="RelativeAngleDegrees">
/// How far the creature sits off the direction the player faces, in degrees
/// from -180 to 180; zero is straight ahead.
/// </param>
/// <param name="IsHealthKnown">
/// True when the server has told the client this creature's health at least
/// once. While it is false <paramref name="HealthFraction"/> reads as full.
/// </param>
/// <param name="HealthFraction">
/// The creature's health as a share of its maximum, from 0 to 1.
/// </param>
public readonly record struct PluginCombatTarget(
    uint ObjectId,
    string Name,
    uint WeenieClassId,
    float Distance,
    float RelativeAngleDegrees,
    bool IsHealthKnown,
    float HealthFraction)
{
    /// <summary>The creature's species, or zero when the client does not know it.</summary>
    public int SpeciesId { get; init; }

    /// <summary>
    /// The species' display name, or an empty string when the host cannot
    /// resolve one.
    /// </summary>
    public string SpeciesName { get; init; } = string.Empty;

    /// <summary>Spawn/appraisal maximum HP, or zero until the host knows it.</summary>
    public int MaximumHealth { get; init; }

    /// <summary>True when the creature has something the client counts as armor equipped.</summary>
    public bool HasShield { get; init; }

    /// <summary>
    /// The client's re-use counter for this object id, which tells a fresh
    /// creature apart from an earlier one that carried the same id.
    /// </summary>
    public ushort Incarnation { get; init; }

    /// <summary>Monotonic revision of the last server health update.</summary>
    public long HealthRevision { get; init; }

    /// <summary>
    /// How long ago the last health update for this creature arrived, in
    /// seconds; positive infinity when none ever has.
    /// </summary>
    public double SecondsSinceHealthUpdate { get; init; } =
        double.PositiveInfinity;
}

/// <summary>
/// A single reading of the client's combat state: what is targeted, what
/// stance the character is in, and how far along the current attack is.
/// </summary>
/// <param name="SelectedObjectId">
/// The currently selected object, or zero when nothing is selected.
/// </param>
/// <param name="Mode">The character's current stance.</param>
/// <param name="AttackHeight">The height the current or last attack asked for.</param>
/// <param name="DesiredPower">
/// The power setting an attack was asked for, from 0 to 1.
/// </param>
/// <param name="PowerBarLevel">
/// How far the power bar has charged so far, from 0 to 1.
/// </param>
/// <param name="BuildInProgress">True while the power bar is still charging.</param>
/// <param name="RequestInProgress">
/// True while an attack has been begun and not yet released or aborted.
/// </param>
/// <param name="ServerResponsePending">
/// True while the client is waiting for the server's answer to an attack.
/// </param>
/// <param name="RepeatAttackInProgress">
/// True while the client is repeating attacks on its own.
/// </param>
public readonly record struct PluginCombatSnapshot(
    uint SelectedObjectId,
    PluginCombatMode Mode,
    PluginAttackHeight AttackHeight,
    float DesiredPower,
    float PowerBarLevel,
    bool BuildInProgress,
    bool RequestInProgress,
    bool ServerResponsePending,
    bool RepeatAttackInProgress)
{
    /// <summary>Revision of the last physical AttackDone receipt.</summary>
    public long CompletionRevision { get; init; }

    /// <summary>The attack sequence number that receipt answered.</summary>
    public uint CompletionSequence { get; init; }

    /// <summary>
    /// The error code that receipt carried; zero means the attack was
    /// accepted.
    /// </summary>
    public uint CompletionWeenieError { get; init; }
}

/// <summary>How the client answered a plugin's combat command.</summary>
public enum PluginCombatCommandStatus
{
    /// <summary>
    /// The command could not be run: there is no in-world session, or this
    /// host does not provide the surface.
    /// </summary>
    Unavailable = 0,

    /// <summary>The target is not something the player may attack, or is unknown.</summary>
    InvalidTarget,

    /// <summary>The character's current stance cannot make this attack.</summary>
    WrongMode,

    /// <summary>An attack is already under way.</summary>
    Busy,

    /// <summary>Nothing needed doing -- the character is already in the asked-for stance.</summary>
    AlreadyReady,

    /// <summary>A stance change was sent to the server.</summary>
    ModeChangeSent,

    /// <summary>An attack was begun.</summary>
    Started,

    /// <summary>A held attack was released, sending it to the server.</summary>
    Released,

    /// <summary>An attack was called off, or a client-side target was dismissed.</summary>
    Stopped,

    /// <summary>The client declined the command.</summary>
    Refused,
}

/// <summary>The outcome of one combat command, with an optional explanation.</summary>
/// <param name="Status">What the client did with the command.</param>
/// <param name="Notice">A short human-readable reason, when there is one.</param>
public readonly record struct PluginCombatCommandResult(
    PluginCombatCommandStatus Status,
    string? Notice = null)
{
    /// <summary>
    /// True when the command did what it was asked to, counting the cases
    /// where there was nothing left to do.
    /// </summary>
    public bool Accepted => Status is
        PluginCombatCommandStatus.AlreadyReady
        or PluginCombatCommandStatus.ModeChangeSent
        or PluginCombatCommandStatus.Started
        or PluginCombatCommandStatus.Released
        or PluginCombatCommandStatus.Stopped;
}

/// <summary>
/// Finds things to fight and drives physical attacks. A physical attack is
/// two steps, as it is for the user: begin it, which starts the power bar
/// charging, and release it, which actually swings.
/// </summary>
public interface ICombatAutomation
{
    /// <summary>
    /// The current combat state. Reads as its default value when the session
    /// is not in the world.
    /// </summary>
    PluginCombatSnapshot Snapshot { get; }

    /// <summary>
    /// Lists hostile creatures within <paramref name="maximumDistance"/>
    /// metres that the player could attack, leaving out anything hidden, not
    /// drawn, or already dead. Returns an empty list when the session is not
    /// in the world or the distance is not a positive number.
    /// </summary>
    IReadOnlyList<PluginCombatTarget> CaptureHostileTargets(float maximumDistance);

    /// <summary>
    /// Puts the character into its default combat stance. Reports
    /// <see cref="PluginCombatCommandStatus.AlreadyReady"/> when it is
    /// already in a combat stance of any kind.
    /// </summary>
    PluginCombatCommandResult EnterDefaultMode();

    /// <summary>
    /// Asks the server for a specific stance. Reports
    /// <see cref="PluginCombatCommandStatus.AlreadyReady"/> when the
    /// character is already in it, and
    /// <see cref="PluginCombatCommandStatus.Refused"/> for a stance value the
    /// client does not recognise.
    /// </summary>
    PluginCombatCommandResult EnterMode(PluginCombatMode mode) =>
        new(PluginCombatCommandStatus.Unavailable);

    /// <summary>
    /// Selects the target and begins a physical attack at the given height
    /// and power (0 to 1, clamped), the same as holding the attack button
    /// down. Reports <see cref="PluginCombatCommandStatus.InvalidTarget"/> for
    /// anything that is not an attackable creature,
    /// <see cref="PluginCombatCommandStatus.WrongMode"/> when the stance
    /// cannot make a targeted attack, and
    /// <see cref="PluginCombatCommandStatus.Busy"/> while an earlier attack
    /// is still running. Finish it with <see cref="ReleasePhysicalAttack"/>.
    /// </summary>
    PluginCombatCommandResult BeginPhysicalAttack(
        uint targetObjectId,
        PluginAttackHeight height,
        float power);

    /// <summary>
    /// Releases an attack begun with <see cref="BeginPhysicalAttack"/>,
    /// sending the swing. Reports
    /// <see cref="PluginCombatCommandStatus.Refused"/> when no attack is
    /// being held.
    /// </summary>
    PluginCombatCommandResult ReleasePhysicalAttack();

    /// <summary>
    /// Calls off the attack in progress, including any repeating one, without
    /// swinging. Safe to call when nothing is running.
    /// </summary>
    PluginCombatCommandResult AbortPhysicalAttack();

    /// <summary>
    /// Removes a creature the client is still drawing but the server no
    /// longer has, clearing it from the client's own world only -- nothing is
    /// sent to the server. Reports
    /// <see cref="PluginCombatCommandStatus.InvalidTarget"/> for an unknown
    /// id or for the local player.
    /// </summary>
    PluginCombatCommandResult DismissGhostTarget(uint targetObjectId) =>
        new(PluginCombatCommandStatus.Unavailable);
}
