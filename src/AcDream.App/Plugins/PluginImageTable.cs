using System.Collections.Generic;
using AcDream.Core.Textures;
using StbImageSharp;

namespace AcDream.App.Plugins;

/// <summary>
/// What one plugin holds of an image: a number that means nothing outside
/// the table that issued it, and the size so the caller can lay the image
/// out without asking again. Ids are never reused, so a handle released
/// once stays refused for the rest of the table's life.
/// </summary>
internal readonly record struct PluginImageHandle(int Id, int Width, int Height)
{
    /// <summary>No image. What every refusal returns.</summary>
    public static PluginImageHandle None => default;

    /// <summary>True when the table issued this handle and it has not been released.</summary>
    public bool IsValid => Id != 0;
}

/// <summary>
/// How much of the interface's texture memory one plugin may take.
/// <paramref name="MaximumCount"/> covers every image the plugin holds;
/// <paramref name="MaximumBytes"/> only the ones the plugin brought itself,
/// since client art is shared with every other reader of the same surface
/// and costs the plugin nothing extra. <paramref name="MaximumDimension"/>
/// is the widest or tallest a plugin-supplied image may decode to.
/// </summary>
internal readonly record struct PluginImageBudget(
    int MaximumCount,
    long MaximumBytes,
    int MaximumDimension)
{
    /// <summary>
    /// 256 images, 32 MB of the plugin's own art, and nothing over 2048 on a
    /// side: the starting point recorded in the plan, not a measurement.
    /// </summary>
    public static PluginImageBudget Default { get; } = new(256, 32L * 1024 * 1024, 2048);
}

/// <summary>
/// The texture services one image table draws on. Client art comes back
/// as the shared interface texture for that surface -- the same handle any
/// other plugin, or the client's own windows, gets for it -- and is never
/// released through here. Only art the plugin supplied is uploaded as the
/// plugin's own and given back when it is done.
/// </summary>
internal interface IPluginImageBackend
{
    /// <summary>The shared interface texture for a client render surface.</summary>
    bool TryGetClientArt(uint surfaceId, out uint texture, out int width, out int height);

    /// <summary>The composed icon for a spell, as the spell bar draws it.</summary>
    bool TryGetSpellIcon(uint spellId, out uint texture, out int width, out int height);

    /// <summary>The composed icon for a world object the client knows, as the inventory draws it.</summary>
    bool TryGetObjectIcon(uint objectId, out uint texture, out int width, out int height);

    /// <summary>Uploads art the plugin supplied. The handle comes back through <see cref="ReleaseOwned"/>.</summary>
    uint UploadOwned(byte[] rgba, int width, int height, string debugName);

    /// <summary>
    /// Uploads art the plugin supplied together with its smaller levels, the
    /// image first. A backend that draws no smaller levels uploads the image alone.
    /// </summary>
    uint UploadOwned(IReadOnlyList<DecodedTexture> mipLevels, string debugName) =>
        UploadOwned(mipLevels[0].Rgba8, mipLevels[0].Width, mipLevels[0].Height, debugName);

    /// <summary>Gives back a texture from <see cref="UploadOwned"/>.</summary>
    bool ReleaseOwned(uint texture);
}

/// <summary>
/// One plugin's images: every image it has asked for, counted once per
/// distinct request, with how many times it is held. Two requests for the
/// same thing come back as the same handle and take two releases to let go,
/// so a plugin can hand an image to two of its own features without either
/// one pulling it from under the other.
///
/// <para>The table is bound to the interface's texture services once those
/// exist and unbound when the interface goes away. Unbound, every request
/// answers <see cref="PluginImageHandle.None"/>. Bound, requests must come
/// from the thread the interface runs on: the texture cache underneath is
/// not safe from any other, and a plugin's callbacks already run there.</para>
/// </summary>
internal sealed class PluginImageTable : IDisposable
{
    private enum Kind
    {
        ClientArt,
        SpellIcon,
        ObjectIcon,
        Decoded,
        File,
    }

    private readonly record struct Key(Kind Kind, uint Id, string? Name);

    private sealed class Entry(Key key, int id, uint texture, int width, int height, long ownedBytes)
    {
        internal Key Key { get; } = key;
        internal int Id { get; } = id;
        internal uint Texture { get; } = texture;
        internal int Width { get; } = width;
        internal int Height { get; } = height;

        /// <summary>Zero for shared client art; the upload size for the plugin's own.</summary>
        internal long OwnedBytes { get; } = ownedBytes;

        internal bool IsOwned => OwnedBytes > 0;
        internal int RefCount { get; set; } = 1;

        internal PluginImageHandle Handle => new(Id, Width, Height);
    }

    private readonly string _ownerId;
    private readonly Action<string> _report;
    private readonly Dictionary<Key, Entry> _byKey = [];
    private readonly Dictionary<int, Entry> _byId = [];
    private readonly HashSet<string> _reported = [];
    private IPluginImageBackend? _backend;
    private int _uiThreadId;
    private int _nextId;
    private long _ownedBytes;
    private bool _disposed;

    internal PluginImageTable(
        string ownerId,
        PluginImageBudget budget,
        Action<string>? report = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget.MaximumCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget.MaximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget.MaximumDimension);
        _ownerId = ownerId;
        Budget = budget;
        _report = report ?? (line => Serilog.Log.Warning("{Line}", line));
    }

    internal PluginImageBudget Budget { get; }

    /// <summary>How many distinct images the plugin holds, shared and own alike.</summary>
    internal int Count => _byId.Count;

    /// <summary>The upload size of the plugin's own images, the number the byte budget is held against.</summary>
    internal long OwnedBytes => _ownedBytes;

    internal bool IsBound => _backend is not null;

    /// <summary>
    /// Moves on each time an image is let go of completely and each time the
    /// table is bound: a request refused for want of room, or because the
    /// table was not bound, may succeed once this has moved.
    /// </summary>
    internal long ReleaseGeneration { get; private set; }

    /// <summary>
    /// Points the table at the interface's texture services.
    /// <paramref name="uiThreadId"/> is the interface thread, the only thread
    /// the table then accepts requests from. It is passed in rather than
    /// taken from the caller because a table is made on whatever thread a
    /// plugin first asks for its images from, and that thread is not
    /// necessarily the interface's.
    /// </summary>
    internal void Bind(IPluginImageBackend backend, int uiThreadId)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_backend is not null)
            throw new InvalidOperationException("The image table is already bound.");
        _backend = backend;
        _uiThreadId = uiThreadId;
        ReleaseGeneration++;
    }

    /// <summary>
    /// Lets go of every image and forgets the texture services. Handles the
    /// plugin still holds stop resolving; it asks again once the interface
    /// is back.
    /// </summary>
    internal void Unbind()
    {
        if (_backend is null)
            return;
        Clear();
        _backend = null;
    }

    internal PluginImageHandle AcquireClientArt(uint surfaceId)
    {
        if (surfaceId == 0u) return PluginImageHandle.None;
        return AcquireShared(
            new Key(Kind.ClientArt, surfaceId, null),
            static (IPluginImageBackend backend, uint id, out uint texture, out int width, out int height) =>
                backend.TryGetClientArt(id, out texture, out width, out height),
            $"client art 0x{surfaceId:X8}");
    }

    internal PluginImageHandle AcquireSpellIcon(uint spellId)
    {
        if (spellId == 0u) return PluginImageHandle.None;
        return AcquireShared(
            new Key(Kind.SpellIcon, spellId, null),
            static (IPluginImageBackend backend, uint id, out uint texture, out int width, out int height) =>
                backend.TryGetSpellIcon(id, out texture, out width, out height),
            $"spell icon {spellId}");
    }

    internal PluginImageHandle AcquireObjectIcon(uint objectId)
    {
        if (objectId == 0u) return PluginImageHandle.None;
        return AcquireShared(
            new Key(Kind.ObjectIcon, objectId, null),
            static (IPluginImageBackend backend, uint id, out uint texture, out int width, out int height) =>
                backend.TryGetObjectIcon(id, out texture, out width, out height),
            $"object icon 0x{objectId:X8}");
    }

    /// <summary>
    /// Art the plugin ships, opened through <paramref name="open"/> only when
    /// the table does not already hold an image under <paramref name="name"/>.
    /// The stream is decoded here and never handed to the plugin's caller
    /// again; a second request under the same name is the same image.
    /// </summary>
    internal PluginImageHandle AcquireDecoded(string name, Func<Stream> open)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(open);
        if (!TryEnter(out IPluginImageBackend? backend))
            return PluginImageHandle.None;

        var key = new Key(Kind.Decoded, 0u, name);
        if (_byKey.TryGetValue(key, out Entry? held))
        {
            held.RefCount++;
            return held.Handle;
        }

        if (!HasRoomForOneMore($"image '{name}'"))
            return PluginImageHandle.None;

        ImageResult decoded;
        try
        {
            Stream stream = open()
                ?? throw new InvalidOperationException("the stream factory returned null");
            try
            {
                // The header is read on its own first: a decode allocates the
                // whole image, so an oversized file must be refused on its
                // declared size, before that allocation, not after it. A
                // header the library cannot read falls through to the decode,
                // which reports the failure in its own words.
                if (ImageInfo.FromStream(stream) is { } info
                    && !IsWithinMaximumDimension(name, info.Width, info.Height))
                {
                    return PluginImageHandle.None;
                }
                if (stream.CanSeek)
                {
                    stream.Position = 0;
                }
                else
                {
                    stream.Dispose();
                    stream = open()
                        ?? throw new InvalidOperationException("the stream factory returned null");
                }
                decoded = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            }
            finally
            {
                stream.Dispose();
            }
        }
        catch (Exception failure)
        {
            ReportOnce($"decode:{name}", $"image '{name}' could not be decoded: {failure.Message}");
            return PluginImageHandle.None;
        }

        if (decoded.Width <= 0 || decoded.Height <= 0)
        {
            ReportOnce($"empty:{name}", $"image '{name}' decoded to nothing");
            return PluginImageHandle.None;
        }
        if (!IsWithinMaximumDimension(name, decoded.Width, decoded.Height))
            return PluginImageHandle.None;

        long bytes = checked((long)decoded.Width * decoded.Height * 4L);
        if (!IsWithinByteBudget(name, bytes))
            return PluginImageHandle.None;

        uint texture = backend.UploadOwned(
            decoded.Data, decoded.Width, decoded.Height, $"plugin-{_ownerId}-{name}");
        _ownedBytes += bytes;
        return Add(key, texture, decoded.Width, decoded.Height, bytes).Handle;
    }

    /// <summary>
    /// An image file the plugin ships, which plugin markup names by path,
    /// read and decoded already, away from the interface thread. It is held
    /// under its path and the version of the file it was read from, so a
    /// window built after the file changed gets the new image while a window
    /// still showing the old one keeps it until that window lets go. Its
    /// smaller levels count against the byte budget with the image itself.
    /// </summary>
    internal PluginImageHandle AcquireFile(PluginFileImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfZero(image.MipLevels.Count);
        if (!TryEnter(out IPluginImageBackend? backend))
            return PluginImageHandle.None;

        var key = new Key(Kind.File, 0u, $"{image.Path}\n{image.Version}");
        if (_byKey.TryGetValue(key, out Entry? held))
        {
            held.RefCount++;
            return held.Handle;
        }

        string name = image.Path;
        if (!HasRoomForOneMore($"image '{name}'"))
            return PluginImageHandle.None;
        DecodedTexture top = image.MipLevels[0];
        if (!IsWithinMaximumDimension(name, top.Width, top.Height))
            return PluginImageHandle.None;

        long bytes = 0L;
        foreach (DecodedTexture level in image.MipLevels)
            bytes = checked(bytes + level.Rgba8.LongLength);
        if (!IsWithinByteBudget(name, bytes))
            return PluginImageHandle.None;

        uint texture = backend.UploadOwned(image.MipLevels, $"plugin-{_ownerId}-{name}");
        _ownedBytes += bytes;
        return Add(key, texture, top.Width, top.Height, bytes).Handle;
    }

    /// <summary>
    /// Lets go of one hold on an image. The image stays while another hold
    /// remains; on the last, plugin-supplied art is given back to the
    /// backend and client art is simply forgotten. False for a handle this
    /// table did not issue or has already let go of completely.
    /// </summary>
    internal bool Release(PluginImageHandle handle)
    {
        if (!handle.IsValid || _disposed) return false;
        ThrowIfWrongThread();
        if (!_byId.TryGetValue(handle.Id, out Entry? entry))
            return false;
        if (--entry.RefCount > 0)
            return true;
        Remove(entry);
        return true;
    }

    /// <summary>
    /// The interface texture behind a handle, for the painter. False for a
    /// handle that was released, issued by another table, or never issued.
    /// </summary>
    internal bool TryResolve(PluginImageHandle handle, out uint texture, out int width, out int height)
    {
        if (handle.IsValid && !_disposed && _byId.TryGetValue(handle.Id, out Entry? entry))
        {
            texture = entry.Texture;
            width = entry.Width;
            height = entry.Height;
            return true;
        }
        texture = 0u;
        width = 0;
        height = 0;
        return false;
    }

    /// <summary>Lets go of everything at once, however many holds each image had.</summary>
    internal void Clear()
    {
        if (_byId.Count == 0) return;
        var entries = new List<Entry>(_byId.Values);
        foreach (Entry entry in entries)
            Remove(entry);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Clear();
        _backend = null;
        _disposed = true;
    }

    private delegate bool SharedLookup(
        IPluginImageBackend backend, uint id, out uint texture, out int width, out int height);

    private PluginImageHandle AcquireShared(Key key, SharedLookup lookup, string what)
    {
        if (!TryEnter(out IPluginImageBackend? backend))
            return PluginImageHandle.None;
        if (_byKey.TryGetValue(key, out Entry? held))
        {
            held.RefCount++;
            return held.Handle;
        }
        if (!HasRoomForOneMore(what))
            return PluginImageHandle.None;
        if (!lookup(backend, key.Id, out uint texture, out int width, out int height) || texture == 0u)
        {
            ReportOnce($"missing:{what}", $"{what} is not something the client can draw");
            return PluginImageHandle.None;
        }
        return Add(key, texture, width, height, ownedBytes: 0L).Handle;
    }

    private bool TryEnter(out IPluginImageBackend backend)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_backend is null)
        {
            backend = null!;
            return false;
        }
        ThrowIfWrongThread();
        backend = _backend;
        return true;
    }

    private void ThrowIfWrongThread()
    {
        if (_backend is not null && Environment.CurrentManagedThreadId != _uiThreadId)
        {
            throw new InvalidOperationException(
                "Plugin images may only be acquired and released from the interface thread, "
                + "the thread the plugin's own callbacks run on.");
        }
    }

    private bool IsWithinMaximumDimension(string name, int width, int height)
    {
        if (width <= Budget.MaximumDimension && height <= Budget.MaximumDimension)
            return true;
        ReportOnce(
            $"dimension:{name}",
            $"image '{name}' is {width}x{height}; "
            + $"the most a plugin image may be is {Budget.MaximumDimension} on a side");
        return false;
    }

    private bool IsWithinByteBudget(string name, long bytes)
    {
        if (_ownedBytes + bytes <= Budget.MaximumBytes)
            return true;
        ReportOnce(
            $"bytes:{name}",
            $"image '{name}' would take the plugin's own art to "
            + $"{_ownedBytes + bytes:N0} bytes; the budget is {Budget.MaximumBytes:N0}");
        return false;
    }

    private bool HasRoomForOneMore(string what)
    {
        if (_byId.Count < Budget.MaximumCount)
            return true;
        ReportOnce(
            "count",
            $"{what} refused: the plugin already holds {Budget.MaximumCount} images, "
            + "which is the most it may");
        return false;
    }

    private Entry Add(Key key, uint texture, int width, int height, long ownedBytes)
    {
        var entry = new Entry(key, checked(++_nextId), texture, width, height, ownedBytes);
        _byKey.Add(key, entry);
        _byId.Add(entry.Id, entry);
        return entry;
    }

    private void Remove(Entry entry)
    {
        _byKey.Remove(entry.Key);
        _byId.Remove(entry.Id);
        ReleaseGeneration++;
        if (!entry.IsOwned)
            return;
        _ownedBytes -= entry.OwnedBytes;
        _backend?.ReleaseOwned(entry.Texture);
    }

    private void ReportOnce(string key, string message)
    {
        if (_reported.Add(key))
            _report($"Plugin '{_ownerId}': {message}.");
    }
}
