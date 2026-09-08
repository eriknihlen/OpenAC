using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Chorizite.Core.Render.Enums;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;
using BoundingBox = Chorizite.Core.Lib.BoundingBox;
using CullMode = DatReaderWriter.Enums.CullMode;

namespace AcDream.Content.Pak;

public static class ObjectMeshDataSerializer {
    public static void Write(ObjectMeshData data, Stream stream) {
        using var bw = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        WriteObjectMeshData(bw, data, externalTextures: null);
    }

    public static void WriteExternalTextures(
        ObjectMeshData data,
        Stream stream,
        Func<TextureKey, byte[], ulong> registerTexture) {
        ArgumentNullException.ThrowIfNull(registerTexture);
        using var bw = new BinaryWriter(
            stream,
            System.Text.Encoding.UTF8,
            leaveOpen: true);
        WriteObjectMeshData(bw, data, registerTexture);
    }

    public static ObjectMeshData Read(byte[] bytes) {
        using var ms = new MemoryStream(bytes, writable: false);
        using var br = new BinaryReader(ms, System.Text.Encoding.UTF8, leaveOpen: true);
        return ReadObjectMeshData(br, externalTextures: null);
    }

    public static ObjectMeshData Read(ReadOnlySpan<byte> bytes) => Read(bytes.ToArray());

    public static ObjectMeshData ReadExternalTextures(
        byte[] bytes,
        Func<ulong, byte[]> resolveTexture) {
        ArgumentNullException.ThrowIfNull(resolveTexture);
        using var ms = new MemoryStream(bytes, writable: false);
        using var br = new BinaryReader(
            ms,
            System.Text.Encoding.UTF8,
            leaveOpen: true);
        return ReadObjectMeshData(br, resolveTexture);
    }

    // ---- ObjectMeshData -----------------------------------------------------

    private static void WriteObjectMeshData(
        BinaryWriter w,
        ObjectMeshData data,
        Func<TextureKey, byte[], ulong>? externalTextures) {
        w.Write(data.ObjectId);
        w.Write(data.IsSetup);

        WriteVertexArray(w, data.Vertices);

        w.Write(data.Batches.Count);
        foreach (var batch in data.Batches)
            WriteMeshBatchData(w, batch, externalTextures);

        w.Write(data.UploadAttempts);

        // EnvCellGeometry: recursive nested block.
        w.Write(data.EnvCellGeometry is not null);
        if (data.EnvCellGeometry is not null)
            WriteObjectMeshData(w, data.EnvCellGeometry, externalTextures);

        w.Write(data.SetupParts.Count);
        foreach (var (gfxObjId, transform) in data.SetupParts) {
            w.Write(gfxObjId);
            WriteMatrix4x4(w, transform);
        }

        w.Write(data.ParticleEmitters.Count);
        foreach (var emitter in data.ParticleEmitters) WriteStagedEmitter(w, emitter);

        WriteTextureBatches(w, data.TextureBatches, externalTextures);

        WriteBoundingBox(w, data.BoundingBox);
        WriteVector3(w, data.SortCenter);
        w.Write(data.DIDDegrade);

        w.Write(data.SelectionSphere is not null);
        if (data.SelectionSphere is not null) WriteSphere(w, data.SelectionSphere);

        WriteVector3Array(w, data.EdgeLines);
    }

    private static ObjectMeshData ReadObjectMeshData(
        BinaryReader r,
        Func<ulong, byte[]>? externalTextures) {
        var data = new ObjectMeshData {
            ObjectId = r.ReadUInt64(),
            IsSetup = r.ReadBoolean(),
        };

        data.Vertices = ReadVertexArray(r);

        int batchCount = r.ReadInt32();
        var batches = new List<MeshBatchData>(batchCount);
        for (int i = 0; i < batchCount; i++)
            batches.Add(ReadMeshBatchData(r, externalTextures));
        data.Batches = batches;

        data.UploadAttempts = r.ReadInt32();

        bool hasEnvCellGeometry = r.ReadBoolean();
        data.EnvCellGeometry = hasEnvCellGeometry
            ? ReadObjectMeshData(r, externalTextures)
            : null;

        int setupPartCount = r.ReadInt32();
        var setupParts = new List<(ulong GfxObjId, Matrix4x4 Transform)>(setupPartCount);
        for (int i = 0; i < setupPartCount; i++) {
            ulong gfxObjId = r.ReadUInt64();
            var transform = ReadMatrix4x4(r);
            setupParts.Add((gfxObjId, transform));
        }
        data.SetupParts = setupParts;

        int emitterCount = r.ReadInt32();
        var emitters = new List<StagedEmitter>(emitterCount);
        for (int i = 0; i < emitterCount; i++) emitters.Add(ReadStagedEmitter(r));
        data.ParticleEmitters = emitters;

        data.TextureBatches = ReadTextureBatches(r, externalTextures);

        data.BoundingBox = ReadBoundingBox(r);
        data.SortCenter = ReadVector3(r);
        data.DIDDegrade = r.ReadUInt32();

        bool hasSelectionSphere = r.ReadBoolean();
        data.SelectionSphere = hasSelectionSphere ? ReadSphere(r) : null;

        data.EdgeLines = ReadVector3Array(r);

        return data;
    }

    // ---- MeshBatchData -------------------------------------------------------

    private static void WriteMeshBatchData(
        BinaryWriter w,
        MeshBatchData batch,
        Func<TextureKey, byte[], ulong>? externalTextures) {
        WriteUInt16Array(w, batch.Indices);
        WriteTextureFormatTuple(w, batch.TextureFormat);
        WriteTextureKey(w, batch.TextureKey);
        w.Write(batch.TextureIndex);
        WriteTexturePayload(
            w,
            batch.TextureKey,
            batch.TextureData,
            externalTextures);
        WriteNullableInt32Enum(w, batch.UploadPixelFormat.HasValue, batch.UploadPixelFormat is { } upf ? (int)upf : 0);
        WriteNullableInt32Enum(w, batch.UploadPixelType.HasValue, batch.UploadPixelType is { } upt ? (int)upt : 0);
        w.Write((int)batch.CullMode);
    }

    private static MeshBatchData ReadMeshBatchData(
        BinaryReader r,
        Func<ulong, byte[]>? externalTextures) {
        ushort[] indices = ReadUInt16Array(r);
        (int Width, int Height, TextureFormat Format) format =
            ReadTextureFormatTuple(r);
        TextureKey key = ReadTextureKey(r);
        var batch = new MeshBatchData {
            Indices = indices,
            TextureFormat = format,
            TextureKey = key,
            TextureIndex = r.ReadInt32(),
            TextureData = ReadTexturePayload(r, externalTextures),
        };
        batch.UploadPixelFormat = ReadNullableInt32Enum(r, v => (UploadPixelFormat)v);
        batch.UploadPixelType = ReadNullableInt32Enum(r, v => (UploadPixelType)v);
        batch.CullMode = (CullMode)r.ReadInt32();
        return batch;
    }

    // ---- TextureBatchData / TextureBatches dictionary -------------------------

    private static void WriteTextureBatchData(
        BinaryWriter w,
        TextureBatchData batch,
        Func<TextureKey, byte[], ulong>? externalTextures) {
        WriteTextureKey(w, batch.Key);
        WriteTexturePayload(w, batch.Key, batch.TextureData, externalTextures);
        WriteNullableInt32Enum(w, batch.UploadPixelFormat.HasValue, batch.UploadPixelFormat is { } upf ? (int)upf : 0);
        WriteNullableInt32Enum(w, batch.UploadPixelType.HasValue, batch.UploadPixelType is { } upt ? (int)upt : 0);
        WriteUInt16List(w, batch.Indices);
        w.Write((int)batch.CullMode);
        w.Write((int)batch.Translucency);
        w.Write(batch.IsTransparent);
        w.Write(batch.IsAdditive);
        w.Write(batch.HasWrappingUVs);
        w.Write(batch.SourceSurfaceIndex);
        w.Write(batch.RetailSurfaceMask);
        w.Write(batch.RawSurfaceType);
        w.Write(batch.IsCellShell);
        // OVERHAUL S5-c4: recipe 9 appended authored material opacity. Recipe
        // 10 appends the extraction-resolved SetSurface state byte. Recipe
        // identity rejects older payloads before this serializer is entered.
        w.Write(batch.SurfaceOpacity);
        w.Write(batch.MaterialState.ToPackedByte());
    }

    private static TextureBatchData ReadTextureBatchData(
        BinaryReader r,
        Func<ulong, byte[]>? externalTextures) {
        TextureKey key = ReadTextureKey(r);
        var batch = new TextureBatchData {
            Key = key,
            TextureData = ReadTexturePayload(r, externalTextures),
        };
        batch.UploadPixelFormat = ReadNullableInt32Enum(r, v => (UploadPixelFormat)v);
        batch.UploadPixelType = ReadNullableInt32Enum(r, v => (UploadPixelType)v);
        batch.Indices = ReadUInt16List(r);
        batch.CullMode = (CullMode)r.ReadInt32();
        batch.Translucency =
            (AcDream.Core.Meshing.TranslucencyKind)r.ReadInt32();
        batch.IsTransparent = r.ReadBoolean();
        batch.IsAdditive = r.ReadBoolean();
        batch.HasWrappingUVs = r.ReadBoolean();
        batch.SourceSurfaceIndex = r.ReadInt32();
        batch.RetailSurfaceMask = r.ReadByte();
        batch.RawSurfaceType = r.ReadUInt32();
        batch.IsCellShell = r.ReadBoolean();
        batch.SurfaceOpacity = r.ReadSingle();
        batch.MaterialState =
            AcDream.Core.Meshing.RetailSetSurfaceMaterialState.FromPackedByte(
                r.ReadByte());
        return batch;
    }

    /// <summary>
    /// Writes the TextureBatches dictionary sorted ascending by the key tuple
    /// (Width, Height, Format) — REQUIRED for byte-reproducible bakes
    /// independent of Dictionary iteration/insertion order.
    /// </summary>
    private static void WriteTextureBatches(
        BinaryWriter w,
        Dictionary<(int Width, int Height, TextureFormat Format), List<TextureBatchData>> batches,
        Func<TextureKey, byte[], ulong>? externalTextures) {
        var sortedKeys = batches.Keys
            .OrderBy(k => k.Width)
            .ThenBy(k => k.Height)
            .ThenBy(k => (int)k.Format)
            .ToList();

        w.Write(sortedKeys.Count);
        foreach (var key in sortedKeys) {
            w.Write(key.Width);
            w.Write(key.Height);
            w.Write((int)key.Format);

            var list = batches[key];
            w.Write(list.Count);
            foreach (var item in list)
                WriteTextureBatchData(w, item, externalTextures);
        }
    }

    private static Dictionary<(int Width, int Height, TextureFormat Format), List<TextureBatchData>> ReadTextureBatches(
        BinaryReader r,
        Func<ulong, byte[]>? externalTextures) {
        int groupCount = r.ReadInt32();
        var result = new Dictionary<(int Width, int Height, TextureFormat Format), List<TextureBatchData>>(groupCount);
        for (int i = 0; i < groupCount; i++) {
            int width = r.ReadInt32();
            int height = r.ReadInt32();
            var format = (TextureFormat)r.ReadInt32();

            int listCount = r.ReadInt32();
            var list = new List<TextureBatchData>(listCount);
            for (int j = 0; j < listCount; j++)
                list.Add(ReadTextureBatchData(r, externalTextures));

            result[(width, height, format)] = list;
        }
        return result;
    }

    private static void WriteTexturePayload(
        BinaryWriter writer,
        TextureKey key,
        byte[] bytes,
        Func<TextureKey, byte[], ulong>? externalTextures) {
        if (externalTextures is null)
            WriteByteArray(writer, bytes);
        else
            writer.Write(externalTextures(key, bytes));
    }

    private static byte[] ReadTexturePayload(
        BinaryReader reader,
        Func<ulong, byte[]>? externalTextures) =>
        externalTextures is null
            ? ReadByteArray(reader)
            : externalTextures(reader.ReadUInt64());

    // ---- StagedEmitter / ParticleEmitter (DBObj) ------------------------------

    private static void WriteStagedEmitter(BinaryWriter w, StagedEmitter emitter) {
        w.Write(emitter.PartIndex);
        WriteMatrix4x4(w, emitter.Offset);
        w.Write(emitter.Emitter is not null);
        if (emitter.Emitter is not null) WriteParticleEmitter(w, emitter.Emitter);
    }

    private static StagedEmitter ReadStagedEmitter(BinaryReader r) {
        uint partIndex = r.ReadUInt32();
        var offset = ReadMatrix4x4(r);
        bool hasEmitter = r.ReadBoolean();
        var pe = hasEmitter ? ReadParticleEmitter(r) : null;
        return new StagedEmitter {
            PartIndex = partIndex,
            Offset = offset,
            Emitter = pe!,
        };
    }

    private static void WriteParticleEmitter(BinaryWriter w, ParticleEmitter pe) {
        w.Write(pe.Id);
        w.Write(pe.DataCategory);
        w.Write(pe.Unknown);
        w.Write((int)pe.EmitterType);
        w.Write((int)pe.ParticleType);
        w.Write(pe.GfxObjId.DataId);
        w.Write(pe.HwGfxObjId.DataId);
        w.Write(pe.Birthrate);
        w.Write(pe.MaxParticles);
        w.Write(pe.InitialParticles);
        w.Write(pe.TotalParticles);
        w.Write(pe.TotalSeconds);
        w.Write(pe.Lifespan);
        w.Write(pe.LifespanRand);
        WriteVector3(w, pe.OffsetDir);
        w.Write(pe.MinOffset);
        w.Write(pe.MaxOffset);
        WriteVector3(w, pe.A);
        w.Write(pe.MinA);
        w.Write(pe.MaxA);
        WriteVector3(w, pe.B);
        w.Write(pe.MinB);
        w.Write(pe.MaxB);
        WriteVector3(w, pe.C);
        w.Write(pe.MinC);
        w.Write(pe.MaxC);
        w.Write(pe.StartScale);
        w.Write(pe.FinalScale);
        w.Write(pe.ScaleRand);
        w.Write(pe.StartTrans);
        w.Write(pe.FinalTrans);
        w.Write(pe.TransRand);
        w.Write(pe.IsParentLocal);
    }

    private static ParticleEmitter ReadParticleEmitter(BinaryReader r) {
        var pe = new ParticleEmitter {
            Id = r.ReadUInt32(),
            DataCategory = r.ReadUInt32(),
        };
        pe.Unknown = r.ReadUInt32();
        pe.EmitterType = (DatReaderWriter.Enums.EmitterType)r.ReadInt32();
        pe.ParticleType = (DatReaderWriter.Enums.ParticleType)r.ReadInt32();
        pe.GfxObjId = new QualifiedDataId<GfxObj> { DataId = r.ReadUInt32() };
        pe.HwGfxObjId = new QualifiedDataId<GfxObj> { DataId = r.ReadUInt32() };
        pe.Birthrate = r.ReadDouble();
        pe.MaxParticles = r.ReadInt32();
        pe.InitialParticles = r.ReadInt32();
        pe.TotalParticles = r.ReadInt32();
        pe.TotalSeconds = r.ReadDouble();
        pe.Lifespan = r.ReadDouble();
        pe.LifespanRand = r.ReadDouble();
        pe.OffsetDir = ReadVector3(r);
        pe.MinOffset = r.ReadSingle();
        pe.MaxOffset = r.ReadSingle();
        pe.A = ReadVector3(r);
        pe.MinA = r.ReadSingle();
        pe.MaxA = r.ReadSingle();
        pe.B = ReadVector3(r);
        pe.MinB = r.ReadSingle();
        pe.MaxB = r.ReadSingle();
        pe.C = ReadVector3(r);
        pe.MinC = r.ReadSingle();
        pe.MaxC = r.ReadSingle();
        pe.StartScale = r.ReadSingle();
        pe.FinalScale = r.ReadSingle();
        pe.ScaleRand = r.ReadSingle();
        pe.StartTrans = r.ReadSingle();
        pe.FinalTrans = r.ReadSingle();
        pe.TransRand = r.ReadSingle();
        pe.IsParentLocal = r.ReadBoolean();
        return pe;
    }

    // ---- TextureKey ------------------------------------------------------

    private static void WriteTextureKey(BinaryWriter w, TextureKey key) {
        w.Write(key.SurfaceId);
        w.Write(key.PaletteId);
        w.Write((byte)key.Stippling);
        w.Write(key.IsSolid);
    }

    private static TextureKey ReadTextureKey(BinaryReader r) {
        return new TextureKey {
            SurfaceId = r.ReadUInt32(),
            PaletteId = r.ReadUInt32(),
            Stippling = (DatReaderWriter.Enums.StipplingType)r.ReadByte(),
            IsSolid = r.ReadBoolean(),
        };
    }

    private static void WriteTextureFormatTuple(BinaryWriter w, (int Width, int Height, TextureFormat Format) tuple) {
        w.Write(tuple.Width);
        w.Write(tuple.Height);
        w.Write((int)tuple.Format);
    }

    private static (int Width, int Height, TextureFormat Format) ReadTextureFormatTuple(BinaryReader r) {
        int width = r.ReadInt32();
        int height = r.ReadInt32();
        var format = (TextureFormat)r.ReadInt32();
        return (width, height, format);
    }


    private static void WriteSphere(BinaryWriter w, Sphere sphere) {
        WriteVector3(w, sphere.Origin);
        w.Write(sphere.Radius);
    }

    private static Sphere ReadSphere(BinaryReader r) {
        var origin = ReadVector3(r);
        float radius = r.ReadSingle();
        return new Sphere { Origin = origin, Radius = radius };
    }

    private static void WriteBoundingBox(BinaryWriter w, BoundingBox box) {
        WriteVector3(w, box.Min);
        WriteVector3(w, box.Max);
    }

    private static BoundingBox ReadBoundingBox(BinaryReader r) {
        var min = ReadVector3(r);
        var max = ReadVector3(r);
        return new BoundingBox(min, max);
    }

    // ---- primitive helpers -------------------------------------------------

    private static void WriteVector3(BinaryWriter w, Vector3 v) {
        w.Write(v.X);
        w.Write(v.Y);
        w.Write(v.Z);
    }

    private static Vector3 ReadVector3(BinaryReader r) {
        float x = r.ReadSingle();
        float y = r.ReadSingle();
        float z = r.ReadSingle();
        return new Vector3(x, y, z);
    }

    private static void WriteMatrix4x4(BinaryWriter w, Matrix4x4 m) {
        w.Write(m.M11); w.Write(m.M12); w.Write(m.M13); w.Write(m.M14);
        w.Write(m.M21); w.Write(m.M22); w.Write(m.M23); w.Write(m.M24);
        w.Write(m.M31); w.Write(m.M32); w.Write(m.M33); w.Write(m.M34);
        w.Write(m.M41); w.Write(m.M42); w.Write(m.M43); w.Write(m.M44);
    }

    private static Matrix4x4 ReadMatrix4x4(BinaryReader r) {
        return new Matrix4x4(
            r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(),
            r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(),
            r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(),
            r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    }

    private static void WriteVertexArray(BinaryWriter w, VertexPositionNormalTexture[] vertices) {
        w.Write(vertices.Length);
        if (vertices.Length == 0) return;
        var bytes = MemoryMarshal.AsBytes(vertices.AsSpan());
        w.Write(bytes);
    }

    private static VertexPositionNormalTexture[] ReadVertexArray(BinaryReader r) {
        int count = r.ReadInt32();
        if (count == 0) return Array.Empty<VertexPositionNormalTexture>();
        var result = new VertexPositionNormalTexture[count];
        var bytes = r.ReadBytes(count * VertexPositionNormalTexture.Size);
        bytes.CopyTo(MemoryMarshal.AsBytes(result.AsSpan()));
        return result;
    }

    private static void WriteVector3Array(BinaryWriter w, Vector3[] array) {
        w.Write(array.Length);
        if (array.Length == 0) return;
        var bytes = MemoryMarshal.AsBytes(array.AsSpan());
        w.Write(bytes);
    }

    private static Vector3[] ReadVector3Array(BinaryReader r) {
        int count = r.ReadInt32();
        if (count == 0) return Array.Empty<Vector3>();
        var result = new Vector3[count];
        var bytes = r.ReadBytes(count * 12);
        bytes.CopyTo(MemoryMarshal.AsBytes(result.AsSpan()));
        return result;
    }

    private static void WriteUInt16Array(BinaryWriter w, ushort[] array) {
        w.Write(array.Length);
        if (array.Length == 0) return;
        var bytes = MemoryMarshal.AsBytes(array.AsSpan());
        w.Write(bytes);
    }

    private static ushort[] ReadUInt16Array(BinaryReader r) {
        int count = r.ReadInt32();
        if (count == 0) return Array.Empty<ushort>();
        var result = new ushort[count];
        var bytes = r.ReadBytes(count * sizeof(ushort));
        bytes.CopyTo(MemoryMarshal.AsBytes(result.AsSpan()));
        return result;
    }

    private static void WriteUInt16List(BinaryWriter w, List<ushort> list) {
        w.Write(list.Count);
        if (list.Count == 0) return;
        var array = list.ToArray();
        var bytes = MemoryMarshal.AsBytes(array.AsSpan());
        w.Write(bytes);
    }

    private static List<ushort> ReadUInt16List(BinaryReader r) {
        int count = r.ReadInt32();
        var list = new List<ushort>(count);
        if (count == 0) return list;
        var array = new ushort[count];
        var bytes = r.ReadBytes(count * sizeof(ushort));
        bytes.CopyTo(MemoryMarshal.AsBytes(array.AsSpan()));
        list.AddRange(array);
        return list;
    }

    private static void WriteByteArray(BinaryWriter w, byte[] array) {
        w.Write(array.Length);
        if (array.Length > 0) w.Write(array);
    }

    private static byte[] ReadByteArray(BinaryReader r) {
        int count = r.ReadInt32();
        return count == 0 ? Array.Empty<byte>() : r.ReadBytes(count);
    }

    /// <summary>present:byte + value:i32 for a nullable enum-backed-by-int field.</summary>
    private static void WriteNullableInt32Enum(BinaryWriter w, bool present, int value) {
        w.Write(present);
        w.Write(value);
    }

    private static TEnum? ReadNullableInt32Enum<TEnum>(BinaryReader r, Func<int, TEnum> project) where TEnum : struct, Enum {
        bool present = r.ReadBoolean();
        int value = r.ReadInt32();
        return present ? project(value) : null;
    }
}
