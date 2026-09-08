using System;

namespace AcDream.Content.Pak;

public static class Crc32 {
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable() {
        const uint polynomial = 0xEDB88320u;
        var table = new uint[256];
        for (uint i = 0; i < 256; i++) {
            uint c = i;
            for (int k = 0; k < 8; k++) {
                c = (c & 1) != 0 ? polynomial ^ (c >> 1) : c >> 1;
            }
            table[i] = c;
        }
        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data) {
        uint crc = 0xFFFFFFFFu;
        foreach (var b in data) {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
