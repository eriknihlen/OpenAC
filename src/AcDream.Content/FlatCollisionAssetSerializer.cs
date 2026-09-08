using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using AcDream.Core.Physics;
using DatReaderWriter.Enums;

namespace AcDream.Content;

public static class FlatCollisionAssetSerializer
{
    private const uint Magic = 0x4C_43_43_41u; // "ACCL" little-endian
    private const byte SchemaVersion = 1;
    private const int MaximumRows = 16_777_216;

    public static byte[] Serialize(
        FlatGfxObjCollisionAsset asset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var writer = new CollisionWriter();
        writer.WriteHeader(CollisionPayloadKind.GfxObj);
        WritePhysicsBsp(writer, asset.PhysicsBsp, cancellationToken);
        writer.WriteOptionalSphere(asset.BoundingSphere);
        writer.WriteBoolean(asset.VisualBounds.HasValue);
        if (asset.VisualBounds is FlatGfxObjVisualBounds visual)
        {
            writer.WriteVector3(visual.Min);
            writer.WriteVector3(visual.Max);
            writer.WriteVector3(visual.Center);
            writer.WriteSingle(visual.Radius);
            writer.WriteVector3(visual.HalfExtents);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return writer.ToArray();
    }

    public static byte[] Serialize(
        FlatSetupCollision asset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var writer = new CollisionWriter();
        writer.WriteHeader(CollisionPayloadKind.Setup);
        writer.WriteCount(asset.Cylinders.Length, "Setup cylinder");
        writer.WriteCount(asset.Spheres.Length, "Setup sphere");
        writer.WriteSingle(asset.Height);
        writer.WriteSingle(asset.Radius);
        writer.WriteSingle(asset.StepUpHeight);
        writer.WriteSingle(asset.StepDownHeight);

        for (int i = 0; i < asset.Cylinders.Length; i++)
        {
            CheckCancellation(cancellationToken, i);
            FlatCollisionCylinder cylinder = asset.Cylinders[i];
            writer.WriteVector3(cylinder.Origin);
            writer.WriteSingle(cylinder.Radius);
            writer.WriteSingle(cylinder.Height);
        }
        for (int i = 0; i < asset.Spheres.Length; i++)
        {
            CheckCancellation(cancellationToken, i);
            writer.WriteSphere(asset.Spheres[i]);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return writer.ToArray();
    }

    public static byte[] Serialize(
        FlatCellStructureCollisionAsset asset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var writer = new CollisionWriter();
        writer.WriteHeader(CollisionPayloadKind.CellStructure);
        WritePhysicsBsp(writer, asset.PhysicsBsp, cancellationToken);
        WriteContainmentBsp(writer, asset.ContainmentBsp, cancellationToken);
        WritePolygonTable(writer, asset.PortalPolygons, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return writer.ToArray();
    }

    public static byte[] Serialize(
        FlatEnvCellTopology asset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var writer = new CollisionWriter();
        writer.WriteHeader(CollisionPayloadKind.EnvCellTopology);
        writer.WriteCount(asset.Portals.Length, "EnvCell portal");
        writer.WriteCount(asset.VisibleCellIds.Length, "visible-cell");
        writer.WriteBoolean(asset.SeenOutside);

        for (int i = 0; i < asset.Portals.Length; i++)
        {
            CheckCancellation(cancellationToken, i);
            FlatEnvCellPortal portal = asset.Portals[i];
            writer.WriteUInt16(portal.OtherCellId);
            writer.WriteUInt16(portal.PolygonId);
            writer.WriteUInt16(portal.Flags);
            writer.WriteInt32(portal.PolygonIndex);
        }
        for (int i = 0; i < asset.VisibleCellIds.Length; i++)
        {
            CheckCancellation(cancellationToken, i);
            writer.WriteUInt32(asset.VisibleCellIds[i]);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return writer.ToArray();
    }

    public static FlatGfxObjCollisionAsset DeserializeGfxObj(
        ReadOnlySpan<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        var reader = new CollisionReader(bytes, cancellationToken);
        reader.ReadHeader(CollisionPayloadKind.GfxObj);
        FlatPhysicsBsp physicsBsp = ReadPhysicsBsp(ref reader);
        FlatCollisionSphere? boundingSphere = reader.ReadOptionalSphere();
        FlatGfxObjVisualBounds? visualBounds = null;
        if (reader.ReadBoolean())
        {
            visualBounds = new FlatGfxObjVisualBounds(
                reader.ReadVector3(),
                reader.ReadVector3(),
                reader.ReadVector3(),
                reader.ReadSingle(),
                reader.ReadVector3());
        }
        reader.RequireEnd();
        return new FlatGfxObjCollisionAsset(
            physicsBsp,
            boundingSphere,
            visualBounds);
    }

    public static FlatSetupCollision DeserializeSetup(
        ReadOnlySpan<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        var reader = new CollisionReader(bytes, cancellationToken);
        reader.ReadHeader(CollisionPayloadKind.Setup);
        int cylinderCount = reader.ReadCount(
            "Setup cylinder",
            bytesPerItem: 20);
        int sphereCount = reader.ReadCount(
            "Setup sphere",
            bytesPerItem: 16);
        reader.RequireRemaining(
            checked(16L + (20L * cylinderCount) + (16L * sphereCount)),
            "Setup collision rows");
        float height = reader.ReadSingle();
        float radius = reader.ReadSingle();
        float stepUpHeight = reader.ReadSingle();
        float stepDownHeight = reader.ReadSingle();

        var cylinders =
            ImmutableArray.CreateBuilder<FlatCollisionCylinder>(cylinderCount);
        for (int i = 0; i < cylinderCount; i++)
        {
            reader.CheckCancellation(i);
            cylinders.Add(new FlatCollisionCylinder(
                reader.ReadVector3(),
                reader.ReadSingle(),
                reader.ReadSingle()));
        }

        var spheres =
            ImmutableArray.CreateBuilder<FlatCollisionSphere>(sphereCount);
        for (int i = 0; i < sphereCount; i++)
        {
            reader.CheckCancellation(i);
            spheres.Add(reader.ReadSphere());
        }

        reader.RequireEnd();
        return new FlatSetupCollision(
            cylinders.MoveToImmutable(),
            spheres.MoveToImmutable(),
            height,
            radius,
            stepUpHeight,
            stepDownHeight);
    }

    public static FlatCellStructureCollisionAsset DeserializeCellStructure(
        ReadOnlySpan<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        var reader = new CollisionReader(bytes, cancellationToken);
        reader.ReadHeader(CollisionPayloadKind.CellStructure);
        FlatPhysicsBsp physicsBsp = ReadPhysicsBsp(ref reader);
        FlatCellContainmentBsp containmentBsp = ReadContainmentBsp(ref reader);
        FlatPolygonTable portalPolygons = ReadPolygonTable(ref reader);
        reader.RequireEnd();
        return new FlatCellStructureCollisionAsset(
            physicsBsp,
            containmentBsp,
            portalPolygons);
    }

    public static FlatEnvCellTopology DeserializeEnvCellTopology(
        ReadOnlySpan<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        var reader = new CollisionReader(bytes, cancellationToken);
        reader.ReadHeader(CollisionPayloadKind.EnvCellTopology);
        int portalCount = reader.ReadCount(
            "EnvCell portal",
            bytesPerItem: 10);
        int visibleCount = reader.ReadCount(
            "visible-cell",
            bytesPerItem: 4);
        reader.RequireRemaining(
            checked(1L + (10L * portalCount) + (4L * visibleCount)),
            "EnvCell topology rows");
        bool seenOutside = reader.ReadBoolean();

        var portals =
            ImmutableArray.CreateBuilder<FlatEnvCellPortal>(portalCount);
        for (int i = 0; i < portalCount; i++)
        {
            reader.CheckCancellation(i);
            portals.Add(new FlatEnvCellPortal(
                reader.ReadUInt16(),
                reader.ReadUInt16(),
                reader.ReadUInt16(),
                reader.ReadInt32()));
        }

        var visible =
            ImmutableArray.CreateBuilder<uint>(visibleCount);
        for (int i = 0; i < visibleCount; i++)
        {
            reader.CheckCancellation(i);
            visible.Add(reader.ReadUInt32());
        }

        reader.RequireEnd();
        return new FlatEnvCellTopology(
            portals.MoveToImmutable(),
            visible.MoveToImmutable(),
            seenOutside);
    }

    private static void WritePhysicsBsp(
        CollisionWriter writer,
        FlatPhysicsBsp bsp,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        writer.WriteInt32(bsp.RootIndex);
        writer.WriteCount(bsp.Nodes.Length, "physics BSP node");
        writer.WriteCount(
            bsp.PolygonIndexStream.Length,
            "physics BSP polygon-index");

        for (int i = 0; i < bsp.Nodes.Length; i++)
        {
            CheckCancellation(cancellationToken, i);
            FlatPhysicsBspNode node = bsp.Nodes[i];
            writer.WriteUInt32((uint)node.Type);
            writer.WritePlane(node.SplittingPlane);
            writer.WriteInt32(node.PositiveChildIndex);
            writer.WriteInt32(node.NegativeChildIndex);
            writer.WriteInt32(node.LeafIndex);
            writer.WriteInt32(node.Solid);
            writer.WriteSphere(node.BoundingSphere);
            writer.WriteRange(node.PolygonIndexRange);
        }

        for (int i = 0; i < bsp.PolygonIndexStream.Length; i++)
        {
            CheckCancellation(cancellationToken, i);
            writer.WriteInt32(bsp.PolygonIndexStream[i]);
        }
        WritePolygonTable(writer, bsp.PolygonTable, cancellationToken);
    }

    private static FlatPhysicsBsp ReadPhysicsBsp(
        ref CollisionReader reader)
    {
        int rootIndex = reader.ReadInt32();
        int nodeCount = reader.ReadCount(
            "physics BSP node",
            bytesPerItem: 60);
        int polygonIndexCount = reader.ReadCount(
            "physics BSP polygon-index",
            bytesPerItem: 4);
        reader.RequireRemaining(
            checked((60L * nodeCount) + (4L * polygonIndexCount) + 8L),
            "physics BSP rows");

        var nodes =
            ImmutableArray.CreateBuilder<FlatPhysicsBspNode>(nodeCount);
        for (int i = 0; i < nodeCount; i++)
        {
            reader.CheckCancellation(i);
            nodes.Add(new FlatPhysicsBspNode(
                (BSPNodeType)reader.ReadUInt32(),
                reader.ReadPlane(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadSphere(),
                reader.ReadRange()));
        }

        var polygonIndices =
            ImmutableArray.CreateBuilder<int>(polygonIndexCount);
        for (int i = 0; i < polygonIndexCount; i++)
        {
            reader.CheckCancellation(i);
            polygonIndices.Add(reader.ReadInt32());
        }

        FlatPolygonTable polygons = ReadPolygonTable(ref reader);
        return new FlatPhysicsBsp(
            rootIndex,
            nodes.MoveToImmutable(),
            polygonIndices.MoveToImmutable(),
            polygons);
    }

    private static void WriteContainmentBsp(
        CollisionWriter writer,
        FlatCellContainmentBsp bsp,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bsp);
        writer.WriteInt32(bsp.RootIndex);
        writer.WriteCount(bsp.Nodes.Length, "cell-containment BSP node");
        for (int i = 0; i < bsp.Nodes.Length; i++)
        {
            CheckCancellation(cancellationToken, i);
            FlatCellBspNode node = bsp.Nodes[i];
            writer.WriteUInt32((uint)node.Type);
            writer.WritePlane(node.SplittingPlane);
            writer.WriteInt32(node.PositiveChildIndex);
            writer.WriteInt32(node.NegativeChildIndex);
            writer.WriteInt32(node.LeafIndex);
        }
    }

    private static FlatCellContainmentBsp ReadContainmentBsp(
        ref CollisionReader reader)
    {
        int rootIndex = reader.ReadInt32();
        int nodeCount = reader.ReadCount(
            "cell-containment BSP node",
            bytesPerItem: 32);
        var nodes =
            ImmutableArray.CreateBuilder<FlatCellBspNode>(nodeCount);
        for (int i = 0; i < nodeCount; i++)
        {
            reader.CheckCancellation(i);
            nodes.Add(new FlatCellBspNode(
                (BSPNodeType)reader.ReadUInt32(),
                reader.ReadPlane(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32()));
        }
        return new FlatCellContainmentBsp(
            rootIndex,
            nodes.MoveToImmutable());
    }

    private static void WritePolygonTable(
        CollisionWriter writer,
        FlatPolygonTable table,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(table);
        writer.WriteCount(table.Polygons.Length, "polygon");
        writer.WriteCount(table.Vertices.Length, "polygon vertex");
        for (int i = 0; i < table.Polygons.Length; i++)
        {
            CheckCancellation(cancellationToken, i);
            FlatCollisionPolygon polygon = table.Polygons[i];
            writer.WriteUInt16(polygon.Id);
            writer.WriteUInt32((uint)polygon.SidesType);
            writer.WriteInt32(polygon.NumPoints);
            writer.WriteRange(polygon.VertexRange);
            writer.WritePlane(polygon.Plane);
        }
        for (int i = 0; i < table.Vertices.Length; i++)
        {
            CheckCancellation(cancellationToken, i);
            writer.WriteVector3(table.Vertices[i]);
        }
    }

    private static FlatPolygonTable ReadPolygonTable(
        ref CollisionReader reader)
    {
        int polygonCount = reader.ReadCount(
            "polygon",
            bytesPerItem: 34);
        int vertexCount = reader.ReadCount(
            "polygon vertex",
            bytesPerItem: 12);
        reader.RequireRemaining(
            checked((34L * polygonCount) + (12L * vertexCount)),
            "polygon-table rows");
        var polygons =
            ImmutableArray.CreateBuilder<FlatCollisionPolygon>(polygonCount);
        for (int i = 0; i < polygonCount; i++)
        {
            reader.CheckCancellation(i);
            polygons.Add(new FlatCollisionPolygon(
                reader.ReadUInt16(),
                reader.ReadPlaneAfterMetadata(out CullMode sides, out int count,
                    out FlatIndexRange range),
                sides,
                count,
                range));
        }

        var vertices = ImmutableArray.CreateBuilder<Vector3>(vertexCount);
        for (int i = 0; i < vertexCount; i++)
        {
            reader.CheckCancellation(i);
            vertices.Add(reader.ReadVector3());
        }
        return new FlatPolygonTable(
            polygons.MoveToImmutable(),
            vertices.MoveToImmutable());
    }

    private static void CheckCancellation(
        CancellationToken cancellationToken,
        int index)
    {
        if ((index & 0xFF) == 0)
            cancellationToken.ThrowIfCancellationRequested();
    }

    private enum CollisionPayloadKind : byte
    {
        GfxObj = 1,
        Setup = 2,
        CellStructure = 3,
        EnvCellTopology = 4,
    }

    private sealed class CollisionWriter
    {
        private readonly ArrayBufferWriter<byte> _buffer = new(256);

        public void WriteHeader(CollisionPayloadKind kind)
        {
            WriteUInt32(Magic);
            WriteByte((byte)kind);
            WriteByte(SchemaVersion);
            WriteUInt16(0);
        }

        public void WriteCount(int value, string name)
        {
            if ((uint)value > MaximumRows)
            {
                throw new InvalidDataException(
                    $"{name} count {value} exceeds {MaximumRows}.");
            }
            WriteInt32(value);
        }

        public void WriteOptionalSphere(FlatCollisionSphere? sphere)
        {
            WriteBoolean(sphere.HasValue);
            if (sphere.HasValue)
                WriteSphere(sphere.Value);
        }

        public void WriteBoolean(bool value) => WriteByte(value ? (byte)1 : (byte)0);

        public void WriteSphere(FlatCollisionSphere sphere)
        {
            WriteVector3(sphere.Origin);
            WriteSingle(sphere.Radius);
        }

        public void WriteRange(FlatIndexRange range)
        {
            WriteInt32(range.Start);
            WriteInt32(range.Count);
        }

        public void WritePlane(Plane plane)
        {
            WriteVector3(plane.Normal);
            WriteSingle(plane.D);
        }

        public void WriteVector3(Vector3 vector)
        {
            WriteSingle(vector.X);
            WriteSingle(vector.Y);
            WriteSingle(vector.Z);
        }

        public void WriteSingle(float value) =>
            WriteInt32(BitConverter.SingleToInt32Bits(value));

        public void WriteByte(byte value)
        {
            Span<byte> span = _buffer.GetSpan(1);
            span[0] = value;
            _buffer.Advance(1);
        }

        public void WriteUInt16(ushort value)
        {
            Span<byte> span = _buffer.GetSpan(sizeof(ushort));
            BinaryPrimitives.WriteUInt16LittleEndian(span, value);
            _buffer.Advance(sizeof(ushort));
        }

        public void WriteInt32(int value)
        {
            Span<byte> span = _buffer.GetSpan(sizeof(int));
            BinaryPrimitives.WriteInt32LittleEndian(span, value);
            _buffer.Advance(sizeof(int));
        }

        public void WriteUInt32(uint value)
        {
            Span<byte> span = _buffer.GetSpan(sizeof(uint));
            BinaryPrimitives.WriteUInt32LittleEndian(span, value);
            _buffer.Advance(sizeof(uint));
        }

        public byte[] ToArray() => _buffer.WrittenSpan.ToArray();
    }

    private ref struct CollisionReader
    {
        private readonly ReadOnlySpan<byte> _bytes;
        private readonly CancellationToken _cancellationToken;
        private int _offset;

        public CollisionReader(
            ReadOnlySpan<byte> bytes,
            CancellationToken cancellationToken)
        {
            _bytes = bytes;
            _cancellationToken = cancellationToken;
            _offset = 0;
            _cancellationToken.ThrowIfCancellationRequested();
        }

        private int Remaining => _bytes.Length - _offset;

        public void ReadHeader(CollisionPayloadKind expectedKind)
        {
            uint magic = ReadUInt32();
            byte kind = ReadByte();
            byte version = ReadByte();
            ushort reserved = ReadUInt16();
            if (magic != Magic)
            {
                throw new InvalidDataException(
                    $"collision payload magic 0x{magic:X8} does not match " +
                    $"0x{Magic:X8}.");
            }
            if (kind != (byte)expectedKind)
            {
                throw new InvalidDataException(
                    $"collision payload kind {kind} does not match " +
                    $"{(byte)expectedKind}.");
            }
            if (version != SchemaVersion)
            {
                throw new InvalidDataException(
                    $"collision payload schema {version} is unsupported.");
            }
            if (reserved != 0)
            {
                throw new InvalidDataException(
                    "collision payload reserved header bits are non-zero.");
            }
        }

        public int ReadCount(string name, int bytesPerItem)
        {
            int count = ReadInt32();
            if (count < 0 || count > MaximumRows)
            {
                throw new InvalidDataException(
                    $"{name} count {count} is outside [0,{MaximumRows}].");
            }
            if (bytesPerItem <= 0 || count > Remaining / bytesPerItem)
            {
                throw new InvalidDataException(
                    $"{name} count {count} exceeds the remaining payload.");
            }
            return count;
        }

        public FlatCollisionSphere? ReadOptionalSphere() =>
            ReadBoolean() ? ReadSphere() : null;

        public bool ReadBoolean()
        {
            byte value = ReadByte();
            return value switch
            {
                0 => false,
                1 => true,
                _ => throw new InvalidDataException(
                    $"invalid collision boolean value {value}."),
            };
        }

        public FlatCollisionSphere ReadSphere() =>
            new(ReadVector3(), ReadSingle());

        public FlatIndexRange ReadRange() =>
            new(ReadInt32(), ReadInt32());

        public Plane ReadPlane() =>
            new(ReadVector3(), ReadSingle());

        public Plane ReadPlaneAfterMetadata(
            out CullMode sides,
            out int numPoints,
            out FlatIndexRange range)
        {
            sides = (CullMode)ReadUInt32();
            numPoints = ReadInt32();
            range = ReadRange();
            return ReadPlane();
        }

        public Vector3 ReadVector3() =>
            new(ReadSingle(), ReadSingle(), ReadSingle());

        public float ReadSingle() =>
            BitConverter.Int32BitsToSingle(ReadInt32());

        public byte ReadByte()
        {
            Require(sizeof(byte));
            return _bytes[_offset++];
        }

        public ushort ReadUInt16()
        {
            ReadOnlySpan<byte> span = Take(sizeof(ushort));
            return BinaryPrimitives.ReadUInt16LittleEndian(span);
        }

        public int ReadInt32()
        {
            ReadOnlySpan<byte> span = Take(sizeof(int));
            return BinaryPrimitives.ReadInt32LittleEndian(span);
        }

        public uint ReadUInt32()
        {
            ReadOnlySpan<byte> span = Take(sizeof(uint));
            return BinaryPrimitives.ReadUInt32LittleEndian(span);
        }

        public void CheckCancellation(int index)
        {
            if ((index & 0xFF) == 0)
                _cancellationToken.ThrowIfCancellationRequested();
        }

        public void RequireEnd()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (_offset != _bytes.Length)
            {
                throw new InvalidDataException(
                    $"collision payload contains {_bytes.Length - _offset} " +
                    "trailing byte(s).");
            }
        }

        public void RequireRemaining(long required, string name)
        {
            if (required < 0 || required > Remaining)
            {
                throw new InvalidDataException(
                    $"{name} require {required} bytes but only {Remaining} remain.");
            }
        }

        private ReadOnlySpan<byte> Take(int length)
        {
            Require(length);
            ReadOnlySpan<byte> result = _bytes.Slice(_offset, length);
            _offset += length;
            return result;
        }

        private void Require(int length)
        {
            if (length < 0 || length > Remaining)
                throw new EndOfStreamException("collision payload is truncated.");
        }
    }
}
