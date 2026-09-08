using DatReaderWriter.Enums;
using System;

namespace AcDream.Content;

public struct TextureKey : IEquatable<TextureKey> {
    public uint SurfaceId;
    public uint PaletteId;
    public StipplingType Stippling;
    public bool IsSolid;

    public bool Equals(TextureKey other) {
        return SurfaceId == other.SurfaceId &&
               PaletteId == other.PaletteId &&
               Stippling == other.Stippling &&
               IsSolid == other.IsSolid;
    }

    public override bool Equals(object? obj) {
        return obj is TextureKey other && Equals(other);
    }

    public override int GetHashCode() {
        return HashCode.Combine(SurfaceId, PaletteId, Stippling, IsSolid);
    }
}
