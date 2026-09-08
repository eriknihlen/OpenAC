using System.Net;
using AcDream.Core.Net.Messages;

namespace AcDream.Core.Net.Tests;

public sealed class WorldSessionAdministrationCommandTests
{
    [Fact]
    public void EveryAdministrationWrapper_UsesSharedSequenceAndExactBuilder()
    {
        using var session = new WorldSession(
            new IPEndPoint(IPAddress.Loopback, 65000));
        var actual = new List<byte[]>();
        session.GameActionCapture = actual.Add;

        session.SendBreakAllegianceBoot("Bob", true);
        session.SendAllegianceChatBoot("Bob", "Reason");
        session.SendAllegianceChatGag("Bob", false);
        session.SendAddAllegianceBan("Bob");
        session.SendRemoveAllegianceBan("Bob");
        session.SendListAllegianceBans();
        session.SendSetAllegianceOfficer("Bob", 2u);
        session.SendRemoveAllegianceOfficer("Bob");
        session.SendListAllegianceOfficers();
        session.SendClearAllegianceOfficers();
        session.SendSetAllegianceOfficerTitle(2u, "Regent");
        session.SendListAllegianceOfficerTitles();
        session.SendClearAllegianceOfficerTitles();
        session.SendQueryAllegianceName();
        session.SendSetAllegianceName("Guild");
        session.SendClearAllegianceName();
        session.SendAllegianceLockAction(3u);
        session.SendSetAllegianceApprovedVassal("Bob");
        session.SendAllegianceHouseAction(4u);
        session.SendQueryMotd();
        session.SendSetMotd("Welcome");
        session.SendClearMotd();
        session.SendSetOpenHouseStatus(true);
        session.SendAddPermanentGuest("Bob");
        session.SendRemovePermanentGuest("Bob");
        session.SendRemoveAllPermanentGuests();
        session.SendChangeStoragePermission("Bob", true);
        session.SendAddAllStoragePermission();
        session.SendRemoveAllStoragePermission();
        session.SendRequestFullGuestList();
        session.SendBootSpecificHouseGuest("Bob");
        session.SendBootEveryone();
        session.SendSetHooksVisibility(false);
        session.SendModifyAllegianceGuestPermission(true);
        session.SendModifyAllegianceStoragePermission(false);

        byte[][] expected =
        [
            ClientCommandRequests.BuildBreakAllegianceBoot(1u, "Bob", true),
            ClientCommandRequests.BuildAllegianceChatBoot(2u, "Bob", "Reason"),
            ClientCommandRequests.BuildAllegianceChatGag(3u, "Bob", false),
            ClientCommandRequests.BuildAddAllegianceBan(4u, "Bob"),
            ClientCommandRequests.BuildRemoveAllegianceBan(5u, "Bob"),
            ClientCommandRequests.BuildListAllegianceBans(6u),
            ClientCommandRequests.BuildSetAllegianceOfficer(7u, "Bob", 2u),
            ClientCommandRequests.BuildRemoveAllegianceOfficer(8u, "Bob"),
            ClientCommandRequests.BuildListAllegianceOfficers(9u),
            ClientCommandRequests.BuildClearAllegianceOfficers(10u),
            ClientCommandRequests.BuildSetAllegianceOfficerTitle(11u, 2u, "Regent"),
            ClientCommandRequests.BuildListAllegianceOfficerTitles(12u),
            ClientCommandRequests.BuildClearAllegianceOfficerTitles(13u),
            ClientCommandRequests.BuildQueryAllegianceName(14u),
            ClientCommandRequests.BuildSetAllegianceName(15u, "Guild"),
            ClientCommandRequests.BuildClearAllegianceName(16u),
            ClientCommandRequests.BuildAllegianceLockAction(17u, 3u),
            ClientCommandRequests.BuildSetAllegianceApprovedVassal(18u, "Bob"),
            ClientCommandRequests.BuildAllegianceHouseAction(19u, 4u),
            ClientCommandRequests.BuildQueryMotd(20u),
            ClientCommandRequests.BuildSetMotd(21u, "Welcome"),
            ClientCommandRequests.BuildClearMotd(22u),
            ClientCommandRequests.BuildSetOpenHouseStatus(23u, true),
            ClientCommandRequests.BuildAddPermanentGuest(24u, "Bob"),
            ClientCommandRequests.BuildRemovePermanentGuest(25u, "Bob"),
            ClientCommandRequests.BuildRemoveAllPermanentGuests(26u),
            ClientCommandRequests.BuildChangeStoragePermission(27u, "Bob", true),
            ClientCommandRequests.BuildAddAllStoragePermission(28u),
            ClientCommandRequests.BuildRemoveAllStoragePermission(29u),
            ClientCommandRequests.BuildRequestFullGuestList(30u),
            ClientCommandRequests.BuildBootSpecificHouseGuest(31u, "Bob"),
            ClientCommandRequests.BuildBootEveryone(32u),
            ClientCommandRequests.BuildSetHooksVisibility(33u, false),
            ClientCommandRequests.BuildModifyAllegianceGuestPermission(34u, true),
            ClientCommandRequests.BuildModifyAllegianceStoragePermission(35u, false),
        ];

        Assert.Equal(expected.Length, actual.Count);
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], actual[i]);
    }
}
