namespace AcDream.Content.Pak;

public enum PakAssetType : byte {
    GfxObjMesh = 1,
    SetupMesh = 2,
    EnvCellMesh = 3,
    GfxObjCollision = 4,
    SetupCollision = 5,
    CellStructureCollision = 6,
    EnvCellTopology = 7,
    TexturePayload = 8,
}

public static class PakKey {
    public static ulong Compose(PakAssetType type, uint fileId) {
        return ((ulong)type << 56) | ((ulong)fileId << 24);
    }

    public static (PakAssetType Type, uint FileId) Decompose(ulong key) {
        var type = (PakAssetType)(byte)(key >> 56);
        var fileId = (uint)((key >> 24) & 0xFFFFFFFFu);
        return (type, fileId);
    }

    public static ulong ComposeOpaque(PakAssetType type, ulong payloadId) {
        if ((payloadId & 0xFF00_0000_0000_0000ul) != 0)
            throw new ArgumentOutOfRangeException(nameof(payloadId));
        return ((ulong)type << 56) | payloadId;
    }
}
