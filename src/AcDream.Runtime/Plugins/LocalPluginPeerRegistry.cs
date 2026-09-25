using System.Text.Json;
using System.Text.Json.Serialization;
using AcDream.Plugin.Abstractions;

namespace AcDream.Runtime.Plugins;

/// <summary>
/// One cast this client wants the other clients on this computer to know
/// about, before the registry stamps it with a sequence and a time.
/// </summary>
/// <param name="CasterObjectId">The character that cast it.</param>
/// <param name="TargetObjectId">The object it was cast at.</param>
/// <param name="SpellId">The spell, from this client's own spell table.</param>
/// <param name="EffectiveSkill">The skill it was cast with.</param>
/// <param name="DurationSeconds">
/// How long the effect lasts in total, or zero for an attempt that has not
/// landed yet.
/// </param>
/// <param name="Landed">True when the cast is known to have landed.</param>
internal readonly record struct LocalPluginCast(
    uint CasterObjectId,
    uint TargetObjectId,
    uint SpellId,
    int EffectiveSkill,
    double DurationSeconds,
    bool Landed);

/// <summary>
/// One command line this client wants the other clients on this computer to
/// run, before the registry stamps it with a sequence and a time.
/// </summary>
/// <param name="SenderObjectId">The character asking for it.</param>
/// <param name="Tags">
/// The labels it is aimed at; empty aims it at every client.
/// </param>
/// <param name="Line">The command line, as it would be typed.</param>
/// <param name="DelayMilliseconds">
/// How far apart the recipients are asked to run it, one place in the order
/// per this many milliseconds.
/// </param>
internal readonly record struct LocalPluginCommand(
    uint SenderObjectId,
    IReadOnlyList<string> Tags,
    string Line,
    int DelayMilliseconds);

/// <summary>
/// One command line read from another client on this computer, with the wait
/// this client owes before running it. The wait is not part of what a plugin
/// is handed: it is how the client staggers itself against the other
/// recipients, which is the client's own business.
/// </summary>
/// <param name="Command">What a plugin reading the same line is handed.</param>
/// <param name="StaggerMilliseconds">
/// How long this client waits before running the line, being this client's
/// place in the recipients' order times the delay the sender asked for.
/// </param>
internal readonly record struct LocalPluginPeerCommand(
    PluginPeerCommand Command,
    int StaggerMilliseconds);

internal sealed class LocalPluginPeerRegistry : IDisposable
{
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How many casts one client's note carries. The note is a snapshot --
    /// the whole document is replaced on every write, last writer wins -- so
    /// a cast kept in a single field would be lost by the next heartbeat. A
    /// ring survives that: a reader polling slower than the writer still sees
    /// every cast, as long as no more than this many happen between two of
    /// its reads. Thirty-two is far more than one caster can produce inside
    /// the staleness window, and thirty-two of these entries is a couple of
    /// kilobytes against the document cap below.
    /// </summary>
    internal const int CastRingCapacity = 32;

    /// <summary>
    /// How many casts read from other clients this one keeps to hand back.
    /// Several plugins share one client, each reading with a cursor of its
    /// own, so a read cannot consume: one plugin polling would starve the
    /// next. Entries leave by age as well, so this is only a ceiling for a
    /// burst nobody has read yet.
    /// </summary>
    internal const int ObservedCastCapacity = 128;

    /// <summary>
    /// How often a client looks for command lines the others have asked it
    /// to run. Unlike a cast, which is read when a plugin asks for one, a
    /// broadcast line is something the client itself has to act on, so it
    /// polls. Four times a second is prompt enough for a line somebody just
    /// typed on another client and is a scan of a folder holding one small
    /// file per client running on this computer.
    /// </summary>
    internal static readonly TimeSpan CommandPollPeriod =
        TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How many broadcast command lines one client's note carries, for the
    /// same reason the cast ring exists: the note is a snapshot replaced on
    /// every write, so a line kept in a single field would be lost by the
    /// next heartbeat, and a reader polling slower than the writer still
    /// sees every line as long as no more than this many are sent between
    /// two of its reads.
    /// </summary>
    internal const int CommandRingCapacity = 32;

    /// <summary>
    /// How many command lines read from other clients this one keeps to hand
    /// back. A read cannot consume -- the client's own delivery and each
    /// plugin read with a cursor of their own -- so this is a ceiling for a
    /// burst nobody has read yet.
    /// </summary>
    internal const int ObservedCommandCapacity = 128;

    /// <summary>
    /// The longest command line a broadcast may carry. A command line is a
    /// line somebody could have typed, and 512 characters is far beyond any
    /// of those; a longer one is a broken or hostile note, and thirty-two of
    /// them still sit well inside the document cap below.
    /// </summary>
    internal const int MaximumCommandLineLength = 512;

    /// <summary>
    /// The longest one label may be, and how many a single broadcast may be
    /// aimed at. A label is a word the player chose for a client's role, so
    /// these are generous rather than tight; they keep one note from filling
    /// the document cap with labels alone.
    /// </summary>
    internal const int MaximumTagLength = 64;

    /// <summary>How many labels one broadcast may be aimed at.</summary>
    internal const int MaximumCommandTags = 16;

    /// <summary>
    /// The longest stagger a broadcast may ask for, per place in the
    /// recipients' order. A minute between two clients running the same line
    /// is already far past anything worth waiting for, and a note asking for
    /// more would park a line in every recipient for as long as it liked.
    /// </summary>
    internal const int MaximumCommandDelayMilliseconds = 60_000;

    /// <summary>
    /// The shortest gap between two notes written because of a cast. The
    /// first cast after a quiet moment is written at once; the ones behind it
    /// in a burst ride along on the next write. Without it a six-target
    /// debuff chain would rewrite the whole document six times inside a few
    /// hundred milliseconds.
    /// </summary>
    internal static readonly TimeSpan CastWriteDebounce =
        TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How often the note is rewritten when nothing in particular is
    /// happening -- the hosts tick on this period -- and how long this client
    /// holds off asking for another write after one it could not do. The two
    /// are the same number on purpose: a write that failed or was refused
    /// costs exactly what the heartbeat costs anyway, instead of being retried
    /// on every frame because the ring still has something waiting.
    /// </summary>
    internal static readonly TimeSpan HeartbeatPeriod = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The longest effect a cast may claim, in seconds. A day is far beyond
    /// anything a spell lasts, so a longer one is a broken or hostile note
    /// rather than a long buff, and a reader that believed it would hold a
    /// target as enchanted for ever.
    /// </summary>
    internal const double MaximumCastDurationSeconds = 24d * 60d * 60d;

    /// <summary>
    /// How many peers a plugin may import from elsewhere at once. A relay
    /// carries the characters one player or one group plays, which is a
    /// handful; the cap keeps a relay that imports whatever it is sent from
    /// growing this client's lists without limit. A peer that has gone stale
    /// gives its place up to a new one.
    /// </summary>
    internal const int MaximumRemoteClients = 256;

    /// <summary>
    /// The longest name or world name a note may carry, for a note read from
    /// a file and a peer imported by a plugin alike.
    /// </summary>
    internal const int MaximumNameLength = 128;

    /// <summary>How many labels one client may answer to.</summary>
    internal const int MaximumClientTags = 128;

    /// <summary>
    /// The highest sequence a cast may claim. Sequences count from one and
    /// only ever grow, and a reader sets its cursor for a peer from them, so
    /// a note claiming a number no client could have counted to would park
    /// that cursor past everything the genuine client will ever send. A
    /// thousand million is years of casting several times a second, and a
    /// note above it is broken or hostile rather than long-lived.
    /// </summary>
    internal const long MaximumCastSequence = 1_000_000_000L;

    /// <summary>
    /// The earliest and latest instants a cast may be stamped with. A note is
    /// a file written by another process, so its stamp is any eight bytes at
    /// all, and a number outside this range is not a time: converting it
    /// throws rather than returning something absurd, and that throw would
    /// come out of the plugin's own capture call. The window rule below
    /// narrows this to a few seconds; this pair only keeps the arithmetic
    /// legal.
    /// </summary>
    private static readonly long EarliestCastUnixMs =
        DateTimeOffset.MinValue.ToUnixTimeMilliseconds();

    private static readonly long LatestCastUnixMs =
        DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    /// <summary>
    /// What a scan of a folder that is not there answers. Shared because
    /// every caller only reads it, and a client with no company on the
    /// machine would otherwise allocate one of these on every poll.
    /// </summary>
    private static readonly List<PeerNote> NoNotes = [];

    /// <summary>
    /// How many files' contents are remembered between scans. One entry per
    /// client whose note is in the folder; the folder keeps one for every
    /// client that ever ran here, so past this many the lot is dropped and
    /// rebuilt rather than grown without limit.
    /// </summary>
    private const int ParsedNoteCapacity = 64;

    /// <summary>
    /// The coarsest file-time resolution a peer folder is expected to sit on
    /// (two seconds covers FAT and every common Unix mount), so a note
    /// written within that window is never served from the parse cache.
    /// </summary>
    private static readonly TimeSpan FileTimeGranularity = TimeSpan.FromSeconds(2);

    private const long MaximumDocumentBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _directory;
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly Guid _instanceId;
    private readonly object _gate = new();

    /// <summary>What this client is telling the others, oldest first.</summary>
    private readonly List<PeerCastEntry> _ring = [];

    /// <summary>
    /// The command lines this client is asking the others to run, oldest
    /// first. A ring of its own beside the cast ring: the two carry
    /// different things and are read with cursors of their own, and folding
    /// them together would have a client that only wants one of them move
    /// the other's cursor past lines it never looked at.
    /// </summary>
    private readonly List<PeerCommandEntry> _commandRing = [];

    /// <summary>The highest sequence taken in from each peer's note.</summary>
    private readonly Dictionary<PeerCursorKey, ObservedPeer> _observedPeers = [];

    /// <summary>What this client has read from the others, oldest first.</summary>
    private readonly List<ObservedCast> _observedCasts = [];

    /// <summary>The command lines read from the others, oldest first.</summary>
    private readonly List<ObservedCommand> _observedCommands = [];

    /// <summary>
    /// Peers a plugin imported from somewhere this client cannot read
    /// itself, keyed by their character. Each one is held as a note of the
    /// same shape a neighbour writes, so every rule that applies to a note
    /// read from the folder -- staleness, world, labels, cursors -- applies
    /// to these through the same code.
    /// </summary>
    private readonly Dictionary<uint, RemotePeer> _remotePeers = [];

    /// <summary>
    /// What each note file said, against the version of the file that said
    /// it, so a scan re-reads only what has changed since the last one.
    /// </summary>
    private readonly Dictionary<string, CachedNote> _parsedNotes =
        new(StringComparer.OrdinalIgnoreCase);


    private long _castSequence;
    private long _commandSequence;
    private long _observedSequence;
    private long _observedCommandSequence;
    private bool _castWritePending;
    private bool _commandWritePending;
    private DateTimeOffset? _lastWriteAt;

    /// <summary>
    /// When a write that failed or was refused stops holding the next one
    /// back. Null when the last attempt got the note out.
    /// </summary>
    private DateTimeOffset? _holdWritesUntil;
    private bool _disposed;

    public LocalPluginPeerRegistry(
        string directory,
        TimeProvider? timeProvider = null,
        Guid? instanceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _time = timeProvider ?? TimeProvider.System;
        _instanceId = instanceId ?? Guid.NewGuid();
        _path = Path.Combine(_directory, $"peer-{_instanceId:N}.json");
        ClientId = BitConverter.ToUInt32(_instanceId.ToByteArray(), 0);
        if (ClientId == 0u)
            ClientId = 1u;
    }

    public uint ClientId { get; private set; }

    /// <summary>
    /// Writes this client's note, replacing whatever it said before.
    /// </summary>
    /// <returns>
    /// False when the note was refused rather than written, because this
    /// client's own position or heading is not a finite number and such a
    /// note cannot be written as JSON at all. Every reader refuses a note
    /// like that, so refusing it here costs nothing and keeps the failure
    /// out of the caller, which is a plugin tick.
    /// </returns>
    public bool Publish(in PluginNetworkClient client)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DateTimeOffset now = _time.GetUtcNow();
        if (!CanBeWritten(client))
        {
            HoldOffAfterWriteThatDidNotHappen(now);
            return false;
        }
        PeerDocument document;
        long publishedThrough;
        long publishedCommandsThrough;
        lock (_gate)
        {
            publishedThrough = _castSequence;
            publishedCommandsThrough = _commandSequence;
            document = PeerDocument.From(
                client with { ClientId = ClientId },
                _instanceId,
                now.ToUnixTimeMilliseconds(),
                [.. _ring],
                [.. _commandRing]);
        }
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool written = false;
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
            written = true;
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
            if (written)
            {
                // The note carries the whole ring, so any write -- a
                // heartbeat as much as a cast -- is what the debounce is
                // counting from. A cast recorded WHILE the file was being
                // written is not in the note that just went out, so it stays
                // waiting rather than being cleared with the ones that did.
                lock (_gate)
                {
                    if (_castSequence == publishedThrough)
                        _castWritePending = false;
                    if (_commandSequence == publishedCommandsThrough)
                        _commandWritePending = false;
                    _lastWriteAt = now;
                    _holdWritesUntil = null;
                }
            }
            else
            {
                // The ring still has something waiting and the last-write
                // stamp has not moved, so without this the write stays due
                // and the host's tick, which skips its early-out whenever a
                // write is due, would attempt one on every frame.
                HoldOffAfterWriteThatDidNotHappen(now);
            }
        }
        return true;
    }

    private void HoldOffAfterWriteThatDidNotHappen(DateTimeOffset now)
    {
        lock (_gate)
            _holdWritesUntil = now + HeartbeatPeriod;
    }

    /// <summary>
    /// Whether this client's own state can go in a note at all. A position or
    /// heading that is not a finite number cannot be written as JSON -- the
    /// writer refuses not-a-number and infinity outright -- and a reader
    /// refuses a note carrying one anyway. These are the only numbers in the
    /// document that are not whole; a cast's duration is held to the same
    /// rule on its way into the ring.
    /// </summary>
    private static bool CanBeWritten(in PluginNetworkClient client) =>
        double.IsFinite(client.Position.EastWest)
        && double.IsFinite(client.Position.NorthSouth)
        && double.IsFinite(client.Position.Elevation)
        && float.IsFinite(client.Heading);

    /// <summary>
    /// Adds a cast to the ring this client publishes. The note itself is not
    /// written here: the caller asks <see cref="IsCastWriteDue"/> and writes
    /// when the debounce allows, so a burst of casts costs one or two writes
    /// instead of one each.
    /// </summary>
    /// <returns>
    /// False for a cast that makes no sense, which is left out of the ring
    /// entirely. This is not only tidiness: a duration that is not a finite
    /// number cannot be written as JSON at all, so one such entry would make
    /// every later write of the whole note throw and this client would go
    /// silent for the rest of the session.
    /// </returns>
    public bool RecordCast(in LocalPluginCast cast)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long atUnixMs = _time.GetUtcNow().ToUnixTimeMilliseconds();
        lock (_gate)
        {
            var entry = new PeerCastEntry
            {
                Sequence = _castSequence + 1L,
                AtUnixMs = atUnixMs,
                CasterObjectId = cast.CasterObjectId,
                TargetObjectId = cast.TargetObjectId,
                SpellId = cast.SpellId,
                EffectiveSkill = cast.EffectiveSkill,
                DurationSeconds = cast.DurationSeconds,
                Landed = cast.Landed,
            };
            if (!IsWellFormed(entry))
                return false;
            _castSequence = entry.Sequence;
            _ring.Add(entry);
            if (_ring.Count > CastRingCapacity)
                _ring.RemoveRange(0, _ring.Count - CastRingCapacity);
            _castWritePending = true;
            return true;
        }
    }

    /// <summary>
    /// Whether a recorded cast is waiting to be published and enough time has
    /// passed since the last write to publish it. False while a write that
    /// could not be done is being held off: that one is retried on the
    /// heartbeat, not on the debounce and not on the frame.
    /// </summary>
    public bool IsCastWriteDue()
    {
        if (_disposed)
            return false;
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_holdWritesUntil is { } until && now < until)
                return false;
            return _castWritePending
                && (_lastWriteAt is not { } last
                    || now - last >= CastWriteDebounce);
        }
    }

    /// <summary>
    /// This registry's clock, so the one owner that schedules a broadcast
    /// line measures its wait on the same clock the line was stamped by. Two
    /// clocks behind one wait is a silent refusal waiting to happen.
    /// </summary>
    internal DateTimeOffset UtcNow => _time.GetUtcNow();

    /// <summary>
    /// Adds a command line to the ring this client publishes. As with a
    /// cast, the note is not written here: the caller asks
    /// <see cref="IsCommandWriteDue"/> and writes when the debounce allows.
    /// </summary>
    /// <returns>
    /// False for a line that makes no sense, which is left out of the ring
    /// entirely, by the same rule a peer's note is read under.
    /// </returns>
    public bool RecordCommand(in LocalPluginCommand command)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long atUnixMs = _time.GetUtcNow().ToUnixTimeMilliseconds();
        lock (_gate)
        {
            var entry = new PeerCommandEntry
            {
                Sequence = _commandSequence + 1L,
                AtUnixMs = atUnixMs,
                SenderObjectId = command.SenderObjectId,
                Tags = NormalizeTags(command.Tags),
                Line = command.Line ?? string.Empty,
                DelayMilliseconds = command.DelayMilliseconds,
            };
            if (!IsWellFormed(entry))
                return false;
            _commandSequence = entry.Sequence;
            _commandRing.Add(entry);
            if (_commandRing.Count > CommandRingCapacity)
            {
                _commandRing.RemoveRange(
                    0, _commandRing.Count - CommandRingCapacity);
            }
            _commandWritePending = true;
            return true;
        }
    }

    /// <summary>
    /// Whether a recorded command line is waiting to be published and enough
    /// time has passed since the last write to publish it. The same debounce
    /// the cast ring uses, because the note carries both rings and one write
    /// gets both out.
    /// </summary>
    public bool IsCommandWriteDue()
    {
        if (_disposed)
            return false;
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_holdWritesUntil is { } until && now < until)
                return false;
            return _commandWritePending
                && (_lastWriteAt is not { } last
                    || now - last >= CastWriteDebounce);
        }
    }

    /// <summary>
    /// The command lines other clients on this computer have asked for that
    /// this one has not handed back yet under a sequence above
    /// <paramref name="afterSequence"/>.
    /// </summary>
    /// <param name="afterSequence">
    /// The highest sequence the caller has already dealt with; zero for
    /// everything still inside the staleness window.
    /// </param>
    /// <param name="worldName">
    /// The world this client is logged in to. A peer naming a different one
    /// is playing somewhere else, so its lines are skipped entirely.
    /// </param>
    /// <param name="ownPlayerObjectId">
    /// This client's own character, so a line it asked for is never read
    /// back as somebody else's.
    /// </param>
    /// <param name="ownTags">
    /// The labels this client answers to. A line aimed at labels is taken
    /// only when one of them is here; a line aimed at none is taken by
    /// everybody.
    /// </param>
    public IReadOnlyList<LocalPluginPeerCommand> CaptureRemoteCommands(
        long afterSequence,
        string worldName,
        uint ownPlayerObjectId,
        IReadOnlyList<string>? ownTags)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            TakeInRemoteCommands(
                now,
                worldName ?? string.Empty,
                ownPlayerObjectId,
                ownTags);
            ForgetStaleObservations(now);
            // This one is read on a timer rather than when a plugin asks, so
            // the case where there is nothing to hand back is the usual one
            // and costs nothing.
            if (_observedCommands.Count == 0)
                return Array.Empty<LocalPluginPeerCommand>();
            List<LocalPluginPeerCommand>? result = null;
            foreach (ObservedCommand observed in _observedCommands)
            {
                if (observed.Sequence > afterSequence)
                    (result ??= []).Add(observed.Project());
            }
            return result is null
                ? Array.Empty<LocalPluginPeerCommand>()
                : result.ToArray();
        }
    }

    public IReadOnlyList<PluginNetworkClient> CaptureRemoteClients(
        uint ownPlayerObjectId = 0u)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            return ReadRemoteNotes(now)
                .Where(note => IsRecent(note.Document, now)
                    // An imported peer that turns out to be the character
                    // this client is now playing is not somebody else.
                    && !(note.Document.IsRemote
                        && note.Document.PlayerId == ownPlayerObjectId))
                .Select(static note => note.Document.ToClient())
                .OrderBy(static client => client.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static client => client.ClientId)
                .ToArray();
        }
    }

    /// <summary>
    /// The casts other clients on this computer have reported that this one
    /// has not handed back yet under a sequence above
    /// <paramref name="afterSequence"/>.
    /// </summary>
    /// <param name="afterSequence">
    /// The highest sequence the caller has already dealt with; zero for
    /// everything still inside the staleness window.
    /// </param>
    /// <param name="worldName">
    /// The world this client is logged in to. A peer that names a different
    /// one is playing somewhere else, where none of its object ids mean
    /// anything here, so its casts are skipped entirely.
    /// </param>
    /// <param name="ownPlayerObjectId">
    /// This client's own character, so a cast attributed to it is never read
    /// back as somebody else's.
    /// </param>
    public IReadOnlyList<PluginPeerCast> CaptureRemoteCasts(
        long afterSequence,
        string worldName,
        uint ownPlayerObjectId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            TakeInRemoteCasts(now, worldName ?? string.Empty, ownPlayerObjectId);
            ForgetStaleObservations(now);
            var result = new List<PluginPeerCast>();
            foreach (ObservedCast observed in _observedCasts)
            {
                if (observed.Sequence > afterSequence)
                    result.Add(observed.Project(now));
            }
            return result.Count == 0
                ? Array.Empty<PluginPeerCast>()
                : result.ToArray();
        }
    }

    /// <summary>
    /// The casts this client itself announced above
    /// <paramref name="afterSequence"/> that a neighbour would still take in,
    /// oldest first, with what is left of each success's duration.
    /// </summary>
    public IReadOnlyList<PluginPeerCast> CaptureOwnCasts(long afterSequence)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            List<PluginPeerCast>? result = null;
            foreach (PeerCastEntry entry in _ring)
            {
                if (entry.Sequence <= afterSequence || Age(entry, now) > StaleAfter)
                    continue;
                (result ??= []).Add(new ObservedCast(
                    entry.Sequence, ClientId, entry, IsRemote: false)
                    .Project(now));
            }
            return result is null
                ? Array.Empty<PluginPeerCast>()
                : result.ToArray();
        }
    }

    /// <summary>
    /// The lines this client itself broadcast above
    /// <paramref name="afterSequence"/> that a neighbour would still take in,
    /// oldest first.
    /// </summary>
    public IReadOnlyList<PluginPeerCommand> CaptureOwnCommands(long afterSequence)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            List<PluginPeerCommand>? result = null;
            foreach (PeerCommandEntry entry in _commandRing)
            {
                if (entry.Sequence <= afterSequence || Age(entry, now) > StaleAfter)
                    continue;
                (result ??= []).Add(new PluginPeerCommand(
                    entry.Sequence,
                    ClientId,
                    entry.SenderObjectId,
                    NormalizeTags(entry.Tags),
                    entry.Line,
                    DateTimeOffset.FromUnixTimeMilliseconds(entry.AtUnixMs)));
            }
            return result is null
                ? Array.Empty<PluginPeerCommand>()
                : result.ToArray();
        }
    }

    /// <summary>
    /// Adds or refreshes a peer a plugin carried here from somewhere this
    /// client cannot read, stamped with this client's clock.
    /// </summary>
    /// <param name="client">What the peer said about itself.</param>
    /// <param name="ownPlayerObjectId">
    /// This client's own character, which is never imported as somebody
    /// else.
    /// </param>
    /// <returns>
    /// False for a peer that would not be accepted from a note in the folder
    /// either, for this client's own character, and when the import cap is
    /// full of peers that are still recent.
    /// </returns>
    public bool ImportRemoteClient(
        in PluginNetworkClient client,
        uint ownPlayerObjectId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (client.PlayerId == 0u
            || client.PlayerId == ownPlayerObjectId
            || string.IsNullOrWhiteSpace(client.Name)
            || client.Name.Length > MaximumNameLength
            || client.WorldName is not { Length: <= MaximumNameLength }
            || !CanBeWritten(client))
        {
            return false;
        }
        IReadOnlyList<string> tags = client.Tags ?? Array.Empty<string>();
        if (tags.Count > MaximumClientTags)
            return false;
        foreach (string tag in tags)
        {
            // A label too long to travel is refused rather than cut short,
            // the rule this client's own labels are set under.
            if (tag is not null && tag.Trim().Length > MaximumTagLength)
                return false;
        }
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!_remotePeers.TryGetValue(client.PlayerId, out RemotePeer? remote))
            {
                if (_remotePeers.Count >= MaximumRemoteClients)
                {
                    foreach (uint gone in _remotePeers
                        .Where(pair => !IsRecent(pair.Value.Document, now))
                        .Select(static pair => pair.Key)
                        .ToArray())
                    {
                        _remotePeers.Remove(gone);
                    }
                    if (_remotePeers.Count >= MaximumRemoteClients)
                        return false;
                }
                remote = new RemotePeer(NewRemoteIdentity());
                _remotePeers.Add(client.PlayerId, remote);
            }
            PeerDocument document = remote.Document;
            document.UpdatedUnixMs = now.ToUnixTimeMilliseconds();
            document.PlayerId = client.PlayerId;
            document.Name = client.Name;
            document.WorldName = client.WorldName;
            document.Tags = NormalizeTags(tags);
            document.CellId = client.Position.CellId;
            document.EastWest = client.Position.EastWest;
            document.NorthSouth = client.Position.NorthSouth;
            document.Elevation = client.Position.Elevation;
            document.IsOutdoor = client.Position.IsOutdoor;
            document.Heading = client.Heading;
            document.CurrentHealth = client.CurrentHealth;
            document.CurrentMana = client.CurrentMana;
            document.CurrentStamina = client.CurrentStamina;
            document.MaxHealth = client.MaxHealth;
            document.MaxMana = client.MaxMana;
            document.MaxStamina = client.MaxStamina;
            return true;
        }
    }

    /// <summary>
    /// Adds a cast an imported peer made to that peer's ring, stamped with
    /// this client's clock, under the rule a cast in a note is read by.
    /// </summary>
    /// <returns>
    /// False when the caster is not an imported peer that is still recent,
    /// or the cast is not well formed.
    /// </returns>
    public bool ImportRemoteCast(
        uint casterObjectId,
        uint targetObjectId,
        uint spellId,
        int effectiveSkill,
        double secondsRemaining,
        bool landed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!TryGetRecentRemote(casterObjectId, now, out RemotePeer? remote))
                return false;
            var entry = new PeerCastEntry
            {
                Sequence = remote.CastSequence + 1L,
                AtUnixMs = now.ToUnixTimeMilliseconds(),
                CasterObjectId = casterObjectId,
                TargetObjectId = targetObjectId,
                SpellId = spellId,
                EffectiveSkill = effectiveSkill,
                // An attempt carries no duration, whatever the relay said.
                DurationSeconds = landed ? secondsRemaining : 0d,
                Landed = landed,
            };
            if (!IsWellFormed(entry))
                return false;
            remote.CastSequence = entry.Sequence;
            remote.Document.Casts = Appended(
                remote.Document.Casts, entry, CastRingCapacity);
            return true;
        }
    }

    /// <summary>
    /// Adds a command line an imported peer broadcast to that peer's ring,
    /// stamped with this client's clock, under the rule a line in a note is
    /// read by.
    /// </summary>
    /// <returns>
    /// False when the sender is not an imported peer that is still recent,
    /// or the line is not well formed.
    /// </returns>
    public bool ImportRemoteCommand(
        uint senderObjectId,
        IReadOnlyList<string>? tags,
        string line,
        int delayMilliseconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!TryGetRecentRemote(senderObjectId, now, out RemotePeer? remote))
                return false;
            var entry = new PeerCommandEntry
            {
                Sequence = remote.CommandSequence + 1L,
                AtUnixMs = now.ToUnixTimeMilliseconds(),
                SenderObjectId = senderObjectId,
                Tags = NormalizeTags(tags),
                Line = line ?? string.Empty,
                DelayMilliseconds = delayMilliseconds,
            };
            if (!IsWellFormed(entry)
                || (tags is not null && tags.Count > MaximumCommandTags))
            {
                return false;
            }
            remote.CommandSequence = entry.Sequence;
            remote.Document.Commands = Appended(
                remote.Document.Commands, entry, CommandRingCapacity);
            return true;
        }
    }

    private bool TryGetRecentRemote(
        uint playerObjectId,
        DateTimeOffset now,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RemotePeer? remote) =>
        _remotePeers.TryGetValue(playerObjectId, out remote)
        && IsRecent(remote.Document, now);

    private static T?[] Appended<T>(T?[] ring, T entry, int capacity)
        where T : class
    {
        int keep = Math.Min(ring.Length, capacity - 1);
        var result = new T?[keep + 1];
        Array.Copy(ring, ring.Length - keep, result, 0, keep);
        result[keep] = entry;
        return result;
    }

    /// <summary>
    /// A fresh identity for an imported peer: an instance id no file in the
    /// folder carries and a client id drawn the way a client draws its own,
    /// never zero and never this client's.
    /// </summary>
    private (Guid InstanceId, uint ClientId) NewRemoteIdentity()
    {
        while (true)
        {
            Guid instanceId = Guid.NewGuid();
            uint clientId = BitConverter.ToUInt32(instanceId.ToByteArray(), 0);
            if (clientId != 0u && clientId != ClientId)
                return (instanceId, clientId);
        }
    }

    /// <summary>
    /// Drops every cast and line this client announced, for a character that
    /// has left the world: what the next character is read back as saying
    /// starts empty. The numbering carries on, so a neighbour's mark on this
    /// client stays valid.
    /// </summary>
    public void ForgetOwnAnnouncements()
    {
        if (_disposed)
            return;
        lock (_gate)
        {
            _ring.Clear();
            _commandRing.Clear();
            _castWritePending = false;
            _commandWritePending = false;
        }
    }

    public void Withdraw()
    {
        if (_disposed)
            return;
        lock (_gate)
        {
            // Nothing to write the rings into any more; a later publish will
            // carry whatever is still young enough to matter.
            _castWritePending = false;
            _commandWritePending = false;
        }
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Withdraw();
        _disposed = true;
    }

    /// <summary>
    /// Every other client's note that is present, recent and well shaped.
    /// One reader for both what a peer is and what it cast, so a note cannot
    /// be trusted for one and unchecked for the other.
    /// </summary>
    private List<PeerNote> ReadRemoteNotes(DateTimeOffset now)
    {
        // The folder's own stamp, which moves whenever a note is put there,
        // removed or replaced -- every client writes its note by renaming a
        // new file over the old one, which is a change to the folder. Asked
        // for first because a client reads this on a timer whether or not
        // anything has happened, and a folder that has not changed holds
        // exactly what the last scan found.
        //
        // A folder that is not there answers with the zero of file time
        // rather than throwing, which is the same answer as "nothing has
        // ever announced itself here".
        if (!Directory.Exists(_directory))
        {
            return _remotePeers.Count == 0
                ? NoNotes
                : WithRemotePeers([], now);
        }
        var notes = new List<PeerNote>();
        foreach (string file in Directory.EnumerateFiles(
            _directory,
            "peer-*.json",
            SearchOption.TopDirectoryOnly))
        {
            if (file.Equals(_path, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var info = new FileInfo(file);
                if (info.Length is <= 0 or > MaximumDocumentBytes)
                    continue;
                // What the file looked like when it was last read. It says
                // whether this file has to be READ again -- a note is
                // rewritten every few seconds and most reads find exactly
                // what the last one did -- and nothing else. Whether a note
                // is still fresh is the note's own stamp against this
                // registry's clock, asked where the note is used; a file
                // time is the file system's clock, and judging freshness by
                // both would be two clocks deciding one question.
                DateTime writtenAt = info.LastWriteTimeUtc;
                // The file time is the cache key, and file times are coarse on
                // some file systems: a note rewritten within the same tick at
                // the same length would look unchanged. A file written within
                // the last granularity window is read again regardless, so the
                // cache only ever spares files that have been still for a while.
                bool justWritten =
                    DateTime.UtcNow - writtenAt < FileTimeGranularity;
                if (justWritten
                    || !_parsedNotes.TryGetValue(file, out CachedNote cached)
                    || cached.Length != info.Length
                    || cached.LastWriteUtc != writtenAt)
                {
                    // A note is read on a timer and rewritten every few
                    // seconds, so most reads find exactly what the last one
                    // did. What a file says is settled once per version of
                    // that file; only whether it is still recent is asked
                    // every time.
                    PeerDocument? parsed = JsonSerializer.Deserialize<PeerDocument>(
                        File.ReadAllText(file),
                        JsonOptions);
                    cached = new CachedNote(
                        info.Length,
                        writtenAt,
                        Accept(parsed));
                    if (_parsedNotes.Count >= ParsedNoteCapacity)
                        _parsedNotes.Clear();
                    _parsedNotes[file] = cached;
                }
                if (cached.Document is { } document)
                    notes.Add(new PeerNote(file, document));
            }
            catch (IOException)
            {
                // A peer can atomically replace or remove its own heartbeat
                // between enumeration and read. It will reappear next scan.
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (JsonException)
            {
            }
        }
        return WithRemotePeers(notes, now);
    }

    /// <summary>
    /// Adds the peers a plugin imported to the notes read from the folder. A
    /// character that a recent note in the folder already speaks for, in the
    /// same world, is read from there: the same character arriving by two
    /// routes would otherwise be listed twice and have every cast it made
    /// counted twice. What the hidden copy is handed meanwhile is passed over
    /// as it arrives, so nothing it carried is taken in when the note goes
    /// away and the copy shows again; and a note that first shows while a
    /// copy of the same character was listed starts at the end of its rings,
    /// because the copy already carried what they hold.
    /// </summary>
    private List<PeerNote> WithRemotePeers(List<PeerNote> notes, DateTimeOffset now)
    {
        if (_remotePeers.Count == 0)
            return notes;
        int local = notes.Count;
        foreach (RemotePeer remote in _remotePeers.Values)
        {
            int shadowing = -1;
            for (int index = 0; index < local; index++)
            {
                PeerDocument document = notes[index].Document;
                if (document.PlayerId == remote.Document.PlayerId
                    && IsRecent(document, now)
                    && IsSameWorld(document, remote.Document.WorldName ?? string.Empty))
                {
                    shadowing = index;
                    break;
                }
            }
            if (shadowing < 0)
            {
                notes.Add(new PeerNote(remote.Path, remote.Document));
                remote.Listed = true;
                continue;
            }
            // The hidden copy: everything it holds counts as read.
            _observedPeers[new PeerCursorKey(remote.Path, remote.Document.InstanceId)] =
                new ObservedPeer(remote.CastSequence, remote.CommandSequence, now);
            PeerNote note = notes[shadowing];
            var noteCursor = new PeerCursorKey(note.Path, note.Document.InstanceId);
            if (remote.Listed
                && IsRecent(remote.Document, now)
                && !_observedPeers.ContainsKey(noteCursor))
            {
                _observedPeers[noteCursor] = new ObservedPeer(
                    HighestSequence(note.Document.Casts),
                    HighestSequence(note.Document.Commands),
                    now);
            }
            remote.Listed = false;
        }
        return notes;
    }

    private static long HighestSequence(PeerCastEntry?[]? ring)
    {
        long highest = 0L;
        if (ring is null)
            return highest;
        foreach (PeerCastEntry? entry in ring)
        {
            if (entry is not null && entry.Sequence > highest)
                highest = entry.Sequence;
        }
        return highest;
    }

    private static long HighestSequence(PeerCommandEntry?[]? ring)
    {
        long highest = 0L;
        if (ring is null)
            return highest;
        foreach (PeerCommandEntry? entry in ring)
        {
            if (entry is not null && entry.Sequence > highest)
                highest = entry.Sequence;
        }
        return highest;
    }

    /// <summary>
    /// Whether a note is recent enough to say anything about the client that
    /// wrote it. The scan itself no longer asks, because what the folder
    /// holds is remembered between reads while nothing in it changes, and
    /// the answer to this changes with the clock rather than with the
    /// folder. What a note's rings say is held to the same window entry by
    /// entry, so this is the rule for the client record alone.
    /// </summary>
    private static bool IsRecent(PeerDocument document, DateTimeOffset now) =>
        document.UpdatedUnixMs
            >= now.Subtract(StaleAfter).ToUnixTimeMilliseconds();

    /// <summary>
    /// Takes every cast this client has not seen before out of the peers'
    /// notes and into its own list, numbering them in the order it first read
    /// them. A cast is counted against the peer's high-water mark even when
    /// it is then dropped, so a note nobody can use is not re-examined on
    /// every read.
    /// </summary>
    private void TakeInRemoteCasts(
        DateTimeOffset now,
        string worldName,
        uint ownPlayerObjectId)
    {
        foreach (PeerNote note in ReadRemoteNotes(now)
            .OrderBy(static note => note.Document.ClientId)
            .ThenBy(static note => note.Document.InstanceId))
        {
            PeerDocument document = note.Document;
            // A note nobody is writing any more speaks for nobody: every
            // cast in it is older than the window as well, so there is
            // nothing in it to take and nothing to remember about the client
            // that left it behind.
            if (!IsRecent(document, now))
                continue;
            // A peer logged in somewhere else shares nothing but a hard disk:
            // its object ids name other creatures entirely.
            if (!string.Equals(
                document.WorldName,
                worldName,
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var cursor = new PeerCursorKey(note.Path, document.InstanceId);
            ObservedPeer peer = _observedPeers.TryGetValue(
                cursor, out ObservedPeer known)
                ? known
                : default;
            long highest = peer.HighestCastSequence;
            foreach (PeerCastEntry entry in OldestFirst(document.Casts))
            {
                if (entry.Sequence <= highest)
                    continue;
                highest = entry.Sequence;
                if (!IsUsable(entry, now, ownPlayerObjectId))
                    continue;
                _observedCasts.Add(new ObservedCast(
                    ++_observedSequence,
                    document.ClientId,
                    entry,
                    document.IsRemote));
            }
            _observedPeers[cursor] = peer with
            {
                HighestCastSequence = highest,
                LastSeen = now,
            };
        }
        if (_observedCasts.Count > ObservedCastCapacity)
        {
            _observedCasts.RemoveRange(
                0,
                _observedCasts.Count - ObservedCastCapacity);
        }
    }

    /// <summary>
    /// Takes every command line this client has not seen before out of the
    /// peers' notes and into its own list, numbering them in the order it
    /// first read them. A line is counted against the peer's high-water mark
    /// even when it is then dropped -- as one aimed at labels this client
    /// does not answer to is -- so a note nobody here can use is not
    /// re-examined on every read.
    /// </summary>
    /// <param name="now">This read's instant.</param>
    /// <param name="worldName">The world this client is playing in.</param>
    /// <param name="ownPlayerObjectId">This client's own character.</param>
    /// <param name="ownTags">The labels this client answers to.</param>
    private void TakeInRemoteCommands(
        DateTimeOffset now,
        string worldName,
        uint ownPlayerObjectId,
        IReadOnlyList<string>? tags)
    {
        List<PeerNote> notes = ReadRemoteNotes(now);
        // No other client is running here, which is the ordinary case on a
        // machine playing one character. Nothing below it is worth doing,
        // and this is reached on a timer rather than when a plugin asks.
        if (notes.Count == 0)
            return;
        string[] ownTags = NormalizeTags(tags);
        foreach (PeerNote note in notes
            .OrderBy(static note => note.Document.ClientId)
            .ThenBy(static note => note.Document.InstanceId))
        {
            PeerDocument document = note.Document;
            // The same rule as the casts: a note nobody is writing any more
            // carries no line this client could still run, and the client
            // that left it behind is not worth remembering a cursor for.
            if (!IsRecent(document, now) || !IsSameWorld(document, worldName))
                continue;
            var cursor = new PeerCursorKey(note.Path, document.InstanceId);
            ObservedPeer peer = _observedPeers.TryGetValue(
                cursor, out ObservedPeer known)
                ? known
                : default;
            long highest = peer.HighestCommandSequence;
            foreach (PeerCommandEntry entry in OldestFirst(document.Commands))
            {
                if (entry.Sequence <= highest)
                    continue;
                highest = entry.Sequence;
                if (!IsUsable(entry, now, ownPlayerObjectId))
                    continue;
                string[] aimedAt = NormalizeTags(entry.Tags);
                // A line aimed at labels is for the clients wearing one of
                // them and nobody else; a line aimed at none is for
                // everybody.
                if (!IsAimedAt(aimedAt, ownTags))
                    continue;
                _observedCommands.Add(new ObservedCommand(
                    ++_observedCommandSequence,
                    document.ClientId,
                    document.IsRemote,
                    entry,
                    aimedAt,
                    StaggerFor(
                        notes,
                        now,
                        worldName,
                        document.ClientId,
                        aimedAt,
                        entry.DelayMilliseconds)));
            }
            _observedPeers[cursor] = peer with
            {
                HighestCommandSequence = highest,
                LastSeen = now,
            };
        }
        if (_observedCommands.Count > ObservedCommandCapacity)
        {
            _observedCommands.RemoveRange(
                0,
                _observedCommands.Count - ObservedCommandCapacity);
        }
    }

    /// <summary>
    /// How long this client waits before running a broadcast line, so the
    /// clients taking it do not all act on the same instant.
    ///
    /// <para>The order is by client id with the sender first, and every
    /// recipient can work out its own place in it from the notes in the
    /// folder: the recipients are this client and every other client playing
    /// in the same world whose labels the line is aimed at. The sender holds
    /// place zero, so the first recipient waits one delay, the second two,
    /// and so on.</para>
    /// </summary>
    /// <param name="notes">Every other client's note, as read this pass.</param>
    /// <param name="now">This read's instant.</param>
    /// <param name="worldName">The world this client is playing in.</param>
    /// <param name="senderClientId">The client that asked for the line.</param>
    /// <param name="aimedAt">The labels the line is aimed at.</param>
    /// <param name="delayMilliseconds">The delay the sender asked for.</param>
    private int StaggerFor(
        List<PeerNote> notes,
        DateTimeOffset now,
        string worldName,
        uint senderClientId,
        string[] aimedAt,
        int delayMilliseconds)
    {
        if (delayMilliseconds <= 0)
            return 0;
        int ahead = 0;
        foreach (PeerNote note in notes)
        {
            PeerDocument document = note.Document;
            // Lying in the folder is not the same as playing: a client that
            // crashed leaves its note behind and the scan hands it over like
            // any other. A place in the order belongs to a client that is
            // still saying so, by the same rule that decides whether a note
            // speaks for anybody at all -- otherwise every client behind a
            // dead one waits one place too many, for as long as the file
            // lies there.
            // Only the clients on this computer share one numbering: an
            // imported peer's client id is this client's own invention, and
            // the peer takes its own place wherever it is played.
            if (document.IsRemote
                || document.ClientId == senderClientId
                || document.ClientId >= ClientId
                || !IsRecent(document, now)
                || !IsSameWorld(document, worldName)
                || !IsAimedAt(aimedAt, NormalizeTags(document.Tags)))
            {
                continue;
            }
            ahead++;
        }
        return (ahead + 1) * delayMilliseconds;
    }

    private static bool IsSameWorld(PeerDocument document, string worldName) =>
        string.Equals(
            document.WorldName,
            worldName,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a line aimed at <paramref name="aimedAt"/> is for a client
    /// wearing <paramref name="clientTags"/>. A line aimed at nothing is for
    /// everybody, which is what a broadcast with no labels means.
    /// </summary>
    private static bool IsAimedAt(string[] aimedAt, string[] clientTags) =>
        aimedAt.Length == 0
        || aimedAt.Any(tag => clientTags.Contains(
            tag, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// The labels as they travel: trimmed, empties dropped, repeats ignoring
    /// case folded together, and no more than the cap. One rule for the
    /// labels a client wears and the labels a line is aimed at, so the two
    /// are compared as the same kind of thing.
    /// </summary>
    private static string[] NormalizeTags(IReadOnlyList<string?>? tags) =>
        tags is null
            ? []
            : tags
                .Where(static tag => !string.IsNullOrWhiteSpace(tag))
                .Select(static tag => tag!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaximumClientTags)
                .ToArray();

    /// <summary>
    /// What this client will read out of a note, or <see langword="null"/>
    /// when the note does not describe a client at all. Everything here is
    /// settled by what the file says, so it is settled once per version of
    /// the file; whether the note is still recent is not here, because that
    /// answer changes with the clock.
    ///
    /// <para>A ring is refused on its own. One bad entry means the ring it
    /// is in was not written by an honest client and none of that ring is
    /// worth more than the bad row -- but the other ring and the client's
    /// own row were written by the same client through different code, and a
    /// broken cast is no reason to stop seeing where a character is standing
    /// or to stop taking the lines it asks for.</para>
    /// </summary>
    private PeerDocument? Accept(PeerDocument? parsed)
    {
        if (!IsAcceptable(parsed))
            return null;
        PeerDocument document = parsed!;
        if (!AreCastsWellFormed(document.Casts))
            document.Casts = [];
        if (!AreCommandsWellFormed(document.Commands))
            document.Commands = [];
        return document;
    }

    /// <summary>
    /// Whether a note describes a client at all: written by somebody else,
    /// naming a character, standing somewhere that is a place, and carrying
    /// rings of a size this client could have written. What is IN the rings
    /// is each ring's own business; see <see cref="Accept"/>.
    /// </summary>
    private bool IsAcceptable(PeerDocument? document) =>
        document is not null
        && document.InstanceId != _instanceId
        && document.InstanceId != Guid.Empty
        && document.ClientId != 0u
        && document.PlayerId != 0u
        && !string.IsNullOrWhiteSpace(document.Name)
        && document.Name.Length <= MaximumNameLength
        && document.WorldName is not null
        && document.WorldName.Length <= MaximumNameLength
        && document.Tags is not null
        && document.Tags.Length <= MaximumClientTags
        && AreTagsWellFormed(document.Tags)
        && double.IsFinite(document.EastWest)
        && double.IsFinite(document.NorthSouth)
        && double.IsFinite(document.Elevation)
        && float.IsFinite(document.Heading)
        && document.Casts is not null
        && document.Casts.Length <= CastRingCapacity
        && document.Commands is not null
        && document.Commands.Length <= CommandRingCapacity;

    /// <summary>
    /// Whether every cast in a note is one this client could have written
    /// itself. Applied where the note is accepted, before anything walks it:
    /// a JSON null element and a stamp that is not a time both throw in that
    /// walk, and the throw would leave through the plugin's capture call.
    ///
    /// <para>One bad entry refuses this ring of this note. The same rule
    /// runs before an entry reaches this client's own ring, so a ring
    /// carrying one was not written by an honest client and none of it is
    /// worth more than the bad row.</para>
    /// </summary>
    private static bool AreCastsWellFormed(PeerCastEntry?[] casts)
    {
        foreach (PeerCastEntry? entry in casts)
        {
            if (entry is null || !IsWellFormed(entry))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Whether every command line in a note is one this client could have
    /// written itself, by the same rule and for the same reason as the
    /// casts: one bad entry refuses this ring of this note.
    /// </summary>
    private static bool AreCommandsWellFormed(PeerCommandEntry?[] commands)
    {
        foreach (PeerCommandEntry? entry in commands)
        {
            if (entry is null || !IsWellFormed(entry))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Whether a note's own labels are labels at all. They are compared
    /// against what a broadcast is aimed at, so an empty or absurdly long
    /// one is a broken or hostile note rather than a role.
    /// </summary>
    private static bool AreTagsWellFormed(string?[] tags)
    {
        foreach (string? tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag) || tag.Length > MaximumTagLength)
                return false;
        }
        return true;
    }

    /// <summary>
    /// A note's casts, oldest first. Null elements cannot reach here: a note
    /// carrying one was refused whole when it was read.
    /// </summary>
    private static IEnumerable<PeerCastEntry> OldestFirst(PeerCastEntry?[] casts) =>
        casts.OfType<PeerCastEntry>().OrderBy(static entry => entry.Sequence);

    /// <summary>A note's command lines, oldest first, by the same rule.</summary>
    private static IEnumerable<PeerCommandEntry> OldestFirst(
        PeerCommandEntry?[] commands) =>
        commands.OfType<PeerCommandEntry>()
            .OrderBy(static entry => entry.Sequence);

    /// <summary>
    /// Whether a cast makes sense at all. The one rule, applied on the way
    /// into this client's own ring and again on the way out of a peer's
    /// note, so a note this client would refuse to read is a note it never
    /// writes either.
    /// </summary>
    private static bool IsWellFormed(PeerCastEntry entry) =>
        entry.Sequence > 0L
        && entry.Sequence <= MaximumCastSequence
        && entry.AtUnixMs >= EarliestCastUnixMs
        && entry.AtUnixMs <= LatestCastUnixMs
        && entry.CasterObjectId != 0u
        && entry.TargetObjectId != 0u
        && entry.SpellId != 0u
        && entry.EffectiveSkill >= 0
        && double.IsFinite(entry.DurationSeconds)
        && entry.DurationSeconds >= 0d
        && entry.DurationSeconds <= MaximumCastDurationSeconds
        // An attempt carries no duration; something that landed must.
        && (!entry.Landed || entry.DurationSeconds > 0d);

    /// <summary>
    /// Whether one entry in a peer's note is a cast this client can act on:
    /// well formed, recent, and not this client's own doing. Whether the
    /// spell itself means anything is settled against this client's own
    /// spell table, by the caller that has one.
    /// </summary>
    private static bool IsUsable(
        PeerCastEntry entry,
        DateTimeOffset now,
        uint ownPlayerObjectId)
    {
        // The well-formed rule comes first and the age is only measured
        // after it: a stamp this rule refuses is one the measurement would
        // throw on.
        if (!IsWellFormed(entry) || entry.CasterObjectId == ownPlayerObjectId)
            return false;
        TimeSpan age = Age(entry, now);
        // Too old to act on, or stamped in the future by a note whose clock
        // cannot be trusted.
        return age <= StaleAfter && age >= -StaleAfter;
    }

    private static TimeSpan Age(PeerCastEntry entry, DateTimeOffset now) =>
        now - DateTimeOffset.FromUnixTimeMilliseconds(entry.AtUnixMs);

    private static TimeSpan Age(PeerCommandEntry entry, DateTimeOffset now) =>
        now - DateTimeOffset.FromUnixTimeMilliseconds(entry.AtUnixMs);

    /// <summary>
    /// Whether a command line makes sense at all. The one rule, applied on
    /// the way into this client's own ring and again on the way out of a
    /// peer's note, so a note this client would refuse to read is a note it
    /// never writes either.
    ///
    /// <para>A control character is refused because the line is handed to
    /// the command bus as though it had been typed, and nothing a player can
    /// type carries one.</para>
    /// </summary>
    private static bool IsWellFormed(PeerCommandEntry entry) =>
        entry.Sequence > 0L
        && entry.Sequence <= MaximumCastSequence
        && entry.AtUnixMs >= EarliestCastUnixMs
        && entry.AtUnixMs <= LatestCastUnixMs
        && entry.SenderObjectId != 0u
        && entry.DelayMilliseconds >= 0
        && entry.DelayMilliseconds <= MaximumCommandDelayMilliseconds
        && !string.IsNullOrWhiteSpace(entry.Line)
        && entry.Line.Length <= MaximumCommandLineLength
        && !entry.Line.Any(char.IsControl)
        && entry.Tags is not null
        && entry.Tags.Length <= MaximumCommandTags
        && AreTagsWellFormed(entry.Tags);

    /// <summary>
    /// Whether one command line in a peer's note is one this client can act
    /// on: well formed, recent, and not its own doing. Whether it is aimed
    /// at this client is settled by the caller, which knows this client's
    /// labels.
    /// </summary>
    private static bool IsUsable(
        PeerCommandEntry entry,
        DateTimeOffset now,
        uint ownPlayerObjectId)
    {
        if (!IsWellFormed(entry) || entry.SenderObjectId == ownPlayerObjectId)
            return false;
        TimeSpan age = Age(entry, now);
        return age <= StaleAfter && age >= -StaleAfter;
    }

    /// <summary>
    /// Drops casts that have aged out of the staleness window and peers that
    /// have not been heard from for several windows, so neither list grows
    /// with the length of the session.
    /// </summary>
    private void ForgetStaleObservations(DateTimeOffset now)
    {
        // Nothing has ever been read from anybody, which is the usual state
        // on a machine playing one character, and this runs on a timer.
        if (_observedCasts.Count == 0
            && _observedCommands.Count == 0
            && _observedPeers.Count == 0)
        {
            return;
        }
        _observedCasts.RemoveAll(observed => Age(observed.Entry, now) > StaleAfter);
        _observedCommands.RemoveAll(
            observed => Age(observed.Entry, now) > StaleAfter);
        TimeSpan forgetPeerAfter = StaleAfter * 4;
        foreach (PeerCursorKey cursor in _observedPeers
            .Where(pair => now - pair.Value.LastSeen > forgetPeerAfter)
            .Select(static pair => pair.Key)
            .ToArray())
        {
            _observedPeers.Remove(cursor);
        }
    }

    /// <summary>A peer's note: the file it was read from, and what it says.</summary>
    private readonly record struct PeerNote(string Path, PeerDocument Document);

    /// <summary>
    /// One file's contents as they were last read, with what the file looked
    /// like then. A document of <see langword="null"/> is a file this client
    /// refused, remembered so it is not read again while it is unchanged.
    /// </summary>
    private readonly record struct CachedNote(
        long Length,
        DateTime LastWriteUtc,
        PeerDocument? Document);

    /// <summary>
    /// What a high-water mark belongs to. The identity in a note is a field
    /// another process wrote, so two files can claim one identity; keyed on
    /// the claim alone, whichever file was read first moved the mark the
    /// other's casts are measured against, and the genuine client was
    /// silenced for good. Keyed on the file as well, a note can only ever
    /// move its own file's mark.
    ///
    /// <para>The path is compared exactly as the directory scan reports it,
    /// which is the same spelling every scan, and is the right comparison on
    /// a file system where two names differing only in case are two
    /// files.</para>
    /// </summary>
    private readonly record struct PeerCursorKey(string Path, Guid InstanceId);

    /// <summary>
    /// How far one peer's note has been read. A mark per ring, because the
    /// two count from one independently of each other and a client reading
    /// only one of them must not move the other's mark past lines it never
    /// looked at.
    /// </summary>
    private readonly record struct ObservedPeer(
        long HighestCastSequence,
        long HighestCommandSequence,
        DateTimeOffset LastSeen);

    /// <summary>One cast read from a peer, under this client's numbering.</summary>
    private readonly record struct ObservedCast(
        long Sequence,
        uint ClientId,
        PeerCastEntry Entry,
        bool IsRemote)
    {
        /// <summary>
        /// What the plugin is handed: the time left rather than the total, so
        /// a success read five seconds after it happened is not applied as if
        /// it were fresh.
        /// </summary>
        internal PluginPeerCast Project(DateTimeOffset now)
        {
            double elapsed = Math.Max(0d, Age(Entry, now).TotalSeconds);
            return new PluginPeerCast(
                Sequence,
                ClientId,
                Entry.CasterObjectId,
                Entry.TargetObjectId,
                Entry.SpellId,
                Entry.EffectiveSkill,
                Math.Clamp(
                    Entry.DurationSeconds - elapsed,
                    0d,
                    Entry.DurationSeconds),
                Entry.Landed)
            {
                IsRemote = IsRemote,
            };
        }
    }

    /// <summary>
    /// One command line read from a peer, under this client's numbering,
    /// with the wait this client owes before running it.
    /// </summary>
    private readonly record struct ObservedCommand(
        long Sequence,
        uint ClientId,
        bool IsRemote,
        PeerCommandEntry Entry,
        string[] AimedAt,
        int StaggerMilliseconds)
    {
        internal LocalPluginPeerCommand Project() => new(
            new PluginPeerCommand(
                Sequence,
                ClientId,
                Entry.SenderObjectId,
                AimedAt,
                Entry.Line,
                DateTimeOffset.FromUnixTimeMilliseconds(Entry.AtUnixMs))
            {
                IsRemote = IsRemote,
            },
            StaggerMilliseconds);
    }

    /// <summary>One cast as it travels in the document.</summary>
    private sealed class PeerCastEntry
    {
        public long Sequence { get; set; }
        public long AtUnixMs { get; set; }
        public uint CasterObjectId { get; set; }
        public uint TargetObjectId { get; set; }
        public uint SpellId { get; set; }
        public int EffectiveSkill { get; set; }
        public double DurationSeconds { get; set; }
        public bool Landed { get; set; }
    }

    /// <summary>One broadcast command line as it travels in the document.</summary>
    private sealed class PeerCommandEntry
    {
        public long Sequence { get; set; }
        public long AtUnixMs { get; set; }
        public uint SenderObjectId { get; set; }

        /// <summary>
        /// The labels the line is aimed at; empty aims it at everybody.
        /// Nullable elements because the wire is a file another process
        /// wrote: a JSON null lands here as a null element.
        /// </summary>
        public string?[] Tags { get; set; } = [];

        public string Line { get; set; } = string.Empty;
        public int DelayMilliseconds { get; set; }
    }

    /// <summary>
    /// A peer a plugin imported, held as the note a neighbour would have
    /// written, with the counters its rings are numbered by.
    /// </summary>
    private sealed class RemotePeer
    {
        public RemotePeer((Guid InstanceId, uint ClientId) identity)
        {
            Document = new PeerDocument
            {
                InstanceId = identity.InstanceId,
                ClientId = identity.ClientId,
                IsRemote = true,
            };
            // Not a file: a key no directory scan can report, so a cursor on
            // an imported peer can never be moved by a note in the folder.
            Path = "remote:" + identity.InstanceId.ToString("N");
        }

        public PeerDocument Document { get; }
        public string Path { get; }
        public long CastSequence { get; set; }
        public long CommandSequence { get; set; }

        /// <summary>Whether the last read listed this copy rather than a note.</summary>
        public bool Listed { get; set; }
    }

    private sealed class PeerDocument
    {
        public Guid InstanceId { get; set; }

        /// <summary>
        /// True for a peer a plugin imported. Never read from or written to
        /// a file, so a note in the folder cannot claim to be one.
        /// </summary>
        [JsonIgnore]
        public bool IsRemote { get; set; }

        public long UpdatedUnixMs { get; set; }
        public uint ClientId { get; set; }
        public uint PlayerId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string WorldName { get; set; } = string.Empty;

        /// <summary>
        /// The labels this client answers to. Nullable elements for the same
        /// reason the rings' are: the wire is a file another process wrote.
        /// </summary>
        public string?[] Tags { get; set; } = [];
        public uint CellId { get; set; }
        public double EastWest { get; set; }
        public double NorthSouth { get; set; }
        public double Elevation { get; set; }
        public bool IsOutdoor { get; set; }
        public float Heading { get; set; }
        public uint CurrentHealth { get; set; }
        public uint CurrentMana { get; set; }
        public uint CurrentStamina { get; set; }
        public uint MaxHealth { get; set; }
        public uint MaxMana { get; set; }
        public uint MaxStamina { get; set; }

        /// <summary>
        /// The recent casts this client has announced, oldest first. A
        /// heartbeat carries them again unchanged; a reader tells old from
        /// new by the sequence.
        ///
        /// <para>Nullable because the wire is a file another process wrote:
        /// a JSON null in the array lands here as a null element, and the
        /// type has to say so or the check for one reads as dead code.</para>
        /// </summary>
        public PeerCastEntry?[] Casts { get; set; } = [];

        /// <summary>
        /// The recent command lines this client has asked the others to run,
        /// oldest first. Nullable for the same reason as the casts.
        /// </summary>
        public PeerCommandEntry?[] Commands { get; set; } = [];

        public static PeerDocument From(
            in PluginNetworkClient client,
            Guid instanceId,
            long updatedUnixMs,
            PeerCastEntry[] casts,
            PeerCommandEntry[] commands) => new()
        {
            InstanceId = instanceId,
            UpdatedUnixMs = updatedUnixMs,
            ClientId = client.ClientId,
            PlayerId = client.PlayerId,
            Name = client.Name,
            WorldName = client.WorldName,
            Tags = NormalizeTags(client.Tags),
            CellId = client.Position.CellId,
            EastWest = client.Position.EastWest,
            NorthSouth = client.Position.NorthSouth,
            Elevation = client.Position.Elevation,
            IsOutdoor = client.Position.IsOutdoor,
            Heading = client.Heading,
            CurrentHealth = client.CurrentHealth,
            CurrentMana = client.CurrentMana,
            CurrentStamina = client.CurrentStamina,
            MaxHealth = client.MaxHealth,
            MaxMana = client.MaxMana,
            MaxStamina = client.MaxStamina,
            Casts = casts,
            Commands = commands,
        };

        public PluginNetworkClient ToClient() => new(
            ClientId,
            PlayerId,
            Name,
            WorldName,
            new PluginNavigationPosition(
                CellId,
                EastWest,
                NorthSouth,
                Elevation,
                Heading,
                IsOutdoor),
            // Every element is a label: a note carrying a null or blank one
            // was refused when it was read.
            NormalizeTags(Tags),
            CurrentHealth,
            CurrentMana,
            CurrentStamina,
            MaxHealth,
            MaxMana,
            MaxStamina,
            Heading)
        {
            IsRemote = IsRemote,
        };
    }
}
