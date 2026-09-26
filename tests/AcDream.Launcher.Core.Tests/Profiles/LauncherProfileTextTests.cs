using System.Text.Json;
using AcDream.Launcher.Core.Profiles;

namespace AcDream.Launcher.Core.Tests.Profiles;

public sealed class LauncherProfileTextTests
{
    private static LauncherProfileDocument Sample() => new()
    {
        Servers =
        [
            new()
            {
                Name = "Coldeve", Host = "play.coldeve.ac", Port = 9000,
                Accounts =
                [
                    new()
                    {
                        Account = "notan3", Password = "secret", Profiles = ["Bots"],
                        Plugins = ["acdream.mosstank"], LoginCommands = ["/vt start"],
                        SelectedCharacter = "Festivus", SelectedLaunchMode = LaunchMode.Headless,
                        Characters = [new() { Name = "Festivus", Id = "0x5005FBB5", LaunchMode = LaunchMode.Gui, Plugins = ["x.y"] }],
                    },
                    new() { Account = "notan", Password = "other" },
                ],
            },
            new()
            {
                Name = "sawato", Host = "localhost", Port = 9000,
                Accounts = [new() { Account = "testaccount", Password = "testpassword", LoginCommands = ["/vt nav load bore_circuit1"] }],
            },
        ],
    };

    private static string Json(LauncherProfileDocument document) => JsonSerializer.Serialize(document);

    // --- Accounts -------------------------------------------------------------

    [Fact]
    public void AccountsReadAsServerSectionsWithOneAccountPerLine()
    {
        string text = LauncherProfileText.Read(Sample(), LauncherTextEditorKind.Accounts);

        Assert.Equal(
            string.Join(Environment.NewLine,
                "#Coldeve",
                "Name=notan3,Password=secret,Profiles=Bots",
                "Name=notan,Password=other",
                "",
                "#sawato",
                "Name=testaccount,Password=testpassword",
                ""),
            text);
    }

    [Fact]
    public void AccountsRoundTripExactly()
    {
        LauncherProfileDocument document = Sample();
        document.Servers[0].Accounts[0].Password = " a,b \"c\" \\d ";
        document.Servers[0].Accounts[0].Profiles = ["Main", "Bots and Mules"];
        string before = Json(document);

        LauncherProfileText.Apply(document, LauncherTextEditorKind.Accounts,
            LauncherProfileText.Read(document, LauncherTextEditorKind.Accounts));

        Assert.Equal(before, Json(document));
    }

    [Fact]
    public void AccountsKeepEverythingElseAboutAnAccountThatStays()
    {
        LauncherProfileDocument document = Sample();

        LauncherProfileText.Apply(document, LauncherTextEditorKind.Accounts,
            "#Coldeve\nName=notan3,Password=changed,Profiles=Main;Bots\nName=notan4,Password=new\n");

        AccountProfile kept = document.Servers[0].Accounts[0];
        Assert.Equal("changed", kept.Password);
        Assert.Equal(["Main", "Bots"], kept.Profiles);
        Assert.Equal(["acdream.mosstank"], kept.Plugins);
        Assert.Equal(["/vt start"], kept.LoginCommands);
        Assert.Equal("Festivus", Assert.Single(kept.Characters).Name);
        Assert.Equal(["notan3", "notan4"], document.Servers[0].Accounts.Select(account => account.Account));
        Assert.Equal("testaccount", Assert.Single(document.Servers[1].Accounts).Account);
    }

    [Fact]
    public void AServerMatchesIgnoringCaseAndItsEmptySectionRemovesItsAccounts()
    {
        LauncherProfileDocument document = Sample();

        LauncherProfileText.Apply(document, LauncherTextEditorKind.Accounts, "#COLDEVE\n");

        Assert.Empty(document.Servers[0].Accounts);
        Assert.Single(document.Servers[1].Accounts);
    }

    [Fact]
    public void ARemovedOrRenamedAccountIsDescribedWithWhatIsSavedOnIt()
    {
        LauncherProfileDocument document = Sample();
        string before = Json(document);

        IReadOnlyList<string> removals = LauncherProfileText.DescribeAccountRemovals(
            document, "#Coldeve\nName=notan3-renamed,Password=secret\n#sawato\n");

        Assert.Equal(
            ["notan3 on Coldeve (1 character, 1 plugin, 1 logon command)", "testaccount on sawato (1 logon command)"],
            removals);
        Assert.Equal(before, Json(document));
        Assert.Empty(LauncherProfileText.DescribeAccountRemovals(document, "#Nowhere\n"));
    }

    [Fact]
    public void AnAccountWithoutPasswordHasAnEmptyOne()
    {
        LauncherProfileDocument document = Sample();

        LauncherProfileText.Apply(document, LauncherTextEditorKind.Accounts, "#sawato\r\nName=fresh\r\n");

        AccountProfile account = Assert.Single(document.Servers[1].Accounts);
        Assert.Equal("fresh", account.Account);
        Assert.Equal("", account.Password);
    }

    [Theory]
    [InlineData("Name=a,Password=b", "Line 1: put a #Server line above the accounts on that server.")]
    [InlineData("#Nowhere\nName=a,Password=b", "Line 1: there is no server named 'Nowhere'. Add it with Add server first.")]
    [InlineData("#Coldeve\nName=a\nName=a", "Line 3: account 'a' appears more than once on this server.")]
    [InlineData("#Coldeve\nPassword=b", "Line 2: each account needs Name=.")]
    [InlineData("#Coldeve\nName=a,Colour=red", "Line 2: 'Colour' is not Name, Password or Profiles, or appears twice.")]
    [InlineData("#Coldeve\nName=a,Password=\"open", "Line 2: A quoted value is missing its closing double quote.")]
    [InlineData("#Coldeve\n\n#coldeve", "Line 3: server 'Coldeve' appears more than once.")]
    [InlineData("#Coldeve\nName=a,Profiles=Bots;bots", "Line 2: profile 'bots' is listed twice.")]
    [InlineData("#Coldeve\njust words", "Line 2: expected Name=…,Password=… (a server line starts with #).")]
    public void AccountErrorsNameTheirLineAndChangeNothing(string text, string error)
    {
        LauncherProfileDocument document = Sample();
        string before = Json(document);

        var exception = Assert.Throws<LauncherProfileException>(() =>
            LauncherProfileText.Apply(document, LauncherTextEditorKind.Accounts, text));

        Assert.Contains(error, exception.Message.Split(Environment.NewLine));
        Assert.Equal(before, Json(document));
    }

    [Fact]
    public void EveryAccountErrorIsReportedAtOnceWithoutAPassword()
    {
        LauncherProfileDocument document = Sample();

        var exception = Assert.Throws<LauncherProfileException>(() =>
            LauncherProfileText.Apply(document, LauncherTextEditorKind.Accounts,
                "#Coldeve\nName=a,Password=hunter2,Bad=1\n#Nowhere\nName=b,Password=hunter3"));

        Assert.Equal(
            ["Line 2: 'Bad' is not Name, Password or Profiles, or appears twice.",
             "Line 3: there is no server named 'Nowhere'. Add it with Add server first."],
            exception.Message.Split(Environment.NewLine));
        Assert.DoesNotContain("hunter", exception.Message);
    }

    // --- Logon commands -------------------------------------------------------

    [Fact]
    public void LogonCommandsReadEveryServerAndAccountWithTheirCommands()
    {
        string text = LauncherProfileText.Read(Sample(), LauncherTextEditorKind.LogonCommands);

        Assert.Equal(
            string.Join(Environment.NewLine,
                "#Coldeve",
                "##notan3",
                "/vt start",
                "##notan",
                "",
                "#sawato",
                "##testaccount",
                "/vt nav load bore_circuit1",
                ""),
            text);
    }

    [Fact]
    public void LogonCommandsRoundTripExactly()
    {
        LauncherProfileDocument document = Sample();
        string before = Json(document);

        LauncherProfileText.Apply(document, LauncherTextEditorKind.LogonCommands,
            LauncherProfileText.Read(document, LauncherTextEditorKind.LogonCommands));

        Assert.Equal(before, Json(document));
    }

    [Fact]
    public void LogonCommandsReplaceEachListedAccountsCommandsInOrder()
    {
        LauncherProfileDocument document = Sample();

        LauncherProfileText.Apply(document, LauncherTextEditorKind.LogonCommands,
            "#coldeve\n##notan\n/mm ws enable\n  /example set navNumber 1  \n\n/vt start\n");

        Assert.Equal(["/mm ws enable", "/example set navNumber 1", "/vt start"],
            document.Servers[0].Accounts[1].LoginCommands);
        // An account left out of a listed server's section runs nothing; a server left out keeps its commands.
        Assert.Empty(document.Servers[0].Accounts[0].LoginCommands);
        Assert.Equal(["/vt nav load bore_circuit1"], document.Servers[1].Accounts[0].LoginCommands);
        Assert.Equal(["acdream.mosstank"], document.Servers[0].Accounts[0].Plugins);
    }

    [Fact]
    public void ACommandStartingWithAHashOrBackslashIsWrittenWithABackslashAndRoundTrips()
    {
        LauncherProfileDocument document = Sample();
        document.Servers[1].Accounts[0].LoginCommands = ["#not a server", "##not an account", @"\starts with a backslash", "/plain"];
        string before = Json(document);

        string text = LauncherProfileText.Read(document, LauncherTextEditorKind.LogonCommands);
        LauncherProfileText.Apply(document, LauncherTextEditorKind.LogonCommands, text);

        Assert.Contains(string.Join(Environment.NewLine, @"\#not a server", @"\##not an account", @"\\starts with a backslash", "/plain"), text);
        Assert.Equal(before, Json(document));
    }

    [Theory]
    [InlineData("/vt start", "Line 1: a command needs an ##Account line above it.")]
    [InlineData("##notan", "Line 1: put a #Server line above ##Account.")]
    [InlineData("#Coldeve\n/vt start", "Line 2: a command needs an ##Account line above it.")]
    [InlineData("#Coldeve\n##nobody\n/vt start", "Line 2: server 'Coldeve' has no account 'nobody'. Add it in Edit accounts first.")]
    [InlineData("#Nowhere\n##notan\n/vt start", "Line 1: there is no server named 'Nowhere'. Add it with Add server first.")]
    [InlineData("#Coldeve\n##notan\n##notan", "Line 3: account 'notan' appears more than once under 'Coldeve'.")]
    [InlineData("#Coldeve\n#Coldeve", "Line 2: server 'Coldeve' appears more than once.")]
    public void LogonCommandErrorsNameTheirLineAndChangeNothing(string text, string error)
    {
        LauncherProfileDocument document = Sample();
        string before = Json(document);

        var exception = Assert.Throws<LauncherProfileException>(() =>
            LauncherProfileText.Apply(document, LauncherTextEditorKind.LogonCommands, text));

        Assert.Contains(error, exception.Message.Split(Environment.NewLine));
        Assert.Equal(before, Json(document));
    }

    [Theory]
    [InlineData(LauncherTextEditorKind.Accounts)]
    [InlineData(LauncherTextEditorKind.LogonCommands)]
    public void NamesWithEdgeSpacesQuotesOrALeadingHashRoundTrip(LauncherTextEditorKind kind)
    {
        LauncherProfileDocument document = Sample();
        document.Servers[0].Name = " spaced \"server\" ";
        document.Servers[1].Name = "#hash";
        document.Servers[0].Accounts[0].Account = "#notan3";
        document.Servers[0].Accounts[1].Account = " a,b ";
        string before = Json(document);

        LauncherProfileText.Apply(document, kind, LauncherProfileText.Read(document, kind));

        Assert.Equal(before, Json(document));
    }

    // --- Servers --------------------------------------------------------------

    [Fact]
    public void ServerEndpointEditRetainsAccounts()
    {
        LauncherProfileDocument document = Sample();

        LauncherProfileText.Apply(document, LauncherTextEditorKind.Servers, "Coldeve | new.example | 9010");

        ServerProfile server = Assert.Single(document.Servers);
        Assert.Equal("new.example", server.Host);
        Assert.Equal("Festivus", server.Accounts[0].Characters[0].Name);
    }

    [Fact]
    public void QuotedServerSeparatorsRoundTrip()
    {
        LauncherProfileDocument document = Sample();
        document.Servers[0].Name = "Server, One | Test";

        LauncherProfileText.Apply(document, LauncherTextEditorKind.Servers,
            LauncherProfileText.Read(document, LauncherTextEditorKind.Servers));

        Assert.Equal("Server, One | Test", document.Servers[0].Name);
    }

    [Fact]
    public void FailedDiskWriteRollsBackTheWholeAccountEdit()
    {
        string directory = Path.Combine(Path.GetTempPath(), "launcher-text-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new LauncherProfileStore(directory);
            store.AddServer("One", "localhost", 9000);
            store.AddAccount("One", "Original", "original-password");
            var error = Record.Exception(() => store.ExecuteTransaction(() => LauncherProfileText.Apply(
                store.Document, LauncherTextEditorKind.Accounts, "#One\nName=New,Password=new-password")));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Equal("Original", store.Document.Servers[0].Accounts[0].Account);
            Assert.Equal("original-password", store.Document.Servers[0].Accounts[0].Password);
        }
        finally { Directory.Delete(directory); }
    }
}
