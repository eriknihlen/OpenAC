using System;
using System.Collections.Generic;
using AcDream.Core.Items;
using AcDream.Content;
using DatReaderWriter;

namespace AcDream.App.UI.Layout;

public static class PaperdollSlotBackgrounds
{
    internal readonly record struct Definition(
        uint Element,
        EquipMask Mask,
        uint EmptyPrototype,
        AetheriaUnlockState UnlockBit = AetheriaUnlockState.None);

    private const EquipMask WeaponSlotMask =
        EquipMask.MeleeWeapon | EquipMask.MissileWeapon | EquipMask.Held | EquipMask.TwoHanded;

    internal static readonly Definition[] Definitions =
    {
        new(0x100005ABu, EquipMask.HeadWear,        0x100005B4u),
        new(0x100001E2u, EquipMask.ChestWear,       0x1000044Eu),
        new(0x100001E3u, EquipMask.UpperLegWear,    0x1000044Fu),
        new(0x100005B0u, EquipMask.HandWear,        0x100005B9u),
        new(0x100005B3u, EquipMask.FootWear,        0x100005BDu),
        new(0x100005ACu, EquipMask.ChestArmor,      0x100005B5u),
        new(0x100005ADu, EquipMask.AbdomenArmor,    0x100005B6u),
        new(0x100005AEu, EquipMask.UpperArmArmor,   0x100005B7u),
        new(0x100005AFu, EquipMask.LowerArmArmor,   0x100005B8u),
        new(0x100005B1u, EquipMask.UpperLegArmor,   0x100005BAu),
        new(0x100005B2u, EquipMask.LowerLegArmor,   0x100005BBu),
        new(0x100001DAu, EquipMask.NeckWear,        0x10000446u),
        new(0x100001DBu, EquipMask.WristWearLeft,   0x10000447u),
        new(0x100001DDu, EquipMask.WristWearRight,  0x10000449u),
        new(0x100001DCu, EquipMask.FingerWearLeft,  0x10000448u),
        new(0x100001DEu, EquipMask.FingerWearRight, 0x1000044Au),
        new(0x100001E1u, EquipMask.Shield,          0x1000044Du),
        new(0x100001E0u, EquipMask.MissileAmmo,     0x1000044Cu),
        new(0x100001DFu, WeaponSlotMask,            0x1000044Bu),
        new(0x1000058Eu, EquipMask.TrinketOne,      0x1000058Fu),
        new(0x100005E9u, EquipMask.Cloak,           0x100005EAu),
        new(0x10000595u, EquipMask.SigilOne,         0x10000592u, AetheriaUnlockState.Blue),
        new(0x10000596u, EquipMask.SigilTwo,         0x10000593u, AetheriaUnlockState.Yellow),
        new(0x10000597u, EquipMask.SigilThree,       0x10000594u, AetheriaUnlockState.Red),
    };

    /// <summary>Resolves every supported slot's exact empty RenderSurface from the live DAT.</summary>
    public static IReadOnlyDictionary<uint, uint> ResolveEmptySprites(IDatReaderWriter dats)
    {
        var result = new Dictionary<uint, uint>(Definitions.Length);
        foreach (Definition definition in Definitions)
        {
            uint sprite = ItemListCellTemplate.ResolvePrototypeEmptySprite(
                dats,
                definition.EmptyPrototype);
            if (sprite == 0)
            {
                throw new InvalidOperationException(
                    $"Retail paperdoll UIItem prototype 0x{definition.EmptyPrototype:X8} " +
                    $"for slot 0x{definition.Element:X8} has no ItemSlot_Empty surface.");
            }
            result.Add(definition.Element, sprite);
        }
        return result;
    }

    public static bool TryGetPrototype(uint slotElementId, out uint prototypeElementId)
    {
        foreach (Definition definition in Definitions)
        {
            if (definition.Element != slotElementId) continue;
            prototypeElementId = definition.EmptyPrototype;
            return true;
        }
        prototypeElementId = 0;
        return false;
    }
}
