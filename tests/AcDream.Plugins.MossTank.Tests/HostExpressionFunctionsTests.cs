using AcDream.Plugin.Abstractions;
using AcDream.Plugins.MossTank.Expressions;

namespace AcDream.Plugins.MossTank.Tests;

public sealed class HostExpressionFunctionsTests
{
    [Fact]
    public void CharacterAndNavigationFunctionsReadCanonicalAutomationFacts()
    {
        var automation = CreateAutomation();
        using var runtime = new MossTankExpressionRuntime(new Host(automation));

        Assert.Equal("Coldeve", runtime.Evaluate("getworldname[]").AsString());
        Assert.Equal(2d, runtime.Evaluate("getcharacterindex[]").AsNumber());
        Assert.Equal(90d, runtime.Evaluate("getcharvital_current[1]").AsNumber());
        Assert.Equal(100d, runtime.Evaluate("getcharvital_buffedmax[1]").AsNumber());
        Assert.Equal(100d, runtime.Evaluate("getcharattribute_base[1]").AsNumber());
        Assert.Equal(110d, runtime.Evaluate("getcharattribute_buffed[1]").AsNumber());
        Assert.Equal(275d, runtime.Evaluate("getcharskill_base[34]").AsNumber());
        Assert.Equal(300d, runtime.Evaluate("getcharskill_buffed[34]").AsNumber());
        Assert.Equal(2d, runtime.Evaluate("getcharskill_traininglevel[34]").AsNumber());
        Assert.Equal(0x7F7Fu, runtime.Evaluate("getplayerlandblock[]").AsNumber());
        Assert.Equal(90d, runtime.Evaluate("getheading[wobjectgetplayer[]]").AsNumber());
    }

    [Fact]
    public void ObjectDiscoveryCountsPropertiesAndNearestMatchUtilityBeltShape()
    {
        var automation = CreateAutomation();
        using var runtime = new MossTankExpressionRuntime(new Host(automation));

        Assert.Equal(2d, runtime.Evaluate(
            "listcount[wobjectfindallinventory[]]").AsNumber());
        Assert.Equal(10d, runtime.Evaluate(
            "getitemcountininventorybyname['Health Elixir']").AsNumber());
        Assert.Equal(9d, runtime.Evaluate("getfreeitemslots[]").AsNumber());
        Assert.Equal(1d, runtime.Evaluate("getfreecontainerslots[]").AsNumber());
        Assert.Equal("Drudge", runtime.Evaluate(
            "wobjectgetname[wobjectfindnearestmonster[]]").AsString());
        Assert.Equal(77d, runtime.Evaluate(
            "wobjectgetintprop[wobjectfindbyid[20],25]").AsNumber());
        Assert.Equal(2d, runtime.Evaluate(
            "listcount[wobjectfindallbynamerx['(?i)elixir|drudge']]").AsNumber());
        Assert.Equal((double)PluginObjectClass.Monster, runtime.Evaluate(
            "wobjectgetobjectclass[wobjectfindbyid[20]]").AsNumber());
        Assert.Equal(54321d, runtime.Evaluate(
            "wobjectlastidtime[wobjectfindbyid[20]]").AsNumber());
    }

    [Fact]
    public void ActionFunctionsUseSharedSelectionInventoryMagicAndMovementCommands()
    {
        var automation = CreateAutomation();
        var host = new Host(automation);
        using var runtime = new MossTankExpressionRuntime(host);

        Assert.True(runtime.Evaluate("actiontryselect[20]").IsTruthy);
        Assert.Equal(20u, host.Selection.SelectedObjectId);
        Assert.True(runtime.Evaluate("actiontryuseitem[10]").IsTruthy);
        Assert.Equal(10u, automation.UsedObject);
        Assert.Equal(1d, runtime.Evaluate("actiontrycastbyidontarget[1001,20]").AsNumber());
        Assert.Equal((1001u, 20u), automation.LastCast);
        Assert.True(runtime.Evaluate("setmotion['Forward',1]").IsTruthy);
        Assert.True(automation.LastIntent.Forward);
        Assert.True(runtime.Evaluate("clearmotion[]").IsTruthy);
        Assert.Equal(1, automation.ClearMovementCount);
    }

    [Fact]
    public void LoginFunctionsUseTheAuthoritativeSortedRosterAndOneShotOwner()
    {
        var automation = CreateAutomation();
        automation.LoginRoster =
        [
            new PluginLoginCharacter(10u, "Beta", 2, false),
            new PluginLoginCharacter(1u, "Expression Tester", 0, false),
            new PluginLoginCharacter(30u, "Mule", 1, false),
        ];
        using var runtime = new MossTankExpressionRuntime(new Host(automation));

        Assert.Equal(1d, runtime.Evaluate("getcharacterindex[]").AsNumber());
        Assert.Equal(0d, runtime.Evaluate(
            "getcharacterindex['bet']").AsNumber());
        Assert.True(runtime.Evaluate("setnextlogin[1]").IsTruthy);
        Assert.Equal(30u, automation.NextLoginObjectId);
        Assert.True(runtime.Evaluate("setnextlogin['bet']").IsTruthy);
        Assert.Equal(10u, automation.NextLoginObjectId);
        Assert.True(runtime.Evaluate("clearnextlogin[]").IsTruthy);
        Assert.Equal(0u, automation.NextLoginObjectId);
    }

    [Fact]
    public void NetworkClientsReturnUtilityBeltDictionariesAndTagFiltering()
    {
        var automation = CreateAutomation();
        automation.NetworkClients =
        [
            new PluginNetworkClient(
                7u,
                70u,
                "Remote Mule",
                "Coldeve",
                automation.Position,
                ["mules", "trade"],
                90u,
                70u,
                80u,
                100u,
                100u,
                100u,
                90f),
        ];
        using var runtime = new MossTankExpressionRuntime(new Host(automation));

        ExpressionList clients = runtime.Evaluate("netclients['mules']").AsList();
        ExpressionDictionary client = Assert.Single(clients.Items).AsDictionary();
        Assert.Equal("Remote Mule", client.Items["Name"].AsString());
        Assert.Equal(70d, client.Items["PlayerId"].AsNumber());
        Assert.Equal(2, client.Items["Tags"].AsList().Items.Count);
        Assert.Empty(runtime.Evaluate("netclients['combat']").AsList().Items);
    }

    [Fact]
    public void DelayedExecutionUsesTickAndCanBeCancelled()
    {
        using var runtime = new MossTankExpressionRuntime(
            new Host(CreateAutomation()));

        double id = runtime.Evaluate(
            "delayexec[100,\"setvar['done',1]\"]").AsNumber();
        runtime.OnTick(0.099);
        Assert.Equal(0d, runtime.Evaluate("getvar['done']").AsNumber());
        runtime.OnTick(0.001);
        Assert.Equal(1d, runtime.Evaluate("getvar['done']").AsNumber());

        double cancelled = runtime.Evaluate(
            "delayexec[1,\"setvar['bad',1]\"]").AsNumber();
        Assert.True(runtime.Evaluate($"clearexec[{cancelled}]").IsTruthy);
        runtime.OnTick(1d);
        Assert.Equal(0d, runtime.Evaluate("getvar['bad']").AsNumber());
        Assert.NotEqual(id, cancelled);
    }

    [Fact]
    public void PersistentAndGlobalVariablesRoundTripThroughPluginStorage()
    {
        var storage = new MemoryStorage();
        var automation = CreateAutomation();
        using (var first = new MossTankExpressionRuntime(new Host(automation, storage)))
        {
            first.Evaluate("setpvar['count',42];setgvar['names',listcreate['a','b']]");
        }

        using var second = new MossTankExpressionRuntime(
            new Host(CreateAutomation(), storage));
        Assert.Equal(42d, second.Evaluate("getpvar['count']").AsNumber());
        Assert.Equal(2d, second.Evaluate("listcount[getgvar['names']]").AsNumber());
        Assert.Contains(
            storage.Text.Keys,
            static key => key.StartsWith("expressions/persistent/", StringComparison.Ordinal));
        Assert.Contains(
            storage.Text.Keys,
            static key => key.StartsWith("expressions/global/", StringComparison.Ordinal));
    }

    [Fact]
    public void FellowshipFunctionsExposeTheAuthoritativeCompleteRoster()
    {
        var automation = CreateAutomation();
        automation.FellowRoster =
        [
            new PluginFellowMember(1, "Expression Tester", 90, 100, 80, 100, 70, 100, 0),
            new PluginFellowMember(22, "Fellow Two", 50, 60, 40, 60, 30, 60, 1),
        ];
        using var runtime = new MossTankExpressionRuntime(new Host(automation));

        Assert.True(runtime.Evaluate("getfellowshipstatus[]").IsTruthy);
        Assert.Equal("Test Fellowship", runtime.Evaluate("getfellowshipname[]").AsString());
        Assert.Equal(2d, runtime.Evaluate("getfellowshipcount[]").AsNumber());
        Assert.True(runtime.Evaluate("getfellowshipisleader[]").IsTruthy);
        Assert.True(runtime.Evaluate("getfellowshipcanrecruit[]").IsTruthy);
        Assert.Equal("Fellow Two", runtime.Evaluate("getfellowname[1]").AsString());
        Assert.Equal(22d, runtime.Evaluate("getfellowid[1]").AsNumber());
        Assert.Equal(2d, runtime.Evaluate("listcount[getfellowids[]]").AsNumber());
    }

    [Fact]
    public void DerethTimeFunctionsUseTheRuntimeWorldClockProjection()
    {
        var automation = CreateAutomation();
        automation.Time = new PluginWorldTimeSnapshot(
            true,
            123456d,
            142,
            6,
            17,
            10,
            "HarvestGain",
            "Warmtide",
            true,
            0d,
            12.5d);
        using var runtime = new MossTankExpressionRuntime(new Host(automation));

        Assert.Equal(142d, runtime.Evaluate("getgameyear[]").AsNumber());
        Assert.Equal(6d, runtime.Evaluate("getgamemonth[]").AsNumber());
        Assert.Equal("HarvestGain", runtime.Evaluate("getgamemonthname[6]").AsString());
        Assert.Equal(17d, runtime.Evaluate("getgameday[]").AsNumber());
        Assert.Equal("Warmtide", runtime.Evaluate("getgamehourname[10]").AsString());
        Assert.Equal(123456d, runtime.Evaluate("getgameticks[]").AsNumber());
        Assert.True(runtime.Evaluate("getisday[]").IsTruthy);
        Assert.Equal(12.5d, runtime.Evaluate("getminutesuntilnight[]").AsNumber());
    }

    [Fact]
    public void ExperienceMeterAccumulatesCanonicalXpAndLuminanceDeltas()
    {
        var automation = CreateAutomation();
        automation.Properties[1] = Properties(
            ints: new Dictionary<uint, int> { [5] = 50, [96] = 100 },
            int64s: new Dictionary<uint, long> { [1] = 1000, [6] = 10 });
        using var runtime = new MossTankExpressionRuntime(new Host(automation));
        runtime.OnTick(1d);
        automation.Properties[1] = Properties(
            ints: new Dictionary<uint, int> { [5] = 50, [96] = 100 },
            int64s: new Dictionary<uint, long> { [1] = 1100, [6] = 15 });
        runtime.OnTick(1d);

        Assert.Equal(100d, runtime.Evaluate("xptotal[]").AsNumber());
        Assert.Equal(5d, runtime.Evaluate("lumtotal[]").AsNumber());
        Assert.Equal(2d, runtime.Evaluate("xpduration[]").AsNumber());
        Assert.Equal(180000d, runtime.Evaluate("xpavg[]").AsNumber());
        Assert.Contains("100 XP", runtime.Evaluate("xpmeter[]").AsString());
        Assert.True(runtime.Evaluate("xpreset[]").IsTruthy);
        Assert.Equal(0d, runtime.Evaluate("xptotal[]").AsNumber());
    }

    [Fact]
    public void QuestFunctionsParseTheAuthoritativeMyquestsTranscript()
    {
        var automation = CreateAutomation();
        using var runtime = new MossTankExpressionRuntime(new Host(automation));
        Assert.Equal(["/myquests"], automation.SubmittedChat);
        Assert.True(runtime.Evaluate("isrefreshingquests[]").IsTruthy);

        automation.ChatMessages.Add(new PluginChatMessage(
            1, 0, 0, string.Empty,
            "killtaskdrudges - 3 solves (0)\"Drudges killed\" 10 0",
            string.Empty));
        automation.ChatMessages.Add(new PluginChatMessage(
            2, 0, 0, string.Empty,
            "onevisit - 1 solves (1700000000)\"Visited once\" 1 0",
            string.Empty));
        runtime.OnTick(0d);

        Assert.True(runtime.Evaluate("testquestflag['killtaskdrudges']").IsTruthy);
        Assert.Equal(3d, runtime.Evaluate(
            "getquestktprogress['killtaskdrudges']").AsNumber());
        Assert.Equal(10d, runtime.Evaluate(
            "getquestktrequired['killtaskdrudges']").AsNumber());
        Assert.False(runtime.Evaluate("getqueststatus['onevisit']").IsTruthy);
        Assert.True(runtime.Evaluate("getqueststatus['unknown']").IsTruthy);

        runtime.OnTick(1.001d);
        Assert.False(runtime.Evaluate("isrefreshingquests[]").IsTruthy);
    }

    [Fact]
    public void CorpseFunctionsUseTheCanonicalExternalContainerHistory()
    {
        var automation = CreateAutomation();
        automation.Corpses =
        [
            new PluginLootContainer(30, 300, "Corpse", 2f, false, false, false),
            new PluginLootContainer(31, 301, "Corpse", 3f, true, false, false),
        ];
        using var runtime = new MossTankExpressionRuntime(new Host(automation));

        Assert.False(runtime.Evaluate("hascorpsebeenopenedbyme[30]").IsTruthy);
        Assert.True(runtime.Evaluate("hascorpsebeenopenedbyme[31]").IsTruthy);
        Assert.Equal(30d, runtime.Evaluate(
            "listgetitem[getcorpsesunopenedbyme[],0]").AsNumber());
    }

    [Fact]
    public void ComponentFunctionsUseTheRetailDatCatalogProjection()
    {
        var automation = CreateAutomation();
        automation.Components[7] = new PluginSpellComponentInfo(
            7, 101, "Malar Herb", 0.25, 0x13000001, 0.75,
            0x06000010, 4, "Herb", "Malar");
        using var runtime = new MossTankExpressionRuntime(new Host(automation));

        Assert.Equal("Malar Herb", runtime.Evaluate("componentname[7]").AsString());
        Assert.Equal(0.25d, runtime.Evaluate(
            "dictgetitem[componentdata[7],'BurnRate']").AsNumber());
        Assert.Equal("Malar", runtime.Evaluate(
            "dictgetitem[componentdata[7],'Word']").AsString());
    }

    [Fact]
    public void UstFunctionsStageAndSubmitThroughTheCanonicalSalvageCommand()
    {
        var automation = CreateAutomation();
        automation.InventoryItems =
        [
            InventoryItem(40, "Ust"),
            InventoryItem(41, "Salvage One"),
            InventoryItem(42, "Salvage Two"),
        ];
        using var runtime = new MossTankExpressionRuntime(new Host(automation));

        Assert.True(runtime.Evaluate("ustopen[]").IsTruthy);
        Assert.Equal(40u, automation.UsedObject);
        Assert.True(runtime.Evaluate("ustadd[41]").IsTruthy);
        Assert.True(runtime.Evaluate("ustadd[42]").IsTruthy);
        Assert.True(runtime.Evaluate("ustsalvage[]").IsTruthy);
        Assert.Equal(40u, automation.LastSalvage.Tool);
        Assert.Equal([41u, 42u], automation.LastSalvage.Items);
        Assert.False(runtime.Evaluate("ustsalvage[]").IsTruthy);
    }

    private static Automation CreateAutomation()
    {
        var automation = new Automation
        {
            Position = new PluginNavigationPosition(
                0x7F7F0001u, 10d, 20d, 3d, 90f, true),
            Attributes =
            [
                new PluginAttributeInfo(0, "Strength", 110) { Base = 100 },
            ],
            Skills =
            [
                new PluginSkillInfo(34, "War Magic", PluginSkillTraining.Trained, 300)
                {
                    Base = 275,
                },
            ],
        };
        automation.WorldObjects.Add(new PluginWorldObject(
            1, 1, "Expression Tester", PluginObjectClass.Player, 0x10, 0, 0)
        {
            HasPosition = true,
            Position = automation.Position,
            ItemsCapacity = 10,
            ContainersCapacity = 2,
        });
        automation.WorldObjects.Add(new PluginWorldObject(
            10, 100, "Health Elixir", PluginObjectClass.Food, 0x20, 1, 0)
        {
            IsOwned = true,
            StackSize = 10,
        });
        automation.WorldObjects.Add(new PluginWorldObject(
            11, 101, "Small Pack", PluginObjectClass.Container, 0x200, 1, 0)
        {
            IsOwned = true,
            ItemsCapacity = 8,
        });
        automation.WorldObjects.Add(new PluginWorldObject(
            20, 200, "Drudge", PluginObjectClass.Monster, 0x10, 0, 0)
        {
            IsLandscape = true,
            HasPosition = true,
            Position = automation.Position with { EastWest = 10.1d },
            HasAppraisalData = true,
            LastIdTime = 54321,
        });
        automation.Properties[1] = Properties(
            ints: new Dictionary<uint, int> { [5] = 50, [96] = 100 },
            strings: new Dictionary<uint, string> { [1] = "Expression Tester" });
        automation.Properties[20] = Properties(
            ints: new Dictionary<uint, int> { [25] = 77 });
        return automation;
    }

    private static PluginItemProperties Properties(
        IReadOnlyDictionary<uint, int>? ints = null,
        IReadOnlyDictionary<uint, long>? int64s = null,
        IReadOnlyDictionary<uint, string>? strings = null) => new(
            ints ?? new Dictionary<uint, int>(),
            int64s ?? new Dictionary<uint, long>(),
            new Dictionary<uint, bool>(),
            new Dictionary<uint, double>(),
            strings ?? new Dictionary<uint, string>(),
            new Dictionary<uint, uint>(),
            new Dictionary<uint, uint>());

    private static PluginInventoryItem InventoryItem(uint id, string name) => new(
        id, 0u, name, 0u, 1u, 0u, 0u, 0u, 0u, 0u, 0u,
        1, 0, 0, 0u, 0, 0, 0u, false, 0d, 0, 0, 0, 0d, 0, 0, 0);

    private sealed class Host(
        Automation automation,
        IPluginStorage? storage = null) : IPluginHost
    {
        public bool HasUi => false;
        public IPluginLogger Log { get; } = new Logger();
        public IGameState State { get; } = new State();
        public IEvents Events { get; } = new Events();
        public Selection Selection { get; } = new();
        ISelectionService IPluginHost.Selection => Selection;
        public IUiRegistry Ui => NoOpUiRegistry.Instance;
        public IPluginStorage Storage { get; } = storage ?? NoOpPluginStorage.Instance;
        public IAutomationSurface Automation { get; } = automation;
    }

    private sealed class Automation :
        IAutomationSurface,
        ICharacterInfo,
        ISpellCatalog,
        IMagicCommands,
        IPluginChat,
        IWorldObjectAutomation,
        IItemAutomation,
        INavigationAutomation,
        IFellowshipAutomation,
        IWorldTimeAutomation,
        ILootAutomation,
        ILoginAutomation,
        INetworkAutomation
    {
        public bool IsAvailable => true;
        public ICharacterInfo Character => this;
        public ISpellCatalog Spells => this;
        public IMagicCommands Magic => this;
        public IPluginChat Chat => this;
        public IWorldObjectAutomation Objects => this;
        public IItemAutomation Items => this;
        public INavigationAutomation Navigation => this;
        public IFellowshipAutomation Fellowship => this;
        public ILootAutomation Loot => this;
        public IWorldTimeAutomation WorldTime => this;
        public ILoginAutomation Login => this;
        public INetworkAutomation Network => this;
        public bool IsInWorld => true;
        public string Name => "Expression Tester";
        public string WorldName => "Coldeve";
        public string AccountName => "testaccount";
        public int CharacterIndex => 2;
        public uint ObjectId => 1;
        public uint CurrentHealth => 90;
        public uint MaxHealth => 100;
        public uint CurrentStamina => 80;
        public uint MaxStamina => 100;
        public uint CurrentMana => 70;
        public uint MaxMana => 100;
        public IReadOnlyList<PluginSkillInfo> Skills { get; set; } = [];
        public IReadOnlyList<PluginAttributeInfo> Attributes { get; set; } = [];
        public IReadOnlyList<PluginActiveEnchantment> ActiveEnchantments => [];
        public IReadOnlyList<PluginSpellInfo> KnownSelfBuffs => [];
        public IReadOnlyList<PluginSpellInfo> KnownAttackSpells => [];
        public IReadOnlyList<PluginSpellInfo> KnownCombatSpells => [];
        public List<PluginWorldObject> WorldObjects { get; } = [];
        public Dictionary<uint, PluginItemProperties> Properties { get; } = [];
        public Dictionary<uint, PluginSpellComponentInfo> Components { get; } = [];
        public PluginNavigationPosition Position { get; set; }
        public uint UsedObject { get; private set; }
        public (uint SpellId, uint TargetId) LastCast { get; private set; }
        public PluginMovementIntent LastIntent { get; private set; }
        public int ClearMovementCount { get; private set; }
        public List<PluginChatMessage> ChatMessages { get; } = [];
        public List<string> SubmittedChat { get; } = [];
        public IReadOnlyList<PluginInventoryItem> InventoryItems { get; set; } = [];
        public (uint Tool, IReadOnlyList<uint> Items) LastSalvage { get; private set; }
        public IReadOnlyList<PluginFellowMember> FellowRoster { get; set; } = [];
        public IReadOnlyList<PluginLoginCharacter> LoginRoster { get; set; } = [];
        public uint NextLoginObjectId { get; private set; }
        public IReadOnlyList<PluginNetworkClient> NetworkClients { get; set; } = [];
        public IReadOnlyList<PluginLootContainer> Corpses { get; set; } = [];
        public IReadOnlyList<PluginLootContainer> CaptureCorpses(float maximumDistance) =>
            Corpses.Where(corpse => corpse.Distance <= maximumDistance).ToArray();
        public bool IsInFellowship => FellowRoster.Count != 0;
        string IFellowshipAutomation.Name => "Test Fellowship";
        public uint LeaderObjectId => 1;
        public bool IsOpen => true;
        public bool IsLocked => false;
        public int MemberCount => FellowRoster.Count;
        IReadOnlyList<PluginFellowMember> IFellowshipAutomation.CaptureRoster() =>
            FellowRoster;
        bool ILoginAutomation.IsAvailable => true;
        IReadOnlyList<PluginLoginCharacter> ILoginAutomation.CaptureRoster() =>
            LoginRoster;
        public bool SetNextLogin(uint characterObjectId)
        {
            if (!LoginRoster.Any(character =>
                character.ObjectId == characterObjectId
                && !character.IsPendingDelete))
            {
                return false;
            }
            NextLoginObjectId = characterObjectId;
            return true;
        }
        public bool ClearNextLogin()
        {
            NextLoginObjectId = 0u;
            return true;
        }
        bool INetworkAutomation.IsAvailable => true;
        IReadOnlyList<PluginNetworkClient> INetworkAutomation.CaptureClients() =>
            NetworkClients;
        public PluginWorldTimeSnapshot Time { get; set; }
        PluginWorldTimeSnapshot IWorldTimeAutomation.Snapshot => Time;

        public bool TryGetSkill(uint skillId, out PluginSkillInfo skill)
        {
            foreach (PluginSkillInfo candidate in Skills)
            {
                if (candidate.SkillId == skillId)
                {
                    skill = candidate;
                    return true;
                }
            }
            skill = default;
            return false;
        }

        public bool IsKnown(uint spellId) => spellId == 1001;
        public bool TryGet(uint spellId, out PluginSpellInfo info)
        {
            info = default;
            return false;
        }
        public bool TryGetComponent(uint componentId, out PluginSpellComponentInfo info) =>
            Components.TryGetValue(componentId, out info);
        public bool IsCasting => false;
        public PluginCastGate EvaluateGate(uint spellId) =>
            spellId == 1001 ? PluginCastGate.Ready : PluginCastGate.NotKnown;
        public PluginCastGate EvaluateGate(uint spellId, uint targetObjectId) =>
            EvaluateGate(spellId);
        public bool Cast(uint spellId) => Cast(spellId, 0);
        public bool Cast(uint spellId, uint targetObjectId)
        {
            LastCast = (spellId, targetObjectId);
            return true;
        }

        public IReadOnlyList<PluginWorldObject> CaptureObjects() => WorldObjects;
        public bool TryGet(uint objectId, out PluginWorldObject value)
        {
            foreach (PluginWorldObject candidate in WorldObjects)
            {
                if (candidate.ObjectId == objectId)
                {
                    value = candidate;
                    return true;
                }
            }
            value = default;
            return false;
        }
        public bool TryCaptureProperties(uint objectId, out PluginItemProperties value) =>
            Properties.TryGetValue(objectId, out value);

        public PluginItemCommandResult Use(uint objectId)
        {
            UsedObject = objectId;
            return new PluginItemCommandResult(PluginItemCommandStatus.Started);
        }
        public IReadOnlyList<PluginInventoryItem> CaptureOwnedItems() => InventoryItems;
        public PluginItemCommandResult Salvage(
            uint toolObjectId,
            IReadOnlyList<uint> itemObjectIds)
        {
            LastSalvage = (toolObjectId, itemObjectIds.ToArray());
            return new PluginItemCommandResult(PluginItemCommandStatus.Started);
        }

        public PluginNavigationSnapshot Snapshot => new(
            true,
            false,
            ObjectId,
            Position,
            false,
            false);
        public bool TryGetObject(uint objectId, out PluginNavigationObject value)
        {
            if (TryGet(objectId, out PluginWorldObject obj) && obj.HasPosition)
            {
                value = new PluginNavigationObject(obj.ObjectId, obj.Name, obj.Position);
                return true;
            }
            value = default;
            return false;
        }
        public PluginNavigationCommandStatus SetMovementIntent(
            in PluginMovementIntent intent)
        {
            LastIntent = intent;
            return PluginNavigationCommandStatus.Accepted;
        }
        public PluginNavigationCommandStatus ClearMovementIntent()
        {
            ClearMovementCount++;
            return PluginNavigationCommandStatus.Accepted;
        }
        public void PostSystemMessage(string text) { }
        public IReadOnlyList<PluginChatMessage> CaptureMessages(ulong afterSequence) =>
            ChatMessages.Where(message => message.Sequence > afterSequence).ToArray();
        public bool Submit(string text)
        {
            SubmittedChat.Add(text);
            return true;
        }
    }

    private sealed class Selection : ISelectionService
    {
        public uint? SelectedObjectId { get; private set; }
        public uint? PreviousObjectId { get; private set; }
        public event Action<SelectionChangedEvent> Changed
        {
            add { }
            remove { }
        }
        public bool Select(uint objectId)
        {
            PreviousObjectId = SelectedObjectId;
            SelectedObjectId = objectId;
            return true;
        }
        public bool Clear()
        {
            PreviousObjectId = SelectedObjectId;
            SelectedObjectId = null;
            return true;
        }
    }

    private sealed class MemoryStorage : IPluginStorage
    {
        public Dictionary<string, string> Text { get; } = [];
        public bool IsAvailable => true;
        public string? ReadText(string key) =>
            Text.TryGetValue(key, out string? value) ? value : null;
        public void WriteText(string key, string content) => Text[key] = content;
        public bool Delete(string key) => Text.Remove(key);
    }

    private sealed class Logger : IPluginLogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class State : IGameState
    {
        public IReadOnlyList<WorldEntitySnapshot> Entities => [];
    }

    private sealed class Events : IEvents
    {
        public event Action<WorldEntitySnapshot> EntitySpawned
        {
            add { }
            remove { }
        }
        public event Action<double> Tick
        {
            add { }
            remove { }
        }
    }
}
