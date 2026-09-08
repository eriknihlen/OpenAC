using System.Buffers.Binary;
using AcDream.Core.Net.Cryptography;

namespace AcDream.Core.Net.Tests.Transport;

internal sealed class AceCryptoModel
{
    public const int MaximumEffortLevel = 256;

    private readonly IsaacRandom _keystream;

    private readonly HashSet<uint> _xors = new();

    public uint CurrentKey { get; private set; }

    public AceCryptoModel(uint seed)
    {
        Span<byte> seedBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(seedBytes, seed);
        _keystream = new IsaacRandom(seedBytes);
        CurrentKey = _keystream.Next();
    }

    public int Headroom => MaximumEffortLevel - _xors.Count;

    public int OrphanCount => _xors.Count;

    public void ConsumeKey(uint x)
    {
        if (CurrentKey == x)
            CurrentKey = _keystream.Next();
        else
            _xors.Remove(x);
    }

    public bool Search(uint x)
    {
        if (CurrentKey == x)
            return true;
        if (_xors.Contains(x))
            return true;

        int g = _xors.Count;
        for (int i = 0; i < MaximumEffortLevel - g; i++)
        {
            _xors.Add(CurrentKey);
            ConsumeKey(CurrentKey);
            if (CurrentKey == x)
                return true;
        }

        return false;
    }
}
