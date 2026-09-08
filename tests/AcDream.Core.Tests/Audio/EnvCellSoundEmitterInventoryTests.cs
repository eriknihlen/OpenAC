using System;
using System.Collections.Generic;
using System.Linq;
using AcDream.Core.Tests.Conformance;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.Core.Tests.Audio;

[Trait("Lane", "InstalledDat")]
public sealed class EnvCellSoundEmitterInventoryTests
{
    private readonly ITestOutputHelper _out;

    public EnvCellSoundEmitterInventoryTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void HoltburgInteriors_CarryNoDatAuthoredAmbientEmitters()
    {
        string? datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null)
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(new DatCollectionOptions
        {
            DatDirectory = datDir,
            AccessType = DatAccessType.Read,
        });

        // Holtburg landblock: envcells are 0xA9B4_0100 upward.
        const uint landblock = 0xA9B40000u;
        var setupTables = new Dictionary<uint, uint>();   // setup -> soundtable
        var emitterCells = new List<(uint Cell, uint Setup, uint Table, string Slots)>();

        for (uint cellId = landblock | 0x0100u; cellId <= (landblock | 0x01FFu); cellId++)
        {
            EnvCell? cell;
            try
            {
                cell = dats.Get<EnvCell>(cellId);
            }
            catch
            {
                continue;
            }
            if (cell is null)
                continue;

            foreach (var stab in cell.StaticObjects)
            {
                if ((stab.Id & 0xFF000000u) != 0x02000000u)
                    continue;

                if (!setupTables.TryGetValue(stab.Id, out uint tableDid))
                {
                    Setup? setup = dats.Get<Setup>(stab.Id);
                    tableDid = setup?.DefaultSoundTable?.DataId ?? 0u;
                    setupTables[stab.Id] = tableDid;
                }
                if (tableDid == 0u)
                    continue;

                SoundTable? table = dats.Get<SoundTable>(tableDid);
                if (table is null)
                    continue;

                var ambientSlots = table.Sounds.Keys
                    .Where(sound => (uint)sound is >= 0x46u and <= 0x4Eu)
                    .OrderBy(sound => (uint)sound)
                    .ToList();
                if (ambientSlots.Count == 0)
                    continue;

                emitterCells.Add((
                    cellId,
                    stab.Id,
                    tableDid,
                    string.Join(",", ambientSlots)));
            }
        }

        foreach (var row in emitterCells)
        {
            _out.WriteLine(
                $"cell=0x{row.Cell:X8} setup=0x{row.Setup:X8} " +
                $"table=0x{row.Table:X8} slots=[{row.Slots}]");
        }
        _out.WriteLine($"total emitter placements: {emitterCells.Count}");

        Assert.Empty(emitterCells);
    }

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void Diagnostic_PortalDat_SetupsWithAmbientSlotSoundTables()
    {
        string? datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null)
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(new DatCollectionOptions
        {
            DatDirectory = datDir,
            AccessType = DatAccessType.Read,
        });

        int scanned = 0;
        var hits = new List<string>();
        foreach (var entry in dats.Portal.Tree
                     .Where(e => e.Id >= 0x02000000u && e.Id <= 0x0200FFFFu))
        {
            Setup? setup;
            try
            {
                setup = dats.Portal.Get<Setup>(entry.Id);
            }
            catch
            {
                continue;
            }
            if (setup is null)
                continue;
            scanned++;

            uint tableDid = setup.DefaultSoundTable?.DataId ?? 0u;
            if (tableDid == 0u)
                continue;
            SoundTable? table = dats.Get<SoundTable>(tableDid);
            if (table is null)
                continue;

            var ambient = table.Sounds.Keys
                .Where(s => (uint)s is >= 0x46u and <= 0x4Eu)
                .OrderBy(s => (uint)s)
                .ToList();
            if (ambient.Count == 0)
                continue;

            hits.Add(
                $"setup=0x{entry.Id:X8} table=0x{tableDid:X8} " +
                $"mtable=0x{(setup.DefaultMotionTable?.DataId ?? 0):X8} " +
                $"script=0x{(setup.DefaultScript?.DataId ?? 0):X8} " +
                $"stable-slots=[{string.Join(",", ambient)}]");
        }

        foreach (string hit in hits)
            _out.WriteLine(hit);
        _out.WriteLine($"setups scanned: {scanned}; ambient-slot setups: {hits.Count}");
    }

    [Fact]
    [Trait("Purpose", "Diagnostic")]
    public void Diagnostic_SoundTables_WithAmbientSlots_ExistForWireBinding()
    {
        string? datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null)
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(new DatCollectionOptions
        {
            DatDirectory = datDir,
            AccessType = DatAccessType.Read,
        });

        int tables = 0;
        var hits = new List<string>();
        foreach (var entry in dats.Portal.Tree
                     .Where(e => e.Id >= 0x20000000u && e.Id <= 0x2000FFFFu))
        {
            SoundTable? table;
            try
            {
                table = dats.Get<SoundTable>(entry.Id);
            }
            catch
            {
                continue;
            }
            if (table is null)
                continue;
            tables++;

            var ambient = table.Sounds.Keys
                .Where(s => (uint)s is >= 0x46u and <= 0x4Eu)
                .OrderBy(s => (uint)s)
                .ToList();
            if (ambient.Count == 0)
                continue;
            hits.Add(
                $"table=0x{entry.Id:X8} ambient-slots=[{string.Join(",", ambient)}] " +
                $"total-slots={table.Sounds.Count}");
        }

        foreach (string hit in hits)
            _out.WriteLine(hit);
        _out.WriteLine($"tables scanned: {tables}; with ambient slots: {hits.Count}");
    }
}
