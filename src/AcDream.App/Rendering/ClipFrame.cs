using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace AcDream.App.Rendering;

public sealed class ClipFrame : IDisposable
{

    public const int MaxPlanes = 8;

    public const int CellClipStrideBytes = 16 + MaxPlanes * 16; // 144

    public const int CellClipPlanesOffset = 16;

    public const int TerrainUboBytes = 16 + MaxPlanes * 16; // 144

    public const uint TerrainClipUboBinding = 2;

    // ---- CPU-side state ------------------------------------------------------

    private byte[] _regionBytes;
    private int _slotCount;

    internal int DynamicBufferSetCount => 0;

    private ClipFrame(byte[] regionBytes, int slotCount)
    {
        _regionBytes = regionBytes;
        _slotCount = slotCount;
    }

    public static ClipFrame NoClip()
    {
        var bytes = new byte[CellClipStrideBytes];
        return new ClipFrame(bytes, slotCount: 1);
    }

    public int SlotCount => _slotCount;

    public void Reset()
    {
        if (_regionBytes.Length < CellClipStrideBytes)
            EnsureRegionCapacity(CellClipStrideBytes);
        Array.Clear(_regionBytes, 0, CellClipStrideBytes);
        _slotCount = 1;
    }

    public void BeginFrame(int frameSlot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameSlot);
    }

    public int AppendSlot(ClipPlaneSet set)
    {
        int count = Math.Min(set.Count, MaxPlanes);
        if (count == 0)
            return AppendSlot(ReadOnlySpan<Vector4>.Empty);

        Span<Vector4> planes = stackalloc Vector4[count];
        for (int i = 0; i < count; i++)
            planes[i] = set.Planes[i];
        return AppendSlot(planes);
    }

    public int AppendSlot(ReadOnlySpan<Vector4> planes)
    {
        int count = Math.Min(planes.Length, MaxPlanes);

        int slot = _slotCount;
        int byteOffset = slot * CellClipStrideBytes;
        EnsureRegionCapacity(byteOffset + CellClipStrideBytes);

        WriteUInt(_regionBytes, byteOffset, (uint)count);

        for (int i = 0; i < count; i++)
        {
            int po = byteOffset + CellClipPlanesOffset + i * 16;
            WriteVec4(_regionBytes, po, planes[i]);
        }

        _slotCount++;
        return slot;
    }

    internal ReadOnlySpan<Vector4> GetSlotPlanes(uint slot)
    {
        if (slot >= (uint)_slotCount)
            throw new ArgumentOutOfRangeException(nameof(slot));

        int byteOffset = checked((int)slot * CellClipStrideBytes);
        int count = checked((int)ReadUInt(_regionBytes, byteOffset));
        if ((uint)count > MaxPlanes)
        {
            throw new InvalidOperationException(
                $"Clip slot {slot} contains invalid plane count {count}.");
        }

        return MemoryMarshal.Cast<byte, Vector4>(
            _regionBytes.AsSpan(
                byteOffset + CellClipPlanesOffset,
                count * sizeof(float) * 4));
    }


    public void Dispose()
    {
    }

    // ---- byte helpers (little-endian; matches x86/x64 GPU upload) ------------

    private void EnsureRegionCapacity(int requiredBytes)
    {
        if (_regionBytes.Length >= requiredBytes) return;
        int newLen = Math.Max(requiredBytes, _regionBytes.Length * 2);
        Array.Resize(ref _regionBytes, newLen);
    }

    private static void WriteUInt(byte[] dst, int offset, uint value)
    {
        dst[offset + 0] = (byte)(value & 0xFF);
        dst[offset + 1] = (byte)((value >> 8) & 0xFF);
        dst[offset + 2] = (byte)((value >> 16) & 0xFF);
        dst[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static uint ReadUInt(byte[] src, int offset) =>
        (uint)(src[offset + 0]
            | (src[offset + 1] << 8)
            | (src[offset + 2] << 16)
            | (src[offset + 3] << 24));

    private static void WriteInt(byte[] dst, int offset, int value)
        => WriteUInt(dst, offset, unchecked((uint)value));

    private static void WriteVec4(byte[] dst, int offset, Vector4 v)
    {
        WriteFloat(dst, offset + 0, v.X);
        WriteFloat(dst, offset + 4, v.Y);
        WriteFloat(dst, offset + 8, v.Z);
        WriteFloat(dst, offset + 12, v.W);
    }

    private static void WriteFloat(byte[] dst, int offset, float value)
    {
        uint bits = BitConverter.SingleToUInt32Bits(value);
        WriteUInt(dst, offset, bits);
    }

    // ---- Packed bytes --------------------------------------------------------

    internal ReadOnlySpan<byte> RegionBytes =>
        _regionBytes.AsSpan(0, _slotCount * CellClipStrideBytes);

    // ---- Test seams ----------------------------------------------------------

    internal ReadOnlySpan<byte> RegionBytesForTest => RegionBytes;
}
