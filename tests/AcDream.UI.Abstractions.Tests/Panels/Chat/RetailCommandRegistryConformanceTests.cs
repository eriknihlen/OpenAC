using AcDream.UI.Abstractions;
using AcDream.UI.Abstractions.Panels.Chat;

namespace AcDream.UI.Abstractions.Tests.Panels.Chat;

public sealed class RetailCommandRegistryConformanceTests
{
    private enum Status { Implemented, HelpOnly, ServerPassthrough }

    private sealed record Entry(Status Status, params string[] Verbs);

    private static readonly Entry[] Registry =
    [
        // §2.1 Help / group nodes (9)
        new(Status.Implemented, "help", "?"),
        new(Status.HelpOnly, "commands"),
        new(Status.HelpOnly, "allegiances"),
        new(Status.HelpOnly, "channels"),
        new(Status.HelpOnly, "chatting"),
        new(Status.HelpOnly, "death"),
        new(Status.HelpOnly, "status"),
        new(Status.HelpOnly, "text"),

        new(Status.Implemented, "say", "s"),
        new(Status.Implemented, "tell", "t", "send", "whisper", "w"),
        new(Status.Implemented, "reply", "r", "rp"),
        new(Status.HelpOnly, "mr"),
        new(Status.HelpOnly, "pr"),
        new(Status.Implemented, "retell", "rt"),
        new(Status.Implemented, "chat"),
        new(Status.Implemented, "notell"),
        new(Status.Implemented, "join"),
        new(Status.Implemented, "leave"),
        new(Status.Implemented, "index"),
        new(Status.Implemented, "clist"),
        new(Status.Implemented, "on"),
        new(Status.Implemented, "off"),
        new(Status.Implemented, "title"),
        new(Status.Implemented, "log"),
        new(Status.Implemented, "clear"),
        new(Status.Implemented, "filter"),
        new(Status.Implemented, "unfilter"),
        new(Status.Implemented, "messagetypes", "message_types", "msgtypes", "msg_types"),
        new(Status.ServerPassthrough, "loadfile"), // deliberate — doc §3 "explicitly do NOT implement"

        new(Status.Implemented, "a", "ab"),
        new(Status.Implemented, "co-vassals", "covassals", "covassal", "c"),
        new(Status.Implemented, "monarch", "m"),
        new(Status.Implemented, "patron", "p"),
        new(Status.Implemented, "vassals", "vassal", "v"),
        new(Status.Implemented, "fellowship", "fellows", "fellow", "f", "group", "g", "party"),

        // §2.3 fallback: 22 GetChannelID tags with NO registered verb (22)
        new(Status.Implemented, "av", "av1", "advocate", "advocate1"),
        new(Status.Implemented, "av2", "advocate2"),
        new(Status.Implemented, "av3", "advocate3"),
        new(Status.Implemented, "abuse"),
        new(Status.Implemented, "ad", "admin"),
        new(Status.Implemented, "au", "audit"),
        new(Status.Implemented, "sent", "sentinel"),
        new(Status.Implemented, "celestialhand", "celhan"),
        new(Status.Implemented, "eldrytchweb", "eldweb"),
        new(Status.Implemented, "radiantblood", "radblo"),
        new(Status.Implemented, "ol"),

        new(Status.Implemented, "guild", "gu"),
        new(Status.Implemented, "general", "cg"),
        new(Status.Implemented, "trade", "ct"),
        new(Status.Implemented, "lfg", "clfg"),
        new(Status.Implemented, "roleplay", "crp"),
        new(Status.Implemented, "society", "soc"),
        new(Status.Implemented, "olthoi", "o"),

        new(Status.Implemented, "allegiance", "all"),
        new(Status.Implemented, "alh", "ah"),
        new(Status.Implemented, "motd"),
        new(Status.Implemented, "speaker"),

        // §2.5b Housing (7)
        new(Status.Implemented, "house", "hou"),
        new(Status.Implemented, "hor", "hr"),
        new(Status.Implemented, "hom", "hoa"),
        new(Status.Implemented, "hslist"),

        // §2.6 Death / recall / PK (17)
        new(Status.Implemented, "lifestone", "lif", "ls"),
        new(Status.Implemented, "marketplace", "mar", "mp"),
        new(Status.Implemented, "pkarena", "pka"),
        new(Status.Implemented, "pklarena", "pla"),
        new(Status.Implemented, "pklite", "pkl"),
        new(Status.Implemented, "die"),
        new(Status.Implemented, "corpse", "cor"),
        new(Status.Implemented, "consent"),
        new(Status.Implemented, "permit"),

        // §2.7 Status / display (8)
        new(Status.Implemented, "age"),
        new(Status.Implemented, "birth"),
        new(Status.Implemented, "day"),
        new(Status.Implemented, "endurance"),
        new(Status.Implemented, "framerate"),
        new(Status.Implemented, "loc"),
        new(Status.Implemented, "version"),
        new(Status.Implemented, "render"),

        new(Status.Implemented, "saveui"),
        new(Status.Implemented, "loadui"),
        new(Status.Implemented, "saveautoui"),
        new(Status.Implemented, "loadautoui"),
        new(Status.Implemented, "lockui"),
        new(Status.Implemented, "emote", "e", "em", "me"),
        new(Status.Implemented, "emotes"),
        new(Status.Implemented, "afk"),
        new(Status.Implemented, "friends"),
        new(Status.Implemented, "friends_add"),
        new(Status.Implemented, "friends_remove"),
        new(Status.Implemented, "squelch"),
        new(Status.Implemented, "unsquelch"),
        new(Status.Implemented, "fillcomps"),
    ];

    private static readonly HashSet<string> CatalogVerbs =
        new(RetailClientCommandCatalog.KnownVerbs, StringComparer.OrdinalIgnoreCase);

    private static bool IsExecutable(string verb)
    {
        if (verb is "help" or "?")
            return true;
        if (CatalogVerbs.Contains(verb))
            return true;
        if (ChatInputParser.IsKnownVerb("/" + verb))
            return true;
        if (RetailChannelTagTable.IsUnregisteredFallbackTag(verb))
            return true;
        return false;
    }

    public static IEnumerable<object[]> AllVerbsWithStatus() =>
        Registry.SelectMany(entry => entry.Verbs.Select(verb => new object[] { verb, entry.Status }));

    [Theory]
    [MemberData(nameof(AllVerbsWithStatus))]
    public void EveryRegistryVerb_HasTheCorrectOwnershipStatus(string verb, object statusObj)
    {
        var status = (Status)statusObj;
        bool executable = IsExecutable(verb);
        if (status == Status.Implemented)
        {
            Assert.True(executable,
                $"'{verb}' is marked Implemented in the registry but no production surface " +
                "(RetailClientCommandCatalog, ChatInputParser, or RetailChannelTagTable) recognizes it.");
        }
        else
        {
            Assert.False(executable,
                $"'{verb}' is marked {status} (must fall through to server passthrough) but a " +
                "production surface claims to execute it locally — that's a real behavior change " +
                "the registry doesn't know about yet.");
        }
    }

    [Fact]
    public void Registry_EnumeratesExactly152Verbs()
    {
        int total = Registry.Sum(entry => entry.Verbs.Length);
        Assert.Equal(152, total);
    }

    [Fact]
    public void Registry_StatusCountsMatchTheAuditedTotals()
    {
        Assert.Equal(9, Registry.Where(e => e.Status == Status.HelpOnly).Sum(e => e.Verbs.Length));
        Assert.Equal(1, Registry.Where(e => e.Status == Status.ServerPassthrough).Sum(e => e.Verbs.Length));
        Assert.Equal(142, Registry.Where(e => e.Status == Status.Implemented).Sum(e => e.Verbs.Length));
    }

    [Fact]
    public void NoDuplicateVerbsAcrossEntries()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Entry entry in Registry)
        {
            foreach (string verb in entry.Verbs)
            {
                Assert.True(seen.Add(verb), $"'{verb}' appears more than once in the registry.");
            }
        }
    }


    [Fact]
    public void RetailClientCommandCatalog_HasNoVerbsOutsideTheRegistry()
    {
        var registryVerbs = new HashSet<string>(
            Registry.SelectMany(e => e.Verbs), StringComparer.OrdinalIgnoreCase);

        foreach (string verb in RetailClientCommandCatalog.KnownVerbs)
        {
            Assert.True(registryVerbs.Contains(verb),
                $"RetailClientCommandCatalog recognizes '{verb}', which is not in the retail " +
                "command-registry doc — either it's a genuine retail verb missing from this " +
                "test's registry, or it's an invented alias that must be deleted.");
        }
    }

    [Fact]
    public void ChatInputParser_HasNoVerbsOutsideTheRegistry()
    {
        var registryVerbs = new HashSet<string>(
            Registry.SelectMany(e => e.Verbs), StringComparer.OrdinalIgnoreCase);

        foreach (string verbWithSlash in ChatInputParser.KnownVerbs)
        {
            string verb = verbWithSlash.TrimStart('/');
            Assert.True(registryVerbs.Contains(verb),
                $"ChatInputParser recognizes '{verbWithSlash}', which is not in the retail " +
                "command-registry doc — either it's a genuine retail verb missing from this " +
                "test's registry, or it's an invented alias that must be deleted.");
        }
    }


    [Fact]
    public void GVerb_BindsToFellowship_NotGeneral()
    {
        ChatInputParser.ParsedInput? parsed = ChatInputParser.Parse(
            "/g hi gang", ChatChannelKind.Say, lastTellSender: null);

        Assert.NotNull(parsed);
        Assert.Equal(ChatChannelKind.Fellowship, parsed!.Value.Channel);
    }

    [Fact]
    public void RpVerb_BindsToReply_NotRoleplay()
    {
        ChatInputParser.ParsedInput? parsed = ChatInputParser.Parse(
            "/rp hello back", ChatChannelKind.Say, lastTellSender: "Aunt Agatha");

        Assert.NotNull(parsed);
        Assert.Equal(ChatChannelKind.Tell, parsed!.Value.Channel);
        Assert.Equal("Aunt Agatha", parsed!.Value.TargetName);
    }
}
