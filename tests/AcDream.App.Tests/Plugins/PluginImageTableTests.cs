using System.Collections.Generic;
using System.Threading;
using AcDream.App.Plugins;
using AcDream.Core.Textures;
using AcDream.Tests.Fixtures.PluginIcons;

namespace AcDream.App.Tests.Plugins;

/// <summary>
/// One plugin's images: refcounted per distinct request, held to a count
/// and a byte budget, with plugin-supplied art decoded by the host and
/// refused past a maximum dimension. Client art rides the shared cache and
/// is never given back through the plugin's own release.
/// </summary>
public sealed class PluginImageTableTests
{
    private sealed class FakeBackend : IPluginImageBackend
    {
        private uint _nextTexture = 100u;
        public List<(uint Texture, int Width, int Height, string Name)> Uploads { get; } = [];
        public List<uint> Released { get; } = [];
        public HashSet<uint> KnownArt { get; } = [0x06001234u];

        public bool TryGetClientArt(uint surfaceId, out uint texture, out int width, out int height)
        {
            if (KnownArt.Contains(surfaceId))
            {
                texture = 7u; width = 32; height = 32;
                return true;
            }
            texture = 0u; width = 0; height = 0;
            return false;
        }

        public bool TryGetSpellIcon(uint spellId, out uint texture, out int width, out int height)
        {
            texture = 8u; width = 32; height = 32;
            return true;
        }

        public bool TryGetObjectIcon(uint objectId, out uint texture, out int width, out int height)
        {
            texture = 9u; width = 32; height = 32;
            return true;
        }

        public uint UploadOwned(byte[] rgba, int width, int height, string debugName)
        {
            uint texture = _nextTexture++;
            Uploads.Add((texture, width, height, debugName));
            return texture;
        }

        public bool ReleaseOwned(uint texture)
        {
            Released.Add(texture);
            return true;
        }
    }

    private static (PluginImageTable Table, FakeBackend Backend, List<string> Reports) Bound(
        PluginImageBudget? budget = null)
    {
        var reports = new List<string>();
        var table = new PluginImageTable("example.plugin", budget ?? PluginImageBudget.Default, reports.Add);
        var backend = new FakeBackend();
        table.Bind(backend, Environment.CurrentManagedThreadId);
        return (table, backend, reports);
    }

    private static Func<Stream> Png64() => static () => new MemoryStream(PngTestData.Valid());

    private static Func<Stream> Png32() => static () => new MemoryStream(PngTestData.WrongDimensions());

    [Fact]
    public void TheSameRequestTwiceIsOneImageHeldTwice()
    {
        (PluginImageTable table, FakeBackend backend, _) = Bound();

        PluginImageHandle first = table.AcquireDecoded("map", Png64());
        PluginImageHandle second = table.AcquireDecoded("map", Png64());

        Assert.True(first.IsValid);
        Assert.Equal(first, second);
        Assert.Single(backend.Uploads);
        Assert.Equal(1, table.Count);
        Assert.Equal(64L * 64L * 4L, table.OwnedBytes);

        Assert.True(table.Release(first));
        Assert.Empty(backend.Released);
        Assert.True(table.TryResolve(first, out uint texture, out _, out _));
        Assert.Equal(backend.Uploads[0].Texture, texture);

        Assert.True(table.Release(first));
        Assert.Equal([backend.Uploads[0].Texture], backend.Released);
        Assert.Equal(0, table.Count);
        Assert.Equal(0L, table.OwnedBytes);
    }

    [Fact]
    public void ReleasingAnImageAlreadyLetGoIsRefusedNotUnderflowed()
    {
        (PluginImageTable table, FakeBackend backend, _) = Bound();
        PluginImageHandle image = table.AcquireDecoded("map", Png64());
        Assert.True(table.Release(image));

        Assert.False(table.Release(image));
        Assert.False(table.Release(image));
        Assert.False(table.TryResolve(image, out _, out _, out _));
        Assert.Single(backend.Released);

        // A fresh request under the same name is a new handle, never the
        // old number brought back to life.
        PluginImageHandle again = table.AcquireDecoded("map", Png64());
        Assert.True(again.IsValid);
        Assert.NotEqual(image.Id, again.Id);
        Assert.False(table.TryResolve(image, out _, out _, out _));
    }

    [Fact]
    public void ReleasingAHandleTheTableNeverIssuedIsRefused()
    {
        (PluginImageTable table, _, _) = Bound();

        Assert.False(table.Release(PluginImageHandle.None));
        Assert.False(table.Release(new PluginImageHandle(42, 8, 8)));
    }

    [Fact]
    public void TheCountBudgetRefusesTheImagePastIt()
    {
        (PluginImageTable table, FakeBackend backend, List<string> reports) =
            Bound(new PluginImageBudget(MaximumCount: 2, MaximumBytes: 1L << 30, MaximumDimension: 2048));

        Assert.True(table.AcquireDecoded("a", Png32()).IsValid);
        Assert.True(table.AcquireDecoded("b", Png32()).IsValid);
        PluginImageHandle third = table.AcquireDecoded("c", Png32());

        Assert.False(third.IsValid);
        Assert.Equal(2, backend.Uploads.Count);
        Assert.Contains(reports, line => line.Contains("holds 2 images", StringComparison.Ordinal));

        // Shared art counts against the same ceiling.
        Assert.False(table.AcquireClientArt(0x06001234u).IsValid);
        // A repeat of something held is not one more.
        Assert.True(table.AcquireDecoded("a", Png32()).IsValid);
    }

    [Fact]
    public void TheByteBudgetRefusesTheUploadThatWouldCrossIt()
    {
        long one32 = 32L * 32L * 4L;
        (PluginImageTable table, FakeBackend backend, List<string> reports) =
            Bound(new PluginImageBudget(MaximumCount: 256, MaximumBytes: one32 * 2, MaximumDimension: 2048));

        Assert.True(table.AcquireDecoded("a", Png32()).IsValid);
        Assert.True(table.AcquireDecoded("b", Png32()).IsValid);
        PluginImageHandle third = table.AcquireDecoded("c", Png32());

        Assert.False(third.IsValid);
        Assert.Equal(2, backend.Uploads.Count);
        Assert.Equal(one32 * 2, table.OwnedBytes);
        Assert.Contains(reports, line => line.Contains("budget is", StringComparison.Ordinal));

        // Room comes back when something is released.
        Assert.True(table.Release(new PluginImageHandle(1, 32, 32)));
        Assert.True(table.AcquireDecoded("c", Png32()).IsValid);
    }

    [Fact]
    public void ADecodeWiderThanTheMaximumDimensionIsRefusedBeforeUpload()
    {
        (PluginImageTable table, FakeBackend backend, List<string> reports) =
            Bound(new PluginImageBudget(MaximumCount: 256, MaximumBytes: 1L << 30, MaximumDimension: 32));

        Assert.False(table.AcquireDecoded("big", Png64()).IsValid);
        Assert.Empty(backend.Uploads);
        Assert.Contains(reports, line => line.Contains("64x64", StringComparison.Ordinal));

        PluginImageHandle small = table.AcquireDecoded("small", Png32());
        Assert.True(small.IsValid);
        Assert.Equal((32, 32), (small.Width, small.Height));
    }

    /// <summary>
    /// A decode allocates the whole image, so a file that declares itself
    /// far too large is refused on its header, before any of that: the
    /// stream is read only as far as the header, and the refusal names the
    /// dimension, not a decode failure.
    /// </summary>
    [Fact]
    public void AnOversizedHeaderIsRefusedBeforeAnyDecodeIsAttempted()
    {
        (PluginImageTable table, FakeBackend backend, List<string> reports) = Bound();
        const int bodyLength = 64 * 1024;
        var stream = new CountingStream(PngTestData.OversizedHeader(bodyLength));

        PluginImageHandle refused = table.AcquireDecoded("huge", () => stream);

        Assert.False(refused.IsValid);
        Assert.Empty(backend.Uploads);
        Assert.Contains(reports, line => line.Contains("16384x16384", StringComparison.Ordinal));
        Assert.DoesNotContain(reports, line => line.Contains("could not be decoded", StringComparison.Ordinal));
        Assert.True(
            stream.BytesRead < 1024,
            $"the file was read {stream.BytesRead:N0} bytes deep; a header read stops well before the pixel data");
    }

    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public long BytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = base.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            int read = base.Read(buffer);
            BytesRead += read;
            return read;
        }

        public override int ReadByte()
        {
            int value = base.ReadByte();
            if (value >= 0)
                BytesRead++;
            return value;
        }
    }

    [Fact]
    public void AStreamThatCannotBeDecodedIsRefusedWithoutThrowing()
    {
        (PluginImageTable table, FakeBackend backend, List<string> reports) = Bound();

        PluginImageHandle garbage = table.AcquireDecoded(
            "garbage", static () => new MemoryStream([1, 2, 3, 4]));
        PluginImageHandle thrown = table.AcquireDecoded(
            "thrown", static () => throw new IOException("no such file"));

        Assert.False(garbage.IsValid);
        Assert.False(thrown.IsValid);
        Assert.Empty(backend.Uploads);
        Assert.Contains(reports, line => line.Contains("no such file", StringComparison.Ordinal));
    }

    [Fact]
    public void ClientArtIsSharedNeverOwnedAndNeverGivenBack()
    {
        (PluginImageTable table, FakeBackend backend, _) = Bound();

        PluginImageHandle art = table.AcquireClientArt(0x06001234u);
        PluginImageHandle spell = table.AcquireSpellIcon(2u);
        PluginImageHandle item = table.AcquireObjectIcon(0x80000001u);

        Assert.True(art.IsValid);
        Assert.True(spell.IsValid);
        Assert.True(item.IsValid);
        Assert.Equal(0L, table.OwnedBytes);
        Assert.Equal(3, table.Count);
        Assert.True(table.TryResolve(art, out uint texture, out int width, out _));
        Assert.Equal(7u, texture);
        Assert.Equal(32, width);

        Assert.True(table.Release(art));
        Assert.True(table.Release(spell));
        Assert.True(table.Release(item));
        Assert.Empty(backend.Released);
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void ArtTheClientDoesNotHaveIsRefused()
    {
        (PluginImageTable table, _, List<string> reports) = Bound();

        Assert.False(table.AcquireClientArt(0x06009999u).IsValid);
        Assert.False(table.AcquireClientArt(0u).IsValid);
        Assert.Equal(0, table.Count);
        Assert.Contains(reports, line => line.Contains("0x06009999", StringComparison.Ordinal));
    }

    [Fact]
    public void ClearGivesBackEveryOwnedImageOnceHoweverManyTimesItWasHeld()
    {
        (PluginImageTable table, FakeBackend backend, _) = Bound();
        PluginImageHandle map = table.AcquireDecoded("map", Png64());
        table.AcquireDecoded("map", Png64());
        table.AcquireDecoded("hud", Png32());
        table.AcquireClientArt(0x06001234u);

        table.Clear();

        Assert.Equal(2, backend.Released.Count);
        Assert.Equal(0, table.Count);
        Assert.Equal(0L, table.OwnedBytes);
        Assert.False(table.TryResolve(map, out _, out _, out _));
        Assert.False(table.Release(map));
    }

    [Fact]
    public void UnboundTheTableAnswersNoneAndBindingLaterMakesItLive()
    {
        var table = new PluginImageTable("example.plugin", PluginImageBudget.Default, _ => { });

        Assert.False(table.IsBound);
        Assert.False(table.AcquireClientArt(0x06001234u).IsValid);
        Assert.False(table.AcquireDecoded("map", Png64()).IsValid);

        table.Bind(new FakeBackend(), Environment.CurrentManagedThreadId);
        Assert.True(table.AcquireClientArt(0x06001234u).IsValid);
    }

    [Fact]
    public void UnbindLetsGoOfEverythingAndForgetsTheBackend()
    {
        (PluginImageTable table, FakeBackend backend, _) = Bound();
        PluginImageHandle map = table.AcquireDecoded("map", Png64());

        table.Unbind();

        Assert.Single(backend.Released);
        Assert.False(table.IsBound);
        Assert.False(table.TryResolve(map, out _, out _, out _));
        Assert.False(table.AcquireDecoded("map", Png64()).IsValid);
        Assert.Single(backend.Uploads);
    }

    [Fact]
    public void ABoundTableRefusesAnotherThread()
    {
        (PluginImageTable table, _, _) = Bound();
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try { table.AcquireClientArt(0x06001234u); }
            catch (Exception caught) { failure = caught; }
        });
        thread.Start();
        thread.Join();

        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(0, table.Count);
    }

    private static PluginFileImage FileImage(string path, string version, int width, int height) =>
        new(path, version, RgbaMipChain.Build(new byte[width * height * 4], width, height));

    [Fact]
    public void AFileIsOneImagePerPathAndVersionHeldOncePerAsker()
    {
        (PluginImageTable table, FakeBackend backend, _) = Bound();

        PluginImageHandle first = table.AcquireFile(FileImage("icons/sword.png", "1", 16, 16));
        PluginImageHandle second = table.AcquireFile(FileImage("icons/sword.png", "1", 16, 16));
        PluginImageHandle changed = table.AcquireFile(FileImage("icons/sword.png", "2", 8, 8));

        Assert.Equal(first, second);
        Assert.NotEqual(first.Id, changed.Id);
        Assert.Equal(2, backend.Uploads.Count);

        // The window still showing the old version keeps it after another lets go.
        Assert.True(table.Release(first));
        Assert.True(table.TryResolve(second, out _, out _, out _));
        Assert.True(table.Release(second));
        Assert.False(table.TryResolve(second, out _, out _, out _));
        Assert.True(table.TryResolve(changed, out _, out int width, out _));
        Assert.Equal(8, width);
        Assert.Single(backend.Released);
    }

    [Fact]
    public void AFileAndAStreamOfTheSameNameAreDifferentImages()
    {
        (PluginImageTable table, FakeBackend backend, _) = Bound();

        PluginImageHandle stream = table.AcquireDecoded("icons/sword.png", Png32());
        PluginImageHandle file = table.AcquireFile(FileImage("icons/sword.png", "1", 16, 16));

        Assert.NotEqual(stream.Id, file.Id);
        Assert.Equal(2, backend.Uploads.Count);
    }

    [Fact]
    public void AFilesSmallerLevelsCountAgainstTheByteBudget()
    {
        long image = 16L * 16L * 4L;
        (PluginImageTable table, FakeBackend backend, List<string> reports) =
            Bound(new PluginImageBudget(MaximumCount: 256, MaximumBytes: image + 1, MaximumDimension: 2048));

        PluginImageHandle refused = table.AcquireFile(FileImage("icons/sword.png", "1", 16, 16));

        Assert.False(refused.IsValid);
        Assert.Empty(backend.Uploads);
        Assert.Contains(reports, line => line.Contains("budget is", StringComparison.Ordinal));

        Assert.True(table.AcquireFile(FileImage("icons/dot.png", "1", 1, 1)).IsValid);
        Assert.Equal(4L, table.OwnedBytes);
    }

    [Fact]
    public void FilesShareTheCountWithThePluginsOtherImages()
    {
        (PluginImageTable table, _, List<string> reports) =
            Bound(new PluginImageBudget(MaximumCount: 2, MaximumBytes: 1024 * 1024, MaximumDimension: 2048));

        Assert.True(table.AcquireDecoded("map", Png32()).IsValid);
        Assert.True(table.AcquireFile(FileImage("icons/a.png", "1", 4, 4)).IsValid);
        Assert.False(table.AcquireFile(FileImage("icons/b.png", "1", 4, 4)).IsValid);

        Assert.Contains(reports, line => line.Contains("2 images", StringComparison.Ordinal));
    }

    [Fact]
    public void AFileWiderThanTheMaximumDimensionIsRefused()
    {
        (PluginImageTable table, FakeBackend backend, List<string> reports) =
            Bound(new PluginImageBudget(MaximumCount: 256, MaximumBytes: 1024 * 1024, MaximumDimension: 8));

        Assert.False(table.AcquireFile(FileImage("icons/wide.png", "1", 16, 4)).IsValid);

        Assert.Empty(backend.Uploads);
        Assert.Contains(reports, line => line.Contains("16x4", StringComparison.Ordinal));
    }

    [Fact]
    public void UnboundAFileIsRefused()
    {
        var table = new PluginImageTable("example.plugin", PluginImageBudget.Default, _ => { });

        Assert.False(table.AcquireFile(FileImage("icons/sword.png", "1", 4, 4)).IsValid);
    }
}
