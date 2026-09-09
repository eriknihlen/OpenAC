using System.Collections.Generic;
using System.Linq;
using AcDream.Content;
using AcDream.Core.Input;
using AcDream.Runtime.Gameplay;
using AcDream.UI.Abstractions.Input;
using DatReaderWriter;
using Xunit;

namespace AcDream.Core.Tests.Input;

[Trait("Lane", "InstalledDat")]
public sealed class RetailActionIdentityRoundTripTests
{
    [Fact]
    public void MappedActions_DatUnionDefaultBindings_MatchRetailDefaults()
    {
        string? datDir = Conformance.ConformanceDats.ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir);
        var source = new DatCollectionAdapter(dats);
        RetailActionMapSnapshot? snapshot = RetailActionMapReader.Read(source);
        Assert.NotNull(snapshot);

        Assert.Equal(306, snapshot!.Rows.Count);
        var unresolvedRows = snapshot.Rows
            .Where(row => !RetailActionIdentityTable.TryResolve(
                row.InputMapId,
                row.ActionId,
                out _))
            .Select(row => $"0x{row.InputMapId:X8}/0x{row.ActionId:X8}")
            .ToArray();
        Assert.Empty(unresolvedRows);
        Assert.Equal(306, RetailActionIdentityTable.Map.Count);
        Assert.Equal(306, RetailActionIdentityTable.Map.Values.Distinct().Count());
        Assert.Equal(306, RetailActionIdentityTable.ReverseMap.Count);

        var optionIds = new HashSet<uint>();
        foreach (RetailActionMapRow row in snapshot.Rows.Where(
                     static row => row.InputMapId == 0x10000008u))
        {
            Assert.True(RetailActionIdentityTable.TryResolve(
                row.InputMapId,
                row.ActionId,
                out InputAction action));
            Assert.True(
                RetailActionIdentityTable.TryGetCharacterOptionId(
                    action,
                    out uint optionId),
                $"CharacterSettings row 0x{row.ActionId:X8} has no PlayerOption id");
            Assert.True(CharacterOptionTable.TryGet(optionId, out _));
            Assert.True(optionIds.Add(optionId), $"duplicate PlayerOption id 0x{optionId:X2}");
        }
        Assert.Equal(48, optionIds.Count);

        KeyBindings retailDefaults = KeyBindings.RetailDefaults();

        var datChordsByAction = new Dictionary<InputAction, HashSet<KeyChord>>();
        var unresolvedScanCodes = new List<string>();
        foreach (RetailActionMapRow row in snapshot.Rows)
        {
            if (!RetailActionIdentityTable.TryResolve(row.InputMapId, row.ActionId, out InputAction action))
                continue;
            if (!datChordsByAction.TryGetValue(action, out HashSet<KeyChord>? set))
                datChordsByAction[action] = set = new HashSet<KeyChord>();

            foreach (RetailKeyChord raw in row.DefaultBindings)
            {
                Silk.NET.Input.Key? key = RetailScanCodeMap.ToSilkKey(raw.Scan, raw.Device);
                if (key is null)
                {
                    unresolvedScanCodes.Add(
                        $"{action}: DAT default scan=0x{raw.Scan:X2} dev={raw.Device} has no "
                        + "RetailScanCodeMap entry");
                    continue;
                }
                set.Add(new KeyChord(key.Value, RetailScanCodeMap.ToModifierMask(raw.Modifier), (byte)raw.Device));
            }
        }

        Assert.Equal(306, datChordsByAction.Count);
        Assert.Empty(unresolvedScanCodes);

        var mismatches = new List<string>();
        foreach ((InputAction action, HashSet<KeyChord> datChords) in datChordsByAction)
        {
            var acdreamChords = retailDefaults.ForAction(action).Select(b => b.Chord).ToHashSet();
            if (!datChords.SetEquals(acdreamChords))
            {
                mismatches.Add(
                    $"{action}: DAT union=[{string.Join(",", datChords)}] vs "
                    + $"RetailDefaults()=[{string.Join(",", acdreamChords)}]");
            }
        }

        Assert.True(mismatches.Count == 0,
            $"{mismatches.Count} DAT-vs-RetailDefaults() disagreements:\n"
            + string.Join("\n", mismatches));
    }
}
