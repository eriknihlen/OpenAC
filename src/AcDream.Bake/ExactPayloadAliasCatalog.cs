using System.Security.Cryptography;

namespace AcDream.Bake;

internal sealed class ExactPayloadAliasCatalog
{
    private readonly Dictionary<string, List<Entry>> _entriesByDigest =
        new(StringComparer.Ordinal);

    public bool TryFind(ReadOnlySpan<byte> payload, out ulong primaryKey)
    {
        string digest = Digest(payload);
        if (_entriesByDigest.TryGetValue(digest, out List<Entry>? candidates))
        {
            foreach (Entry candidate in candidates)
            {
                if (payload.SequenceEqual(candidate.Payload))
                {
                    primaryKey = candidate.PrimaryKey;
                    return true;
                }
            }
        }

        primaryKey = 0;
        return false;
    }

    public void Add(ulong primaryKey, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        string digest = Digest(payload);
        if (!_entriesByDigest.TryGetValue(digest, out List<Entry>? entries))
        {
            entries = [];
            _entriesByDigest.Add(digest, entries);
        }

        entries.Add(new Entry(primaryKey, payload));
    }

    private static string Digest(ReadOnlySpan<byte> payload) =>
        Convert.ToHexString(SHA256.HashData(payload));

    private sealed record Entry(ulong PrimaryKey, byte[] Payload);
}
