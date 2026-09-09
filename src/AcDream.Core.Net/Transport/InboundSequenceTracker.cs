using System.Buffers.Binary;
using AcDream.Core.Net.Cryptography;

namespace AcDream.Core.Net.Transport;

internal sealed class InboundSequenceTracker
{
    internal const uint AceInitialWatermark = 1;

    internal const uint SanityWindow = 0x7FFF;

    private readonly IsaacRandom _inboundIsaac;
    private readonly TransportStats _stats;

    internal readonly record struct ParkedWord(uint Word, ulong DrawOrder);

    private readonly SortedDictionary<uint, ParkedWord> _nakSet = new();

    private readonly PriorityQueue<uint, ulong> _reclaimedWords = new();

    /// <summary>Scratch for the reject bubble-shift — reused, never
    /// allocated on the steady-state path.</summary>
    private readonly List<uint> _rejectShiftScratch = new();

    /// <summary>Monotonic ordinal stamped on every fresh inbound ISAAC
    /// draw; positions in this sequence ARE server stream positions minus
    /// the reclaimed (never-drawn-server-side) entries.</summary>
    private ulong _drawOrdinal;

    public uint HighestIdReceived { get; private set; }

    public int NakCount => _nakSet.Count;

    public int ReclaimedWordCount => _reclaimedWords.Count;

    public InboundSequenceTracker(
        IsaacRandom inboundIsaac,
        TransportStats stats,
        uint initialWatermark = AceInitialWatermark)
    {
        ArgumentNullException.ThrowIfNull(inboundIsaac);
        ArgumentNullException.ThrowIfNull(stats);
        _inboundIsaac = inboundIsaac;
        _stats = stats;
        HighestIdReceived = initialWatermark;
    }

    public readonly record struct Admission(
        bool Drop,
        uint? VerifyKey,
        ulong VerifyKeyDrawOrder)
    {
        public static Admission Dropped => new(true, null, 0);

        public static Admission Process(
            uint? verifyKey,
            ulong verifyKeyDrawOrder = ulong.MaxValue) =>
            new(false, verifyKey, verifyKeyDrawOrder);
    }

    public Admission Admit(uint sequence, bool encrypted)
    {
        if (SequenceMath.IsNewer(
                sequence,
                unchecked(HighestIdReceived + SanityWindow)))
        {
            _stats.InboundSanityDrops++;
            return Admission.Dropped;
        }

        bool newer = SequenceMath.IsNewer(sequence, HighestIdReceived);

        ParkedWord? parkedKey = null;
        if (encrypted && !newer)
        {
            if (!_nakSet.Remove(sequence, out ParkedWord parked))
            {
                _stats.InboundDupsDropped++;
                return Admission.Dropped;
            }

            parkedKey = parked;
        }

        if (newer)
        {
            uint end = encrypted ? sequence : unchecked(sequence + 1u);
            for (uint id = unchecked(HighestIdReceived + 1u);
                 id != end;
                 id = unchecked(id + 1u))
            {
                if (id != 0)
                    AddNakked(id);
            }

            HighestIdReceived = sequence;
        }

        if (!encrypted)
            return Admission.Process(null);

        ParkedWord own = parkedKey ?? NextWord();
        return Admission.Process(own.Word, own.DrawOrder);
    }

    public void ReparkKey(uint sequence, uint key, ulong drawOrder)
    {
        if (_nakSet.ContainsKey(sequence))
            return;

        _nakSet.Add(sequence, new ParkedWord(key, drawOrder));
        _stats.KeysParked++;
    }

    public void OnRejectRetransmit(ReadOnlySpan<byte> idBytes, int count)
    {
        if (count <= 0 || idBytes.Length < count * 4)
            return;

        for (int i = 0; i < count; i++)
        {
            _nakSet.Remove(
                BinaryPrimitives.ReadUInt32LittleEndian(
                    idBytes.Slice(i * 4)));
        }
    }

    public void CopyNakkedSequencesAscending(
        List<uint> destination,
        int maxCount = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        foreach (uint sequence in _nakSet.Keys)
        {
            if (destination.Count >= maxCount)
                break;
            destination.Add(sequence);
        }
    }

    public void OnCleartextRejectSequence(uint sequence)
    {
        if (!_nakSet.Remove(sequence, out ParkedWord reclaimed))
            return;

        // Everything drawn after the mis-park sits one server position
        // ahead. Ascending draw order ⇔ ascending wrap-safe id inside the
        // set, so ordering by draw ordinal is both wrap-proof and exactly
        // "the ids newer than the reject".
        _rejectShiftScratch.Clear();
        foreach (KeyValuePair<uint, ParkedWord> entry in _nakSet)
        {
            if (entry.Value.DrawOrder > reclaimed.DrawOrder)
                _rejectShiftScratch.Add(entry.Key);
        }

        _rejectShiftScratch.Sort(
            (a, b) => _nakSet[a].DrawOrder.CompareTo(_nakSet[b].DrawOrder));

        ParkedWord carry = reclaimed;
        foreach (uint id in _rejectShiftScratch)
        {
            ParkedWord displaced = _nakSet[id];
            _nakSet[id] = carry;
            carry = displaced;
        }

        _reclaimedWords.Enqueue(carry.Word, carry.DrawOrder);
        _stats.RejectWordsReclaimed++;
    }

    private ParkedWord NextWord()
    {
        if (_reclaimedWords.TryDequeue(out uint word, out ulong order))
            return new ParkedWord(word, order);

        return new ParkedWord(_inboundIsaac.Next(), ++_drawOrdinal);
    }

    private void AddNakked(uint sequence)
    {
        if (_nakSet.ContainsKey(sequence))
            return;

        _nakSet.Add(sequence, NextWord());
        _stats.KeysParked++;
    }
}
