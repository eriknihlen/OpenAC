using System.Buffers.Binary;
using System.Text;
using AcDream.Core.Items;
using AcDream.Core.Net.Messages;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class CreateObjectTests
{
    [Fact]
    public void TryParse_WeenieHeaderPrefix_ReturnsNameAndItemType()
    {
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x50000002u,
            name: "Drudge",
            itemType: (uint)ItemType.Creature);

        var parsed = CreateObject.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal(0x50000002u, parsed.Value.Guid);
        Assert.Equal("Drudge", parsed.Value.Name);
        Assert.Equal((uint)ItemType.Creature, parsed.Value.ItemType);
    }


    [Fact]
    public void TryParse_PhysicsState_Parsed()
    {
        // ETHEREAL_PS = 0x4 + IGNORE_COLLISIONS_PS = 0x10 → 0x14
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x50000003u, name: "GhostNpc",
            itemType: (uint)ItemType.Creature,
            physicsState: 0x14u);

        var parsed = CreateObject.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal(0x14u, parsed!.Value.PhysicsState);
    }

    [Fact]
    public void TryParse_ObjectDescriptionFlags_PlayerKillerBitsSurface()
    {
        // BF_PLAYER (0x8) | BF_PLAYER_KILLER (0x20) → a PK player.
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x50000004u, name: "+PkPlayer",
            itemType: (uint)ItemType.Creature,
            objectDescriptionFlags: 0x8u | 0x20u);

        var parsed = CreateObject.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal(0x28u, parsed!.Value.ObjectDescriptionFlags);
    }

    [Fact]
    public void TryParse_ObjectDescriptionFlags_PkLiteBit()
    {
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x50000005u, name: "+PklPlayer",
            itemType: (uint)ItemType.Creature,
            objectDescriptionFlags: 0x8u | 0x2000000u);

        var parsed = CreateObject.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal(0x2000008u, parsed!.Value.ObjectDescriptionFlags);
    }


    [Fact]
    public void TryParse_NoWeenieFlags_LeavesUseabilityNull()
    {
        // Sign-like entity: weenieFlags=0 (no optional fields).
        // Useability stays null (parser walked past nothing).
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x50000006u, name: "Holtburg Sign",
            itemType: 0x8000u);

        var parsed = CreateObject.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Null(parsed!.Value.Useability);
        Assert.Null(parsed.Value.UseRadius);
    }

    [Fact]
    public void TryParse_WeenieFlagsUsable_ReadsUseability()
    {
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x50000007u, name: "Tirenia",
            itemType: (uint)ItemType.Creature,
            weenieFlags: 0x10u,
            useability: 0x20u);

        var parsed = CreateObject.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal(0x20u, parsed!.Value.Useability);
    }

    [Fact]
    public void TryParse_WeenieFlagsUsable_ReadsUseableNoValue()
    {
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x7A9B3001u, name: "Holtburg",
            itemType: 0x80u,           // Misc
            weenieFlags: 0x10u,
            useability: 0x01u);        // USEABLE_NO

        var parsed = CreateObject.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal(0x01u, parsed!.Value.Useability);
    }

    [Fact]
    public void TryParse_WeenieFlagsValueAndUsableAndUseRadius_AllReadInOrder()
    {
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x50000008u, name: "PriceyDoor",
            itemType: (uint)ItemType.Misc,
            weenieFlags: 0x8u | 0x10u | 0x20u,
            value: 0x12345678u,
            useability: 0x20u,
            useRadius: 2.5f);

        var parsed = CreateObject.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal(0x20u, parsed!.Value.Useability);
        Assert.NotNull(parsed.Value.UseRadius);
        Assert.Equal(2.5f, parsed.Value.UseRadius!.Value, precision: 3);
    }

    [Fact]
    public void TryParse_WeenieFlagsTargetType_ReadsTargetType()
    {
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x50000009u,
            name: "Healing Kit",
            itemType: (uint)ItemType.Misc,
            weenieFlags: 0x00080000u,
            targetType: (uint)ItemType.Creature);

        var parsed = CreateObject.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal((uint)ItemType.Creature, parsed!.Value.TargetType);
    }

    [Fact]
    public void TryParse_CombatUseFlag_CapturesByte()
    {
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x5000000Du,
            name: "Sword",
            itemType: (uint)ItemType.MeleeWeapon,
            weenieFlags: 0x00000200u,
            combatUse: 2);

        var parsed = CreateObject.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal((byte)2, parsed.Value.CombatUse);
    }

    [Fact]
    public void TryParse_AmmoTypeFlag_CapturesValue()
    {
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x5000000Eu,
            name: "Bow",
            itemType: (uint)ItemType.MissileWeapon,
            weenieFlags: 0x00000100u,
            ammoType: 1);

        var parsed = CreateObject.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal((ushort)1, parsed.Value.AmmoType);
    }

    [Fact]
    public void TryParse_ParentedChild_CapturesPlacementParentAndPositionSequence()
    {
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x60000002u,
            name: "Bow",
            itemType: (uint)ItemType.MissileWeapon,
            placementId: (uint)DatReaderWriter.Enums.Placement.LeftHand,
            parentGuid: 0x50000001u,
            parentLocation: (uint)DatReaderWriter.Enums.ParentLocation.LeftHand,
            positionSeq: 0x3456,
            instanceSeq: 0x789A);

        var parsed = CreateObject.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal((uint)DatReaderWriter.Enums.Placement.LeftHand, parsed.Value.PlacementId);
        Assert.Equal(0x50000001u, parsed.Value.ParentGuid);
        Assert.Equal((uint)DatReaderWriter.Enums.ParentLocation.LeftHand, parsed.Value.ParentLocation);
        Assert.Equal((ushort)0x3456, parsed.Value.PositionSequence);
        Assert.Equal((ushort)0x789A, parsed.Value.InstanceSequence);
    }


    [Fact]
    public void TryParse_NoRadarFlags_LeavesRadarFieldsNull()
    {
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x5000000Au,
            name: "Unspecified Radar Object",
            itemType: (uint)ItemType.Misc);

        var parsed = CreateObject.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Null(parsed.Value.RadarBlipColor);
        Assert.Null(parsed.Value.RadarBehavior);
    }

    [Fact]
    public void TryParse_RadarFlags_CapturesBothBytesAndContinuesTailWalk()
    {
        const uint radarBlipColorFlag = 0x00100000u;
        const uint radarBehaviorFlag = 0x00800000u;
        const uint workmanshipFlag = 0x01000000u;
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x5000000Bu,
            name: "Radar Portal",
            itemType: (uint)ItemType.Portal,
            weenieFlags: radarBlipColorFlag | radarBehaviorFlag | workmanshipFlag,
            radarBlipColor: 4,
            radarBehavior: 4,
            workmanship: 6.25f);

        var parsed = CreateObject.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal((byte)4, parsed.Value.RadarBlipColor);
        Assert.Equal((byte)4, parsed.Value.RadarBehavior);
        Assert.Equal(6.25f, parsed.Value.Workmanship);
    }

    [Fact]
    public void TryParse_RadarFlagsWithZero_PreservesExplicitDefaultValues()
    {
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x5000000Cu,
            name: "Explicit Radar Defaults",
            itemType: (uint)ItemType.Misc,
            weenieFlags: 0x00100000u | 0x00800000u,
            radarBlipColor: 0,
            radarBehavior: 0);

        var parsed = CreateObject.TryParse(body);

        Assert.NotNull(parsed);
        Assert.True(parsed.Value.RadarBlipColor.HasValue);
        Assert.Equal((byte)0, parsed.Value.RadarBlipColor.Value);
        Assert.True(parsed.Value.RadarBehavior.HasValue);
        Assert.Equal((byte)0, parsed.Value.RadarBehavior.Value);
    }


    [Fact]
    public void TryParse_IconId_Surfaced()
    {
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x50000009u,
            name: "SwordIcon",
            itemType: (uint)ItemType.MeleeWeapon,
            iconId: 0x1234u);

        var parsed = CreateObject.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal(0x06001234u, parsed!.Value.IconId);
    }


    [Fact]
    public void TryParse_IconOverlay_CapturedFromExtendedTail()
    {
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x5000000Au,
            name: "EnchantedSword",
            itemType: (uint)ItemType.MeleeWeapon,
            weenieFlags: 0x40000000u,        // IconOverlay
            iconOverlayId: 0x1ABCu);

        var parsed = CreateObject.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal(0x06001ABCu, parsed!.Value.IconOverlayId);
        Assert.Equal(0u, parsed.Value.IconUnderlayId);
    }

    [Fact]
    public void TryParse_IconOverlayAndUnderlay_BothCaptured()
    {
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x5000000Bu,
            name: "MagicRing",
            itemType: (uint)ItemType.Jewelry,
            objectDescriptionFlags: 0x04000000u,
            weenieFlags: 0x40000000u,
            weenieFlags2: 0x00000001u,
            iconOverlayId: 0x5678u,
            iconUnderlayId: 0x9ABCu);

        var parsed = CreateObject.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal(0x06005678u, parsed!.Value.IconOverlayId);
        Assert.Equal(0x06009ABCu, parsed.Value.IconUnderlayId);
    }

    [Fact]
    public void TryParse_NoOverlayBits_CommonCase_OverlaysStayZero()
    {
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x5000000Cu,
            name: "CommonDrudge",
            itemType: (uint)ItemType.Creature,
            weenieFlags: 0u);

        var parsed = CreateObject.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal("CommonDrudge", parsed!.Value.Name);
        Assert.Equal(0u, parsed.Value.IconOverlayId);
        Assert.Equal(0u, parsed.Value.IconUnderlayId);
        Assert.Null(parsed.Value.Useability);
    }

    [Fact]
    public void TryParse_IntermediateFieldsBeforeIconOverlay_SkippedCorrectly()
    {
        const uint flags = 0x40000000u | 0x00200000u | 0x00001000u | 0x00000800u | 0x00000400u;
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x5000000Du,
            name: "FancySword",
            itemType: (uint)ItemType.MeleeWeapon,
            weenieFlags: flags,
            structure: 50,
            maxStructure: 100,
            stackSize: 1,
            burden: 300,
            iconOverlayId: 0x2222u);

        var parsed = CreateObject.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal(0x06002222u, parsed!.Value.IconOverlayId);
    }

    [Fact]
    public void TryParse_HouseRestrictionsCaptured_ThenIconOverlayCaptured()
    {
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x5000000Eu,
            name: "HousePortal",
            itemType: (uint)ItemType.Portal,
            objectDescriptionFlags: 0x04000000u,          // IncludesSecondHeader
            weenieFlags: 0x04000000u | 0x40000000u,       // HouseRestrictions + IconOverlay
            weenieFlags2: 0x00000001u,                    // IconUnderlay
            iconOverlayId: 0x3333u,
            iconUnderlayId: 0x4444u);

        var parsed = CreateObject.TryParse(body);

        Assert.NotNull(parsed);
        Assert.NotNull(parsed!.Value.Restrictions);
        Assert.False(parsed.Value.Restrictions!.OpenToPublic);
        Assert.Equal(0u, parsed.Value.Restrictions.AllegianceMonarchId);
        Assert.Empty(parsed.Value.Restrictions.Guests);
        Assert.Equal(0x06003333u, parsed.Value.IconOverlayId);
        Assert.Equal(0x06004444u, parsed.Value.IconUnderlayId);
    }

    [Fact]
    public void TryParse_HouseRestrictionsWithGuests_CapturesGuestTable()
    {
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x5000000Fu,
            name: "HouseCottage",
            itemType: (uint)ItemType.Portal,
            weenieFlags: 0x04000000u | 0x20000000u | 0x00000040u | 0x10000000u | 0x40000000u,
            houseRestrictionOpen: false,
            houseRestrictionMonarchId: 0x50000500u,
            houseRestrictionGuests: new Dictionary<uint, uint> { [0x50000001u] = 0u, [0x50000002u] = 1u },
            hookItemTypes: 0x1u,
            hookType: 2,
            iconOverlayId: 0x5555u);

        var parsed = CreateObject.TryParse(body);

        Assert.NotNull(parsed);
        Assert.NotNull(parsed!.Value.Restrictions);
        Assert.False(parsed.Value.Restrictions!.OpenToPublic);
        Assert.Equal(0x50000500u, parsed.Value.Restrictions.AllegianceMonarchId);
        Assert.Equal(2, parsed.Value.Restrictions.Guests.Count);
        Assert.Equal(0u, parsed.Value.Restrictions.Guests[0x50000001u]);
        Assert.Equal(1u, parsed.Value.Restrictions.Guests[0x50000002u]);
        Assert.Equal(0x1u, parsed.Value.HookItemTypes);
        Assert.Equal((uint)2, parsed.Value.HookType);
        Assert.Equal(0x06005555u, parsed.Value.IconOverlayId);
    }

    [Fact]
    public void TryParse_HouseOwnerAndMonarch_Captured()
    {
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x50000012u,
            name: "HouseDoor",
            itemType: (uint)ItemType.Portal,
            weenieFlags: 0x02000000u | 0x00000040u,
            houseOwnerId: 0x50000042u,
            monarchId: 0x50000777u);

        var parsed = CreateObject.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal(0x50000042u, parsed!.Value.HouseOwnerId);
        Assert.Equal(0x50000777u, parsed.Value.MonarchId);
    }


    [Fact]
    public void TryParse_UiEffects_Captured()
    {
        // weenieFlags 0x80 = UiEffects; value 0x1 = Magical.
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x50000010u, name: "MagicWand", itemType: (uint)ItemType.Caster,
            weenieFlags: 0x80u, uiEffects: 0x1u);

        var parsed = CreateObject.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal(0x1u, parsed!.Value.UiEffects);
    }

    [Fact]
    public void TryParse_UiEffectsThenIconOverlay_BothCaptured()
    {
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x50000011u, name: "GlowSword", itemType: (uint)ItemType.MeleeWeapon,
            weenieFlags: 0x80u | 0x40000000u, uiEffects: 0x4u, iconOverlayId: 0x1ABCu);

        var parsed = CreateObject.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal(0x4u, parsed!.Value.UiEffects);
        Assert.Equal(0x06001ABCu, parsed.Value.IconOverlayId);
    }

    [Fact]
    public void TryParse_NoUiEffectsBit_LeavesUiEffectsZero()
    {
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x50000012u, name: "PlainRock", itemType: (uint)ItemType.Misc, weenieFlags: 0u);

        var parsed = CreateObject.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal(0u, parsed!.Value.UiEffects);
    }

    [Fact]
    public void TryParse_WeenieClassId_Surfaced()
    {
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x50000020u, name: "Sword", itemType: (uint)ItemType.MeleeWeapon,
            weenieClassId: 0xABCDu);
        var parsed = CreateObject.TryParse(body);
        Assert.NotNull(parsed);
        Assert.Equal(0xABCDu, parsed!.Value.WeenieClassId);
    }

    [Fact]
    public void TryParse_FullItemFields_Captured()
    {
        uint flags =
            0x00000008u | 0x00001000u | 0x00002000u | 0x00200000u |
            0x00000002u | 0x00000004u | 0x00004000u | 0x00008000u |
            0x00010000u | 0x00020000u | 0x00040000u | 0x00000400u |
            0x00000800u | 0x01000000u;
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x50000021u, name: "Pack", itemType: (uint)ItemType.Container,
            weenieFlags: flags,
            value: 250u, stackSize: 7, maxStackSize: 100u, burden: 42,
            itemsCapacity: 24, containersCapacity: 7,
            container: 0x50000099u, wielder: 0x5000009Au,
            validLocations: 0x02000000u, currentWieldedLocation: 0x02000000u,
            priority: 8u, structure: 5, maxStructure: 10, workmanship: 7.5f);
        var parsed = CreateObject.TryParse(body);
        Assert.NotNull(parsed);
        var p = parsed!.Value;
        Assert.Equal(250, p.Value);
        Assert.Equal(7, p.StackSize);
        Assert.Equal(100, p.StackSizeMax);
        Assert.Equal(42, p.Burden);
        Assert.Equal(24, p.ItemsCapacity);
        Assert.Equal(7, p.ContainersCapacity);
        Assert.Equal(0x50000099u, p.ContainerId);
        Assert.Equal(0x5000009Au, p.WielderId);
        Assert.Equal(0x02000000u, p.ValidLocations);
        Assert.Equal(0x02000000u, p.CurrentWieldedLocation);
        Assert.Equal(8u, p.Priority);
        Assert.Equal(5, p.Structure);
        Assert.Equal(10, p.MaxStructure);
        Assert.Equal(7.5f, p.Workmanship);
    }

    [Fact]
    public void TryParse_CapacityBytes_AreSignExtendedLikeRetail()
    {
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x50000029u,
            name: "Black Phyntos Hive",
            itemType: (uint)ItemType.Creature,
            weenieClassId: 28249u,
            weenieFlags: 0x00000002u | 0x00000004u,
            itemsCapacity: -1,
            containersCapacity: -1);

        var parsed = CreateObject.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal(-1, parsed.Value.ItemsCapacity);
        Assert.Equal(-1, parsed.Value.ContainersCapacity);
    }

    [Fact]
    public void TryParse_HookIdentityFields_AreCapturedWithoutMisaligningOverlay()
    {
        const uint hookItemTypes = (uint)ItemType.Container;
        const ushort hookType = 4;
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x50000027u,
            name: "Black Phyntos Hive",
            itemType: (uint)ItemType.Container,
            weenieFlags: 0x20000000u | 0x10000000u | 0x40000000u,
            hookItemTypes: hookItemTypes,
            hookType: hookType,
            iconOverlayId: 0x4567u);

        var parsed = CreateObject.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal(hookItemTypes, parsed.Value.HookItemTypes);
        Assert.Equal((uint)hookType, parsed.Value.HookType);
        Assert.Equal(0x06004567u, parsed.Value.IconOverlayId);
    }

    [Fact]
    public void TryParse_AbsentHookIdentityFields_RemainNull()
    {
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x50000028u,
            name: "Ordinary Chest",
            itemType: (uint)ItemType.Container);

        var parsed = CreateObject.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Null(parsed.Value.HookItemTypes);
        Assert.Null(parsed.Value.HookType);
    }

    [Fact]
    public void TryParse_MovementSequence_SurfacedFromTimestampBlock()
    {
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x50000030u, name: "Runner", itemType: 0x10u,
            movementSeq: 0x9000);
        var parsed = CreateObject.TryParse(body);
        Assert.NotNull(parsed);
        Assert.Equal((ushort)0x9000, parsed!.Value.MovementSequence);
    }

    [Fact]
    public void TryParse_MidTailFieldsSet_StillReachesIconOverlay()
    {
        uint flags = 0x00001000u | 0x00004000u | 0x40000000u;
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x50000022u, name: "Ring", itemType: (uint)ItemType.Jewelry,
            weenieFlags: flags, stackSize: 1, container: 0x500000F0u,
            iconOverlayId: 0x4321u);
        var parsed = CreateObject.TryParse(body);
        Assert.NotNull(parsed);
        Assert.Equal(0x06004321u, parsed!.Value.IconOverlayId);
        Assert.Equal(0x500000F0u, parsed.Value.ContainerId);
    }

    [Fact]
    public void WeenieHeader_pluralName_isPreserved()
    {
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x50000023u,
            name: "Pyreal Scarab",
            itemType: (uint)ItemType.Misc,
            weenieFlags: 0x00000001u | 0x00001000u,
            stackSize: 2,
            pluralName: "Pyreal Scarabs");

        var parsed = CreateObject.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal("Pyreal Scarabs", parsed.Value.PluralName);
    }

    [Fact]
    public void WeenieHeader_secondHeaderPetOwner_isPreservedAfterPrecedingFields()
    {
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x50000024u,
            name: "Combat Pet",
            itemType: (uint)ItemType.Creature,
            objectDescriptionFlags: 0x04000000u,
            weenieFlags: 0x80000000u,
            weenieFlags2: 0x0000000Eu,
            materialType: 7u,
            cooldownId: 11u,
            cooldownDuration: 12.5,
            petOwnerId: 0x50000001u);

        var parsed = CreateObject.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal(7u, parsed.Value.MaterialType);
        Assert.Equal(11u, parsed.Value.CooldownId);
        Assert.Equal(12.5, parsed.Value.CooldownDuration);
        Assert.Equal(0x50000001u, parsed.Value.PetOwnerId);
    }

    [Fact]
    public void WeenieHeader_absentCooldownFields_remainNull()
    {
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x50000026u,
            name: "Ordinary Stone",
            itemType: (uint)ItemType.Misc,
            objectDescriptionFlags: 0x04000000u,
            weenieFlags2: 0u);

        var parsed = CreateObject.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Null(parsed.Value.CooldownId);
        Assert.Null(parsed.Value.CooldownDuration);
    }

    [Fact]
    public void WeenieHeader_spellId_isPreservedForCasterEndowment()
    {
        byte[] body = BuildMinimalCreateObjectWithWeenieHeader(
            guid: 0x50000025u,
            name: "Orb",
            itemType: (uint)ItemType.Caster,
            weenieFlags: 0x00400000u,
            spellId: 0x0A6E);

        var parsed = CreateObject.TryParse(body);

        Assert.NotNull(parsed);
        Assert.Equal(0x0A6Eu, parsed.Value.SpellId);
    }

    private static byte[] BuildMinimalCreateObjectWithWeenieHeader(
        uint guid,
        string name,
        uint itemType,
        uint physicsState = 0,
        uint objectDescriptionFlags = 0,
        uint weenieFlags = 0,
        uint weenieFlags2 = 0,
        uint iconId = 0,
        uint uiEffects = 0,
        uint? value = null,
        uint? useability = null,
        float? useRadius = null,
        uint? targetType = null,
        uint iconOverlayId = 0,
        uint iconUnderlayId = 0,
        ushort? structure = null,
        ushort? maxStructure = null,
        ushort? stackSize = null,
        ushort? burden = null,
        uint weenieClassId = 0x1234,
        uint? maxStackSize = null,
        sbyte? itemsCapacity = null,
        sbyte? containersCapacity = null,
        uint? container = null,
        uint? wielder = null,
        uint? validLocations = null,
        uint? currentWieldedLocation = null,
        uint? priority = null,
        float? workmanship = null,
        string? pluralName = null,
        byte? radarBlipColor = null,
        byte? radarBehavior = null,
        byte? combatUse = null,
        ushort? ammoType = null,
        ushort movementSeq = 0,
        uint? placementId = null,
        uint? parentGuid = null,
        uint? parentLocation = null,
        ushort positionSeq = 0,
        ushort instanceSeq = 0,
        uint materialType = 0,
        uint cooldownId = 0,
        double cooldownDuration = 0,
        uint petOwnerId = 0,
        ushort spellId = 0,
        uint hookItemTypes = 0,
        ushort hookType = 0,
        uint? houseOwnerId = null,
        uint? monarchId = null,
        bool houseRestrictionOpen = false,
        uint houseRestrictionMonarchId = 0,
        IReadOnlyDictionary<uint, uint>? houseRestrictionGuests = null)
    {
        var bytes = new List<byte>();
        WriteU32(bytes, CreateObject.Opcode);
        WriteU32(bytes, guid);

        bytes.Add(0x11);
        bytes.Add(0);
        bytes.Add(0);
        bytes.Add(0);

        // PhysicsData: optional placement + parent bootstrap, then 9 seq stamps
        // (PhysicsTimeStamp enum order; index 1 = ObjectMovement).
        uint physicsFlags = 0;
        if (placementId.HasValue) physicsFlags |= (uint)CreateObject.PhysicsDescriptionFlag.AnimationFrame;
        if (parentGuid.HasValue) physicsFlags |= (uint)CreateObject.PhysicsDescriptionFlag.Parent;
        WriteU32(bytes, physicsFlags);
        WriteU32(bytes, physicsState);
        if (placementId.HasValue) WriteU32(bytes, placementId.Value);
        if (parentGuid.HasValue)
        {
            WriteU32(bytes, parentGuid.Value);
            WriteU32(bytes, parentLocation ?? 0u);
        }
        for (int i = 0; i < 9; i++)
            WriteU16(bytes, i switch
            {
                0 => positionSeq,
                1 => movementSeq,
                8 => instanceSeq,
                _ => 0,
            });
        Align4(bytes);

        WriteU32(bytes, weenieFlags);    // weenieFlags
        WriteString16L(bytes, name);
        WritePackedDword(bytes, weenieClassId);   // WeenieClassId
        WritePackedDword(bytes, iconId);
        WriteU32(bytes, itemType);
        WriteU32(bytes, objectDescriptionFlags);
        Align4(bytes);

        if ((objectDescriptionFlags & 0x04000000u) != 0)
            WriteU32(bytes, weenieFlags2);

        if ((weenieFlags & 0x00000001u) != 0) WriteString16L(bytes, pluralName ?? "");
        if ((weenieFlags & 0x00000002u) != 0)
            bytes.Add(unchecked((byte)(itemsCapacity ?? 0)));       // ItemsCapacity s8
        if ((weenieFlags & 0x00000004u) != 0)
            bytes.Add(unchecked((byte)(containersCapacity ?? 0)));
        if ((weenieFlags & 0x00000100u) != 0) WriteU16(bytes, ammoType ?? 0);  // AmmoType u16
        if ((weenieFlags & 0x00000008u) != 0) WriteU32(bytes, value ?? 0u);   // Value u32
        if ((weenieFlags & 0x00000010u) != 0) WriteU32(bytes, useability ?? 0u);  // Usable u32
        if ((weenieFlags & 0x00000020u) != 0)  // UseRadius f32
        {
            Span<byte> tmp = stackalloc byte[4];
            BinaryPrimitives.WriteSingleLittleEndian(tmp, useRadius ?? 0f);
            bytes.AddRange(tmp.ToArray());
        }
        if ((weenieFlags & 0x00080000u) != 0) WriteU32(bytes, targetType ?? 0u);  // TargetType u32
        if ((weenieFlags & 0x00000080u) != 0) WriteU32(bytes, uiEffects);  // UiEffects u32
        if ((weenieFlags & 0x00000200u) != 0) bytes.Add(combatUse ?? 0);
        if ((weenieFlags & 0x00000400u) != 0) WriteU16(bytes, structure ?? 0);  // Structure u16
        if ((weenieFlags & 0x00000800u) != 0) WriteU16(bytes, maxStructure ?? 0); // MaxStructure u16
        if ((weenieFlags & 0x00001000u) != 0) WriteU16(bytes, stackSize ?? 0);    // StackSize u16
        if ((weenieFlags & 0x00002000u) != 0) WriteU16(bytes, (ushort)(maxStackSize ?? 0)); // MaxStackSize u16
        if ((weenieFlags & 0x00004000u) != 0) WriteU32(bytes, container ?? 0);
        if ((weenieFlags & 0x00008000u) != 0) WriteU32(bytes, wielder ?? 0);      // Wielder u32
        if ((weenieFlags & 0x00010000u) != 0) WriteU32(bytes, validLocations ?? 0);          // ValidLocations u32
        if ((weenieFlags & 0x00020000u) != 0) WriteU32(bytes, currentWieldedLocation ?? 0);
        if ((weenieFlags & 0x00040000u) != 0) WriteU32(bytes, priority ?? 0);     // Priority u32
        if ((weenieFlags & 0x00100000u) != 0) bytes.Add(radarBlipColor ?? 0); // RadarBlipColor u8
        if ((weenieFlags & 0x00800000u) != 0) bytes.Add(radarBehavior ?? 0);  // RadarBehavior u8
        if ((weenieFlags & 0x08000000u) != 0) WriteU16(bytes, 0);  // PScript u16
        if ((weenieFlags & 0x01000000u) != 0)                      // Workmanship f32
        {
            Span<byte> tmp = stackalloc byte[4];
            BinaryPrimitives.WriteSingleLittleEndian(tmp, workmanship ?? 0f);
            bytes.AddRange(tmp.ToArray());
        }
        if ((weenieFlags & 0x00200000u) != 0) WriteU16(bytes, burden ?? 0);  // Burden u16
        if ((weenieFlags & 0x00400000u) != 0) WriteU16(bytes, spellId);  // Spell u16
        if ((weenieFlags & 0x02000000u) != 0) WriteU32(bytes, houseOwnerId ?? 0);  // HouseOwner u32
        if ((weenieFlags & 0x04000000u) != 0)
        {
            WriteU32(bytes, 0x10000002u); // Version
            WriteU32(bytes, houseRestrictionOpen ? 1u : 0u); // Flags
            WriteU32(bytes, houseRestrictionMonarchId);      // MonarchId
            var guests = houseRestrictionGuests ?? new Dictionary<uint, uint>();
            WriteU32(bytes, (uint)guests.Count);
            foreach (var kvp in guests)
            {
                WriteU32(bytes, kvp.Key);
                WriteU32(bytes, kvp.Value);
            }
        }
        if ((weenieFlags & 0x20000000u) != 0) WriteU32(bytes, hookItemTypes);  // HookItemTypes u32
        if ((weenieFlags & 0x00000040u) != 0) WriteU32(bytes, monarchId ?? 0);  // Monarch u32
        if ((weenieFlags & 0x10000000u) != 0) WriteU16(bytes, hookType);  // HookType u16
        if ((weenieFlags & 0x40000000u) != 0) WritePackedDword(bytes, iconOverlayId);   // IconOverlay
        if ((weenieFlags2 & 0x00000001u) != 0) WritePackedDword(bytes, iconUnderlayId); // IconUnderlay
        if ((weenieFlags & 0x80000000u) != 0) WriteU32(bytes, materialType); // MaterialType
        if ((weenieFlags2 & 0x00000002u) != 0) WriteU32(bytes, cooldownId);
        if ((weenieFlags2 & 0x00000004u) != 0)
        {
            Span<byte> tmp = stackalloc byte[8];
            BinaryPrimitives.WriteDoubleLittleEndian(tmp, cooldownDuration);
            bytes.AddRange(tmp.ToArray());
        }
        if ((weenieFlags2 & 0x00000008u) != 0) WriteU32(bytes, petOwnerId); // PetOwner
        Align4(bytes);

        return bytes.ToArray();
    }

    private static void WriteU32(List<byte> bytes, uint value)
    {
        Span<byte> tmp = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(tmp, value);
        bytes.AddRange(tmp.ToArray());
    }

    private static void WriteU16(List<byte> bytes, ushort value)
    {
        Span<byte> tmp = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(tmp, value);
        bytes.AddRange(tmp.ToArray());
    }

    private static void WritePackedDword(List<byte> bytes, uint value)
    {
        if (value <= 0x7FFF)
        {
            WriteU16(bytes, (ushort)value);
            return;
        }

        WriteU16(bytes, (ushort)(((value >> 16) & 0x7FFF) | 0x8000));
        WriteU16(bytes, (ushort)(value & 0xFFFF));
    }

    private static void WriteString16L(List<byte> bytes, string value)
    {
        byte[] encoded = Encoding.GetEncoding(1252).GetBytes(value);
        WriteU16(bytes, checked((ushort)encoded.Length));
        bytes.AddRange(encoded);
        Align4(bytes);
    }

    private static void Align4(List<byte> bytes)
    {
        while ((bytes.Count & 3) != 0)
            bytes.Add(0);
    }
}
