namespace AcDream.Core.Net.Cryptography;

public sealed class IsaacRandom
{
    private const int StateSize = 256;
    private const uint GoldenRatio = 0x9E3779B9u;

    private readonly uint[] _mm = new uint[StateSize];
    private readonly uint[] _rsl = new uint[StateSize];
    private uint _a;
    private uint _b;
    private uint _c;

    private int _offset;

    public IsaacRandom(ReadOnlySpan<byte> seedBytes)
    {
        if (seedBytes.Length < 4)
            throw new ArgumentException("seed must be at least 4 bytes", nameof(seedBytes));

        Initialize();

        uint seed = (uint)seedBytes[0]
                  | ((uint)seedBytes[1] << 8)
                  | ((uint)seedBytes[2] << 16)
                  | ((uint)seedBytes[3] << 24);
        _a = _b = _c = seed;

        Scramble();
        _offset = StateSize - 1;
    }

    public uint Next()
    {
        uint value = _rsl[_offset];
        if (_offset > 0)
        {
            _offset--;
        }
        else
        {
            Scramble();
            _offset = StateSize - 1;
        }
        return value;
    }

    private static void Mix(Span<uint> s)
    {
        s[0] ^= s[1] << 11; s[3] += s[0]; s[1] += s[2];
        s[1] ^= s[2] >>  2; s[4] += s[1]; s[2] += s[3];
        s[2] ^= s[3] <<  8; s[5] += s[2]; s[3] += s[4];
        s[3] ^= s[4] >> 16; s[6] += s[3]; s[4] += s[5];
        s[4] ^= s[5] << 10; s[7] += s[4]; s[5] += s[6];
        s[5] ^= s[6] >>  4; s[0] += s[5]; s[6] += s[7];
        s[6] ^= s[7] <<  8; s[1] += s[6]; s[7] += s[0];
        s[7] ^= s[0] >>  9; s[2] += s[7]; s[0] += s[1];
    }

    private void Initialize()
    {
        // mm[] and rsl[] start as all zeroes.
        Span<uint> s = stackalloc uint[8];
        for (int i = 0; i < 8; i++) s[i] = GoldenRatio;

        // 4 warmup rounds so the initial state diverges from the golden-ratio
        // pattern before we start folding in real values.
        for (int i = 0; i < 4; i++) Mix(s);

        // First pass folds _rsl (zeroes on a fresh instance) into mm[].
        for (int j = 0; j < StateSize; j += 8)
        {
            for (int k = 0; k < 8; k++) s[k] += _rsl[j + k];
            Mix(s);
            for (int k = 0; k < 8; k++) _mm[j + k] = s[k];
        }

        // Second pass folds mm[] (now populated) back into itself.
        for (int j = 0; j < StateSize; j += 8)
        {
            for (int k = 0; k < 8; k++) s[k] += _mm[j + k];
            Mix(s);
            for (int k = 0; k < 8; k++) _mm[j + k] = s[k];
        }
    }

    private void Scramble()
    {
        _c++;
        _b += _c;

        for (int i = 0; i < StateSize; i++)
        {
            uint x = _mm[i];
            switch (i & 3)
            {
                case 0: _a ^= _a << 13; break;
                case 1: _a ^= _a >>  6; break;
                case 2: _a ^= _a <<  2; break;
                case 3: _a ^= _a >> 16; break;
            }

            _a += _mm[(i + 128) & 0xFF];

            uint y = _mm[(int)((x >> 2) & 0xFF)] + _a + _b;
            _mm[i] = y;

            uint nextB = _mm[(int)((y >> 10) & 0xFF)] + x;
            _rsl[i] = nextB;
            _b = nextB;
        }
    }
}
