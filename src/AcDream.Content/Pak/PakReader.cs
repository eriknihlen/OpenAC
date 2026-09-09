using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace AcDream.Content.Pak;

public sealed class PakReader : IDisposable {
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly long _fileLength;
    private readonly PakTocEntry[] _toc; // sorted ascending by Key
    private readonly PakTexturePayloadCache _texturePayloads = new();

    /// <summary>Lazy per-entry verdict: absent = not yet judged, 0 = bad (bounds/crc/structure), 1 = ok.</summary>
    private readonly ConcurrentDictionary<int, int> _entryVerdictByTocIndex = new();
    private readonly ConcurrentDictionary<int, bool> _loggedCorruption = new();

    public PakHeader Header { get; }
    public long FileLength => _fileLength;

    public PakReader(string path) {
        _fileLength = new FileInfo(path).Length;
        if (_fileLength < PakHeader.Size) {
            throw new InvalidDataException($"pak file '{path}' is {_fileLength} bytes — smaller than the {PakHeader.Size}-byte header");
        }

        _mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
        _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

        var headerBytes = new byte[PakHeader.Size];
        _accessor.ReadArray(0, headerBytes, 0, PakHeader.Size);
        Header = PakHeader.ReadFrom((ReadOnlySpan<byte>)headerBytes);

        if (Header.FormatVersion != PakFormat.CurrentFormatVersion) {
            throw new InvalidDataException(
                $"pak file '{path}' has format version {Header.FormatVersion}; this build reads only " +
                $"version {PakFormat.CurrentFormatVersion}. Re-bake with the matching acdream-bake.");
        }

        if (Header.TocOffset < PakHeader.Size) {
            throw new InvalidDataException(
                $"pak file '{path}' has an unfinalized header (tocOffset={Header.TocOffset}) — " +
                "the bake was interrupted before Finish(); re-bake.");
        }

        long tocBytesTotal = (long)Header.TocCount * PakTocEntry.Size;
        if ((long)Header.TocOffset + tocBytesTotal > _fileLength) {
            throw new InvalidDataException(
                $"pak file '{path}' TOC (offset {Header.TocOffset}, {Header.TocCount} entries) extends past " +
                $"the file's actual length ({_fileLength} bytes) — truncated or corrupt file");
        }

        _toc = new PakTocEntry[Header.TocCount];
        var tocBytes = new byte[PakTocEntry.Size];
        long tocPos = (long)Header.TocOffset;
        for (int i = 0; i < _toc.Length; i++) {
            _accessor.ReadArray(tocPos, tocBytes, 0, PakTocEntry.Size);
            _toc[i] = PakTocEntry.ReadFrom((ReadOnlySpan<byte>)tocBytes);
            tocPos += PakTocEntry.Size;

            ref readonly var entry = ref _toc[i];
            bool invalid = !IsEntryRangeValid(entry);
            if (invalid) {
                _entryVerdictByTocIndex[i] = 0;
                LogCorruptionOnce(i, $"TOC entry out of bounds (offset={entry.Offset}, storedLength={entry.StoredLength}, " +
                    $"file={_fileLength}, toc@{Header.TocOffset})");
            }
        }
    }

    /// <summary>True if <paramref name="key"/> is present AND its blob verifies (bounds + CRC).</summary>
    public bool ContainsKey(ulong key) {
        int index = BinarySearch(key);
        if (index < 0) return false;
        return VerdictFor(index) == 1;
    }

    public PakEntryState ProbeEntry(ulong key) {
        int index = BinarySearch(key);
        if (index < 0)
            return PakEntryState.Missing;
        return _entryVerdictByTocIndex.TryGetValue(index, out int verdict)
            && verdict == 0
                ? PakEntryState.Corrupt
                : PakEntryState.Available;
    }

    public bool TryReadObjectMeshData(ulong key, out ObjectMeshData? data) =>
        ReadObjectMeshData(key, out data) == PakObjectReadStatus.Loaded;

    public PakObjectReadStatus ReadObjectMeshData(
        ulong key,
        out ObjectMeshData? data) {
        data = null;
        PakAssetType type = PakKey.Decompose(key).Type;
        if (type is not (
            PakAssetType.GfxObjMesh or
            PakAssetType.SetupMesh or
            PakAssetType.EnvCellMesh)) {
            return PakObjectReadStatus.Missing;
        }

        PakObjectReadStatus status = ReadBlobBytes(key, out byte[]? bytes);
        if (status != PakObjectReadStatus.Loaded || bytes is null)
            return status;

        try {
            data = ObjectMeshDataSerializer.ReadExternalTextures(
                bytes,
                ResolveTexturePayload);
            return PakObjectReadStatus.Loaded;
        }
        catch (Exception ex) {
            // Structurally malformed blob behind a valid CRC (bake-side bug or
            // a tamper that recomputed the CRC). External-file input: demote
            // to missing, log once — never propagate from a lookup.
            data = null;
            MarkPayloadCorrupt(
                key,
                $"deserialization failed despite matching CRC: " +
                $"{ex.GetType().Name}: {ex.Message}");
            return PakObjectReadStatus.Corrupt;
        }
    }

    internal PakObjectReadStatus ReadBlobBytes(
        ulong key,
        out byte[]? bytes) {
        bytes = null;
        int index = BinarySearch(key);
        if (index < 0) return PakObjectReadStatus.Missing;

        // Previously judged bad (bounds at open, or an earlier CRC/structure
        // failure): missing, no re-read, no re-log.
        bool judged = _entryVerdictByTocIndex.TryGetValue(index, out var verdict);
        if (judged && verdict == 0) return PakObjectReadStatus.Corrupt;

        ref readonly var entry = ref _toc[index];
        uint storedLength = entry.StoredLength;
        bytes = new byte[checked((int)storedLength)];
        _accessor.ReadArray((long)entry.Offset, bytes, 0, checked((int)storedLength));

        if (!judged) {
            uint actualCrc = Crc32.Compute(bytes);
            if (actualCrc != entry.Crc32) {
                _entryVerdictByTocIndex[index] = 0;
                LogCorruptionOnce(index, $"crc mismatch (expected 0x{entry.Crc32:X8}, got 0x{actualCrc:X8})");
                bytes = null;
                return PakObjectReadStatus.Corrupt;
            }
            _entryVerdictByTocIndex[index] = 1;
        }

        if (entry.IsCompressed) {
            try {
                bytes = PakBlobCodec.Decode(bytes);
            }
            catch (Exception ex) when (ex is InvalidDataException or OverflowException) {
                _entryVerdictByTocIndex[index] = 0;
                LogCorruptionOnce(
                    index,
                    $"decompression failed despite matching CRC: {ex.Message}");
                bytes = null;
                return PakObjectReadStatus.Corrupt;
            }
        }

        return PakObjectReadStatus.Loaded;
    }

    private byte[] ResolveTexturePayload(ulong key) {
        if (PakKey.Decompose(key).Type != PakAssetType.TexturePayload) {
            throw new InvalidDataException(
                $"mesh references non-texture pak key 0x{key:X16}");
        }

        if (_texturePayloads.TryGet(key, out byte[] cachedBytes))
            return cachedBytes;

        PakObjectReadStatus status = ReadBlobBytes(key, out byte[]? loadedBytes);
        if (status != PakObjectReadStatus.Loaded || loadedBytes is null) {
            throw new InvalidDataException(
                $"mesh references {status.ToString().ToLowerInvariant()} texture payload 0x{key:X16}");
        }

        return _texturePayloads.AddOrGet(key, loadedBytes);
    }

    internal void MarkPayloadCorrupt(ulong key, string reason) {
        int index = BinarySearch(key);
        if (index < 0)
            return;
        _entryVerdictByTocIndex[index] = 0;
        LogCorruptionOnce(index, reason);
    }

    /// <summary>Returns the blob's file offset for alignment assertions in tests.</summary>
    public long GetBlobOffsetForTest(ulong key) {
        int index = BinarySearch(key);
        if (index < 0) throw new KeyNotFoundException($"pak key 0x{key:X16} not found");
        return (long)_toc[index].Offset;
    }

    /// <summary>Returns the immutable TOC receipt for alias/layout assertions in tests.</summary>
    public PakTocEntry GetTocEntryForTest(ulong key) {
        int index = BinarySearch(key);
        if (index < 0) throw new KeyNotFoundException($"pak key 0x{key:X16} not found");
        return _toc[index];
    }

    public int CountEntries(PakAssetType type) {
        byte rawType = (byte)type;
        int count = 0;
        for (int i = 0; i < _toc.Length; i++) {
            if ((byte)(_toc[i].Key >> 56) == rawType)
                count++;
        }
        return count;
    }

    public void ValidateTocStructure() {
        ulong previousKey = 0;

        for (int i = 0; i < _toc.Length; i++) {
            ref readonly var entry = ref _toc[i];
            if (i > 0 && entry.Key <= previousKey) {
                throw new InvalidDataException(
                    $"pak TOC is not strictly sorted at entry {i}: " +
                    $"0x{entry.Key:X16} follows 0x{previousKey:X16}");
            }
            previousKey = entry.Key;

            if (!IsEntryRangeValid(entry)) {
                throw new InvalidDataException(
                    $"pak TOC entry 0x{entry.Key:X16} has an invalid range " +
                    $"(offset={entry.Offset}, storedLength={entry.StoredLength}, " +
                    $"file={_fileLength}, toc@{Header.TocOffset})");
            }
        }
    }

    public bool DebugLinearScanContainsKey(ulong key) {
        for (int i = 0; i < _toc.Length; i++) {
            if (_toc[i].Key == key) return VerdictFor(i) == 1;
        }
        return false;
    }

    private int VerdictFor(int tocIndex) {
        if (_entryVerdictByTocIndex.TryGetValue(tocIndex, out var cached)) return cached;

        ref readonly var entry = ref _toc[tocIndex];
        uint storedLength = entry.StoredLength;
        var bytes = new byte[checked((int)storedLength)];
        _accessor.ReadArray((long)entry.Offset, bytes, 0, checked((int)storedLength));
        uint actualCrc = Crc32.Compute(bytes);
        bool ok = actualCrc == entry.Crc32;

        _entryVerdictByTocIndex[tocIndex] = ok ? 1 : 0;
        if (!ok) {
            LogCorruptionOnce(tocIndex, $"crc mismatch (expected 0x{entry.Crc32:X8}, got 0x{actualCrc:X8})");
        }
        return ok ? 1 : 0;
    }

    private void LogCorruptionOnce(int tocIndex, string reason) {
        if (!_loggedCorruption.TryAdd(tocIndex, true)) return;
        ref readonly var entry = ref _toc[tocIndex];
        Console.Error.WriteLine(
            $"[pak-corrupt] key 0x{entry.Key:X16} at offset {entry.Offset} " +
            $"(storedLength {entry.StoredLength}, compressed={entry.IsCompressed}): " +
            $"{reason} — treating as missing");
    }

    private int BinarySearch(ulong key) {
        int lo = 0, hi = _toc.Length - 1;
        while (lo <= hi) {
            int mid = lo + (hi - lo) / 2;
            ulong midKey = _toc[mid].Key;
            if (midKey == key) return mid;
            if (midKey < key) lo = mid + 1;
            else hi = mid - 1;
        }
        return -1;
    }

    private bool IsEntryRangeValid(in PakTocEntry entry) {
        ulong fileLength = (ulong)_fileLength;
        if (entry.Offset < PakHeader.Size ||
            entry.Offset > long.MaxValue ||
            entry.Offset > Header.TocOffset ||
            entry.Offset > fileLength) {
            return false;
        }

        if (entry.StoredLength > PakBlobCodec.MaximumDecodedBytes)
            return false;

        return entry.StoredLength <= Header.TocOffset - entry.Offset &&
               entry.StoredLength <= fileLength - entry.Offset;
    }

    public void Dispose() {
        _accessor.Dispose();
        _mmf.Dispose();
    }
}
