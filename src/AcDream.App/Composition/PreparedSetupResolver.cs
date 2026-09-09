using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using AcDream.Content;
using AcDream.Content.Pak;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Composition;

internal sealed class PreparedSetupResolver
{
    private readonly IPreparedAssetSource _preparedAssets;
    private readonly Func<uint, Setup?> _loadSetup;
    private readonly Action<string> _diagnostic;
    private readonly ConcurrentDictionary<uint, byte> _diagnosed = new();

    public PreparedSetupResolver(
        IPreparedAssetSource preparedAssets,
        Func<uint, Setup?> loadSetup,
        Action<string> diagnostic)
    {
        _preparedAssets = preparedAssets
            ?? throw new ArgumentNullException(nameof(preparedAssets));
        _loadSetup = loadSetup
            ?? throw new ArgumentNullException(nameof(loadSetup));
        _diagnostic = diagnostic
            ?? throw new ArgumentNullException(nameof(diagnostic));
    }

    public bool TryResolve(
        uint sourceId,
        [NotNullWhen(true)] out Setup? setup)
    {
        PreparedAssetPresence presence =
            _preparedAssets.Probe(PakAssetType.SetupMesh, sourceId);
        if (presence == PreparedAssetPresence.Missing)
        {
            setup = null;
            return false;
        }
        if (presence == PreparedAssetPresence.Corrupt)
        {
            throw new InvalidDataException(
                $"Prepared Setup entry 0x{sourceId:X8} is corrupt; " +
                "activation cannot safely infer presentation metadata.");
        }

        try
        {
            setup = _loadSetup(sourceId);
            if (setup is not null)
                return true;

            DiagnoseOnce(
                sourceId,
                $"Prepared Setup entry 0x{sourceId:X8} exists but its " +
                "matching DAT record could not be loaded; activation skipped.");
            return false;
        }
        catch (InvalidDataException ex)
        {
            return DiagnoseCorruptData(sourceId, ex, out setup);
        }
        catch (EndOfStreamException ex)
        {
            return DiagnoseCorruptData(sourceId, ex, out setup);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return DiagnoseCorruptData(sourceId, ex, out setup);
        }
    }

    private bool DiagnoseCorruptData(
        uint sourceId,
        Exception exception,
        out Setup? setup)
    {
        setup = null;
        DiagnoseOnce(
            sourceId,
            $"Setup DAT record 0x{sourceId:X8} is malformed " +
            $"({exception.GetType().Name}: {exception.Message}); " +
            "activation skipped.");
        return false;
    }

    private void DiagnoseOnce(uint sourceId, string message)
    {
        if (_diagnosed.TryAdd(sourceId, 0))
            _diagnostic(message);
    }
}
