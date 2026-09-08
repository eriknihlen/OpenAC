using AcDream.Core.Items;

namespace AcDream.Core.Spells;

public readonly record struct SpellTargetPolicyResult(
    bool Allowed,
    string? Message)
{
    public static SpellTargetPolicyResult Accept { get; } =
        new(true, null);
}

public static class RetailSpellTargetPolicy
{
    private const uint SpecialTargetMask = 0x00008107u;

    public static SpellTargetPolicyResult Evaluate(
        uint localPlayerId,
        ClientObject target,
        SpellMetadata spell)
    {
        uint mask = spell.TargetMask;
        uint special = mask & SpecialTargetMask;
        if (target.ObjectId == localPlayerId && special == 0u)
            return new(false, "You cannot cast this spell upon yourself.");
        if (target.StackSize > 1)
            return new(false, "Cannot cast spell on a stack of items.");

        if (((uint)target.Type & mask) == 0u && special == 0u)
            return new(false, $"This spell cannot be cast on {target.Name}.");

        var flags = (PublicWeenieFlags)
            target.PublicWeenieBitfield.GetValueOrDefault();
        bool playerOrAttackable = (flags
            & (PublicWeenieFlags.Player
                | PublicWeenieFlags.Attackable)) != 0;
        if (!playerOrAttackable || target.PetOwnerId != 0u)
            return new(false, $"This spell cannot be cast on {target.Name}.");

        return SpellTargetPolicyResult.Accept;
    }
}
