using AcDream.Core.Items;

namespace AcDream.Core.Physics;

[Flags]
public enum EntityCollisionFlags : byte
{
    None           = 0x00,
    /// <summary>Set when <c>BF_PLAYER (0x8)</c> is set in <c>pwd._bitfield</c>.</summary>
    IsPlayer       = 0x01,
    IsCreature     = 0x02,
    /// <summary>Set when <c>BF_PLAYER_KILLER (0x20)</c> is set.</summary>
    IsPK           = 0x04,
    IsPKLite       = 0x08,
    IsImpenetrable = 0x10,
    HasWeenie      = 0x20,
    CanBypassMoveRestrictions = 0x40,
}

public static class EntityCollisionFlagsExt
{
    public const EntityCollisionFlags PwdBitfieldDerivedMask =
        EntityCollisionFlags.IsPlayer
        | EntityCollisionFlags.IsPK
        | EntityCollisionFlags.IsPKLite
        | EntityCollisionFlags.IsImpenetrable
        | EntityCollisionFlags.CanBypassMoveRestrictions;

    public static EntityCollisionFlags FromPwdBitfield(uint bitfield)
    {
        var flags = EntityCollisionFlags.None;
        if ((bitfield & 0x8u)        != 0) flags |= EntityCollisionFlags.IsPlayer;
        if ((bitfield & 0x20u)       != 0) flags |= EntityCollisionFlags.IsPK;
        if ((bitfield & 0x200000u)   != 0) flags |= EntityCollisionFlags.IsImpenetrable;
        if ((bitfield & 0x2000000u)  != 0) flags |= EntityCollisionFlags.IsPKLite;
        if ((bitfield & 0x100000u) != 0 && (bitfield & 0x400000u) != 0)
            flags |= EntityCollisionFlags.CanBypassMoveRestrictions;
        return flags;
    }

    public static ObjectInfoState ToMoverState(this EntityCollisionFlags flags)
    {
        var state = ObjectInfoState.None;
        if ((flags & EntityCollisionFlags.IsPK) != 0) state |= ObjectInfoState.IsPK;
        if ((flags & EntityCollisionFlags.IsPKLite) != 0) state |= ObjectInfoState.IsPKLite;
        if ((flags & EntityCollisionFlags.IsImpenetrable) != 0) state |= ObjectInfoState.IsImpenetrable;
        if ((flags & EntityCollisionFlags.CanBypassMoveRestrictions) != 0)
            state |= ObjectInfoState.CanBypassMoveRestrictions;
        return state;
    }

    public static ObjectInfoState ResolveMoverPvpState(this ClientObjectTable objects, uint serverGuid)
    {
        ArgumentNullException.ThrowIfNull(objects);
        return objects.Get(serverGuid)?.PublicWeenieBitfield is { } bitfield
            ? FromPwdBitfield(bitfield).ToMoverState()
            : ObjectInfoState.None;
    }
}
