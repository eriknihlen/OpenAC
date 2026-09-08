using System.Buffers.Binary;
using System.Collections.Generic;
using AcDream.Core.Items;

namespace AcDream.Core.Net.Messages;

public readonly record struct PublicWeenieDescBody(
    string? Name = null,
    uint? ItemType = null,
    uint? ObjectDescriptionFlags = null,
    uint IconId = 0,
    uint WeenieClassId = 0,
    int? Value = null,
    int? StackSize = null,
    int? StackSizeMax = null,
    int? Burden = null,
    int? ItemsCapacity = null,
    int? ContainersCapacity = null,
    uint? HookItemTypes = null,
    uint? HookType = null,
    uint? ContainerId = null,
    uint? WielderId = null,
    uint? ValidLocations = null,
    uint? CurrentWieldedLocation = null,
    uint? Priority = null,
    int? Structure = null,
    int? MaxStructure = null,
    float? Workmanship = null,
    uint? Useability = null,
    float? UseRadius = null,
    uint? TargetType = null,
    uint IconOverlayId = 0,
    uint IconUnderlayId = 0,
    uint UiEffects = 0,
    byte? RadarBlipColor = null,
    byte? RadarBehavior = null,
    byte? CombatUse = null,
    string? PluralName = null,
    uint? PetOwnerId = null,
    ushort? AmmoType = null,
    uint? SpellId = null,
    uint? CooldownId = null,
    double? CooldownDuration = null,
    uint? MaterialType = null,
    uint? HouseOwnerId = null,
    uint? MonarchId = null,
    HouseRestrictionRecord? Restrictions = null);

public static class PublicWeenieDescParser
{
    public static PublicWeenieDescBody Parse(ReadOnlySpan<byte> body, ref int pos)
    {
        string? name = null;
        uint? itemType = null;
        uint weenieFlags = 0;
        string? pluralName = null;
        uint iconId = 0;
        uint weenieClassId = 0;
        int? wValue = null;
        int? wStackSize = null;
        int? wMaxStackSize = null;
        int? wBurden = null;
        int? wItemsCapacity = null;
        int? wContainersCapacity = null;
        uint? wHookItemTypes = null;
        uint? wHookType = null;
        uint? wContainerId = null;
        uint? wWielderId = null;
        uint? wValidLocations = null;
        uint? wCurrentWieldedLocation = null;
        uint? wPriority = null;
        int? wStructure = null;
        int? wMaxStructure = null;
        float? wWorkmanship = null;
        uint? objectDescriptionFlags = null;
        if (body.Length - pos >= 4)
        {
            weenieFlags = CreateObject.ReadU32(body, ref pos);
            try
            {
                name = CreateObject.ReadString16L(body, ref pos);
                weenieClassId = CreateObject.ReadPackedDword(body, ref pos);  // WeenieClassId (D.5.4: was discarded)
                iconId = CreateObject.ReadPackedDwordOfKnownType(body, ref pos, CreateObject.IconTypePrefix);
                if (body.Length - pos >= 4)
                    itemType = CreateObject.ReadU32(body, ref pos);
                if (body.Length - pos >= 4)
                {
                    objectDescriptionFlags = CreateObject.ReadU32(body, ref pos);
                }
                CreateObject.AlignTo4(ref pos);
            }
            catch { /* truncated name — partial result is still useful */ }
        }

        uint? useability = null;
        float? useRadius = null;
        uint? targetType = null;
        byte? radarBlipColor = null;
        byte? radarBehavior = null;
        byte? combatUse = null;
        ushort? ammoType = null;
        uint? spellId = null;
        uint iconOverlayId = 0;
        uint iconUnderlayId = 0;
        uint uiEffects = 0;
        uint weenieFlags2 = 0;
        uint? petOwnerId = null;
        uint? cooldownId = null;
        double? cooldownDuration = null;
        uint? materialType = null;
        uint? houseOwnerId = null;
        uint? monarchId = null;
        HouseRestrictionRecord? restrictions = null;
        try
        {
            bool hasSecondHeader = objectDescriptionFlags.HasValue
                && (objectDescriptionFlags.Value & 0x04000000u) != 0;
            if (hasSecondHeader)
            {
                if (body.Length - pos < 4) throw new FormatException("trunc weenieFlags2");
                weenieFlags2 = CreateObject.ReadU32(body, ref pos);
            }

            if ((weenieFlags & 0x00000001u) != 0)         // PluralName
                pluralName = CreateObject.ReadString16L(body, ref pos);

            if ((weenieFlags & 0x00000002u) != 0)         // ItemsCapacity s8 -> int
            {
                if (body.Length - pos < 1) throw new FormatException("trunc ItemCap");
                wItemsCapacity = unchecked((sbyte)body[pos]);
                pos += 1;
            }
            if ((weenieFlags & 0x00000004u) != 0)
            {
                if (body.Length - pos < 1) throw new FormatException("trunc ContCap");
                wContainersCapacity = unchecked((sbyte)body[pos]);
                pos += 1;
            }
            if ((weenieFlags & 0x00000100u) != 0)         // AmmoType u16
            {
                if (body.Length - pos < 2) throw new FormatException("trunc AmmoType");
                ammoType = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos));
                pos += 2;
            }
            if ((weenieFlags & 0x00000008u) != 0)         // Value u32
            {
                if (body.Length - pos < 4) throw new FormatException("trunc Value");
                wValue = (int)CreateObject.ReadU32(body, ref pos);
            }
            if ((weenieFlags & 0x00000010u) != 0)         // Usable u32  ← KEEP
            {
                if (body.Length - pos < 4) throw new FormatException("trunc Useability");
                useability = CreateObject.ReadU32(body, ref pos);
            }
            if ((weenieFlags & 0x00000020u) != 0)         // UseRadius f32  ← KEEP
            {
                if (body.Length - pos < 4) throw new FormatException("trunc UseRadius");
                useRadius = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos));
                pos += 4;
            }


            if ((weenieFlags & 0x00080000u) != 0)         // TargetType u32
            {
                if (body.Length - pos < 4) throw new FormatException("trunc TargetType");
                targetType = CreateObject.ReadU32(body, ref pos);
            }
            if ((weenieFlags & 0x00000080u) != 0)
            {
                if (body.Length - pos < 4) throw new FormatException("trunc UiEffects");
                uiEffects = CreateObject.ReadU32(body, ref pos);
            }
            if ((weenieFlags & 0x00000200u) != 0)
            {
                if (body.Length - pos < 1) throw new FormatException("trunc CombatUse");
                combatUse = body[pos]; pos += 1;
            }
            if ((weenieFlags & 0x00000400u) != 0)         // Structure u16
            {
                if (body.Length - pos < 2) throw new FormatException("trunc Structure");
                wStructure = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos)); pos += 2;
            }
            if ((weenieFlags & 0x00000800u) != 0)         // MaxStructure u16
            {
                if (body.Length - pos < 2) throw new FormatException("trunc MaxStructure");
                wMaxStructure = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos)); pos += 2;
            }
            if ((weenieFlags & 0x00001000u) != 0)         // StackSize u16
            {
                if (body.Length - pos < 2) throw new FormatException("trunc StackSize");
                wStackSize = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos)); pos += 2;
            }
            if ((weenieFlags & 0x00002000u) != 0)         // MaxStackSize u16
            {
                if (body.Length - pos < 2) throw new FormatException("trunc MaxStackSize");
                wMaxStackSize = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos)); pos += 2;
            }
            if ((weenieFlags & 0x00004000u) != 0)
            {
                if (body.Length - pos < 4) throw new FormatException("trunc Container");
                wContainerId = CreateObject.ReadU32(body, ref pos);
            }
            if ((weenieFlags & 0x00008000u) != 0)         // Wielder u32
            {
                if (body.Length - pos < 4) throw new FormatException("trunc Wielder");
                wWielderId = CreateObject.ReadU32(body, ref pos);
            }
            if ((weenieFlags & 0x00010000u) != 0)         // ValidLocations u32
            {
                if (body.Length - pos < 4) throw new FormatException("trunc ValidLocations");
                wValidLocations = CreateObject.ReadU32(body, ref pos);
            }
            if ((weenieFlags & 0x00020000u) != 0)
            {
                if (body.Length - pos < 4) throw new FormatException("trunc CurrentlyWieldedLocation");
                wCurrentWieldedLocation = CreateObject.ReadU32(body, ref pos);
            }
            if ((weenieFlags & 0x00040000u) != 0)         // Priority u32
            {
                if (body.Length - pos < 4) throw new FormatException("trunc Priority");
                wPriority = CreateObject.ReadU32(body, ref pos);
            }
            if ((weenieFlags & 0x00100000u) != 0)         // RadarBlipColor u8
            {
                if (body.Length - pos < 1) throw new FormatException("trunc RadarBlipColor");
                radarBlipColor = body[pos]; pos += 1;
            }
            if ((weenieFlags & 0x00800000u) != 0)         // RadarBehavior u8
            {
                if (body.Length - pos < 1) throw new FormatException("trunc RadarBehavior");
                radarBehavior = body[pos]; pos += 1;
            }
            if ((weenieFlags & 0x08000000u) != 0)         // PScript u16
            {
                if (body.Length - pos < 2) throw new FormatException("trunc PScript");
                pos += 2;
            }
            if ((weenieFlags & 0x01000000u) != 0)         // Workmanship f32
            {
                if (body.Length - pos < 4) throw new FormatException("trunc Workmanship");
                wWorkmanship = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos)); pos += 4;
            }
            if ((weenieFlags & 0x00200000u) != 0)         // Burden u16
            {
                if (body.Length - pos < 2) throw new FormatException("trunc Burden");
                wBurden = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos)); pos += 2;
            }
            if ((weenieFlags & 0x00400000u) != 0)         // Spell u16
            {
                if (body.Length - pos < 2) throw new FormatException("trunc Spell");
                spellId = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos));
                pos += 2;
            }
            if ((weenieFlags & 0x02000000u) != 0)
            {
                if (body.Length - pos < 4) throw new FormatException("trunc HouseOwner");
                houseOwnerId = CreateObject.ReadU32(body, ref pos);
            }
            if ((weenieFlags & 0x04000000u) != 0)
            {
                if (body.Length - pos < 12) throw new FormatException("trunc RestrictionDB header");
                pos += 4;
                uint flags = CreateObject.ReadU32(body, ref pos);                // 0 = private, 1 = open
                uint restrictionMonarchId = CreateObject.ReadU32(body, ref pos);
                if (body.Length - pos < 4) throw new FormatException("trunc RestrictionDB PHashTable header");
                uint packedSize = CreateObject.ReadU32(body, ref pos);
                uint entryCount = packedSize & 0xFFFFFFu;
                long entryBytes = (long)entryCount * 8;             // each entry: u32 guid + u32 value
                if (body.Length - pos < entryBytes) throw new FormatException("trunc RestrictionDB entries");
                var guests = new Dictionary<uint, uint>((int)entryCount);
                for (uint i = 0; i < entryCount; i++)
                {
                    uint guestId = CreateObject.ReadU32(body, ref pos);
                    uint permission = CreateObject.ReadU32(body, ref pos);
                    guests[guestId] = permission;
                }
                restrictions = new HouseRestrictionRecord(
                    OpenToPublic: flags != 0,
                    AllegianceMonarchId: restrictionMonarchId,
                    Guests: guests);
            }
            if ((weenieFlags & 0x20000000u) != 0)         // HookItemTypes u32
            {
                if (body.Length - pos < 4) throw new FormatException("trunc HookItemTypes");
                wHookItemTypes = CreateObject.ReadU32(body, ref pos);
            }
            if ((weenieFlags & 0x00000040u) != 0)
            {
                if (body.Length - pos < 4) throw new FormatException("trunc Monarch");
                monarchId = CreateObject.ReadU32(body, ref pos);
            }
            if ((weenieFlags & 0x10000000u) != 0)         // HookType u16
            {
                if (body.Length - pos < 2) throw new FormatException("trunc HookType");
                wHookType = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos));
                pos += 2;
            }
            if ((weenieFlags & 0x40000000u) != 0)
            {
                iconOverlayId = CreateObject.ReadPackedDwordOfKnownType(body, ref pos, CreateObject.IconTypePrefix);
            }
            if ((weenieFlags2 & 0x00000001u) != 0)
            {
                iconUnderlayId = CreateObject.ReadPackedDwordOfKnownType(body, ref pos, CreateObject.IconTypePrefix);
            }
            if ((weenieFlags & 0x80000000u) != 0)         // MaterialType u32
            {
                if (body.Length - pos < 4) throw new FormatException("trunc MaterialType");
                materialType = CreateObject.ReadU32(body, ref pos);
            }
            if ((weenieFlags2 & 0x00000002u) != 0)
            {
                if (body.Length - pos < 4) throw new FormatException("trunc CooldownId");
                cooldownId = CreateObject.ReadU32(body, ref pos);
            }
            if ((weenieFlags2 & 0x00000004u) != 0)
            {
                if (body.Length - pos < 8) throw new FormatException("trunc CooldownDuration");
                cooldownDuration = BinaryPrimitives.ReadDoubleLittleEndian(body.Slice(pos));
                pos += 8;
            }
            if ((weenieFlags2 & 0x00000008u) != 0)        // PetOwner u32
            {
                if (body.Length - pos < 4) throw new FormatException("trunc PetOwner");
                petOwnerId = CreateObject.ReadU32(body, ref pos);
            }
        }
        catch { /* truncated weenie tail — keep whatever we got. */ }

        return new PublicWeenieDescBody(
            Name: name,
            ItemType: itemType,
            ObjectDescriptionFlags: objectDescriptionFlags,
            IconId: iconId,
            WeenieClassId: weenieClassId,
            Value: wValue,
            StackSize: wStackSize,
            StackSizeMax: wMaxStackSize,
            Burden: wBurden,
            ItemsCapacity: wItemsCapacity,
            ContainersCapacity: wContainersCapacity,
            HookItemTypes: wHookItemTypes,
            HookType: wHookType,
            ContainerId: wContainerId,
            WielderId: wWielderId,
            ValidLocations: wValidLocations,
            CurrentWieldedLocation: wCurrentWieldedLocation,
            Priority: wPriority,
            Structure: wStructure,
            MaxStructure: wMaxStructure,
            Workmanship: wWorkmanship,
            Useability: useability,
            UseRadius: useRadius,
            TargetType: targetType,
            IconOverlayId: iconOverlayId,
            IconUnderlayId: iconUnderlayId,
            UiEffects: uiEffects,
            RadarBlipColor: radarBlipColor,
            RadarBehavior: radarBehavior,
            CombatUse: combatUse,
            PluralName: pluralName,
            PetOwnerId: petOwnerId,
            AmmoType: ammoType,
            SpellId: spellId,
            CooldownId: cooldownId,
            CooldownDuration: cooldownDuration,
            MaterialType: materialType,
            HouseOwnerId: houseOwnerId,
            MonarchId: monarchId,
            Restrictions: restrictions);
    }
}
