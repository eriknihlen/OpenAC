using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot;

/// <summary>
/// One consistent view of the world for one engine tick. Every behavior reads
/// the same snapshot, so arbitration never sees two behaviors disagree about
/// what the host said.
/// </summary>
public sealed record Blackboard(
    double Now,
    bool IsInWorld,
    uint SelfId,
    Vitals Vitals,
    IReadOnlyList<PluginActiveEnchantment> Enchantments,
    bool IsCasting,
    PluginCombatSnapshot Combat,
    IReadOnlyList<PluginCombatTarget> Hostiles,
    IReadOnlyList<PluginLootContainer> Corpses,
    PluginNavigationSnapshot Navigation,
    bool LootBusy,
    uint OpenContainerId)
{
    public static Blackboard Capture(
        IAutomationSurface surface,
        IBotClock clock,
        float hostileScanDistance,
        float corpseScanDistance)
    {
        ICharacterInfo character = surface.Character;
        bool inWorld = surface.IsAvailable && character.IsInWorld;
        return new Blackboard(
            clock.Now,
            inWorld,
            character.ObjectId,
            Vitals.From(character),
            inWorld ? character.ActiveEnchantments : [],
            surface.Magic.IsCasting,
            surface.Combat.Snapshot,
            inWorld ? surface.Combat.CaptureHostileTargets(hostileScanDistance) : [],
            inWorld ? surface.Loot.CaptureCorpses(corpseScanDistance) : [],
            surface.Navigation.Snapshot,
            surface.Loot.IsBusy,
            surface.Loot.CurrentContainerId);
    }

    /// <summary>Whether the host is mid-action and a new command would be refused or queued.</summary>
    public bool IsActionPending =>
        IsCasting
        || Combat.RequestInProgress
        || Combat.ServerResponsePending;
}

public readonly record struct Vitals(
    uint Health,
    uint MaxHealth,
    uint Stamina,
    uint MaxStamina,
    uint Mana,
    uint MaxMana)
{
    public double HealthFraction => Fraction(Health, MaxHealth);
    public double StaminaFraction => Fraction(Stamina, MaxStamina);
    public double ManaFraction => Fraction(Mana, MaxMana);

    public static Vitals From(ICharacterInfo character) => new(
        character.CurrentHealth,
        character.MaxHealth,
        character.CurrentStamina,
        character.MaxStamina,
        character.CurrentMana,
        character.MaxMana);

    private static double Fraction(uint current, uint max) =>
        max == 0u ? 1d : Math.Clamp((double)current / max, 0d, 1d);
}
