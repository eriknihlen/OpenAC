using System.Linq;
using Chorizite.Core.Render.Enums;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.Content.Tests;

public static class ObjectMeshDataEquality {
    public static void AssertEqual(ObjectMeshData? expected, ObjectMeshData? actual, string path = "root") {
        if (expected is null && actual is null) return;
        Assert.True(expected is not null, $"{path}: expected null but actual was non-null");
        Assert.True(actual is not null, $"{path}: expected non-null but actual was null");

        Assert.True(expected.ObjectId == actual.ObjectId, $"{path}.ObjectId: expected 0x{expected.ObjectId:X16}, got 0x{actual.ObjectId:X16}");
        Assert.True(expected.IsSetup == actual.IsSetup, $"{path}.IsSetup: expected {expected.IsSetup}, got {actual.IsSetup}");
        AssertVerticesEqual(expected.Vertices, actual.Vertices, $"{path}.Vertices");

        Assert.True(expected.Batches.Count == actual.Batches.Count,
            $"{path}.Batches.Count: expected {expected.Batches.Count}, got {actual.Batches.Count}");
        for (int i = 0; i < expected.Batches.Count; i++)
            AssertBatchEqual(expected.Batches[i], actual.Batches[i], $"{path}.Batches[{i}]");

        Assert.True(expected.UploadAttempts == actual.UploadAttempts,
            $"{path}.UploadAttempts: expected {expected.UploadAttempts}, got {actual.UploadAttempts}");

        AssertEqual(expected.EnvCellGeometry, actual.EnvCellGeometry, $"{path}.EnvCellGeometry");

        Assert.True(expected.SetupParts.Count == actual.SetupParts.Count,
            $"{path}.SetupParts.Count: expected {expected.SetupParts.Count}, got {actual.SetupParts.Count}");
        for (int i = 0; i < expected.SetupParts.Count; i++) {
            var (eId, eT) = expected.SetupParts[i];
            var (aId, aT) = actual.SetupParts[i];
            Assert.True(eId == aId, $"{path}.SetupParts[{i}].GfxObjId: expected 0x{eId:X16}, got 0x{aId:X16}");
            AssertMatrixEqual(eT, aT, $"{path}.SetupParts[{i}].Transform");
        }

        Assert.True(expected.ParticleEmitters.Count == actual.ParticleEmitters.Count,
            $"{path}.ParticleEmitters.Count: expected {expected.ParticleEmitters.Count}, got {actual.ParticleEmitters.Count}");
        for (int i = 0; i < expected.ParticleEmitters.Count; i++)
            AssertStagedEmitterEqual(expected.ParticleEmitters[i], actual.ParticleEmitters[i], $"{path}.ParticleEmitters[{i}]");

        AssertTextureBatchesEqual(expected.TextureBatches, actual.TextureBatches, $"{path}.TextureBatches");

        AssertBoundingBoxEqual(expected.BoundingBox, actual.BoundingBox, $"{path}.BoundingBox");
        AssertVector3Equal(expected.SortCenter, actual.SortCenter, $"{path}.SortCenter");
        Assert.True(expected.DIDDegrade == actual.DIDDegrade, $"{path}.DIDDegrade: expected {expected.DIDDegrade}, got {actual.DIDDegrade}");

        AssertSphereEqual(expected.SelectionSphere, actual.SelectionSphere, $"{path}.SelectionSphere");
        AssertVector3ArrayEqual(expected.EdgeLines, actual.EdgeLines, $"{path}.EdgeLines");
    }

    private static void AssertVerticesEqual(VertexPositionNormalTexture[] expected, VertexPositionNormalTexture[] actual, string path) {
        Assert.True(expected.Length == actual.Length, $"{path}.Length: expected {expected.Length}, got {actual.Length}");
        for (int i = 0; i < expected.Length; i++) {
            AssertVector3Equal(expected[i].Position, actual[i].Position, $"{path}[{i}].Position");
            AssertVector3Equal(expected[i].Normal, actual[i].Normal, $"{path}[{i}].Normal");
            Assert.True(BitEqual(expected[i].UV, actual[i].UV), $"{path}[{i}].UV: expected {expected[i].UV}, got {actual[i].UV}");
        }
    }

    private static void AssertBatchEqual(MeshBatchData expected, MeshBatchData actual, string path) {
        Assert.True(expected.Indices.SequenceEqual(actual.Indices),
            $"{path}.Indices: expected [{string.Join(",", expected.Indices)}], got [{string.Join(",", actual.Indices)}]");
        Assert.True(expected.TextureFormat == actual.TextureFormat,
            $"{path}.TextureFormat: expected {expected.TextureFormat}, got {actual.TextureFormat}");
        AssertTextureKeyEqual(expected.TextureKey, actual.TextureKey, $"{path}.TextureKey");
        Assert.True(expected.TextureIndex == actual.TextureIndex,
            $"{path}.TextureIndex: expected {expected.TextureIndex}, got {actual.TextureIndex}");
        Assert.True(expected.TextureData.SequenceEqual(actual.TextureData),
            $"{path}.TextureData: length expected {expected.TextureData.Length}, got {actual.TextureData.Length}");
        Assert.True(expected.UploadPixelFormat == actual.UploadPixelFormat,
            $"{path}.UploadPixelFormat: expected {expected.UploadPixelFormat}, got {actual.UploadPixelFormat}");
        Assert.True(expected.UploadPixelType == actual.UploadPixelType,
            $"{path}.UploadPixelType: expected {expected.UploadPixelType}, got {actual.UploadPixelType}");
        Assert.True(expected.CullMode == actual.CullMode,
            $"{path}.CullMode: expected {expected.CullMode}, got {actual.CullMode}");
    }

    private static void AssertTextureBatchDataEqual(TextureBatchData expected, TextureBatchData actual, string path) {
        AssertTextureKeyEqual(expected.Key, actual.Key, $"{path}.Key");
        Assert.True(expected.TextureData.SequenceEqual(actual.TextureData),
            $"{path}.TextureData: length expected {expected.TextureData.Length}, got {actual.TextureData.Length}");
        Assert.True(expected.UploadPixelFormat == actual.UploadPixelFormat,
            $"{path}.UploadPixelFormat: expected {expected.UploadPixelFormat}, got {actual.UploadPixelFormat}");
        Assert.True(expected.UploadPixelType == actual.UploadPixelType,
            $"{path}.UploadPixelType: expected {expected.UploadPixelType}, got {actual.UploadPixelType}");
        Assert.True(expected.Indices.SequenceEqual(actual.Indices),
            $"{path}.Indices: expected [{string.Join(",", expected.Indices)}], got [{string.Join(",", actual.Indices)}]");
        Assert.True(expected.CullMode == actual.CullMode, $"{path}.CullMode: expected {expected.CullMode}, got {actual.CullMode}");
        Assert.True(expected.Translucency == actual.Translucency,
            $"{path}.Translucency: expected {expected.Translucency}, got {actual.Translucency}");
        Assert.True(expected.MaterialState == actual.MaterialState,
            $"{path}.MaterialState: expected {expected.MaterialState}, got {actual.MaterialState}");
        Assert.True(expected.IsTransparent == actual.IsTransparent, $"{path}.IsTransparent: expected {expected.IsTransparent}, got {actual.IsTransparent}");
        Assert.True(expected.IsAdditive == actual.IsAdditive, $"{path}.IsAdditive: expected {expected.IsAdditive}, got {actual.IsAdditive}");
        Assert.True(expected.HasWrappingUVs == actual.HasWrappingUVs, $"{path}.HasWrappingUVs: expected {expected.HasWrappingUVs}, got {actual.HasWrappingUVs}");
        Assert.True(expected.SourceSurfaceIndex == actual.SourceSurfaceIndex, $"{path}.SourceSurfaceIndex: expected {expected.SourceSurfaceIndex}, got {actual.SourceSurfaceIndex}");
        Assert.True(expected.RetailSurfaceMask == actual.RetailSurfaceMask, $"{path}.RetailSurfaceMask: expected {expected.RetailSurfaceMask}, got {actual.RetailSurfaceMask}");
        Assert.True(expected.RawSurfaceType == actual.RawSurfaceType, $"{path}.RawSurfaceType: expected {expected.RawSurfaceType}, got {actual.RawSurfaceType}");
        Assert.True(BitConverter.SingleToInt32Bits(expected.SurfaceOpacity)
            == BitConverter.SingleToInt32Bits(actual.SurfaceOpacity),
            $"{path}.SurfaceOpacity: expected {expected.SurfaceOpacity:R}, got {actual.SurfaceOpacity:R}");
        Assert.True(expected.IsCellShell == actual.IsCellShell, $"{path}.IsCellShell: expected {expected.IsCellShell}, got {actual.IsCellShell}");
    }

    private static void AssertTextureBatchesEqual(
        System.Collections.Generic.Dictionary<(int Width, int Height, TextureFormat Format), System.Collections.Generic.List<TextureBatchData>> expected,
        System.Collections.Generic.Dictionary<(int Width, int Height, TextureFormat Format), System.Collections.Generic.List<TextureBatchData>> actual,
        string path) {
        Assert.True(expected.Count == actual.Count, $"{path}.Count: expected {expected.Count}, got {actual.Count}");
        foreach (var key in expected.Keys) {
            Assert.True(actual.ContainsKey(key), $"{path}: missing key {key}");
            var expList = expected[key];
            var actList = actual[key];
            Assert.True(expList.Count == actList.Count, $"{path}[{key}].Count: expected {expList.Count}, got {actList.Count}");
            for (int i = 0; i < expList.Count; i++)
                AssertTextureBatchDataEqual(expList[i], actList[i], $"{path}[{key}][{i}]");
        }
    }

    private static void AssertStagedEmitterEqual(StagedEmitter expected, StagedEmitter actual, string path) {
        Assert.True(expected.PartIndex == actual.PartIndex, $"{path}.PartIndex: expected {expected.PartIndex}, got {actual.PartIndex}");
        AssertMatrixEqual(expected.Offset, actual.Offset, $"{path}.Offset");
        AssertParticleEmitterEqual(expected.Emitter, actual.Emitter, $"{path}.Emitter");
    }

    private static void AssertParticleEmitterEqual(ParticleEmitter? expected, ParticleEmitter? actual, string path) {
        if (expected is null && actual is null) return;
        Assert.True(expected is not null, $"{path}: expected null but actual was non-null");
        Assert.True(actual is not null, $"{path}: expected non-null but actual was null");

        Assert.True(expected.Id == actual.Id, $"{path}.Id: expected 0x{expected.Id:X8}, got 0x{actual.Id:X8}");
        Assert.True(expected.DataCategory == actual.DataCategory, $"{path}.DataCategory: expected {expected.DataCategory}, got {actual.DataCategory}");
        Assert.True(expected.Unknown == actual.Unknown, $"{path}.Unknown: expected {expected.Unknown}, got {actual.Unknown}");
        Assert.True(expected.EmitterType == actual.EmitterType, $"{path}.EmitterType: expected {expected.EmitterType}, got {actual.EmitterType}");
        Assert.True(expected.ParticleType == actual.ParticleType, $"{path}.ParticleType: expected {expected.ParticleType}, got {actual.ParticleType}");
        Assert.True(expected.GfxObjId.DataId == actual.GfxObjId.DataId, $"{path}.GfxObjId: expected 0x{expected.GfxObjId.DataId:X8}, got 0x{actual.GfxObjId.DataId:X8}");
        Assert.True(expected.HwGfxObjId.DataId == actual.HwGfxObjId.DataId, $"{path}.HwGfxObjId: expected 0x{expected.HwGfxObjId.DataId:X8}, got 0x{actual.HwGfxObjId.DataId:X8}");
        Assert.True(BitEqual(expected.Birthrate, actual.Birthrate), $"{path}.Birthrate: expected {expected.Birthrate}, got {actual.Birthrate}");
        Assert.True(expected.MaxParticles == actual.MaxParticles, $"{path}.MaxParticles: expected {expected.MaxParticles}, got {actual.MaxParticles}");
        Assert.True(expected.InitialParticles == actual.InitialParticles, $"{path}.InitialParticles: expected {expected.InitialParticles}, got {actual.InitialParticles}");
        Assert.True(expected.TotalParticles == actual.TotalParticles, $"{path}.TotalParticles: expected {expected.TotalParticles}, got {actual.TotalParticles}");
        Assert.True(BitEqual(expected.TotalSeconds, actual.TotalSeconds), $"{path}.TotalSeconds: expected {expected.TotalSeconds}, got {actual.TotalSeconds}");
        Assert.True(BitEqual(expected.Lifespan, actual.Lifespan), $"{path}.Lifespan: expected {expected.Lifespan}, got {actual.Lifespan}");
        Assert.True(BitEqual(expected.LifespanRand, actual.LifespanRand), $"{path}.LifespanRand: expected {expected.LifespanRand}, got {actual.LifespanRand}");
        AssertVector3Equal(expected.OffsetDir, actual.OffsetDir, $"{path}.OffsetDir");
        Assert.True(BitEqual(expected.MinOffset, actual.MinOffset), $"{path}.MinOffset: expected {expected.MinOffset}, got {actual.MinOffset}");
        Assert.True(BitEqual(expected.MaxOffset, actual.MaxOffset), $"{path}.MaxOffset: expected {expected.MaxOffset}, got {actual.MaxOffset}");
        AssertVector3Equal(expected.A, actual.A, $"{path}.A");
        Assert.True(BitEqual(expected.MinA, actual.MinA), $"{path}.MinA: expected {expected.MinA}, got {actual.MinA}");
        Assert.True(BitEqual(expected.MaxA, actual.MaxA), $"{path}.MaxA: expected {expected.MaxA}, got {actual.MaxA}");
        AssertVector3Equal(expected.B, actual.B, $"{path}.B");
        Assert.True(BitEqual(expected.MinB, actual.MinB), $"{path}.MinB: expected {expected.MinB}, got {actual.MinB}");
        Assert.True(BitEqual(expected.MaxB, actual.MaxB), $"{path}.MaxB: expected {expected.MaxB}, got {actual.MaxB}");
        AssertVector3Equal(expected.C, actual.C, $"{path}.C");
        Assert.True(BitEqual(expected.MinC, actual.MinC), $"{path}.MinC: expected {expected.MinC}, got {actual.MinC}");
        Assert.True(BitEqual(expected.MaxC, actual.MaxC), $"{path}.MaxC: expected {expected.MaxC}, got {actual.MaxC}");
        Assert.True(BitEqual(expected.StartScale, actual.StartScale), $"{path}.StartScale: expected {expected.StartScale}, got {actual.StartScale}");
        Assert.True(BitEqual(expected.FinalScale, actual.FinalScale), $"{path}.FinalScale: expected {expected.FinalScale}, got {actual.FinalScale}");
        Assert.True(BitEqual(expected.ScaleRand, actual.ScaleRand), $"{path}.ScaleRand: expected {expected.ScaleRand}, got {actual.ScaleRand}");
        Assert.True(BitEqual(expected.StartTrans, actual.StartTrans), $"{path}.StartTrans: expected {expected.StartTrans}, got {actual.StartTrans}");
        Assert.True(BitEqual(expected.FinalTrans, actual.FinalTrans), $"{path}.FinalTrans: expected {expected.FinalTrans}, got {actual.FinalTrans}");
        Assert.True(BitEqual(expected.TransRand, actual.TransRand), $"{path}.TransRand: expected {expected.TransRand}, got {actual.TransRand}");
        Assert.True(expected.IsParentLocal == actual.IsParentLocal, $"{path}.IsParentLocal: expected {expected.IsParentLocal}, got {actual.IsParentLocal}");
    }

    private static void AssertTextureKeyEqual(TextureKey expected, TextureKey actual, string path) {
        Assert.True(expected.Equals(actual),
            $"{path}: expected SurfaceId=0x{expected.SurfaceId:X8} PaletteId=0x{expected.PaletteId:X8} Stippling={expected.Stippling} IsSolid={expected.IsSolid}, " +
            $"got SurfaceId=0x{actual.SurfaceId:X8} PaletteId=0x{actual.PaletteId:X8} Stippling={actual.Stippling} IsSolid={actual.IsSolid}");
    }

    private static void AssertBoundingBoxEqual(Chorizite.Core.Lib.BoundingBox expected, Chorizite.Core.Lib.BoundingBox actual, string path) {
        AssertVector3Equal(expected.Min, actual.Min, $"{path}.Min");
        AssertVector3Equal(expected.Max, actual.Max, $"{path}.Max");
    }

    private static void AssertSphereEqual(Sphere? expected, Sphere? actual, string path) {
        if (expected is null && actual is null) return;
        Assert.True(expected is not null, $"{path}: expected null but actual was non-null");
        Assert.True(actual is not null, $"{path}: expected non-null but actual was null");
        AssertVector3Equal(expected.Origin, actual.Origin, $"{path}.Origin");
        Assert.True(BitEqual(expected.Radius, actual.Radius), $"{path}.Radius: expected {expected.Radius}, got {actual.Radius}");
    }

    private static void AssertVector3Equal(System.Numerics.Vector3 expected, System.Numerics.Vector3 actual, string path) {
        Assert.True(BitEqual(expected, actual), $"{path}: expected {expected}, got {actual}");
    }

    private static void AssertVector3ArrayEqual(System.Numerics.Vector3[] expected, System.Numerics.Vector3[] actual, string path) {
        Assert.True(expected.Length == actual.Length, $"{path}.Length: expected {expected.Length}, got {actual.Length}");
        for (int i = 0; i < expected.Length; i++)
            AssertVector3Equal(expected[i], actual[i], $"{path}[{i}]");
    }

    private static void AssertMatrixEqual(System.Numerics.Matrix4x4 expected, System.Numerics.Matrix4x4 actual, string path) {
        Assert.True(BitEqual(expected, actual), $"{path}: expected {expected}, got {actual}");
    }


    private static bool BitEqual(float a, float b) =>
        BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

    private static bool BitEqual(double a, double b) =>
        BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

    private static bool BitEqual(System.Numerics.Vector2 a, System.Numerics.Vector2 b) =>
        BitEqual(a.X, b.X) && BitEqual(a.Y, b.Y);

    private static bool BitEqual(System.Numerics.Vector3 a, System.Numerics.Vector3 b) =>
        BitEqual(a.X, b.X) && BitEqual(a.Y, b.Y) && BitEqual(a.Z, b.Z);

    private static bool BitEqual(System.Numerics.Matrix4x4 a, System.Numerics.Matrix4x4 b) =>
        BitEqual(a.M11, b.M11) && BitEqual(a.M12, b.M12) && BitEqual(a.M13, b.M13) && BitEqual(a.M14, b.M14) &&
        BitEqual(a.M21, b.M21) && BitEqual(a.M22, b.M22) && BitEqual(a.M23, b.M23) && BitEqual(a.M24, b.M24) &&
        BitEqual(a.M31, b.M31) && BitEqual(a.M32, b.M32) && BitEqual(a.M33, b.M33) && BitEqual(a.M34, b.M34) &&
        BitEqual(a.M41, b.M41) && BitEqual(a.M42, b.M42) && BitEqual(a.M43, b.M43) && BitEqual(a.M44, b.M44);
}
