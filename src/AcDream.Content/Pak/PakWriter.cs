using System.Buffers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace AcDream.Content.Pak;

public sealed class PakWriter : IDisposable {
    private const int Alignment = 64;

    private readonly FileStream _stream;
    private readonly PakHeader _headerTemplate;
    private readonly List<PakTocEntry> _tocEntries = new();
    private readonly HashSet<ulong> _seenKeys = new();
    private readonly Dictionary<ulong, PakTocEntry> _receiptByKey = new();
    private readonly Dictionary<ulong, TextureIdentity> _texturePayloadByKey = new();
    private bool _finished;

    public int TextureBlobCount { get; private set; }
    public int EntryCount => _tocEntries.Count;
    public int PhysicalBlobCount { get; private set; }
    public long DecodedPayloadBytes { get; private set; }
    public long StoredPayloadBytes { get; private set; }
    public int CompressedBlobCount { get; private set; }

    public PakWriter(string path, PakHeader headerTemplate) {
        _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        _headerTemplate = headerTemplate;
        _headerTemplate.FormatVersion = PakFormat.CurrentFormatVersion;
        _headerTemplate.BakeToolVersion = PakFormat.CurrentBakeToolVersion;

        // Write a header placeholder now; finalized in Finish() once TocOffset/TocCount are known.
        _headerTemplate.WriteTo(_stream);
        PadToAlignment();
    }

    public void AddBlob(ulong key, ObjectMeshData data) {
        ThrowIfFinished();
        ReserveKey(key);

        using var ms = new MemoryStream();
        ObjectMeshDataSerializer.WriteExternalTextures(
            data,
            ms,
            RegisterTexturePayload);
        if (!ms.TryGetBuffer(out ArraySegment<byte> buffer))
            throw new InvalidOperationException("mesh serializer did not expose its write buffer");
        AddReservedBlob(key, buffer.AsSpan(0, checked((int)ms.Length)));
    }

    public void AddBlob(ulong key, byte[] bytes) {
        ArgumentNullException.ThrowIfNull(bytes);
        ThrowIfFinished();
        ReserveKey(key);
        AddReservedBlob(key, bytes);
    }

    private void AddReservedBlob(ulong key, ReadOnlySpan<byte> bytes) {
        bool compressed = PakBlobCodec.TryCompress(
            bytes,
            out byte[]? rented,
            out int storedLength);
        try
        {
            ReadOnlySpan<byte> stored = compressed
                ? rented.AsSpan(0, storedLength)
                : bytes;
            long offset = _stream.Position;
            _stream.Write(stored);
            PadToAlignment();

            var receipt = new PakTocEntry {
                Key = key,
                Offset = (ulong)offset,
                Length = checked((uint)stored.Length)
                    | (compressed ? PakTocEntry.CompressionFlag : 0u),
                Crc32 = Content.Pak.Crc32.Compute(stored),
            };
            _tocEntries.Add(receipt);
            _receiptByKey.Add(key, receipt);
            PhysicalBlobCount++;
            DecodedPayloadBytes = checked(DecodedPayloadBytes + bytes.Length);
            StoredPayloadBytes = checked(StoredPayloadBytes + stored.Length);
            if (compressed)
                CompressedBlobCount++;
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private ulong RegisterTexturePayload(TextureKey _, byte[] bytes)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, digest);
        ulong payloadId =
            ((ulong)digest[0] << 48)
            | ((ulong)digest[1] << 40)
            | ((ulong)digest[2] << 32)
            | ((ulong)digest[3] << 24)
            | ((ulong)digest[4] << 16)
            | ((ulong)digest[5] << 8)
            | digest[6];
        ulong key = PakKey.ComposeOpaque(
            PakAssetType.TexturePayload,
            payloadId);

        if (_texturePayloadByKey.TryGetValue(key, out TextureIdentity existing))
        {
            if (existing.Length != bytes.Length
                || !CryptographicOperations.FixedTimeEquals(
                    existing.Sha256,
                    digest))
            {
                throw new InvalidDataException(
                    $"texture payload identity collision at key 0x{key:X16}");
            }

            return key;
        }

        ReserveKey(key);
        AddReservedBlob(key, bytes);
        _texturePayloadByKey.Add(
            key,
            new TextureIdentity(bytes.Length, digest.ToArray()));
        TextureBlobCount++;
        return key;
    }

    private readonly record struct TextureIdentity(int Length, byte[] Sha256);

    private void ReserveKey(ulong key) {
        if (!_seenKeys.Add(key)) {
            throw new ArgumentException($"duplicate pak key 0x{key:X16}", nameof(key));
        }
    }

    /// <summary>
    /// Adds a second independently addressable key for an existing physical
    /// blob. The alias receives a normal TOC row with the source blob's exact
    /// offset, length, and CRC; no payload bytes are written again.
    /// </summary>
    public void AddAlias(ulong aliasKey, ulong existingKey) {
        ThrowIfFinished();
        if (aliasKey == existingKey) {
            throw new ArgumentException("an alias key must differ from its source key", nameof(aliasKey));
        }
        if (!_receiptByKey.TryGetValue(existingKey, out var source)) {
            throw new KeyNotFoundException($"source pak key 0x{existingKey:X16} has not been written");
        }
        if (!_seenKeys.Add(aliasKey)) {
            throw new ArgumentException($"duplicate pak key 0x{aliasKey:X16}", nameof(aliasKey));
        }

        var alias = source;
        alias.Key = aliasKey;
        _tocEntries.Add(alias);
        _receiptByKey.Add(aliasKey, alias);
    }

    public void Finish() {
        ThrowIfFinished();
        _finished = true;

        var sorted = _tocEntries.OrderBy(e => e.Key).ToList();
        ulong tocOffset = (ulong)_stream.Position;
        foreach (var entry in sorted) {
            entry.WriteTo(_stream);
        }

        var finalHeader = _headerTemplate;
        finalHeader.TocOffset = tocOffset;
        finalHeader.TocCount = (uint)sorted.Count;

        _stream.Position = 0;
        finalHeader.WriteTo(_stream);
        _stream.Flush();
    }

    private void PadToAlignment() {
        long pos = _stream.Position;
        long remainder = pos % Alignment;
        if (remainder == 0) return;
        long pad = Alignment - remainder;
        Span<byte> zeros = stackalloc byte[(int)pad];
        zeros.Clear();
        _stream.Write(zeros);
    }

    private void ThrowIfFinished() {
        if (_finished) throw new InvalidOperationException("PakWriter.Finish() has already been called.");
    }

    public void Dispose() {
        try {
            if (!_finished) {
                Finish();
            }
        }
        finally {
            _stream.Dispose();
        }
    }
}
