using System.Net;
using AcDream.Core.Net.Tests.Transport;

namespace AcDream.Core.Net.Tests;

[Collection(NetProcessStaticsCollection.Name)]
public sealed class NetProbeTests
{
    [Fact]
    public void FormatNetTickLine_CarriesTheN5TransportFields()
    {
        string line = WorldSession.FormatNetTickLine(
            windowSeconds: 2.0,
            processed: 10,
            queueDepth: 3,
            budgetBreaks: 1,
            maxGapMs: 17.4,
            sends: 8,
            acks: 2,
            resends: 4,
            naksOut: 6,
            naksIn: 8,
            rejsIn: 2,
            dupDrops: 10,
            parked: 12,
            reclaimed: 2,
            cacheDepth: 5,
            nakSetDepth: 7,
            WorldSession.State.InWorld);

        Assert.Equal(
            "[net-tick] in/s=5 q=3 budget-breaks=1 maxgap=17ms out/s=4"
            + " acks/s=1 resend/s=2 nak-out/s=3 nak-in/s=4 rej-in/s=1"
            + " dup-drop/s=5 parked/s=6 reclaim/s=1 cache=5 nakset=7"
            + " st=InWorld",
            line);
    }

    [Fact]
    public void ProbeOn_EmitsTheExtendedNetTickLine_OncePerSecond()
    {
        bool savedProbe = NetDiagnostics.ProbeNet;
        TextWriter savedOut = Console.Out;
        var captured = new LockedStringWriter();
        var fake = new FakeAceTransport();
        var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 9000),
            fake);
        try
        {
            NetDiagnostics.ProbeNet = true;
            Console.SetOut(captured);

            session.Connect(
                "testaccount", "testpassword", TimeSpan.FromSeconds(10));
            session.EnterWorld(0, TimeSpan.FromSeconds(10));

            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline
                   && !captured.Snapshot().Contains(
                       "[net-tick]", StringComparison.Ordinal))
            {
                session.Tick();
                Thread.Sleep(25);
            }
        }
        finally
        {
            session.Dispose();
            Console.SetOut(savedOut);
            NetDiagnostics.ProbeNet = savedProbe;
        }

        string output = captured.Snapshot();
        string tickLine = output
            .Split('\n')
            .First(l => l.Contains("[net-tick]", StringComparison.Ordinal));
        foreach (string field in new[]
        {
            "resend/s=", "nak-out/s=", "nak-in/s=", "rej-in/s=",
            "dup-drop/s=", "parked/s=", "reclaim/s=", "cache=", "nakset=",
        })
        {
            Assert.Contains(field, tickLine, StringComparison.Ordinal);
        }

        Assert.Contains("[net-final] resends=", output, StringComparison.Ordinal);
        Assert.Contains(" nak-out=", output, StringComparison.Ordinal);
        Assert.Contains(" nak-in=", output, StringComparison.Ordinal);
    }

    private sealed class LockedStringWriter : TextWriter
    {
        private readonly System.Text.StringBuilder _buffer = new();
        private readonly object _gate = new();

        public override System.Text.Encoding Encoding =>
            System.Text.Encoding.Unicode;

        public override void Write(char value)
        {
            lock (_gate)
            {
                _buffer.Append(value);
            }
        }

        public override void Write(string? value)
        {
            lock (_gate)
            {
                _buffer.Append(value);
            }
        }

        public string Snapshot()
        {
            lock (_gate)
            {
                return _buffer.ToString();
            }
        }
    }
}
