using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The fellowship as a plugin reads it, on both clients: whether it shares
/// experience, whether the split is even, and each member's level. A status
/// line that says who is in the fellowship and on what terms is built from
/// exactly these, so a client that leaves one out prints the wrong terms.
///
/// Regression guard: the fellowship owner and the plugin surface that reads
/// it are shared runtime code, so both arms answer from the same place; the
/// scenario fails if either client stops routing the server's roster to it
/// or stops handing a plugin that surface. Every answer is asserted outright
/// as well as recorded: two clients that reported no fellowship would write
/// identical transcripts.
///
/// Mutation check (2026-09-25), run: making the shared surface report no
/// level turned the level assertions red; reporting the even split as the
/// sharing flag, and the sharing flag as the even split, each turned the
/// terms assertions red. Restoring each turned it green.
/// </summary>
public sealed class FellowshipParityTests
{
    private const string FellowshipName = "Parity Fellows";
    private const uint OwnLevel = 30u;
    private const uint FellowLevel = 42u;

    [Fact]
    public void AFellowshipsTermsAndLevelsReachAPluginOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);
            ParityWorld.StageAnotherPlayer(arm.Runtime);
            IFellowshipAutomation fellowship = arm.Host.Automation.Fellowship;

            transcript.Step("no fellowship yet");
            transcript.Record("sharing", fellowship.SharesExperience);
            Assert.False(fellowship.IsInFellowship);
            Assert.False(fellowship.SharesExperience);
            Assert.False(fellowship.SplitsExperienceEvenly);

            transcript.Step("the server states a fellowship sharing unevenly");
            arm.Server.FellowshipFullUpdate(
                FellowshipName,
                ParityWorld.Player,
                shareExperience: true,
                evenSplit: false,
                new ParityServer.Fellow(ParityWorld.Player, "Parity", OwnLevel),
                new ParityServer.Fellow(
                    ParityWorld.OtherPlayer, "Another", FellowLevel));
            arm.Advance();
            transcript.Record("inFellowship", fellowship.IsInFellowship);
            transcript.Record("sharing", fellowship.SharesExperience);
            transcript.Record("even", fellowship.SplitsExperienceEvenly);
            Assert.True(fellowship.IsInFellowship);
            Assert.True(fellowship.SharesExperience);
            Assert.False(fellowship.SplitsExperienceEvenly);

            IReadOnlyList<PluginFellowMember> roster = fellowship.CaptureRoster();
            transcript.Record(
                "levels",
                string.Join(
                    ",",
                    roster
                        .OrderBy(static member => member.ObjectId)
                        .Select(static member =>
                            $"{member.ObjectId:X8}:{member.Level}")));
            Assert.Equal(
                OwnLevel,
                Assert.Single(
                    roster,
                    static member => member.ObjectId == ParityWorld.Player).Level);
            Assert.Equal(
                FellowLevel,
                Assert.Single(
                    roster,
                    static member => member.ObjectId == ParityWorld.OtherPlayer)
                    .Level);

            transcript.Step("it is restated with an even split, not sharing");
            arm.Server.FellowshipFullUpdate(
                FellowshipName,
                ParityWorld.Player,
                shareExperience: false,
                evenSplit: true,
                new ParityServer.Fellow(ParityWorld.Player, "Parity", OwnLevel),
                new ParityServer.Fellow(
                    ParityWorld.OtherPlayer, "Another", FellowLevel));
            arm.Advance();
            transcript.Record("sharing", fellowship.SharesExperience);
            transcript.Record("even", fellowship.SplitsExperienceEvenly);
            Assert.False(fellowship.SharesExperience);
            Assert.True(fellowship.SplitsExperienceEvenly);
        });
}
