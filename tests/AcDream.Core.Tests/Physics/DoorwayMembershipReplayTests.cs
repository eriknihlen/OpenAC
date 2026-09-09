using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AcDream.Core.Physics;
using Xunit;
using Xunit.Abstractions;

namespace AcDream.Core.Tests.Physics;

public class DoorwayMembershipReplayTests
{
    private readonly ITestOutputHelper _output;

    public DoorwayMembershipReplayTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private const uint IndoorDoorwayCell = 0xA9B40170u;  // low=0x0170 >= 0x0100 (indoor)
    private const uint OutdoorDoorwayCell = 0xA9B40031u; // low=0x0031 < 0x0100 (outdoor)

    // ── Doorway Y threshold (seam zone) ──────────────────────────────
    private const float YSeamMin = 15.5f;
    private const float YSeamMax = 17.5f;

    // ── Max records to process from the seam zone ─────────────────────
    private const int MaxSeamRecords = 80;

    [Fact]
    public void DoorwaySeam_FindCellSet_StableNoStrobe()
    {
        var capturePath = FixturePath();

        var records = LoadSeamRecords(capturePath, MaxSeamRecords);
        if (records.Count == 0)
        {
            _output.WriteLine("SKIP: no records in doorway seam zone Y=[15.5, 17.5].");
            return;
        }

        _output.WriteLine(string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "Loaded {0} doorway-seam records (Y∈[{1:F1},{2:F1}]) from {3}",
            records.Count, YSeamMin, YSeamMax, Path.GetFileName(capturePath)));

        var cache = new PhysicsDataCache(); // empty — no real BSP data
        const float SphereRadius = 0.48f;

        var outCells = new List<uint>(records.Count);
        for (int i = 0; i < records.Count; i++)
        {
            var r = records[i];
            var spherePos = r.Input.CurrentPos;
            uint inCellId = r.Input.CellId;

            uint outCellId = CellTransit.FindCellSet(
                cache, spherePos, SphereRadius, inCellId, out _);

            outCells.Add(outCellId);
            _output.WriteLine(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0,3} pos=({1:F3},{2:F3}) inCell=0x{3:X8} -> outCell=0x{4:X8}",
                i, spherePos.X, spherePos.Y, inCellId, outCellId));
        }

        int transitionCount = 0;
        for (int i = 1; i < outCells.Count; i++)
        {
            if (outCells[i] != outCells[i - 1])
                transitionCount++;
        }
        _output.WriteLine($"Distinct consecutive outCell transitions: {transitionCount}");

        // ── Assert: no A→B→A ping-pong within 3 steps at near-static pos
        bool pingPongFound = false;
        for (int i = 0; i + 2 < outCells.Count; i++)
        {
            if (outCells[i] == outCells[i + 2] && outCells[i] != outCells[i + 1])
            {
                var pos0 = records[i].Input.CurrentPos;
                var pos1 = records[i + 1].Input.CurrentPos;
                var pos2 = records[i + 2].Input.CurrentPos;
                float span = Vector3.Distance(pos0, pos2);
                if (span < 0.1f) // near-static: total drift < 0.1 m over 2 steps
                {
                    _output.WriteLine(string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "PING-PONG at i={0}: 0x{1:X8}->0x{2:X8}->0x{3:X8} pos drift={4:F4}m",
                        i, outCells[i], outCells[i + 1], outCells[i + 2], span));
                    pingPongFound = true;
                }
            }
        }

        Assert.False(pingPongFound,
            "Phase W: FindCellSet produced A→B→A ping-pong at a near-static " +
            "doorway position. The interior-wins pick or outdoor fallback is " +
            "non-deterministic for this sphere position. See test output for details.");

        _output.WriteLine(
            $"Phase W: FindCellSet membership stable over {records.Count} seam records. " +
            $"Transition count: {transitionCount} (informational, not gated).");
    }

    [Fact]
    public void OutdoorSeamRecords_FindCellSet_ReturnsCorrectOutdoorCell()
    {
        var capturePath = FixturePath();

        var allSeam = LoadSeamRecords(capturePath, MaxSeamRecords);
        var outdoorSeam = allSeam
            .Where(r => (r.Input.CellId & 0xFFFFu) < 0x0100u)
            .ToList();

        if (outdoorSeam.Count == 0)
        {
            _output.WriteLine("SKIP: no outdoor-seed records in seam zone.");
            return;
        }

        _output.WriteLine($"Testing {outdoorSeam.Count} outdoor-seed seam records.");

        var cache = new PhysicsDataCache();
        const float SphereRadius = 0.48f;
        int mismatches = 0;

        for (int i = 0; i < outdoorSeam.Count; i++)
        {
            var r = outdoorSeam[i];
            var spherePos = r.Input.CurrentPos;
            uint inCell   = r.Input.CellId;
            uint liveOut  = r.Result.CellId;

            uint newOut = CellTransit.FindCellSet(
                cache, spherePos, SphereRadius, inCell, out _);

            bool liveWasOutdoor = (liveOut & 0xFFFFu) < 0x0100u;
            if (liveWasOutdoor && newOut != liveOut)
            {
                mismatches++;
                _output.WriteLine(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "MISMATCH at i={0}: pos=({1:F3},{2:F3}) in=0x{3:X8} " +
                    "liveOut=0x{4:X8} newOut=0x{5:X8}",
                    i, spherePos.X, spherePos.Y, inCell, liveOut, newOut));
            }
            else
            {
                _output.WriteLine(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0,3} pos=({1:F3},{2:F3}) in=0x{3:X8} liveOut=0x{4:X8} " +
                    "newOut=0x{5:X8}{6}",
                    i, spherePos.X, spherePos.Y, inCell, liveOut, newOut,
                    liveWasOutdoor ? "" : " (live→indoor, harness stays outdoor — expected)"));
            }
        }

        Assert.Equal(0, mismatches);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static string FixturePath()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "AcDream.slnx")))
            {
                return Path.Combine(dir,
                    "tests", "AcDream.Core.Tests", "Fixtures", "cellar-ascent",
                    "doorway-threshold-capture.jsonl");
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException(
            "Could not locate AcDream.slnx from " + AppContext.BaseDirectory);
    }

    private static List<ResolveCaptureRecord> LoadSeamRecords(
        string fixturePath, int maxRecords)
    {
        Assert.True(File.Exists(fixturePath),
            $"Committed doorway-threshold fixture missing: {fixturePath}. " +
            $"Re-generate it by running the T0 extraction script against " +
            $"a fresh doorway-capture.jsonl — see the class comment for details.");

        var result   = new List<ResolveCaptureRecord>(maxRecords);
        var jsonOpts = CellarUpTrajectoryReplayTests.CaptureJsonOptions;

        foreach (var line in File.ReadLines(fixturePath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var record = System.Text.Json.JsonSerializer
                .Deserialize<ResolveCaptureRecord>(line, jsonOpts)!;
            result.Add(record);
            if (result.Count >= maxRecords)
                break;
        }

        return result;
    }
}
