using Chorizite.Core.Lib;
using Chorizite.Core.Render.Enums;
using AcDream.Core.Meshing;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using BoundingBox = Chorizite.Core.Lib.BoundingBox;

namespace AcDream.Content;

/// <summary>
/// Vertex format for scenery mesh rendering: position, normal, UV.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct VertexPositionNormalTexture {
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 UV;

    public static int Size => 8 * sizeof(float); // 3+3+2 = 8 floats = 32 bytes

    public VertexPositionNormalTexture(Vector3 position, Vector3 normal, Vector2 uv) {
        Position = position;
        Normal = normal;
        UV = uv;
    }
}

public struct StagedEmitter {
    public ParticleEmitter Emitter;
    public uint PartIndex;
    public Matrix4x4 Offset;
}

public class ObjectMeshData {
    private long _estimatedUploadBytes = -1;
    public ulong ObjectId { get; set; }
    public bool IsSetup { get; set; }
    public VertexPositionNormalTexture[] Vertices { get; set; } = Array.Empty<VertexPositionNormalTexture>();
    public List<MeshBatchData> Batches { get; set; } = new();

    public int UploadAttempts;

    public ObjectMeshData? EnvCellGeometry { get; set; }

    /// <summary>For Setup objects: parts with their local transforms.</summary>
    public List<(ulong GfxObjId, Matrix4x4 Transform)> SetupParts { get; set; } = new();

    /// <summary>Particle emitters from physics scripts.</summary>
    public List<StagedEmitter> ParticleEmitters { get; set; } = new();

    /// <summary>Per-format texture atlas data (to be uploaded to GPU on main thread).</summary>
    public Dictionary<(int Width, int Height, TextureFormat Format), List<TextureBatchData>> TextureBatches { get; set; } = new();

    /// <summary>Local bounding box.</summary>
    public BoundingBox BoundingBox { get; set; }

    public Vector3 SortCenter { get; set; }

    /// <summary>DataID of a simpler GfxObj to use at long distance / low quality, or GfxObjDegradeInfo.</summary>
    public uint DIDDegrade { get; set; }

    /// <summary>Sphere used for mouse selection.</summary>
    public Sphere? SelectionSphere { get; set; }

    /// <summary>Edge line vertices for Environment wireframe rendering.</summary>
    public Vector3[] EdgeLines { get; set; } = Array.Empty<Vector3>();

    public long GetEstimatedUploadBytes()
    {
        long cached = System.Threading.Volatile.Read(ref _estimatedUploadBytes);
        if (cached >= 0)
            return cached;

        long bytes = IsSetup ? 1024L : 0L;
        bytes = checked(bytes + (long)Vertices.Length * VertexPositionNormalTexture.Size);
        foreach (List<TextureBatchData> batches in TextureBatches.Values)
        {
            foreach (TextureBatchData batch in batches)
            {
                bytes = checked(bytes + batch.TextureData.LongLength);
                bytes = checked(bytes + (long)batch.Indices.Count * sizeof(ushort));
            }
        }
        if (EnvCellGeometry is not null)
            bytes = checked(bytes + EnvCellGeometry.GetEstimatedUploadBytes());

        System.Threading.Interlocked.CompareExchange(ref _estimatedUploadBytes, bytes, -1);
        return System.Threading.Volatile.Read(ref _estimatedUploadBytes);
    }
}

/// <summary>
/// CPU-side data for a single rendering batch (indices + texture reference).
/// </summary>
public class MeshBatchData {
    public ushort[] Indices { get; set; } = Array.Empty<ushort>();
    public (int Width, int Height, TextureFormat Format) TextureFormat { get; set; }
    public TextureKey TextureKey { get; set; }
    public int TextureIndex { get; set; }
    public byte[] TextureData { get; set; } = Array.Empty<byte>();
    public UploadPixelFormat? UploadPixelFormat { get; set; }
    public UploadPixelType? UploadPixelType { get; set; }
    public DatReaderWriter.Enums.CullMode CullMode { get; set; }
}

public class TextureBatchData {
    public TextureKey Key { get; set; }
    public byte[] TextureData { get; set; } = Array.Empty<byte>();
    public UploadPixelFormat? UploadPixelFormat { get; set; }
    public UploadPixelType? UploadPixelType { get; set; }
    public List<ushort> Indices { get; set; } = new();
    public DatReaderWriter.Enums.CullMode CullMode { get; set; }
    public TranslucencyKind Translucency { get; set; } =
        TranslucencyKind.Opaque;
    public bool IsTransparent { get; set; }
    public bool IsAdditive { get; set; }
    public bool HasWrappingUVs { get; set; }

    public float SurfaceOpacity { get; set; } = 1f;

    public RetailSetSurfaceMaterialState MaterialState { get; set; } =
        RetailSetSurfaceMaterialState.Opaque;

    public int SourceSurfaceIndex { get; set; } = -1;

    public byte RetailSurfaceMask { get; set; }

    public uint RawSurfaceType { get; set; }

    public bool IsCellShell { get; set; }
}

public static class CellSurfaceSubsets {
    public static IEnumerable<TextureBatchData> InAscendingSurfaceOrder(ObjectMeshData mesh) =>
        mesh.TextureBatches.Values
            .SelectMany(batches => batches)
            .Where(batch => batch.IsCellShell)
            .OrderBy(batch => batch.SourceSurfaceIndex);
}
