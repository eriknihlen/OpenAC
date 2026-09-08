namespace AcDream.Core.Net.Packets;

public sealed class Packet
{
    public PacketHeader Header;
    public PacketHeaderOptional Optional { get; } = new();
    public List<MessageFragment> Fragments { get; } = new();

    public byte[] BodyBytes { get; set; } = Array.Empty<byte>();
}
