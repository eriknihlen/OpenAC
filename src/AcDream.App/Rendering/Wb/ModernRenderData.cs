using System.Runtime.InteropServices;

namespace AcDream.App.Rendering.Wb {
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct ModernBatchData {
        public uint TextureTableIndex; // 4 bytes — slot into the binding=9 handle table
        public float SurfaceOpacity;   // 4 bytes — authored material alpha
        public uint TextureIndex;      // 4 bytes — layer within the texture array
        public uint Flags;             // 4 bytes — reserved, matches mesh_modern.vert's BatchData.flags
    }

}
