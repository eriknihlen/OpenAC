using System.Collections.Immutable;
using AcDream.Content;
using AcDream.Headless.Configuration;
using DatReaderWriter.DBObjs;

namespace AcDream.Headless.Hosting;

internal readonly record struct HeadlessProcessContentSnapshot(
    int LeaseCount,
    bool IsDisposeRequested,
    bool IsDisposed,
    long MappedVirtualBytes)
{
    internal bool IsConverged =>
        IsDisposed
        && LeaseCount == 0
        && MappedVirtualBytes == 0L;
}

internal interface IHeadlessProcessContentFactory
{
    HeadlessOpenedProcessContent Open(
        HeadlessContentDescriptor descriptor,
        Action<string> diagnostic);
}

internal sealed record HeadlessOpenedProcessContent(
    IDatReaderWriter Dats,
    IPreparedAssetSource Prepared,
    MagicCatalog Magic,
    ImmutableArray<float> HeightTable);

internal sealed class ProductionHeadlessProcessContentFactory
    : IHeadlessProcessContentFactory
{
    public HeadlessOpenedProcessContent Open(
        HeadlessContentDescriptor descriptor,
        Action<string> diagnostic)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(diagnostic);

        string datDirectory = Path.GetFullPath(descriptor.DatDirectory);
        string preparedAssetPath =
            Path.GetFullPath(descriptor.PreparedAssetPath);
        string? overlayPath = string.IsNullOrWhiteSpace(
            descriptor.PreparedAssetOverlayPath)
            ? null
            : Path.GetFullPath(descriptor.PreparedAssetOverlayPath);
        IDatReaderWriter? dats = null;
        IPreparedAssetSource? prepared = null;
        try
        {
            dats = RuntimeDatCollectionFactory.OpenReadOnly(datDirectory);
            if (overlayPath is null)
            {
                prepared = new PakPreparedAssetSource(
                    preparedAssetPath,
                    dats,
                    diagnostic);
            }
            else
            {
                if (descriptor.PreparedAssetBaseRecipeVersion is not > 0
                    || descriptor.PreparedAssetEffectiveRecipeVersion
                        != AcDream.Content.Pak.PakFormat.CurrentBakeToolVersion)
                {
                    throw new InvalidDataException(
                        "Layered prepared content does not match the client's recipe.");
                }

                var baseSource = new PakPreparedAssetSource(
                    preparedAssetPath,
                    PreparedAssetCatalogIdentity.From(
                        dats,
                        descriptor.PreparedAssetBaseRecipeVersion.Value),
                    diagnostic);
                try
                {
                    var overlaySource = new PakPreparedAssetSource(
                        overlayPath,
                        PreparedAssetCatalogIdentity.From(
                            dats,
                            descriptor.PreparedAssetEffectiveRecipeVersion.Value),
                        diagnostic);
                    prepared = new LayeredPreparedAssetSource(
                        baseSource,
                        overlaySource);
                }
                catch
                {
                    baseSource.Dispose();
                    throw;
                }
            }
            MagicCatalog magic = MagicCatalog.Load(dats);
            Region region = dats.Get<Region>(0x13000000u)
                ?? throw new InvalidOperationException(
                    "Region dat id 0x13000000 is missing.");
            float[]? sourceHeightTable =
                region.LandDefs.LandHeightTable;
            if (sourceHeightTable is null
                || sourceHeightTable.Length < 256)
            {
                throw new InvalidOperationException(
                    "Region.LandDefs.LandHeightTable is missing or truncated.");
            }
            ImmutableArray<float> heightTable =
                ImmutableArray.CreateRange(sourceHeightTable);
            return new HeadlessOpenedProcessContent(
                dats,
                prepared,
                magic,
                heightTable);
        }
        catch
        {
            try
            {
                prepared?.Dispose();
            }
            finally
            {
                dats?.Dispose();
            }
            throw;
        }
    }
}

internal sealed class HeadlessProcessContentOwner : IDisposable
{
    private readonly object _gate = new();
    private readonly MagicCatalog _magic;
    private readonly ImmutableArray<float> _heightTable;
    private IDatReaderWriter? _dats;
    private IPreparedAssetSource? _prepared;
    private SharedPreparedCollisionCache? _collision;
    private int _leaseCount;
    private bool _disposeRequested;
    private bool _disposed;

    internal HeadlessProcessContentOwner(
        HeadlessContentDescriptor descriptor,
        Action<string> diagnostic,
        IHeadlessProcessContentFactory? factory = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(diagnostic);
        HeadlessOpenedProcessContent? opened =
            (factory ?? new ProductionHeadlessProcessContentFactory())
                .Open(descriptor, diagnostic);
        if (opened is null)
        {
            throw new InvalidOperationException(
                "The headless content factory returned no content owner.");
        }
        IDatReaderWriter? dats = opened.Dats;
        IPreparedAssetSource? prepared = opened.Prepared;
        MagicCatalog? magic = opened.Magic;
        ImmutableArray<float> heightTable = opened.HeightTable;
        bool incomplete =
            dats is null
            || prepared is null
            || magic is null
            || heightTable.IsDefaultOrEmpty
            || heightTable.Length < 256;
        if (incomplete || prepared is not IPreparedCollisionSource)
        {
            try
            {
                prepared?.Dispose();
            }
            finally
            {
                dats?.Dispose();
            }
            throw !incomplete
                && prepared is not IPreparedCollisionSource
                ? new NotSupportedException(
                    "Headless production content must expose prepared collision.")
                : new InvalidOperationException(
                    "The headless content factory returned an incomplete owner.");
        }
        _dats = dats!;
        _prepared = prepared!;
        _magic = magic!;
        _heightTable = heightTable;
        _collision = new SharedPreparedCollisionCache(
            (IPreparedCollisionSource)prepared!);
    }

    internal HeadlessProcessContentLease AcquireLease(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(
                _disposeRequested || _disposed,
                this);
            checked
            {
                _leaseCount++;
            }
            return new HeadlessProcessContentLease(
                this,
                sessionId,
                _dats!,
                _prepared!,
                _collision!,
                _magic,
                _heightTable);
        }
    }

    internal HeadlessProcessContentSnapshot CaptureSnapshot()
    {
        lock (_gate)
        {
            return new HeadlessProcessContentSnapshot(
                _leaseCount,
                _disposeRequested,
                _disposed,
                _prepared?.MappedVirtualBytes ?? 0L);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposeRequested = true;
            if (_leaseCount != 0)
                return;
            DrainResources();
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            if (_leaseCount <= 0)
            {
                throw new InvalidOperationException(
                    "Headless content lease count underflow.");
            }
            _leaseCount--;
            if (_leaseCount == 0 && _disposeRequested)
                DrainResources();
        }
    }

    private void DrainResources()
    {
        if (_collision is not null)
        {
            _collision.Dispose();
            _collision = null;
        }
        if (_prepared is not null)
        {
            _prepared.Dispose();
            _prepared = null;
        }
        if (_dats is not null)
        {
            _dats.Dispose();
            _dats = null;
        }
        _disposed = true;
    }

    internal sealed class HeadlessProcessContentLease : IDisposable
    {
        private HeadlessProcessContentOwner? _owner;
        private readonly IDatReaderWriter _dats;
        private readonly IPreparedAssetSource _prepared;
        private readonly IPreparedCollisionSource _collision;
        private readonly MagicCatalog _magic;
        private readonly ImmutableArray<float> _heightTable;

        internal HeadlessProcessContentLease(
            HeadlessProcessContentOwner owner,
            string sessionId,
            IDatReaderWriter dats,
            IPreparedAssetSource prepared,
            IPreparedCollisionSource collision,
            MagicCatalog magic,
            ImmutableArray<float> heightTable)
        {
            _owner = owner;
            SessionId = sessionId;
            _dats = dats;
            _prepared = prepared;
            _collision = collision;
            _magic = magic;
            _heightTable = heightTable;
        }

        internal string SessionId { get; }

        internal IDatReaderWriter Dats
        {
            get
            {
                ObjectDisposedException.ThrowIf(_owner is null, this);
                return _dats;
            }
        }

        internal IPreparedAssetSource PreparedAssets
        {
            get
            {
                ObjectDisposedException.ThrowIf(_owner is null, this);
                return _prepared;
            }
        }

        internal IPreparedCollisionSource PreparedCollision
        {
            get
            {
                ObjectDisposedException.ThrowIf(_owner is null, this);
                return _collision;
            }
        }

        internal MagicCatalog MagicCatalog
        {
            get
            {
                ObjectDisposedException.ThrowIf(_owner is null, this);
                return _magic;
            }
        }

        internal ImmutableArray<float> HeightTable
        {
            get
            {
                ObjectDisposedException.ThrowIf(_owner is null, this);
                return _heightTable;
            }
        }

        public void Dispose()
        {
            HeadlessProcessContentOwner? owner =
                Interlocked.Exchange(ref _owner, null);
            owner?.Release();
        }
    }
}
