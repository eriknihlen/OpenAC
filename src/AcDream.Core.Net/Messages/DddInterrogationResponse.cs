using AcDream.Core.Net.Packets;

namespace AcDream.Core.Net.Messages;

public static class DddInterrogationResponse
{
    public const uint Opcode = 0xF7E6u;
    public const uint EnglishLanguage = 1u;

    public static byte[] Build(DddDataVersions? versions = null)
    {
        var w = new PacketWriter(16);
        w.WriteUInt32(Opcode);
        w.WriteUInt32(EnglishLanguage);
        w.WriteUInt32(versions.HasValue ? 3u : 0u);
        if (versions is { } installed)
        {
            WriteVersion(w, 0, 1, installed.Portal);
            WriteVersion(w, 1, 2, installed.Cell);
            WriteVersion(w, 1, 3, installed.Language);
        }
        w.WriteUInt32(0u);
        w.WriteUInt32(0u);
        return w.ToArray();
    }

    private static void WriteVersion(PacketWriter writer, uint type, uint id, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 100_000);
        writer.WriteUInt32(type);
        writer.WriteUInt32(id);
        writer.WriteUInt32((uint)count);
        if (count > 2)
        {
            writer.WriteUInt32(unchecked((uint)-count));
            writer.WriteUInt32(1u);
        }
        else
        {
            for (uint iteration = 1; iteration <= count; iteration++)
                writer.WriteUInt32(iteration);
        }
    }
}
