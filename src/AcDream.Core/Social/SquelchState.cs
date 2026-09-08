namespace AcDream.Core.Social;

public sealed class SquelchState
{
    private readonly object _gate = new();
    private SquelchDatabase _database = SquelchDatabase.Empty;
    private long _revision;

    public long Revision => Interlocked.Read(ref _revision);

    public SquelchDatabase Snapshot()
    {
        lock (_gate) return _database;
    }

    public void Replace(SquelchDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        lock (_gate)
        {
            _database = database;
            Interlocked.Increment(ref _revision);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _database = SquelchDatabase.Empty;
            Interlocked.Increment(ref _revision);
        }
    }
}

public sealed record SquelchInfo(
    string Name,
    bool AccountWide,
    IReadOnlySet<uint> MessageTypes);

public sealed record SquelchDatabase(
    IReadOnlyDictionary<string, uint> Accounts,
    IReadOnlyDictionary<uint, SquelchInfo> Characters,
    SquelchInfo Global)
{
    public static SquelchDatabase Empty { get; } = new(
        new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<uint, SquelchInfo>(),
        new SquelchInfo(string.Empty, false, new HashSet<uint>()));
}
