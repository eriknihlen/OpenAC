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
public sealed class UiSoundTableResolutionTests
{
    private const uint UiSoundTableTypeKey = 0x10000003u;

    /// <summary>The enum slot <c>GetUISoundTable</c> asks for.</summary>
    private const uint UiSoundTableEnumSlot = 7u;

    public const uint ExpectedUiSoundTableDid = 0x2000004Bu;

    private readonly ITestOutputHelper _out;

    public UiSoundTableResolutionTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void UiSoundTable_ResolvesThroughTheEnumIdMapChain()
    {
        string? datDir = ConformanceDats.ResolveDatDir();
        if (datDir is null)
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");   // dats absent (CI) — nothing to resolve

        using var dats = new DatCollection(new DatCollectionOptions
        {
            DatDirectory = datDir,
            AccessType = DatAccessType.Read,
        });

        var candidates = new List<(uint MapId, uint PerTypeMapId, uint SoundTableDid)>();
        for (uint id = 0x25000000u; id <= 0x2500FFFFu; id++)
        {
            EnumIDMap? master;
            try
            {
                master = dats.Get<EnumIDMap>(id);
            }
            catch
            {
                continue;   // not an EnumIDMap / unreadable
            }
            if (master is null)
                continue;

            if (!master.ClientEnumToID.TryGetValue(UiSoundTableEnumSlot, out uint perSlot)
                || perSlot == 0)
            {
                continue;
            }

            EnumIDMap? perSlotMap;
            try
            {
                perSlotMap = dats.Get<EnumIDMap>(perSlot);
            }
            catch
            {
                continue;
            }
            if (perSlotMap is null)
                continue;

            _out.WriteLine(
                $"master 0x{id:X8} -> slot-{UiSoundTableEnumSlot} map 0x{perSlot:X8} " +
                $"({perSlotMap.ClientEnumToID.Count} type keys)");
            foreach (var slot in perSlotMap.ClientEnumToID.OrderBy(kv => kv.Key))
                _out.WriteLine($"    typeKey 0x{slot.Key:X8} -> 0x{slot.Value:X8}");

            if (perSlotMap.ClientEnumToID.TryGetValue(UiSoundTableTypeKey, out uint did)
                && did != 0)
            {
                candidates.Add((id, perSlot, did));
            }
        }

        Assert.NotEmpty(candidates);

        uint resolved = candidates[0].SoundTableDid;
        Assert.All(candidates, c => Assert.Equal(resolved, c.SoundTableDid));

        _out.WriteLine($"RESOLVED UI sound table DID = 0x{resolved:X8}");
        Assert.Equal(ExpectedUiSoundTableDid, resolved);

        // It must be a real SoundTable in the 0x20xxxxxx range and it must load.
        Assert.InRange(resolved, 0x20000000u, 0x2000FFFFu);
        SoundTable? table = dats.Get<SoundTable>(resolved);
        Assert.NotNull(table);
        Assert.NotEmpty(table!.Sounds);

        _out.WriteLine($"UI sound table 0x{resolved:X8} carries {table.Sounds.Count} slots:");
        foreach (var kv in table.Sounds.OrderBy(kv => (uint)kv.Key))
            _out.WriteLine($"    {kv.Key} ({(uint)kv.Key:X2}) -> {kv.Value.Entries.Count} entries");

        Assert.All(
            table.Sounds.Keys,
            slot => Assert.InRange((uint)slot, 0x6Au, 0x8Au));
        Assert.Contains(DatReaderWriter.Enums.Sound.UI_EnterPortal, table.Sounds.Keys);
        Assert.Contains(DatReaderWriter.Enums.Sound.UI_ExitPortal, table.Sounds.Keys);
        Assert.Contains(DatReaderWriter.Enums.Sound.UI_Roar, table.Sounds.Keys);
        Assert.Contains(DatReaderWriter.Enums.Sound.UI_Thunder6, table.Sounds.Keys);
    }
}
