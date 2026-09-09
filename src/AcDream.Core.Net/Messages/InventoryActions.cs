using System.Buffers.Binary;
using AcDream.Core.Items;

namespace AcDream.Core.Net.Messages;

/// <summary>
/// Outbound inventory-manipulation GameActions: stack merge/split, give,
/// drop, shortcuts. All ride in the <c>0xF7B1</c> GameAction envelope.
///
/// <para>
/// References: r08 §3 rows 0x0054-0x0056 / 0x019B-0x019D / 0x00CD.
/// </para>
/// </summary>
public static class InventoryActions
{
    public const uint GameActionEnvelope = 0xF7B1u;

    public const uint StackableMergeOpcode          = 0x0054u;
    public const uint StackableSplitToContainerOpcode = 0x0055u;
    public const uint StackableSplitTo3DOpcode      = 0x0056u;
    public const uint StackableSplitToWieldOpcode   = 0x019Bu;
    public const uint GiveObjectRequestOpcode       = 0x00CDu;
    public const uint AddShortcutOpcode             = 0x019Cu;
    public const uint RemoveShortcutOpcode          = 0x019Du;
    public const uint TeleToPoiOpcode               = 0x00B1u;
    public const uint GetAndWieldItemOpcode         = 0x001Au;
    public const uint DropItemOpcode                = 0x001Bu;
    public const uint NoLongerViewingContentsOpcode = 0x0195u;
    public const uint SetInscriptionOpcode           = 0x00BFu;
    public const uint CreateTinkeringToolOpcode      = 0x027Du;

    public static byte[] BuildStackableMerge(uint seq, uint mergeFromGuid, uint mergeToGuid, uint amount)
    {
        byte[] body = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  seq);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  StackableMergeOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), mergeFromGuid);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), mergeToGuid);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(20), amount);
        return body;
    }

    public static byte[] BuildStackableSplitToContainer(
        uint seq, uint stackGuid, uint containerGuid, uint placement, uint amount)
    {
        byte[] body = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  seq);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  StackableSplitToContainerOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), stackGuid);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), containerGuid);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(20), placement);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(24), amount);
        return body;
    }

    /// <summary>Split N items off a stack and drop them on the ground.</summary>
    public static byte[] BuildStackableSplitTo3D(uint seq, uint stackGuid, uint amount)
    {
        byte[] body = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  seq);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  StackableSplitTo3DOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), stackGuid);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), amount);
        return body;
    }

    /// <summary>Split N items off a stack into an equip slot.</summary>
    public static byte[] BuildStackableSplitToWield(
        uint seq, uint stackGuid, uint equipLocation, uint amount)
    {
        byte[] body = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  seq);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  StackableSplitToWieldOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), stackGuid);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), equipLocation);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(20), amount);
        return body;
    }

    public static byte[] BuildGiveObjectRequest(
        uint seq, uint targetGuid, uint itemGuid, uint amount)
    {
        byte[] body = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  seq);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  GiveObjectRequestOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), targetGuid);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), itemGuid);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(20), amount);
        return body;
    }

    public static byte[] BuildAddShortcut(uint seq, ShortcutEntry entry)
    {
        byte[] body = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  seq);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  AddShortcutOpcode);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(12),  entry.Index);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), entry.ObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(20), entry.SpellId);
        return body;
    }

    /// <summary>Unpin a quickbar slot.</summary>
    public static byte[] BuildRemoveShortcut(uint seq, uint slotIndex)
    {
        byte[] body = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  seq);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  RemoveShortcutOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), slotIndex);
        return body;
    }

    /// <summary>Teleport to a Point of Interest (quest-driven recall).</summary>
    public static byte[] BuildTeleToPoi(uint seq, uint poiId)
    {
        byte[] body = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  seq);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  TeleToPoiOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), poiId);
        return body;
    }

    public static byte[] BuildDropItem(uint seq, uint itemGuid)
    {
        byte[] body = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  seq);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  DropItemOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), itemGuid);
        return body;
    }

    public static byte[] BuildGetAndWieldItem(uint seq, uint itemGuid, uint equipMask)
    {
        byte[] body = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  seq);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  GetAndWieldItemOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), itemGuid);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), equipMask);
        return body;
    }

    public static byte[] BuildNoLongerViewingContents(uint seq, uint containerGuid)
    {
        byte[] body = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  seq);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  NoLongerViewingContentsOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), containerGuid);
        return body;
    }

    public static byte[] BuildSetInscription(
        uint seq,
        uint itemGuid,
        string inscription)
    {
        ArgumentNullException.ThrowIfNull(inscription);
        byte[] text = Encodings.Windows1252.GetBytes(inscription);
        if (text.Length > ushort.MaxValue)
            throw new ArgumentException(
                "Inscription is too long for String16L.",
                nameof(inscription));

        int stringRecordLength = (2 + text.Length + 3) & ~3;
        byte[] body = new byte[16 + stringRecordLength];
        BinaryPrimitives.WriteUInt32LittleEndian(body, GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), seq);
        BinaryPrimitives.WriteUInt32LittleEndian(
            body.AsSpan(8),
            SetInscriptionOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), itemGuid);
        BinaryPrimitives.WriteUInt16LittleEndian(
            body.AsSpan(16),
            (ushort)text.Length);
        text.CopyTo(body, 18);
        return body;
    }

    public static byte[] BuildCreateTinkeringTool(
        uint seq,
        uint toolGuid,
        IReadOnlyList<uint> itemGuids)
    {
        ArgumentNullException.ThrowIfNull(itemGuids);
        if (toolGuid == 0u)
            throw new ArgumentOutOfRangeException(nameof(toolGuid));
        if (itemGuids.Count == 0)
            throw new ArgumentException(
                "At least one item is required for salvage.",
                nameof(itemGuids));

        byte[] body = new byte[20 + (itemGuids.Count * sizeof(uint))];
        BinaryPrimitives.WriteUInt32LittleEndian(body, GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), seq);
        BinaryPrimitives.WriteUInt32LittleEndian(
            body.AsSpan(8),
            CreateTinkeringToolOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), toolGuid);
        BinaryPrimitives.WriteUInt32LittleEndian(
            body.AsSpan(16),
            checked((uint)itemGuids.Count));
        for (int index = 0; index < itemGuids.Count; index++)
        {
            uint itemGuid = itemGuids[index];
            if (itemGuid == 0u)
                throw new ArgumentException(
                    "Salvage item ids must be non-zero.",
                    nameof(itemGuids));
            BinaryPrimitives.WriteUInt32LittleEndian(
                body.AsSpan(20 + (index * sizeof(uint))),
                itemGuid);
        }
        return body;
    }
}
