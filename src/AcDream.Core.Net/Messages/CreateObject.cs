using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Items;

namespace AcDream.Core.Net.Messages;

public static class CreateObject
{
    public const uint Opcode = 0xF745u;

    /// <summary>AC dat id type prefix for GfxObj (visual model) ids.</summary>
    public const uint GfxObjTypePrefix = 0x01000000u;
    /// <summary>Palette dat id type prefix.</summary>
    public const uint PaletteTypePrefix = 0x04000000u;
    /// <summary>SurfaceTexture dat id type prefix.</summary>
    public const uint SurfaceTextureTypePrefix = 0x05000000u;
    /// <summary>Icon dat id type prefix.</summary>
    public const uint IconTypePrefix = 0x06000000u;

    [Flags]
    public enum PhysicsDescriptionFlag : uint
    {
        None                   = 0x000000,
        CSetup                 = 0x000001,
        MTable                 = 0x000002,
        Velocity               = 0x000004,
        Acceleration           = 0x000008,
        Omega                  = 0x000010,
        Parent                 = 0x000020,
        Children               = 0x000040,
        ObjScale               = 0x000080,
        Friction               = 0x000100,
        Elasticity             = 0x000200,
        Timestamps             = 0x000400,
        STable                 = 0x000800,
        PeTable                = 0x001000,
        DefaultScript          = 0x002000,
        DefaultScriptIntensity = 0x004000,
        Position               = 0x008000,
        Movement               = 0x010000,
        AnimationFrame         = 0x020000,
        Translucency           = 0x040000,
    }

    public readonly record struct Parsed(
        uint Guid,
        ServerPosition? Position,
        uint? SetupTableId,
        IReadOnlyList<AnimPartChange> AnimPartChanges,
        IReadOnlyList<TextureChange> TextureChanges,
        IReadOnlyList<SubPaletteSwap> SubPalettes,
        uint? BasePaletteId,
        float? ObjScale,
        string? Name,
        uint? ItemType,
        ServerMotionState? MotionState,
        uint? MotionTableId,
        ushort InstanceSequence = 0,
        ushort TeleportSequence = 0,
        ushort ServerControlSequence = 0,
        ushort ForcePositionSequence = 0,
        // L.2g S1 (DEV-6): ObjectMovement stamp (timestamp block index 1)
        // seeds PhysicsTimestampGate's MOVEMENT_TS at spawn.
        ushort MovementSequence = 0,
        ushort PositionSequence = 0,
        uint? ParentGuid = null,
        uint? ParentLocation = null,
        uint? PlacementId = null,
        uint? PhysicsState = null,
        uint? ObjectDescriptionFlags = null,
        float? Friction = null,
        float? Elasticity = null,
        uint IconId = 0,
        uint? Useability = null,
        float? UseRadius = null,
        uint? TargetType = null,
        uint IconOverlayId = 0,
        uint IconUnderlayId = 0,
        uint UiEffects = 0,
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
        byte? RadarBlipColor = null,
        byte? RadarBehavior = null,
        byte? CombatUse = null,
        string? PluralName = null,
        // PublicWeenieDesc._pet_owner, gated by second-header flag 0x8.
        uint? PetOwnerId = null,
        // PublicWeenieDesc._ammoType, gated by WeenieHeader flag 0x100.
        // AMMO_NONE is the explicit wire value zero; null means absent.
        ushort? AmmoType = null,
        uint? SpellId = null,
        uint? CooldownId = null,
        double? CooldownDuration = null,
        PhysicsSpawnData? Physics = null,
        uint? MaterialType = null,
        uint? HouseOwnerId = null,
        uint? MonarchId = null,
        HouseRestrictionRecord? Restrictions = null);

    public readonly record struct ServerMotionState(
        ushort Stance,
        ushort? ForwardCommand,
        float? ForwardSpeed = null,
        IReadOnlyList<MotionItem>? Commands = null,
        ushort? SideStepCommand = null,
        float? SideStepSpeed = null,
        ushort? TurnCommand = null,
        float? TurnSpeed = null,
        byte MovementType = 0,
        uint? MoveToParameters = null,
        float? MoveToSpeed = null,
        float? MoveToRunRate = null,
        MoveToPathData? MoveToPath = null,
        TurnToPathData? TurnToPath = null,
        uint? StickyObjectGuid = null,
        bool StandingLongJump = false)
    {
        public bool IsServerControlledMoveTo => MovementType is 6 or 7;

        public bool IsServerControlledTurnTo => MovementType is 8 or 9;

        public bool MoveToCanRun => !MoveToParameters.HasValue
            || (MoveToParameters.Value & 0x2u) != 0;

        public bool MoveTowards => MoveToParameters.HasValue
            && (MoveToParameters.Value & 0x200u) != 0;

        public bool CanCharge => MoveToParameters.HasValue
            && (MoveToParameters.Value & 0x10u) != 0;
    }

    public readonly record struct MoveToPathData(
        uint? TargetGuid,
        uint OriginCellId,
        float OriginX,
        float OriginY,
        float OriginZ,
        float DistanceToObject,
        float MinDistance,
        float FailDistance,
        float WalkRunThreshold,
        float DesiredHeading,
        uint Bitfield = 0);  // R4-V4: the raw UnPackNet flags dword, feeds MovementParameters.FromWire

    public readonly record struct TurnToPathData(
        uint? TargetGuid,
        float? WireHeading,
        uint Bitfield,
        float Speed,
        float DesiredHeading);

    public readonly record struct MotionItem(
        ushort Command,
        ushort PackedSequence,
        float Speed);

    public readonly record struct TextureChange(byte PartIndex, uint OldTexture, uint NewTexture);

    public readonly record struct SubPaletteSwap(uint SubPaletteId, byte Offset, byte Length);

    /// <summary>A server-side position: landblock id + local XYZ + unit quaternion rotation.</summary>
    public readonly record struct ServerPosition(
        uint LandblockId,
        float PositionX, float PositionY, float PositionZ,
        float RotationW, float RotationX, float RotationY, float RotationZ);

    public readonly record struct AnimPartChange(byte PartIndex, uint NewModelId);

    public readonly record struct ModelData(
        uint? BasePaletteId,
        IReadOnlyList<SubPaletteSwap> SubPalettes,
        IReadOnlyList<TextureChange> TextureChanges,
        IReadOnlyList<AnimPartChange> AnimPartChanges);

    public static Parsed? TryParse(ReadOnlySpan<byte> body)
    {
        ServerPosition? position = null;
        uint? setupTableId = null;
        float? objScale = null;
        ServerMotionState? motionState = null;
        uint? motionTableId = null;
        PhysicsMovementData? movement = null;
        uint? soundTableId = null;
        uint? physicsScriptTableId = null;
        ReadOnlyMemory<PhysicsAttachment>? children = null;
        float? translucency = null;
        Vector3? velocity = null;
        Vector3? acceleration = null;
        Vector3? angularVelocity = null;
        uint? defaultScriptType = null;
        float? defaultScriptIntensity = null;
        PhysicsSpawnData? physics = null;
        uint? physicsState = null;
        float? friction = null;
        float? elasticity = null;

        try
        {
            int pos = 0;

            uint opcode = ReadU32(body, ref pos);
            if (opcode != Opcode)
                return null;

            uint guid = ReadU32(body, ref pos);

            var modelData = ReadModelData(body, ref pos);
            uint? basePaletteId = modelData.BasePaletteId;
            var subPalettes = modelData.SubPalettes;
            var textureChanges = modelData.TextureChanges;
            var animParts = modelData.AnimPartChanges;

            // --- PhysicsData ---
            if (body.Length - pos < 8) return null;
            var physicsFlags = (PhysicsDescriptionFlag)BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos));
            pos += 4;
            physicsState = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos));
            pos += 4;

            uint? placementId = null;
            uint? parentGuid = null;
            uint? parentLocation = null;

            if ((physicsFlags & PhysicsDescriptionFlag.Movement) != 0)
            {
                if (body.Length - pos < 4) return null;
                uint movementLen = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos));
                pos += 4;
                if (movementLen > 0)
                {
                    if (body.Length - pos < (int)movementLen) return null;
                    int movementStart = pos;
                    ReadOnlySpan<byte> movementBytes = body.Slice(movementStart, (int)movementLen);
                    motionState = TryParseMovementData(movementBytes);
                    pos = movementStart + (int)movementLen;
                    if (body.Length - pos < 4) return null;
                    bool isAutonomous = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos)) != 0;
                    pos += 4;
                    movement = new PhysicsMovementData(movementBytes.ToArray(), motionState, isAutonomous);
                }
                else
                    movement = new PhysicsMovementData(ReadOnlyMemory<byte>.Empty, null, null);
            }
            else if ((physicsFlags & PhysicsDescriptionFlag.AnimationFrame) != 0)
            {
                if (body.Length - pos < 4) return null;
                placementId = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos));
                pos += 4;
            }

            if ((physicsFlags & PhysicsDescriptionFlag.Position) != 0)
            {
                if (body.Length - pos < 32) return null;
                position = new ServerPosition(
                    LandblockId: BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos + 0)),
                    PositionX:   BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos + 4)),
                    PositionY:   BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos + 8)),
                    PositionZ:   BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos + 12)),
                    RotationW:   BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos + 16)),
                    RotationX:   BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos + 20)),
                    RotationY:   BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos + 24)),
                    RotationZ:   BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos + 28)));
                pos += 32;
            }

            if ((physicsFlags & PhysicsDescriptionFlag.MTable) != 0)
            {
                if (body.Length - pos < 4) return null;
                motionTableId = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos));
                pos += 4;
            }

            if ((physicsFlags & PhysicsDescriptionFlag.STable) != 0)
            {
                if (body.Length - pos < 4) return null;
                soundTableId = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos));
                pos += 4;
            }

            if ((physicsFlags & PhysicsDescriptionFlag.PeTable) != 0)
            {
                if (body.Length - pos < 4) return null;
                physicsScriptTableId = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos));
                pos += 4;
            }

            if ((physicsFlags & PhysicsDescriptionFlag.CSetup) != 0)
            {
                if (body.Length - pos < 4) return null;
                setupTableId = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos));
                pos += 4;
            }

            if ((physicsFlags & PhysicsDescriptionFlag.Parent) != 0)
            {
                if (body.Length - pos < 8) return null;
                parentGuid = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos));
                parentLocation = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos + 4));
                pos += 8;
            }
            if ((physicsFlags & PhysicsDescriptionFlag.Children) != 0)
            {
                if (body.Length - pos < 4) return null;
                int childCount = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(pos));
                pos += 4;
                if (childCount < 0 || childCount > 1024) return null;
                if (body.Length - pos < childCount * 8) return null;
                var parsedChildren = new PhysicsAttachment[childCount];
                for (int i = 0; i < parsedChildren.Length; i++)
                {
                    parsedChildren[i] = new PhysicsAttachment(
                        BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos)),
                        BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos + 4)));
                    pos += 8;
                }
                children = parsedChildren;
            }
            if ((physicsFlags & PhysicsDescriptionFlag.ObjScale) != 0)
            {
                if (body.Length - pos < 4) return null;
                objScale = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos));
                pos += 4;
            }
            if ((physicsFlags & PhysicsDescriptionFlag.Friction) != 0)
            {
                if (body.Length - pos < 4) return null;
                friction = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos));
                pos += 4;
            }
            if ((physicsFlags & PhysicsDescriptionFlag.Elasticity) != 0)
            {
                if (body.Length - pos < 4) return null;
                elasticity = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos));
                pos += 4;
            }
            if ((physicsFlags & PhysicsDescriptionFlag.Translucency) != 0)
            {
                if (body.Length - pos < 4) return null;
                translucency = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos));
                pos += 4;
            }
            if ((physicsFlags & PhysicsDescriptionFlag.Velocity) != 0)
            {
                if (!TryReadVector3(body, ref pos, out Vector3 value)) return null;
                velocity = value;
            }
            if ((physicsFlags & PhysicsDescriptionFlag.Acceleration) != 0)
            {
                if (!TryReadVector3(body, ref pos, out Vector3 value)) return null;
                acceleration = value;
            }
            if ((physicsFlags & PhysicsDescriptionFlag.Omega) != 0)
            {
                if (!TryReadVector3(body, ref pos, out Vector3 value)) return null;
                angularVelocity = value;
            }
            if ((physicsFlags & PhysicsDescriptionFlag.DefaultScript) != 0)
            {
                if (body.Length - pos < 4) return null;
                defaultScriptType = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos));
                pos += 4;
            }
            if ((physicsFlags & PhysicsDescriptionFlag.DefaultScriptIntensity) != 0)
            {
                if (body.Length - pos < 4) return null;
                defaultScriptIntensity = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos));
                pos += 4;
            }

            if (body.Length - pos < 9 * 2) return null;
            var seqSpan = body.Slice(pos, 9 * 2);
            ushort positionSeq       = BinaryPrimitives.ReadUInt16LittleEndian(seqSpan.Slice(0 * 2));
            ushort movementSeq       = BinaryPrimitives.ReadUInt16LittleEndian(seqSpan.Slice(1 * 2));
            ushort stateSeq          = BinaryPrimitives.ReadUInt16LittleEndian(seqSpan.Slice(2 * 2));
            ushort vectorSeq         = BinaryPrimitives.ReadUInt16LittleEndian(seqSpan.Slice(3 * 2));
            ushort teleportSeq       = BinaryPrimitives.ReadUInt16LittleEndian(seqSpan.Slice(4 * 2));
            ushort serverControlSeq  = BinaryPrimitives.ReadUInt16LittleEndian(seqSpan.Slice(5 * 2));
            ushort forcePositionSeq  = BinaryPrimitives.ReadUInt16LittleEndian(seqSpan.Slice(6 * 2));
            ushort objDescSeq        = BinaryPrimitives.ReadUInt16LittleEndian(seqSpan.Slice(7 * 2));
            ushort instanceSeq       = BinaryPrimitives.ReadUInt16LittleEndian(seqSpan.Slice(8 * 2));
            pos += 9 * 2;
            AlignTo4(ref pos);
            if (pos > body.Length) return null;

            var timestamps = new PhysicsTimestamps(
                positionSeq, movementSeq, stateSeq, vectorSeq, teleportSeq,
                serverControlSeq, forcePositionSeq, objDescSeq, instanceSeq);
            physics = new PhysicsSpawnData(
                RawState: physicsState.Value,
                Position: position,
                Movement: movement,
                AnimationFrame: placementId,
                SetupTableId: setupTableId,
                MotionTableId: motionTableId,
                SoundTableId: soundTableId,
                PhysicsScriptTableId: physicsScriptTableId,
                Parent: parentGuid.HasValue && parentLocation.HasValue
                    ? new PhysicsAttachment(parentGuid.Value, parentLocation.Value)
                    : null,
                Children: children,
                Scale: objScale,
                Friction: friction,
                Elasticity: elasticity,
                Translucency: translucency,
                Velocity: velocity,
                Acceleration: acceleration,
                AngularVelocity: angularVelocity,
                DefaultScriptType: defaultScriptType,
                DefaultScriptIntensity: defaultScriptIntensity,
                Timestamps: timestamps);

            var desc = PublicWeenieDescParser.Parse(body, ref pos);

            return new Parsed(guid, position, setupTableId, animParts,
                textureChanges, subPalettes, basePaletteId, objScale, desc.Name, desc.ItemType, motionState, motionTableId,
                instanceSeq, teleportSeq, serverControlSeq, forcePositionSeq,
                movementSeq,
                PositionSequence: positionSeq,
                ParentGuid: parentGuid,
                ParentLocation: parentLocation,
                PlacementId: placementId,
                PhysicsState: physicsState,
                ObjectDescriptionFlags: desc.ObjectDescriptionFlags,
                Friction: friction,
                Elasticity: elasticity,
                IconId: desc.IconId,
                Useability: desc.Useability, UseRadius: desc.UseRadius, TargetType: desc.TargetType,
                IconOverlayId: desc.IconOverlayId, IconUnderlayId: desc.IconUnderlayId,
                UiEffects: desc.UiEffects,
                WeenieClassId: desc.WeenieClassId,
                Value: desc.Value, StackSize: desc.StackSize, StackSizeMax: desc.StackSizeMax,
                Burden: desc.Burden, ItemsCapacity: desc.ItemsCapacity, ContainersCapacity: desc.ContainersCapacity,
                HookItemTypes: desc.HookItemTypes, HookType: desc.HookType,
                ContainerId: desc.ContainerId, WielderId: desc.WielderId,
                ValidLocations: desc.ValidLocations, CurrentWieldedLocation: desc.CurrentWieldedLocation,
                Priority: desc.Priority, Structure: desc.Structure, MaxStructure: desc.MaxStructure,
                Workmanship: desc.Workmanship,
                RadarBlipColor: desc.RadarBlipColor, RadarBehavior: desc.RadarBehavior,
                CombatUse: desc.CombatUse,
                PluralName: desc.PluralName,
                PetOwnerId: desc.PetOwnerId,
                AmmoType: desc.AmmoType,
                SpellId: desc.SpellId,
                CooldownId: desc.CooldownId,
                CooldownDuration: desc.CooldownDuration,
                Physics: physics,
                MaterialType: desc.MaterialType,
                HouseOwnerId: desc.HouseOwnerId,
                MonarchId: desc.MonarchId,
                Restrictions: desc.Restrictions);
        }
        catch
        {
            return null;
        }
    }

    public static ModelData ReadModelData(ReadOnlySpan<byte> body, ref int pos)
    {
        if (body.Length - pos < 4) throw new FormatException("truncated ModelData header");
        byte _marker = body[pos]; pos += 1;
        byte subPaletteCount = body[pos]; pos += 1;
        byte textureChangeCount = body[pos]; pos += 1;
        byte animPartChangeCount = body[pos]; pos += 1;

        uint? basePaletteId = null;
        if (subPaletteCount > 0)
            basePaletteId = ReadPackedDwordOfKnownType(body, ref pos, PaletteTypePrefix);

        var subPalettes = subPaletteCount == 0
            ? (IReadOnlyList<SubPaletteSwap>)Array.Empty<SubPaletteSwap>()
            : new SubPaletteSwap[subPaletteCount];
        for (int i = 0; i < subPaletteCount; i++)
        {
            uint subPalId = ReadPackedDwordOfKnownType(body, ref pos, PaletteTypePrefix);
            if (body.Length - pos < 2) throw new FormatException("truncated SubPaletteSwap");
            byte offset = body[pos]; pos += 1;
            byte length = body[pos]; pos += 1;
            ((SubPaletteSwap[])subPalettes)[i] = new SubPaletteSwap(subPalId, offset, length);
        }

        var textureChanges = textureChangeCount == 0
            ? (IReadOnlyList<TextureChange>)Array.Empty<TextureChange>()
            : new TextureChange[textureChangeCount];
        for (int i = 0; i < textureChangeCount; i++)
        {
            if (body.Length - pos < 1) throw new FormatException("truncated TextureChange");
            byte partIndex = body[pos]; pos += 1;
            uint oldTex = ReadPackedDwordOfKnownType(body, ref pos, SurfaceTextureTypePrefix);
            uint newTex = ReadPackedDwordOfKnownType(body, ref pos, SurfaceTextureTypePrefix);
            ((TextureChange[])textureChanges)[i] = new TextureChange(partIndex, oldTex, newTex);
        }

        var animParts = animPartChangeCount == 0
            ? (IReadOnlyList<AnimPartChange>)Array.Empty<AnimPartChange>()
            : new AnimPartChange[animPartChangeCount];
        for (int i = 0; i < animPartChangeCount; i++)
        {
            if (body.Length - pos < 1) throw new FormatException("truncated AnimPartChange");
            byte partIndex = body[pos]; pos += 1;
            uint newModelId = ReadPackedDwordOfKnownType(body, ref pos, GfxObjTypePrefix);
            ((AnimPartChange[])animParts)[i] = new AnimPartChange(partIndex, newModelId);
        }

        AlignTo4(ref pos);
        return new ModelData(basePaletteId, subPalettes, textureChanges, animParts);
    }

    internal static uint ReadU32(ReadOnlySpan<byte> source, ref int pos)
    {
        if (source.Length - pos < 4) throw new FormatException("truncated u32");
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(pos));
        pos += 4;
        return v;
    }

    internal static string ReadString16L(ReadOnlySpan<byte> source, ref int pos)
    {
        if (source.Length - pos < 2) throw new FormatException("truncated String16L length");
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(pos));
        pos += 2;
        if (length > 1024) throw new FormatException($"String16L length {length} exceeds sanity limit");
        if (source.Length - pos < length) throw new FormatException("truncated String16L body");
        string result = System.Text.Encoding.GetEncoding(1252).GetString(source.Slice(pos, length));
        pos += length;
        int recordSize = 2 + length;
        int padding = (4 - (recordSize & 3)) & 3;
        pos += padding;
        return result;
    }

    internal static uint ReadPackedDword(ReadOnlySpan<byte> source, ref int pos)
    {
        if (source.Length - pos < 2) throw new FormatException("truncated PackedDword");
        ushort first = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(pos));
        pos += 2;
        if ((first & 0x8000) == 0)
            return first;

        // Extended form: first holds the HIGH 16 bits with top bit as marker,
        // next u16 holds the LOW 16 bits. Strip the marker bit from the high half.
        if (source.Length - pos < 2) throw new FormatException("truncated PackedDword ext");
        ushort second = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(pos));
        pos += 2;
        uint high = (uint)(first & 0x7FFF);
        return (high << 16) | second;
    }

    /// <summary>
    /// Read a PackedDword that was written via <c>WritePackedDwordOfKnownType</c>.
    /// That writer strips the <paramref name="knownType"/> prefix before
    /// packing if the value had it set, so the reader must OR it back in to
    /// recover the original dat id. The zero sentinel is preserved as-is
    /// (a 0 means "no value" and must not be turned into <c>knownType</c>).
    /// </summary>
    internal static uint ReadPackedDwordOfKnownType(ReadOnlySpan<byte> source, ref int pos, uint knownType)
    {
        uint packed = ReadPackedDword(source, ref pos);
        return packed == 0 ? 0 : (packed | knownType);
    }

    internal static void AlignTo4(ref int pos)
    {
        int padding = (4 - (pos & 3)) & 3;
        pos += padding;
    }

    private static ServerMotionState? TryParseMovementData(ReadOnlySpan<byte> mv)
    {
        try
        {
            int p = 0;
            if (mv.Length < 4) return null;
            byte movementType = mv[p]; p += 1;
            byte _motionFlags = mv[p]; p += 1;
            ushort currentStyle = BinaryPrimitives.ReadUInt16LittleEndian(mv.Slice(p));
            p += 2;

            ushort? forwardCommand = null;
            float? forwardSpeed = null;
            ushort? sidestepCommand = null;
            float? sidestepSpeed = null;
            ushort? turnCommand = null;
            float? turnSpeed = null;
            uint? moveToParameters = null;
            float? moveToSpeed = null;
            float? moveToRunRate = null;
            List<MotionItem>? commands = null;

            if (movementType == 0)
            {
                if (mv.Length - p < 4) return new ServerMotionState(currentStyle, null);
                uint packed = BinaryPrimitives.ReadUInt32LittleEndian(mv.Slice(p));
                p += 4;
                uint flags = packed & 0x7Fu;  // MovementStateFlag bits live in low 7 bits
                uint numCommands = packed >> 7;


                if ((flags & 0x1u) != 0)
                {
                    if (mv.Length - p < 2) return new ServerMotionState(currentStyle, null);
                    currentStyle = BinaryPrimitives.ReadUInt16LittleEndian(mv.Slice(p));
                    p += 2;
                }
                if ((flags & 0x2u) != 0)
                {
                    if (mv.Length - p < 2) return new ServerMotionState(currentStyle, null);
                    forwardCommand = BinaryPrimitives.ReadUInt16LittleEndian(mv.Slice(p));
                    p += 2;
                }
                // SideStepCommand (bit 0x8, ushort)
                if ((flags & 0x8u) != 0)
                {
                    if (mv.Length - p < 2) goto done;
                    sidestepCommand = BinaryPrimitives.ReadUInt16LittleEndian(mv.Slice(p));
                    p += 2;
                }
                // TurnCommand (bit 0x20, ushort)
                if ((flags & 0x20u) != 0)
                {
                    if (mv.Length - p < 2) goto done;
                    turnCommand = BinaryPrimitives.ReadUInt16LittleEndian(mv.Slice(p));
                    p += 2;
                }
                // ForwardSpeed (bit 0x4, float)
                if ((flags & 0x4u) != 0)
                {
                    if (mv.Length - p < 4) goto done;
                    forwardSpeed = BinaryPrimitives.ReadSingleLittleEndian(mv.Slice(p));
                    p += 4;
                }
                // SideStepSpeed (bit 0x10, float)
                if ((flags & 0x10u) != 0)
                {
                    if (mv.Length - p < 4) goto done;
                    sidestepSpeed = BinaryPrimitives.ReadSingleLittleEndian(mv.Slice(p));
                    p += 4;
                }
                // TurnSpeed (bit 0x40, float)
                if ((flags & 0x40u) != 0)
                {
                    if (mv.Length - p < 4) goto done;
                    turnSpeed = BinaryPrimitives.ReadSingleLittleEndian(mv.Slice(p));
                    p += 4;
                }

                if (numCommands > 0 && numCommands < 1024)
                {
                    commands = new List<MotionItem>((int)numCommands);
                    for (int i = 0; i < numCommands; i++)
                    {
                        if (mv.Length - p < 8) break;
                        ushort cmd = BinaryPrimitives.ReadUInt16LittleEndian(mv.Slice(p));
                        ushort seq = BinaryPrimitives.ReadUInt16LittleEndian(mv.Slice(p + 2));
                        float speed = BinaryPrimitives.ReadSingleLittleEndian(mv.Slice(p + 4));
                        p += 8;
                        commands.Add(new MotionItem(cmd, seq, speed));
                    }
                }
                done:;
            }
            else if (movementType is 6 or 7)
            {
                TryParseMoveToPayload(
                    mv,
                    p,
                    movementType,
                    out moveToParameters,
                    out moveToSpeed,
                    out moveToRunRate);
            }

            return new ServerMotionState(
                currentStyle, forwardCommand, forwardSpeed, commands,
                sidestepCommand, sidestepSpeed, turnCommand, turnSpeed,
                movementType,
                moveToParameters,
                moveToSpeed,
                moveToRunRate);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseMoveToPayload(
        ReadOnlySpan<byte> body,
        int pos,
        byte movementType,
        out uint? movementParameters,
        out float? speed,
        out float? runRate)
    {
        movementParameters = null;
        speed = null;
        runRate = null;

        if (movementType == 6)
        {
            if (body.Length - pos < 4) return false;
            pos += 4; // target guid
        }

        if (body.Length - pos < 16 + 28 + 4) return false;
        pos += 16; // Origin

        movementParameters = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(pos));
        pos += 4;
        pos += 4; // distanceToObject
        pos += 4; // minDistance
        pos += 4; // failDistance
        speed = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos));
        pos += 4;
        pos += 4; // walkRunThreshold
        pos += 4; // desiredHeading
        runRate = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos));
        return true;
    }

    private static bool TryReadVector3(ReadOnlySpan<byte> body, ref int pos, out Vector3 value)
    {
        if (body.Length - pos < 12)
        {
            value = default;
            return false;
        }

        value = new Vector3(
            BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos)),
            BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos + 4)),
            BinaryPrimitives.ReadSingleLittleEndian(body.Slice(pos + 8)));
        pos += 12;
        return true;
    }
}
