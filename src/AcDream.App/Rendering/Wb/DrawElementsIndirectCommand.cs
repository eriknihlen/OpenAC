using System.Runtime.InteropServices;

namespace AcDream.App.Rendering.Wb;

/// <summary>
/// Layout matches what <c>glMultiDrawElementsIndirect</c> expects.
/// Total size 20 bytes; arrays are typically uploaded with stride = sizeof(this).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct DrawElementsIndirectCommand
{
    public uint Count;
    public uint InstanceCount;  // number of instances
    public uint FirstIndex;     // offset into IBO, in indices
    public int  BaseVertex;     // vertex offset into VBO
    public uint BaseInstance;   // first instance ID (offsets per-instance attribs / SSBO read)
}
