using AcDream.Content;
using AcDream.Content.Pak;

namespace AcDream.Bake.Tests;

public sealed class BakeOutputTransactionTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"acdream-bake-transaction-{Guid.NewGuid():N}");

    public BakeOutputTransactionTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void StagingPathUsesTheDocumentedLauncherRecoveryContract()
    {
        string destination = Path.Combine(_directory, "pak", "acdream.pak");
        Guid transaction = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");

        string staging = BakeOutputTransaction.CreateStagingPath(
            destination,
            transaction);

        Assert.Equal(
            Path.Combine(
                _directory,
                "pak",
                ".acdream.pak.acdream-bake.0123456789abcdef0123456789abcdef.tmp"),
            staging);
    }

    [Fact]
    public void Publish_ReplacesExistingDestinationOnlyAfterValidation()
    {
        string destination = Path.Combine(_directory, "content.pak");
        File.WriteAllText(destination, "old");

        int result = BakeOutputTransaction.WriteValidateAndPublish(
            destination,
            temporary =>
            {
                File.WriteAllText(temporary, "new");
                return 42;
            },
            (temporary, value) =>
            {
                Assert.Equal(42, value);
                Assert.Equal("new", File.ReadAllText(temporary));
                Assert.Equal("old", File.ReadAllText(destination));
            });

        Assert.Equal(42, result);
        Assert.Equal("new", File.ReadAllText(destination));
        AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Failure_PreservesExistingDestinationAndCleansTemporary(bool failDuringWrite)
    {
        string destination = Path.Combine(_directory, "content.pak");
        File.WriteAllText(destination, "old");

        Assert.Throws<InvalidOperationException>(
            () => BakeOutputTransaction.WriteValidateAndPublish(
                destination,
                temporary =>
                {
                    File.WriteAllText(temporary, "partial");
                    if (failDuringWrite)
                        throw new InvalidOperationException("write failed");
                    return 1;
                },
                (_, _) => throw new InvalidOperationException("validation failed")));

        Assert.Equal("old", File.ReadAllText(destination));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void CancellationBeforeWrite_PreservesExistingDestination()
    {
        string destination = Path.Combine(_directory, "content.pak");
        File.WriteAllText(destination, "old");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => BakeOutputTransaction.WriteValidateAndPublish(
                destination,
                temporary =>
                {
                    File.WriteAllText(temporary, "new");
                    return 1;
                },
                (_, _) => { },
                cancellation.Token));

        Assert.Equal("old", File.ReadAllText(destination));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void ArtifactValidator_RejectsStaleBakeToolVersion()
    {
        string pak = Path.Combine(_directory, "stale.pak");
        var expected = Header();
        using (var writer = new PakWriter(pak, expected))
        {
            writer.AddBlob(
                PakKey.Compose(PakAssetType.GfxObjMesh, 1),
                new ObjectMeshData { ObjectId = 1 });
            writer.Finish();
        }

        using (var stream = new FileStream(pak, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = 36;
            Span<byte> stale = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(stale, 1);
            stream.Write(stale);
        }

        var exception = Assert.Throws<InvalidDataException>(
            () => BakeArtifactValidator.Validate(pak, expected, expectedTocCount: 1));
        Assert.Contains("bake tool version", exception.Message);
    }

    [Fact]
    public void ArtifactValidator_RejectsWrongDatIterationAndCorruptTocRange()
    {
        string pak = Path.Combine(_directory, "content.pak");
        var expected = Header();
        using (var writer = new PakWriter(pak, expected))
        {
            writer.AddBlob(
                PakKey.Compose(PakAssetType.GfxObjMesh, 1),
                new ObjectMeshData { ObjectId = 1 });
            writer.Finish();
        }

        var wrongIteration = expected;
        wrongIteration.CellIteration++;
        Assert.Throws<InvalidDataException>(
            () => BakeArtifactValidator.Validate(pak, wrongIteration, expectedTocCount: 1));

        using (var stream = new FileStream(pak, FileMode.Open, FileAccess.ReadWrite))
        {
            var header = PakHeader.ReadFrom(stream);
            stream.Position = checked((long)header.TocOffset + 16);
            Span<byte> invalidLength = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                invalidLength,
                uint.MaxValue);
            stream.Write(invalidLength);
        }

        Assert.Throws<InvalidDataException>(
            () => BakeArtifactValidator.Validate(pak, expected, expectedTocCount: 1));
    }

    [Fact]
    public void ArtifactValidator_RejectsIncompleteTypedCatalog()
    {
        string pak = Path.Combine(_directory, "typed-content.pak");
        var expected = Header();
        using (var writer = new PakWriter(pak, expected))
        {
            writer.AddBlob(
                PakKey.Compose(PakAssetType.GfxObjMesh, 1),
                new ObjectMeshData { ObjectId = 1 });
            writer.Finish();
        }

        var expectedTypes = new Dictionary<PakAssetType, int>
        {
            [PakAssetType.GfxObjMesh] = 1,
            [PakAssetType.GfxObjCollision] = 1,
        };
        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => BakeArtifactValidator.Validate(
                pak,
                expected,
                expectedTocCount: 1,
                expectedTypeCounts: expectedTypes));
        Assert.Contains("GfxObjCollision", exception.Message);
    }

    private PakHeader Header() =>
        new()
        {
            PortalIteration = 10,
            CellIteration = 20,
            HighResIteration = 30,
            LanguageIteration = 40,
            BakeToolVersion = PakFormat.CurrentBakeToolVersion,
        };

    private void AssertNoTemporaryFiles() =>
        Assert.Empty(Directory.EnumerateFiles(_directory, ".*.tmp"));
}
