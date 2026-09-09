using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class MetafSerializerTests
{
    private static readonly string FixturesRoot = Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "vtank");

    private static string[] AllAfFixtures() => Directory.GetFiles(
        Path.Combine(FixturesRoot, "af"), "*.af");

    private static string[] NavOnlyAfFixtures() => Directory.GetFiles(
        Path.Combine(FixturesRoot, "af"), "nav_*.af");

    private static string[] MetaAfFixtures() => Directory.GetFiles(
        Path.Combine(FixturesRoot, "af"), "*.af")
        .Where(static path => !Path.GetFileName(path).StartsWith(
            "nav_", StringComparison.Ordinal))
        .ToArray();

    public static TheoryData<string> AllAfFixtureData()
    {
        var data = new TheoryData<string>();
        foreach (string path in AllAfFixtures())
            data.Add(path);
        return data;
    }

    public static TheoryData<string> MetaAfFixtureData()
    {
        var data = new TheoryData<string>();
        foreach (string path in MetaAfFixtures())
            data.Add(path);
        return data;
    }

    public static TheoryData<string> NavOnlyAfFixtureData()
    {
        var data = new TheoryData<string>();
        foreach (string path in NavOnlyAfFixtures())
            data.Add(path);
        return data;
    }

    // Proof (1): every .af under the fixture set parses without error.
    [Theory]
    [MemberData(nameof(MetaAfFixtureData))]
    public void EveryMetaAfFixtureParses(string path)
    {
        string text = File.ReadAllText(path);
        Assert.True(
            MetafSerializer.TryLoadMeta(text, NoOpSpellCatalog.Instance, out MetaProfile profile, out string error),
            $"{Path.GetFileName(path)}: {error}");
        Assert.NotEmpty(profile.Rules);
    }

    [Theory]
    [MemberData(nameof(NavOnlyAfFixtureData))]
    public void EveryNavOnlyAfFixtureParses(string path)
    {
        string text = File.ReadAllText(path);
        var target = new NavigationSettings();
        Assert.True(
            MetafSerializer.TryLoadNav(text, target, NoOpSpellCatalog.Instance, out string error),
            $"{Path.GetFileName(path)}: {error}");
    }

    [Theory]
    [MemberData(nameof(NavOnlyAfFixtureData))]
    public void EveryNavOnlyAfFixtureIsRefusedByTryLoadMeta(string path)
    {
        string text = File.ReadAllText(path);
        bool loaded = MetafSerializer.TryLoadMeta(
            text, NoOpSpellCatalog.Instance, out MetaProfile profile, out string error);

        Assert.False(loaded, $"{Path.GetFileName(path)} should not parse as a Meta profile.");
        Assert.Empty(profile.Rules);
        Assert.Contains("navs/", error, StringComparison.Ordinal);
    }

    [Fact]
    public void MetaOnlyContentIsRefusedByTryLoadNavWithMetasFolderNotice()
    {
        string text = File.ReadAllText(Path.Combine(FixturesRoot, "af", "bella.af"));
        var target = new NavigationSettings();

        bool loaded = MetafSerializer.TryLoadNav(
            text, target, NoOpSpellCatalog.Instance, out string error);

        Assert.False(loaded);
        Assert.Contains("metas/", error, StringComparison.Ordinal);
    }

    // Proof (2): parse -> write -> parse is identical (model equality; the
    // writer's own byte-for-byte shape is proof (4), below).
    [Theory]
    [MemberData(nameof(MetaAfFixtureData))]
    public void MetaParseWriteParseIsIdentical(string path)
    {
        string text = File.ReadAllText(path);
        Assert.True(MetafSerializer.TryLoadMeta(
            text, NoOpSpellCatalog.Instance, out MetaProfile first, out string error1), error1);
        string rewritten = MetafSerializer.SaveMeta(first);
        Assert.True(MetafSerializer.TryLoadMeta(
            rewritten, NoOpSpellCatalog.Instance, out MetaProfile second, out string error2), error2);
        AssertProfilesEqual(first, second);
    }

    [Theory]
    [MemberData(nameof(NavOnlyAfFixtureData))]
    public void NavParseWriteParseIsIdentical(string path)
    {
        string text = File.ReadAllText(path);
        var first = new NavigationSettings();
        Assert.True(MetafSerializer.TryLoadNav(
            text, first, NoOpSpellCatalog.Instance, out string error1), error1);
        string rewritten = MetafSerializer.SaveNav(first);
        var second = new NavigationSettings();
        Assert.True(MetafSerializer.TryLoadNav(
            rewritten, second, NoOpSpellCatalog.Instance, out string error2), error2);
        AssertNavigationEqual(first, second);
    }

    public static TheoryData<string, string> MetMatchingAfPairs()
    {
        var data = new TheoryData<string, string>();
        string metDir = Path.Combine(FixturesRoot, "met");
        string afDir = Path.Combine(FixturesRoot, "af");
        foreach (string metPath in Directory.GetFiles(metDir, "*.met"))
        {
            string name = Path.GetFileNameWithoutExtension(metPath);
            string afPath = Path.Combine(afDir, name + ".af");
            if (File.Exists(afPath))
                data.Add(metPath, afPath);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(MetMatchingAfPairs))]
    public void BinaryImportMatchesMetafAfConversion(string metPath, string afPath)
    {
        string metText = File.ReadAllText(metPath);
        Assert.True(
            VtankMetaProfileSerializer.TryLoad(metText, out MetaProfile fromMet, out string metError),
            $"{Path.GetFileName(metPath)}: {metError}");

        string afText = File.ReadAllText(afPath);
        Assert.True(
            MetafSerializer.TryLoadMeta(afText, NoOpSpellCatalog.Instance, out MetaProfile fromAf, out string afError),
            $"{Path.GetFileName(afPath)}: {afError}");

        AssertProfilesEqual(fromMet, fromAf);
    }

    public static TheoryData<string, string> NavMatchingAfPairs()
    {
        var data = new TheoryData<string, string>();
        string navDir = Path.Combine(FixturesRoot, "nav");
        string afDir = Path.Combine(FixturesRoot, "af");
        foreach (string navPath in Directory.GetFiles(navDir, "*.nav"))
        {
            string name = Path.GetFileNameWithoutExtension(navPath);
            string afPath = Path.Combine(afDir, name + ".af");
            if (File.Exists(afPath))
                data.Add(navPath, afPath);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(NavMatchingAfPairs))]
    public void BinaryNavImportMatchesMetafAfConversion(string navPath, string afPath)
    {
        string navText = File.ReadAllText(navPath);
        var fromNav = new NavigationSettings();
        Assert.True(
            VtankNavRouteSerializer.TryLoad(navText, fromNav, NoOpSpellCatalog.Instance, out string navError),
            $"{Path.GetFileName(navPath)}: {navError}");

        string afText = File.ReadAllText(afPath);
        var fromAf = new NavigationSettings();
        Assert.True(
            MetafSerializer.TryLoadNav(afText, fromAf, NoOpSpellCatalog.Instance, out string afError),
            $"{Path.GetFileName(afPath)}: {afError}");

        AssertNavigationEqual(fromNav, fromAf);
    }

    public static TheoryData<string> ByteIdenticalFixtureData()
    {
        var data = new TheoryData<string>();
        foreach (string name in new[]
        {
            "bella", "gauntlet_leader", "empyrean_facility",
            "augments", "example_sort_meta",
        })
        {
            data.Add(Path.Combine(FixturesRoot, "af", name + ".af"));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(ByteIdenticalFixtureData))]
    public void WriterOutputMatchesMetafCanonicalEmission(string path)
    {
        string original = File.ReadAllText(path);
        Assert.True(
            MetafSerializer.TryLoadMeta(original, NoOpSpellCatalog.Instance, out MetaProfile profile, out string error),
            error);
        string rewritten = MetafSerializer.SaveMeta(profile);
        Assert.Equal(original, rewritten);
    }

    public static TheoryData<string> NavByteIdenticalFixtureData()
    {
        var data = new TheoryData<string>();
        foreach (string name in new[]
        {
            "nav_ab", "nav_briennecarlus", "nav_empyrean", "nav_lockandkeyjaw",
        })
        {
            data.Add(Path.Combine(FixturesRoot, "af", name + ".af"));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(NavByteIdenticalFixtureData))]
    public void WriterOutputMatchesMetafCanonicalEmissionNavOnly(string path)
    {
        string original = File.ReadAllText(path);
        var settings = new NavigationSettings();
        Assert.True(
            MetafSerializer.TryLoadNav(original, settings, NoOpSpellCatalog.Instance, out string error),
            error);
        string rewritten = MetafSerializer.SaveNav(settings);
        Assert.Equal(original, rewritten);
    }

    [Fact]
    public void ExampleSortMetaBinaryImportMatchesAf()
    {
        string metText = File.ReadAllText(Path.Combine(FixturesRoot, "met", "example_sort_meta.met"));
        Assert.True(VtankMetaProfileSerializer.TryLoad(metText, out MetaProfile fromMet, out string metError), metError);

        string afText = File.ReadAllText(Path.Combine(FixturesRoot, "af", "example_sort_meta.af"));
        Assert.True(
            MetafSerializer.TryLoadMeta(afText, NoOpSpellCatalog.Instance, out MetaProfile fromAf, out string afError),
            afError);

        AssertProfilesEqual(fromMet, fromAf);
    }

    [Fact]
    public void PtlNodeKeepsBothCoordinateTriplesDistinct()
    {
        string original = File.ReadAllText(Path.Combine(FixturesRoot, "af", "aphus.af"));
        Assert.True(
            MetafSerializer.TryLoadMeta(original, NoOpSpellCatalog.Instance, out MetaProfile profile, out string error),
            error);

        RouteWaypoint waypoint = FindNavWaypoint(profile, "Portal to Town Network");

        Assert.Equal(-101.597905190786d, waypoint.Position.EastWest, 6);
        Assert.Equal(-96.6216093699137d, waypoint.Position.NorthSouth, 6);
        Assert.Equal(2.08333134651184E-05d, waypoint.Position.Elevation, 10);
        Assert.Equal(59.3936458587647d, waypoint.ReferencePosition.EastWest, 6);
        Assert.Equal(-28.7256083488464d, waypoint.ReferencePosition.NorthSouth, 6);
        Assert.Equal(0.0508250035345554d, waypoint.ReferencePosition.Elevation, 6);

        string rewritten = MetafSerializer.SaveMeta(profile);
        Assert.Contains(
            "ptl -101.597905190786 -96.6216093699137 2.08333134651184E-05 "
            + "59.3936458587647 -28.7256083488464 0.0508250035345554 14 "
            + "{Portal to Town Network}",
            rewritten,
            StringComparison.Ordinal);
    }

    private static RouteWaypoint FindNavWaypoint(MetaProfile profile, string objectName)
    {
        foreach (MetaRule rule in profile.Rules)
        {
            RouteWaypoint? found = FindNavWaypoint(rule.Action, objectName);
            if (found is not null)
                return found;
        }
        throw new InvalidOperationException($"no nav waypoint named '{objectName}' found.");
    }

    private static RouteWaypoint? FindNavWaypoint(MetaAction action, string objectName)
    {
        if (action.Kind == MetaActionKind.LoadEmbeddedNavigationRoute
            && action.EmbeddedRoute is { } nav)
        {
            foreach (RouteWaypoint waypoint in nav.Waypoints)
            {
                if (waypoint.ObjectName == objectName)
                    return waypoint;
            }
        }
        foreach (MetaAction child in action.Children)
        {
            RouteWaypoint? found = FindNavWaypoint(child, objectName);
            if (found is not null)
                return found;
        }
        return null;
    }

    [Fact]
    public void SaveMetaRefusesToDropADisabledRuleByDefault()
    {
        var profile = new MetaProfile
        {
            Rules =
            [
                new MetaRule
                {
                    Enabled = false,
                    Condition = MetaCondition.Always(),
                    Action = new MetaAction
                    {
                        Kind = MetaActionKind.ChatCommand,
                        Text = "/say must not silently vanish",
                    },
                },
            ],
        };

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
            () => MetafSerializer.SaveMeta(profile));
        Assert.Contains("1 disabled rule", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveMetaDropsDisabledRulesOnlyWhenExplicitlyToldTo()
    {
        var profile = new MetaProfile
        {
            Rules =
            [
                new MetaRule
                {
                    Enabled = false,
                    Condition = MetaCondition.Always(),
                    Action = new MetaAction
                    {
                        Kind = MetaActionKind.ChatCommand,
                        Text = "/say must not run",
                    },
                },
            ],
        };

        string source = MetafSerializer.SaveMeta(profile, dropDisabledRules: true);

        Assert.DoesNotContain("must not run", source, StringComparison.Ordinal);
        Assert.True(MetafSerializer.TryLoadMeta(
            source, NoOpSpellCatalog.Instance, out MetaProfile loaded, out string error), error);
        Assert.Empty(loaded.Rules);
    }

    [Fact]
    public void NestedAllAnyNotParsesCorrectly()
    {
        string af = string.Join("\r\n",
        [
            "STATE: {Default}",
            "\tIF:\tAll",
            "\t\t\tAlways",
            "\t\t\tNot Death",
            "\t\t\tAny",
            "\t\t\t\tVendorOpen",
            "\t\t\t\tVendorClosed",
            "\t\tDO:\tDoAll",
            "\t\t\t\tChat {hello}",
            "\t\t\t\tSetState {Next}",
        ]) + "\r\n";
        Assert.True(
            MetafSerializer.TryLoadMeta(af, NoOpSpellCatalog.Instance, out MetaProfile profile, out string error),
            error);
        MetaRule rule = Assert.Single(profile.Rules);
        Assert.Equal(MetaConditionKind.All, rule.Condition.Kind);
        Assert.Equal(3, rule.Condition.Children.Count);
        Assert.Equal(MetaConditionKind.Always, rule.Condition.Children[0].Kind);
        Assert.Equal(MetaConditionKind.Not, rule.Condition.Children[1].Kind);
        Assert.Equal(MetaConditionKind.CharacterDeath, rule.Condition.Children[1].Children[0].Kind);
        Assert.Equal(MetaConditionKind.Any, rule.Condition.Children[2].Kind);
        Assert.Equal(2, rule.Condition.Children[2].Children.Count);
        Assert.Equal(MetaActionKind.All, rule.Action.Kind);
        Assert.Equal(2, rule.Action.Children.Count);
        Assert.Equal("hello", rule.Action.Children[0].Text);
        Assert.Equal("Next", rule.Action.Children[1].Text);
    }

    [Fact]
    public void MobsInDistPriorityRoundTripsAllThreeNumbersDistinctly()
    {
        string af = string.Join("\r\n",
        [
            "STATE: {Default}",
            "\tIF:\tMobsInDist_Priority 7 12.5 3",
            "\t\tDO:\tChat {seen}",
        ]) + "\r\n";

        Assert.True(
            MetafSerializer.TryLoadMeta(af, NoOpSpellCatalog.Instance, out MetaProfile profile, out string error),
            error);
        MetaRule rule = Assert.Single(profile.Rules);
        Assert.Equal(MetaConditionKind.MonsterPriorityCountWithinDistance, rule.Condition.Kind);
        Assert.Equal(7, rule.Condition.Number);
        Assert.Equal(12.5, rule.Condition.SecondaryNumber);
        Assert.Equal(3, rule.Condition.TertiaryNumber);

        string rewritten = MetafSerializer.SaveMeta(profile);
        Assert.True(
            MetafSerializer.TryLoadMeta(rewritten, NoOpSpellCatalog.Instance, out MetaProfile reloaded, out string error2),
            error2);
        MetaRule reloadedRule = Assert.Single(reloaded.Rules);
        Assert.Equal(7, reloadedRule.Condition.Number);
        Assert.Equal(12.5, reloadedRule.Condition.SecondaryNumber);
        Assert.Equal(3, reloadedRule.Condition.TertiaryNumber);
    }

    [Fact]
    public void SynthesizedGetOptFollowAndJumpFixtureRoundTrips()
    {
        string af = string.Join("\r\n",
        [
            "STATE: {Default}",
            "\tIF:\tAlways",
            "\t\tDO:\tDoAll",
            "\t\t\t\tGetOpt {AttackDistance} {myvar}",
            "\t\t\t\tSetOpt {AttackDistance} {myvar}",
            "NAV: myfollow follow",
            "\tflw 00001234 {Some Monster}",
        ]) + "\r\n";
        Assert.True(
            MetafSerializer.TryLoadMeta(af, NoOpSpellCatalog.Instance, out MetaProfile profile, out string error),
            error);
        MetaRule rule = Assert.Single(profile.Rules);
        MetaAction getOpt = rule.Action.Children[0];
        MetaAction setOpt = rule.Action.Children[1];
        Assert.Equal(MetaActionKind.GetVtankOption, getOpt.Kind);
        Assert.Equal("AttackDistance", getOpt.Text);
        Assert.Equal("myvar", getOpt.SecondaryText);
        Assert.Equal(MetaActionKind.SetVtankOption, setOpt.Kind);

        var nav = new NavigationSettings();
        bool navLoaded = MetafSerializer.TryLoadNav(
            af, nav, NoOpSpellCatalog.Instance, out string navError);
        Assert.False(navLoaded);
        Assert.Contains("STATE:", navError, StringComparison.Ordinal);
    }

    [Fact]
    public void FollowNavNodeParsesAsANavOnlyDocument()
    {
        string af = string.Join("\r\n",
        [
            "NAV: myfollow follow",
            "\tflw 00001234 {Some Monster}",
        ]) + "\r\n";
        var nav = new NavigationSettings();
        Assert.True(
            MetafSerializer.TryLoadNav(af, nav, NoOpSpellCatalog.Instance, out string error),
            error);
        Assert.Equal(RouteMode.Target, nav.Mode);
        Assert.Equal(0x00001234u, nav.FollowTargetObjectId);
        Assert.Equal("Some Monster", nav.FollowTargetName);
    }

    [Fact]
    public void JumpNodeLoadPreservesAuthoredChargeMillisecondsAboveRetailCeiling()
    {
        const string af = """
            NAV: j once
            	jmp 1 2 3 90 {True} 5000
            """;
        var target = new NavigationSettings();
        Assert.True(MetafSerializer.TryLoadNav(af, target, NoOpSpellCatalog.Instance, out string error), error);
        RouteWaypoint waypoint = Assert.Single(target.Waypoints);
        Assert.Equal(RouteWaypointType.Jump, waypoint.Type);
        Assert.Equal(5000, waypoint.JumpChargeMilliseconds);
        Assert.True(waypoint.JumpRun);
        Assert.Equal(90f, waypoint.JumpHeadingDegrees);
    }

    [Fact]
    public void JumpNodeBelowClampPassesThroughUnchanged()
    {
        const string af = """
            NAV: j once
            	jmp 1 2 3 90 {False} 500
            """;
        var target = new NavigationSettings();
        Assert.True(MetafSerializer.TryLoadNav(af, target, NoOpSpellCatalog.Instance, out string error), error);
        RouteWaypoint waypoint = Assert.Single(target.Waypoints);
        Assert.Equal(500, waypoint.JumpChargeMilliseconds);
        Assert.False(waypoint.JumpRun);
    }

    [Fact]
    public void JumpNodeSaveThenLoadRoundTripsChargeMillisecondsAboveRetailCeiling()
    {
        var source = new NavigationSettings { Mode = RouteMode.Once };
        source.Waypoints.Add(new RouteWaypoint
        {
            Type = RouteWaypointType.Jump,
            JumpHeadingDegrees = 90f,
            JumpRun = true,
            JumpChargeMilliseconds = 5000,
        });

        string af = MetafSerializer.SaveNav(source);

        var target = new NavigationSettings();
        Assert.True(MetafSerializer.TryLoadNav(af, target, NoOpSpellCatalog.Instance, out string error), error);
        RouteWaypoint waypoint = Assert.Single(target.Waypoints);
        Assert.Equal(5000, waypoint.JumpChargeMilliseconds);
    }

    [Fact]
    public void RecallNodeRoundTripsByNameAndResolvesTheRealSpellIdFromTheCatalog()
    {
        var source = new NavigationSettings { Mode = RouteMode.Once };
        RouteRecallKind[] kinds =
        [
            RouteRecallKind.PrimaryPortalRecall,
            RouteRecallKind.MountLetheRecall,
            RouteRecallKind.EldrytchWebStrongholdRecall,
        ];
        foreach (RouteRecallKind kind in kinds)
        {
            source.Waypoints.Add(new RouteWaypoint
            {
                Type = RouteWaypointType.Recall,
                Recall = kind,
                RecallSpellName = RouteWaypoint.RecallDisplayName(kind),
                RecallSpellId = RouteWaypoint.SpellIdForRecall(kind),
            });
        }

        string af = MetafSerializer.SaveNav(source);

        var catalog = new FakeSpellCatalog(kinds.Select(
            kind => new PluginSpellInfo(
                RouteWaypoint.SpellIdForRecall(kind),
                RouteWaypoint.RecallDisplayName(kind),
                Family: 0, Tier: 1, Difficulty: 1, ManaCost: 0,
                DurationSeconds: 0f, School: 0, Description: string.Empty,
                IsSelfTargeted: true, IsBeneficial: true)));
        var target = new NavigationSettings();
        Assert.True(MetafSerializer.TryLoadNav(af, target, catalog, out string error), error);

        Assert.Equal(kinds.Length, target.Waypoints.Count);
        for (int i = 0; i < kinds.Length; i++)
        {
            RouteWaypoint waypoint = target.Waypoints[i];
            Assert.Equal(RouteWaypoint.RecallDisplayName(kinds[i]), waypoint.RecallSpellName);
            Assert.Equal(RouteWaypoint.SpellIdForRecall(kinds[i]), waypoint.RecallSpellId);
        }
    }

    private static void AssertProfilesEqual(MetaProfile expected, MetaProfile actual)
    {
        Assert.Equal(expected.Rules.Count, actual.Rules.Count);
        for (int i = 0; i < expected.Rules.Count; i++)
        {
            MetaRule a = expected.Rules[i];
            MetaRule b = actual.Rules[i];
            Assert.Equal(a.State, b.State);
            AssertConditionsEqual(a.Condition, b.Condition);
            AssertActionsEqual(a.Action, b.Action);
        }
    }

    private static void AssertConditionsEqual(MetaCondition expected, MetaCondition actual)
    {
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.Text, actual.Text);
        Assert.Equal(expected.SecondaryText, actual.SecondaryText);
        Assert.Equal(expected.Number, actual.Number, 6);
        Assert.Equal(expected.SecondaryNumber, actual.SecondaryNumber, 6);
        Assert.Equal(expected.TertiaryNumber, actual.TertiaryNumber, 6);
        Assert.Equal(expected.Children.Count, actual.Children.Count);
        for (int i = 0; i < expected.Children.Count; i++)
            AssertConditionsEqual(expected.Children[i], actual.Children[i]);
    }

    private static void AssertActionsEqual(MetaAction expected, MetaAction actual)
    {
        Assert.Equal(expected.Kind, actual.Kind);
        if (expected.Kind == MetaActionKind.LoadEmbeddedNavigationRoute)
        {
            Assert.Equal(expected.Text, actual.Text);
            Assert.NotNull(expected.EmbeddedRoute);
            Assert.NotNull(actual.EmbeddedRoute);
            AssertNavigationEqual(expected.EmbeddedRoute!, actual.EmbeddedRoute!);
        }
        else
        {
            Assert.Equal(expected.Text, actual.Text);
        }
        Assert.Equal(expected.SecondaryText, actual.SecondaryText);
        Assert.Equal(expected.Number, actual.Number, 6);
        Assert.Equal(expected.SecondaryNumber, actual.SecondaryNumber, 6);
        Assert.Equal(expected.Children.Count, actual.Children.Count);
        for (int i = 0; i < expected.Children.Count; i++)
            AssertActionsEqual(expected.Children[i], actual.Children[i]);
    }

    private static void AssertNavigationEqual(NavigationSettings expected, NavigationSettings actual)
    {
        Assert.Equal(expected.Mode, actual.Mode);
        if (expected.Mode == RouteMode.Target)
        {
            Assert.Equal(expected.FollowTargetObjectId, actual.FollowTargetObjectId);
            Assert.Equal(expected.FollowTargetName, actual.FollowTargetName);
            return;
        }
        Assert.Equal(expected.Waypoints.Count, actual.Waypoints.Count);
        for (int i = 0; i < expected.Waypoints.Count; i++)
        {
            RouteWaypoint a = expected.Waypoints[i];
            RouteWaypoint b = actual.Waypoints[i];
            Assert.Equal(a.Type, b.Type);
            Assert.Equal(a.Position.EastWest, b.Position.EastWest, 3);
            Assert.Equal(a.Position.NorthSouth, b.Position.NorthSouth, 3);
            Assert.Equal(a.Position.Elevation, b.Position.Elevation, 3);
            Assert.Equal(a.ReferencePosition.EastWest, b.ReferencePosition.EastWest, 3);
            Assert.Equal(a.ReferencePosition.NorthSouth, b.ReferencePosition.NorthSouth, 3);
            Assert.Equal(a.ReferencePosition.Elevation, b.ReferencePosition.Elevation, 3);
            Assert.Equal(a.ObjectId, b.ObjectId);
            Assert.Equal(a.ObjectName, b.ObjectName);
            Assert.Equal(a.Text, b.Text);
            Assert.Equal(a.DurationMilliseconds, b.DurationMilliseconds);
            Assert.Equal(a.JumpHeadingDegrees, b.JumpHeadingDegrees, 3);
            Assert.Equal(a.JumpRun, b.JumpRun);
            Assert.Equal(a.JumpChargeMilliseconds, b.JumpChargeMilliseconds);
        }
    }

    private sealed class NoOpSpellCatalog : ISpellCatalog
    {
        public static NoOpSpellCatalog Instance { get; } = new();
        public IReadOnlyList<PluginSpellInfo> KnownSelfBuffs => [];
        public bool TryGet(uint spellId, out PluginSpellInfo info)
        {
            info = default;
            return false;
        }
    }

    private sealed class FakeSpellCatalog(IEnumerable<PluginSpellInfo> selfBuffs) : ISpellCatalog
    {
        public IReadOnlyList<PluginSpellInfo> KnownSelfBuffs { get; } = selfBuffs.ToArray();
        public bool TryGet(uint spellId, out PluginSpellInfo info)
        {
            foreach (PluginSpellInfo spell in KnownSelfBuffs)
            {
                if (spell.SpellId == spellId)
                {
                    info = spell;
                    return true;
                }
            }
            info = default;
            return false;
        }
    }
}
