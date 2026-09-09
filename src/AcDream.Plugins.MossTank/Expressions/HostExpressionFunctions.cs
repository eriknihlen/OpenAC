using System.Globalization;
using System.Text.RegularExpressions;
using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Expressions;

internal static class HostExpressionFunctions
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly string[] GameMonthNames =
    [
        "Morningthaw", "Solclaim", "Seedsow", "Leafdawning", "Verdantine",
        "Thistledown", "HarvestGain", "Leafcull", "Frostfell", "Snowreap",
        "Coldeve", "Wintersebb",
    ];
    private static readonly string[] GameHourNames =
    [
        "Darktide", "Darktide-and-Half", "Foredawn", "Foredawn-and-Half",
        "Dawnsong", "Dawnsong-and-Half", "Morntide", "Morntide-and-Half",
        "Midsong", "Midsong-and-Half", "Warmtide", "Warmtide-and-Half",
        "Evensong", "Evensong-and-Half", "Gloaming", "Gloaming-and-Half",
    ];

    public static void Register(ExpressionFunctionRegistry registry, IPluginHost host)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(host);
        RegisterCharacter(registry, host);
        RegisterSpells(registry, host);
        RegisterObjects(registry, host);
        RegisterLoot(registry, host);
        RegisterFellowship(registry, host);
        RegisterWorldTime(registry, host);
        RegisterUi(registry, host);
        RegisterActions(registry, host);
        RegisterCombatAndMovement(registry, host);
        RegisterLogin(registry, host);
        RegisterNetwork(registry, host);
    }

    private static void RegisterUi(
        ExpressionFunctionRegistry registry,
        IPluginHost host)
    {
        registry.Register("uigetcontrol", 2, 2, (_, args) =>
        {
            string view = args[0].AsString("uigetcontrol");
            string control = args[1].AsString("uigetcontrol");
            return host.Ui.ControlExists(view, control)
                ? ExpressionValue.UiControl(new ExpressionUiControl(view, control))
                : ExpressionValue.Zero;
        }, "uigetcontrol[windowName,controlName]");
        registry.Register("uisetlabel", 2, 2, (_, args) =>
        {
            ExpressionUiControl control = args[0].AsUiControl("uisetlabel");
            return ExpressionValue.Boolean(host.Ui.SetControlLabel(
                control.View,
                control.Control,
                args[1].AsString("uisetlabel")));
        }, "uisetlabel[control,label]");
        registry.Register("uisetvisible", 2, 2, (_, args) =>
        {
            ExpressionUiControl control = args[0].AsUiControl("uisetvisible");
            return ExpressionValue.Boolean(host.Ui.SetControlVisible(
                control.View,
                control.Control,
                args[1].AsNumber("uisetvisible") >= 1d));
        }, "uisetvisible[control,visible]");
        registry.Register("uiviewexists", 1, 1, (_, args) =>
            ExpressionValue.Boolean(host.Ui.ViewExists(
                args[0].AsString("uiviewexists"))), "uiviewexists[windowName]");
        registry.Register("uiviewvisible", 1, 1, (_, args) =>
            ExpressionValue.Boolean(host.Ui.IsViewVisible(
                args[0].AsString("uiviewvisible"))), "uiviewvisible[windowName]");
    }

    private static void RegisterLoot(
        ExpressionFunctionRegistry registry,
        IPluginHost host)
    {
        registry.Register("hascorpsebeenopenedbyme", 1, 1, (_, args) =>
        {
            uint objectId = args[0].AsObjectId("hascorpsebeenopenedbyme");
            return ExpressionValue.Boolean(host.Automation.Loot
                .CaptureCorpses(float.MaxValue)
                .Any(corpse => corpse.ObjectId == objectId && corpse.HasBeenOpened));
        }, "hascorpsebeenopenedbyme[corpse]");
        registry.Register("getcorpsesunopenedbyme", 0, 0, (_, _) =>
            NumberList(host.Automation.Loot.CaptureCorpses(float.MaxValue)
                .Where(static corpse => !corpse.HasBeenOpened)
                .Select(static corpse => corpse.ObjectId)),
            "getcorpsesunopenedbyme[]");
    }

    private static void RegisterCharacter(
        ExpressionFunctionRegistry registry,
        IPluginHost host)
    {
        ICharacterInfo character = host.Automation.Character;
        registry.Register("getworldname", 0, 0, (_, _) =>
            ExpressionValue.String(character.WorldName), "getworldname[]");
        registry.Register("getaccounthash", 0, 0, (_, _) =>
            ExpressionValue.String(LegacyStringHash(character.AccountName)
                .ToString("X", CultureInfo.InvariantCulture)), "getaccounthash[]");
        registry.Register("getcharacterindex", 0, 1, (_, args) =>
        {
            string name = args.Count == 0
                ? character.Name
                : args[0].AsString("getcharacterindex");
            IReadOnlyList<PluginLoginCharacter> roster =
                host.Automation.Login.CaptureRoster();
            if (roster.Count == 0)
                return ExpressionValue.Number(character.CharacterIndex);
            for (int index = 0; index < roster.Count; index++)
            {
                if (roster[index].Name.Contains(
                    name,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return ExpressionValue.Number(index);
                }
            }
            return ExpressionValue.Number(-1d);
        }, "getcharacterindex[name?]");
        registry.Register("getplayercoordinates", 0, 0, (_, _) =>
        {
            PluginNavigationSnapshot snapshot = host.Automation.Navigation.Snapshot;
            return snapshot.IsAvailable
                ? Coordinates(snapshot.Position)
                : ExpressionValue.Zero;
        }, "getplayercoordinates[]");
        registry.Register("getplayerlandblock", 0, 0, (_, _) =>
            ExpressionValue.Number(
                host.Automation.Navigation.Snapshot.Position.CellId >> 16),
            "getplayerlandblock[]");
        registry.Register("getplayerlandcell", 0, 0, (_, _) =>
            ExpressionValue.Number(
                host.Automation.Navigation.Snapshot.Position.CellId),
            "getplayerlandcell[]");
        registry.Register("isportaling", 0, 0, (_, _) =>
            ExpressionValue.Boolean(
                !character.IsInWorld
                || host.Automation.Navigation.Snapshot.IsPortalSpace),
            "isportaling[]");
        registry.Register("getcharburden", 0, 0, (_, _) =>
        {
            if (!TryPlayerProperties(host, out PluginItemProperties properties))
                return ExpressionValue.Zero;
            double burden = Get(properties.Ints, 5u);
            double capacity = Get(properties.Ints, 96u);
            return ExpressionValue.Number(capacity > 0d
                ? Math.Round(burden * 100d / capacity)
                : 0d);
        }, "getcharburden[]");
        registry.Register("getcharintprop", 1, 1, (_, args) =>
            ExpressionValue.Number(PlayerProperty(host, args[0], PropertyKind.Int)),
            "getcharintprop[property]");
        registry.Register("getchardoubleprop", 1, 1, (_, args) =>
            ExpressionValue.Number(PlayerProperty(host, args[0], PropertyKind.Double)),
            "getchardoubleprop[property]");
        registry.Register("getcharquadprop", 1, 1, (_, args) =>
            ExpressionValue.Number(PlayerProperty(host, args[0], PropertyKind.Int64)),
            "getcharquadprop[property]");
        registry.Register("getcharboolprop", 1, 1, (_, args) =>
            ExpressionValue.Boolean(PlayerProperty(host, args[0], PropertyKind.Bool) != 0d),
            "getcharboolprop[property]");
        registry.Register("getcharstringprop", 1, 1, (_, args) =>
        {
            if (!TryPlayerProperties(host, out PluginItemProperties properties))
                return ExpressionValue.Zero;
            uint key = ToUInt(args[0], "getcharstringprop");
            return properties.Strings.TryGetValue(key, out string? value)
                ? ExpressionValue.String(value)
                : ExpressionValue.Zero;
        }, "getcharstringprop[property]");

        registry.Register("getcharattribute_base", 1, 1, (_, args) =>
            ExpressionValue.Number(Attribute(character, args[0], buffed: false)),
            "getcharattribute_base[attributeId]");
        registry.Register("getcharattribute_buffed", 1, 1, (_, args) =>
            ExpressionValue.Number(Attribute(character, args[0], buffed: true)),
            "getcharattribute_buffed[attributeId]");
        registry.Register("getcharskill_base", 1, 1, (_, args) =>
            ExpressionValue.Number(Skill(character, args[0], SkillRead.Base)),
            "getcharskill_base[skillId]");
        registry.Register("getcharskill_buffed", 1, 1, (_, args) =>
            ExpressionValue.Number(Skill(character, args[0], SkillRead.Buffed)),
            "getcharskill_buffed[skillId]");
        registry.Register("getcharskill_traininglevel", 1, 1, (_, args) =>
            ExpressionValue.Number(Skill(character, args[0], SkillRead.Training)),
            "getcharskill_traininglevel[skillId]");
        registry.Register("getcharvital_base", 1, 1, (_, args) =>
            ExpressionValue.Number(Vital(character, args[0], VitalRead.Maximum)),
            "getcharvital_base[vitalId]");
        registry.Register("getcharvital_buffedmax", 1, 1, (_, args) =>
            ExpressionValue.Number(Vital(character, args[0], VitalRead.Maximum)),
            "getcharvital_buffedmax[vitalId]");
        registry.Register("getcharvital_current", 1, 1, (_, args) =>
            ExpressionValue.Number(Vital(character, args[0], VitalRead.Current)),
            "getcharvital_current[vitalId]");
        registry.Register("vitae", 0, 0, (_, _) =>
        {
            if (!TryPlayerProperties(host, out PluginItemProperties properties))
                return ExpressionValue.Number(100d);
            // PropertyFloat.Vitae (129) is the multiplier 0..1.
            double value = Get(properties.Floats, 129u, 1d);
            return ExpressionValue.Number(value * 100d);
        }, "vitae[]");
    }

    private static void RegisterLogin(
        ExpressionFunctionRegistry registry,
        IPluginHost host)
    {
        registry.Register("setnextlogin", 1, 1, (_, args) =>
        {
            ILoginAutomation login = host.Automation.Login;
            IReadOnlyList<PluginLoginCharacter> roster = login.CaptureRoster();
            if (!login.IsAvailable || roster.Count == 0)
                return ExpressionValue.Zero;

            string selector = args[0].ToDisplayString();
            PluginLoginCharacter selected = default;
            if (int.TryParse(
                    selector,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int relative))
            {
                int current = -1;
                for (int index = 0; index < roster.Count; index++)
                {
                    if (roster[index].Name.Equals(
                        host.Automation.Character.Name,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        current = index;
                        break;
                    }
                }
                if (current < 0)
                    return ExpressionValue.Zero;
                int target = ((current + relative) % roster.Count + roster.Count)
                    % roster.Count;
                selected = roster[target];
            }
            else
            {
                foreach (PluginLoginCharacter candidate in roster)
                {
                    if (candidate.Name.Contains(
                        selector,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        selected = candidate;
                        break;
                    }
                }
            }
            return ExpressionValue.Boolean(
                selected.ObjectId != 0u
                && !selected.IsPendingDelete
                && login.SetNextLogin(selected.ObjectId));
        }, "setnextlogin[nameOrRelativeIndex]");
        registry.Register("clearnextlogin", 0, 0, (_, _) =>
            ExpressionValue.Boolean(host.Automation.Login.ClearNextLogin()),
            "clearnextlogin[]");
    }

    private static void RegisterNetwork(
        ExpressionFunctionRegistry registry,
        IPluginHost host)
    {
        registry.Register("netclients", 0, 1, (_, args) =>
        {
            string? tag = args.Count == 0
                ? null
                : args[0].AsString("netclients");
            var clients = new ExpressionList();
            foreach (PluginNetworkClient client in
                host.Automation.Network.CaptureClients())
            {
                if (!string.IsNullOrEmpty(tag)
                    && !client.Tags.Any(candidate => candidate.Equals(
                        tag,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var tags = new ExpressionList();
                foreach (string candidate in client.Tags)
                    tags.Items.Add(ExpressionValue.String(candidate));
                var data = new ExpressionDictionary();
                data.Items["ClientId"] = ExpressionValue.Number(client.ClientId);
                data.Items["PlayerId"] = ExpressionValue.Number(client.PlayerId);
                data.Items["Position"] = Coordinates(client.Position);
                data.Items["Name"] = ExpressionValue.String(client.Name);
                data.Items["Tags"] = ExpressionValue.List(tags);
                data.Items["WorldName"] = ExpressionValue.String(client.WorldName);
                data.Items["CurrentHealth"] =
                    ExpressionValue.Number(client.CurrentHealth);
                data.Items["CurrentMana"] =
                    ExpressionValue.Number(client.CurrentMana);
                data.Items["CurrentStamina"] =
                    ExpressionValue.Number(client.CurrentStamina);
                data.Items["MaxHealth"] = ExpressionValue.Number(client.MaxHealth);
                data.Items["MaxMana"] = ExpressionValue.Number(client.MaxMana);
                data.Items["MaxStamina"] =
                    ExpressionValue.Number(client.MaxStamina);
                data.Items["Heading"] = ExpressionValue.Number(client.Heading);
                clients.Items.Add(ExpressionValue.Dictionary(data));
            }
            return ExpressionValue.List(clients);
        }, "netclients[tag?]");
    }

    private static void RegisterSpells(
        ExpressionFunctionRegistry registry,
        IPluginHost host)
    {
        ISpellCatalog spells = host.Automation.Spells;
        ICharacterInfo character = host.Automation.Character;
        registry.Register("componentname", 1, 1, (_, args) =>
            spells.TryGetComponent(
                ToUInt(args[0], "componentname"),
                out PluginSpellComponentInfo component)
                    ? ExpressionValue.String(component.Name)
                    : ExpressionValue.Zero,
            "componentname[componentid]");
        registry.Register("componentdata", 1, 1, (_, args) =>
        {
            if (!spells.TryGetComponent(
                    ToUInt(args[0], "componentdata"),
                    out PluginSpellComponentInfo component))
            {
                return ExpressionValue.Dictionary(new ExpressionDictionary());
            }
            var data = new ExpressionDictionary();
            data.Items["BurnRate"] = ExpressionValue.Number(component.BurnRate);
            data.Items["GestureId"] = ExpressionValue.Number(component.GestureId);
            data.Items["GestureSpeed"] = ExpressionValue.Number(component.GestureSpeed);
            data.Items["IconId"] = ExpressionValue.Number(component.IconId);
            data.Items["Id"] = ExpressionValue.Number(component.ComponentId);
            data.Items["Name"] = ExpressionValue.String(component.Name);
            data.Items["SortKey"] = ExpressionValue.Number(component.SortKey);
            data.Items["Type"] = ExpressionValue.String(component.Type);
            data.Items["Word"] = ExpressionValue.String(component.Word);
            return ExpressionValue.Dictionary(data);
        }, "componentdata[componentid]");
        registry.Register("getisspellknown", 1, 1, (_, args) =>
            ExpressionValue.Boolean(spells.IsKnown(ToUInt(args[0], "getisspellknown"))),
            "getisspellknown[spellId]");
        registry.Register("getknownspells", 0, 0, (_, _) =>
        {
            IEnumerable<uint> ids = spells.KnownSelfBuffs
                .Concat(spells.KnownCombatSpells)
                .Select(static spell => spell.SpellId)
                .Distinct()
                .Order();
            return NumberList(ids);
        }, "getknownspells[]");
        registry.Register("spellname", 1, 1, (_, args) =>
            spells.TryGet(ToUInt(args[0], "spellname"), out PluginSpellInfo spell)
                ? ExpressionValue.String(spell.Name)
                : ExpressionValue.Zero, "spellname[spellId]");
        registry.Register("spelldata", 2, 2, (_, args) =>
        {
            if (!spells.TryGet(ToUInt(args[0], "spelldata"), out PluginSpellInfo spell))
                return ExpressionValue.Zero;
            return SpellProperty(spell, args[1].ToDisplayString());
        }, "spelldata[spellId,property]");
        registry.Register("getspellexpiration", 1, 1, (_, args) =>
            ExpressionValue.Number(SpellExpiration(
                character.ActiveEnchantments,
                ToUInt(args[0], "getspellexpiration"))),
            "getspellexpiration[spellId]");
        registry.Register("getspellexpirationbyname", 1, 1, (_, args) =>
        {
            string name = args[0].AsString("getspellexpirationbyname");
            PluginSpellInfo? match = spells.KnownSelfBuffs
                .Concat(spells.KnownCombatSpells)
                .FirstOrDefault(spell => spell.Name.Equals(
                    name,
                    StringComparison.OrdinalIgnoreCase));
            return ExpressionValue.Number(match is { SpellId: > 0u } spell
                ? SpellExpiration(character.ActiveEnchantments, spell.SpellId)
                : 0d);
        }, "getspellexpirationbyname[name]");
        registry.Register("getcooldownexpiration", 1, 1, (_, args) =>
            ExpressionValue.Number(spells.GetCooldownRemaining(
                ToUInt(args[0], "getcooldownexpiration"))),
            "getcooldownexpiration[cooldownId]");
        registry.Register("getcancastspell_buff", 1, 1, (_, args) =>
            ExpressionValue.Boolean(host.Automation.Magic.EvaluateGate(
                ToUInt(args[0], "getcancastspell_buff")) == PluginCastGate.Ready),
            "getcancastspell_buff[spellId]");
        registry.Register("getcancastspell_hunt", 1, 1, (_, args) =>
            ExpressionValue.Boolean(host.Automation.Magic.EvaluateGate(
                ToUInt(args[0], "getcancastspell_hunt")) == PluginCastGate.Ready),
            "getcancastspell_hunt[spellId]");
    }

    private static void RegisterObjects(
        ExpressionFunctionRegistry registry,
        IPluginHost host)
    {
        IWorldObjectAutomation objects = host.Automation.Objects;
        registry.Register("wobjectfindbyid", 1, 1, (context, args) =>
        {
            uint id = ToUInt(args[0], "wobjectfindbyid");
            return objects.TryGet(id, out _)
                ? ExpressionValue.WorldObject(id)
                : ExpressionValue.Zero;
        }, "wobjectfindbyid[id]");
        registry.Register("wobjectgetplayer", 0, 0, (_, _) =>
            ExpressionValue.WorldObject(host.Automation.Character.ObjectId),
            "wobjectgetplayer[]");
        registry.Register("wobjectgetselection", 0, 0, (_, _) =>
            host.Selection.SelectedObjectId is uint id
                ? ExpressionValue.WorldObject(id)
                : ExpressionValue.Zero, "wobjectgetselection[]");
        registry.Register("wobjectgetopencontainer", 0, 0, (_, _) =>
            objects.OpenContainerObjectId != 0u
                ? ExpressionValue.WorldObject(objects.OpenContainerObjectId)
                : ExpressionValue.Zero, "wobjectgetopencontainer[]");
        registry.Register("wobjectgetid", 1, 1, (_, args) =>
            ExpressionValue.Number(args[0].AsObjectId("wobjectgetid")),
            "wobjectgetid[object]");
        registry.Register("wobjectgetname", 1, 1, (_, args) =>
            TryObject(objects, args[0], "wobjectgetname", out PluginWorldObject obj)
                ? ExpressionValue.String(obj.Name)
                : ExpressionValue.Zero, "wobjectgetname[object]");
        registry.Register("wobjectgetobjectclass", 1, 1, (_, args) =>
            TryObject(objects, args[0], "wobjectgetobjectclass", out PluginWorldObject obj)
                ? ExpressionValue.Number((int)obj.ObjectClass)
                : ExpressionValue.Zero, "wobjectgetobjectclass[object]");
        registry.Register("wobjectgettemplatetype", 1, 1, (_, args) =>
            TryObject(objects, args[0], "wobjectgettemplatetype", out PluginWorldObject obj)
                ? ExpressionValue.Number(obj.WeenieClassId)
                : ExpressionValue.Zero, "wobjectgettemplatetype[object]");
        registry.Register("wobjectgetinternaltype", 1, 1, (_, args) =>
            TryObject(objects, args[0], "wobjectgetinternaltype", out PluginWorldObject obj)
                ? ExpressionValue.Number(obj.ItemType)
                : ExpressionValue.Zero, "wobjectgetinternaltype[object]");
        registry.Alias("getobjectinternaltype", "wobjectgetinternaltype");
        registry.Register("wobjecthasdata", 1, 1, (_, args) =>
            ExpressionValue.Boolean(
                TryObject(objects, args[0], "wobjecthasdata", out PluginWorldObject obj)
                && obj.HasAppraisalData), "wobjecthasdata[object]");
        registry.Register("wobjectlastidtime", 1, 1, (_, args) =>
            TryObject(objects, args[0], "wobjectlastidtime", out PluginWorldObject obj)
                ? ExpressionValue.Number(obj.LastIdTime)
                : ExpressionValue.Zero,
            "wobjectlastidtime[object]");
        registry.Register("wobjectisvalid", 1, 1, (_, args) =>
            ExpressionValue.Boolean(TryObject(
                objects,
                args[0],
                "wobjectisvalid",
                out PluginWorldObject obj) && obj.HasPosition),
            "wobjectisvalid[object]");
        registry.Register("wobjectrequestdata", 1, 1, (_, args) =>
            ExpressionValue.Boolean(objects.Identify(
                args[0].AsObjectId("wobjectrequestdata")).Accepted),
            "wobjectrequestdata[object]");
        registry.Register("wobjectgetisdooropen", 1, 1, (_, args) =>
            ExpressionValue.Boolean(
                TryObject(objects, args[0], "wobjectgetisdooropen", out PluginWorldObject obj)
                && obj.IsDoorOpen), "wobjectgetisdooropen[object]");
        registry.Register("wobjectgetphysicscoordinates", 1, 1, (_, args) =>
            TryObject(objects, args[0], "wobjectgetphysicscoordinates", out PluginWorldObject obj)
                && obj.HasPosition
                    ? Coordinates(obj.Position)
                    : ExpressionValue.Zero,
            "wobjectgetphysicscoordinates[object]");
        registry.Register("getheading", 1, 1, (_, args) =>
            TryObject(objects, args[0], "getheading", out PluginWorldObject obj)
                && obj.HasPosition
                    ? ExpressionValue.Number(NormalizeHeading(obj.Position.HeadingDegrees))
                    : ExpressionValue.Zero, "getheading[object]");
        registry.Register("getheadingto", 1, 1, (_, args) =>
        {
            PluginNavigationSnapshot player = host.Automation.Navigation.Snapshot;
            return player.IsAvailable
                && TryObject(objects, args[0], "getheadingto", out PluginWorldObject obj)
                && obj.HasPosition
                    ? ExpressionValue.Number(HeadingTo(player.Position, obj.Position))
                    : ExpressionValue.Zero;
        }, "getheadingto[object]");

        RegisterObjectProperty(registry, host, "wobjectgetintprop", PropertyKind.Int);
        RegisterObjectProperty(registry, host, "wobjectgetdoubleprop", PropertyKind.Double);
        RegisterObjectProperty(registry, host, "wobjectgetboolprop", PropertyKind.Bool);
        RegisterObjectProperty(registry, host, "wobjectgetstringprop", PropertyKind.String);
        registry.Register("wobjectgetspellids", 1, 1, (_, args) =>
            TryObject(objects, args[0], "wobjectgetspellids", out PluginWorldObject obj)
                ? NumberList(obj.SpellIds)
                : ExpressionValue.List(new ExpressionList()),
            "wobjectgetspellids[object]");
        registry.Register("wobjectgetactivespellids", 1, 1, (_, args) =>
            TryObject(objects, args[0], "wobjectgetactivespellids", out PluginWorldObject obj)
                ? NumberList(obj.ActiveSpellIds)
                : ExpressionValue.List(new ExpressionList()),
            "wobjectgetactivespellids[object]");

        RegisterObjectFinders(registry, host);
        RegisterInventoryCounts(registry, host);
        RegisterObjectVitals(registry, host);
    }

    private static void RegisterObjectFinders(
        ExpressionFunctionRegistry registry,
        IPluginHost host)
    {
        registry.Register("wobjectfindall", 0, 0, (_, _) =>
            ObjectList(host.Automation.Objects.CaptureObjects()), "wobjectfindall[]");
        RegisterFinder(registry, host, "wobjectfindallbyobjectclass", ObjectSet.All,
            (obj, arg) => (int)obj.ObjectClass == arg.AsInt32());
        RegisterFinder(registry, host, "wobjectfindallbytemplatetype", ObjectSet.All,
            (obj, arg) => obj.WeenieClassId == ToUInt(arg, "template type"));
        RegisterRegexFinder(registry, host, "wobjectfindallbynamerx", ObjectSet.All);
        RegisterFinder(registry, host, "wobjectfindallinventorybyobjectclass", ObjectSet.Inventory,
            (obj, arg) => (int)obj.ObjectClass == arg.AsInt32());
        RegisterFinder(registry, host, "wobjectfindallinventorybytemplatetype", ObjectSet.Inventory,
            (obj, arg) => obj.WeenieClassId == ToUInt(arg, "template type"));
        RegisterRegexFinder(registry, host, "wobjectfindallinventorybynamerx", ObjectSet.Inventory);
        RegisterFinder(registry, host, "wobjectfindalllandscapebyobjectclass", ObjectSet.Landscape,
            (obj, arg) => (int)obj.ObjectClass == arg.AsInt32());
        RegisterFinder(registry, host, "wobjectfindalllandscapebytemplatetype", ObjectSet.Landscape,
            (obj, arg) => obj.WeenieClassId == ToUInt(arg, "template type"));
        RegisterRegexFinder(registry, host, "wobjectfindalllandscapebynamerx", ObjectSet.Landscape);
        registry.Register("wobjectfindallinventory", 0, 0, (_, _) => ObjectList(
            FilterSet(host.Automation.Objects.CaptureObjects(), ObjectSet.Inventory)),
            "wobjectfindallinventory[]");
        registry.Register("wobjectfindalllandscape", 0, 0, (_, _) => ObjectList(
            FilterSet(host.Automation.Objects.CaptureObjects(), ObjectSet.Landscape)),
            "wobjectfindalllandscape[]");
        registry.Register("wobjectfindallbycontainer", 1, 1, (_, args) =>
        {
            uint container = args[0].AsObjectId("wobjectfindallbycontainer");
            return ObjectList(host.Automation.Objects.CaptureObjects().Where(
                obj => obj.ContainerObjectId == container));
        }, "wobjectfindallbycontainer[container]");
        registry.Register("wobjectfindininventorybyname", 1, 1, (_, args) =>
            FirstObject(host, ObjectSet.Inventory, obj => obj.Name.Equals(
                args[0].AsString("wobjectfindininventorybyname"),
                StringComparison.OrdinalIgnoreCase)),
            "wobjectfindininventorybyname[name]");
        registry.Register("wobjectfindininventorybynamerx", 1, 1, (_, args) =>
        {
            Regex regex = CreateRegex(args[0].AsString("wobjectfindininventorybynamerx"));
            return FirstObject(host, ObjectSet.Inventory, obj => regex.IsMatch(obj.Name));
        }, "wobjectfindininventorybynamerx[pattern]");
        registry.Register("wobjectfindininventorybytemplatetype", 1, 1, (_, args) =>
            FirstObject(host, ObjectSet.Inventory, obj =>
                obj.WeenieClassId == ToUInt(args[0], "template type")),
            "wobjectfindininventorybytemplatetype[templateType]");

        RegisterNearest(registry, host, "wobjectfindnearestbyobjectclass",
            (obj, args) => (int)obj.ObjectClass == args[0].AsInt32());
        RegisterNearest(registry, host, "wobjectfindnearestbytemplatetype",
            (obj, args) => obj.WeenieClassId == ToUInt(args[0], "template type"));
        RegisterNearest(registry, host, "wobjectfindnearestbynameandobjectclass",
            (obj, args) => obj.Name.Equals(args[0].AsString(), StringComparison.OrdinalIgnoreCase)
                && (int)obj.ObjectClass == args[1].AsInt32(), argumentCount: 2);
        RegisterNearest(registry, host, "wobjectfindnearestdoor",
            (obj, _) => obj.ObjectClass == PluginObjectClass.Door, argumentCount: 0);
        RegisterNearest(registry, host, "wobjectfindnearestmonster",
            (obj, _) => obj.ObjectClass == PluginObjectClass.Monster, argumentCount: 0);
    }

    private static void RegisterInventoryCounts(
        ExpressionFunctionRegistry registry,
        IPluginHost host)
    {
        registry.Register("getitemcountininventorybyname", 1, 1, (_, args) =>
        {
            string name = args[0].AsString("getitemcountininventorybyname");
            return ExpressionValue.Number(host.Automation.Objects.CaptureObjects()
                .Where(obj => obj.IsOwned && obj.Name.Equals(
                    name,
                    StringComparison.OrdinalIgnoreCase))
                .Sum(static obj => Math.Max(1, obj.StackSize)));
        }, "getitemcountininventorybyname[name]");
        registry.Register("getitemcountininventorybynamerx", 1, 1, (_, args) =>
        {
            Regex regex = CreateRegex(args[0].AsString("getitemcountininventorybynamerx"));
            return ExpressionValue.Number(host.Automation.Objects.CaptureObjects()
                .Where(obj => obj.IsOwned && regex.IsMatch(obj.Name))
                .Sum(static obj => Math.Max(1, obj.StackSize)));
        }, "getitemcountininventorybynamerx[pattern]");
        registry.Register("getinventorycountbytemplatetype", 1, 1, (_, args) =>
        {
            uint template = ToUInt(args[0], "getinventorycountbytemplatetype");
            return ExpressionValue.Number(host.Automation.Objects.CaptureObjects()
                .Where(obj => obj.IsOwned && obj.WeenieClassId == template)
                .Sum(static obj => Math.Max(1, obj.StackSize)));
        }, "getinventorycountbytemplatetype[templateType]");
        registry.Register("getcontaineritemcount", 0, 1, (_, args) =>
        {
            uint container = args.Count == 0
                ? host.Automation.Character.ObjectId
                : args[0].AsObjectId("getcontaineritemcount");
            if (!host.Automation.Objects.TryGet(container, out PluginWorldObject obj)
                || obj.ObjectClass is not (PluginObjectClass.Container or PluginObjectClass.Player))
            {
                return ExpressionValue.Number(-1d);
            }
            return ExpressionValue.Number(host.Automation.Objects.CaptureObjects().Count(
                item => item.ContainerObjectId == container));
        }, "getcontaineritemcount[container?]");
        registry.Register("getfreeitemslots", 0, 1, (_, args) =>
            ExpressionValue.Number(FreeSlots(host, args, containers: false)),
            "getfreeitemslots[container?]");
        registry.Register("getfreecontainerslots", 0, 1, (_, args) =>
            ExpressionValue.Number(FreeSlots(host, args, containers: true)),
            "getfreecontainerslots[container?]");
    }

    private static void RegisterObjectVitals(
        ExpressionFunctionRegistry registry,
        IPluginHost host)
    {
        registry.Register("wobjectgethealth", 1, 1, (_, args) =>
            ExpressionValue.Number(ObjectVital(host, args[0], VitalObjectRead.Fraction)),
            "wobjectgethealth[object]");
        registry.Register("wobjectgethealthvalue", 1, 1, (_, args) =>
            ExpressionValue.Number(ObjectVital(host, args[0], VitalObjectRead.Health)),
            "wobjectgethealthvalue[object]");
        registry.Register("wobjectgetstaminavalue", 1, 1, (_, args) =>
            ExpressionValue.Number(ObjectVital(host, args[0], VitalObjectRead.Stamina)),
            "wobjectgetstaminavalue[object]");
        registry.Register("wobjectgetmanavalue", 1, 1, (_, args) =>
            ExpressionValue.Number(ObjectVital(host, args[0], VitalObjectRead.Mana)),
            "wobjectgetmanavalue[object]");
    }

    private static void RegisterActions(
        ExpressionFunctionRegistry registry,
        IPluginHost host)
    {
        registry.Register("echo", 1, 1, (_, args) =>
        {
            host.Automation.Chat.PostSystemMessage(args[0].ToDisplayString());
            return args[0];
        }, "echo[text]");
        registry.Register("chatbox", 1, 1, (_, args) => ExpressionValue.Boolean(
            host.Automation.Chat.Submit(args[0].ToDisplayString())), "chatbox[text]");
        registry.Register("chatboxpaste", 1, 1, (_, args) => ExpressionValue.Boolean(
            host.Automation.Chat.Submit(args[0].ToDisplayString())), "chatboxpaste[text]");
        registry.Register("actiontryselect", 1, 1, (_, args) => ExpressionValue.Boolean(
            host.Selection.Select(args[0].AsObjectId("actiontryselect"))),
            "actiontryselect[object]");
        registry.Register("actiontryuseitem", 1, 1, (_, args) => ExpressionValue.Boolean(
            host.Automation.Items.Use(args[0].AsObjectId("actiontryuseitem")).Accepted),
            "actiontryuseitem[object]");
        registry.Register("actiontryapplyitem", 2, 2, (_, args) => ExpressionValue.Boolean(
            host.Automation.Items.Apply(
                args[0].AsObjectId("actiontryapplyitem"),
                args[1].AsObjectId("actiontryapplyitem")).Accepted),
            "actiontryapplyitem[source,target]");
        registry.Register("actiontrygiveitem", 2, 3, (_, args) => ExpressionValue.Boolean(
            host.Automation.Items.Give(
                args[0].AsObjectId("actiontrygiveitem"),
                args[1].AsObjectId("actiontrygiveitem"),
                args.Count == 3 ? ToUInt(args[2], "actiontrygiveitem") : 0u).Accepted),
            "actiontrygiveitem[item,target,amount?]");
        registry.Register("actiontrydrop", 1, 2, (_, args) => ExpressionValue.Boolean(
            host.Automation.Items.Drop(
                args[0].AsObjectId("actiontrydrop"),
                args.Count == 2 ? ToUInt(args[1], "actiontrydrop") : 0u).Accepted),
            "actiontrydrop[item,amount?]");
        registry.Register("actiontrymove", 2, 4, (_, args) => ExpressionValue.Boolean(
            host.Automation.Items.MoveToContainer(
                args[0].AsObjectId("actiontrymove"),
                args[1].AsObjectId("actiontrymove"),
                0u,
                args.Count >= 3 ? args[2].AsInt32("actiontrymove") : 0).Accepted),
            "actiontrymove[item,destination,slot?,addToStack?]");
        registry.Register("actiontrysplit", 2, 3, (_, args) =>
        {
            uint destination = args.Count == 3
                ? args[2].AsObjectId("actiontrysplit")
                : host.Automation.Character.ObjectId;
            return ExpressionValue.Boolean(host.Automation.Items.MoveToContainer(
                args[0].AsObjectId("actiontrysplit"),
                destination,
                ToUInt(args[1], "actiontrysplit")).Accepted);
        }, "actiontrysplit[item,newStackSize,destination?]");
        registry.Register("actiontrycastbyid", 1, 1, (_, args) => CastResult(
            host.Automation.Magic,
            ToUInt(args[0], "actiontrycastbyid"),
            target: null), "actiontrycastbyid[spellId]");
        registry.Register("actiontrycastbyidontarget", 2, 2, (_, args) => CastResult(
            host.Automation.Magic,
            ToUInt(args[0], "actiontrycastbyidontarget"),
            args[1].AsObjectId("actiontrycastbyidontarget")),
            "actiontrycastbyidontarget[spellId,target]");
        registry.Register("actiontryequipanywand", 0, 0, (_, _) =>
        {
            PluginEquipmentItem? wand = host.Automation.Equipment
                .CaptureOwnedEquipment()
                .FirstOrDefault(static item =>
                    (item.ValidLocations & 0x01000000u) != 0u);
            return wand is { ObjectId: > 0u } item
                ? ExpressionValue.Boolean(item.IsEquipped
                    || host.Automation.Equipment.Equip(item.ObjectId).Accepted)
                : ExpressionValue.Zero;
        }, "actiontryequipanywand[]");
    }

    private static void RegisterFellowship(
        ExpressionFunctionRegistry registry,
        IPluginHost host)
    {
        IFellowshipAutomation fellowship = host.Automation.Fellowship;
        registry.Register("getfellowshipstatus", 0, 0, (_, _) =>
            ExpressionValue.Boolean(fellowship.IsInFellowship),
            "getfellowshipstatus[]");
        registry.Register("getfellowshipname", 0, 0, (_, _) =>
            ExpressionValue.String(fellowship.Name), "getfellowshipname[]");
        registry.Register("getfellowshipcount", 0, 0, (_, _) =>
            ExpressionValue.Number(fellowship.MemberCount), "getfellowshipcount[]");
        registry.Register("getfellowshipleaderid", 0, 0, (_, _) =>
            ExpressionValue.Number(fellowship.LeaderObjectId),
            "getfellowshipleaderid[]");
        registry.Register("getfellowid", 1, 1, (_, args) =>
        {
            IReadOnlyList<PluginFellowMember> roster = fellowship.CaptureRoster();
            int index = args[0].AsInt32("getfellowid");
            return (uint)index < (uint)roster.Count
                ? ExpressionValue.Number(roster[index].ObjectId)
                : ExpressionValue.Zero;
        }, "getfellowid[index]");
        registry.Register("getfellowname", 1, 1, (_, args) =>
        {
            IReadOnlyList<PluginFellowMember> roster = fellowship.CaptureRoster();
            int index = args[0].AsInt32("getfellowname");
            return (uint)index < (uint)roster.Count
                ? ExpressionValue.String(roster[index].Name)
                : ExpressionValue.String(string.Empty);
        }, "getfellowname[index]");
        registry.Register("getfellowshiplocked", 0, 0, (_, _) =>
            ExpressionValue.Boolean(fellowship.IsLocked), "getfellowshiplocked[]");
        registry.Register("getfellowshipisleader", 0, 0, (_, _) =>
            ExpressionValue.Boolean(
                fellowship.IsInFellowship
                && fellowship.LeaderObjectId == host.Automation.Character.ObjectId),
            "getfellowshipisleader[]");
        registry.Register("getfellowshipisopen", 0, 0, (_, _) =>
            ExpressionValue.Boolean(fellowship.IsOpen), "getfellowshipisopen[]");
        registry.Register("getfellowshipisfull", 0, 0, (_, _) =>
            ExpressionValue.Boolean(
                fellowship.IsInFellowship && fellowship.MemberCount == 9),
            "getfellowshipisfull[]");
        registry.Register("getfellowshipcanrecruit", 0, 0, (_, _) =>
            ExpressionValue.Boolean(
                fellowship.IsInFellowship
                && (fellowship.IsOpen
                    || fellowship.LeaderObjectId == host.Automation.Character.ObjectId)
                && fellowship.MemberCount < 9),
            "getfellowshipcanrecruit[]");
        registry.Register("getfellownames", 0, 0, (_, _) => ExpressionValue.List(
            new ExpressionList(fellowship.CaptureRoster().Select(
                static member => ExpressionValue.String(member.Name)))),
            "getfellownames[]");
        registry.Register("getfellowids", 0, 0, (_, _) => ExpressionValue.List(
            new ExpressionList(fellowship.CaptureRoster().Select(
                static member => ExpressionValue.Number(member.ObjectId)))),
            "getfellowids[]");
    }

    private static void RegisterWorldTime(
        ExpressionFunctionRegistry registry,
        IPluginHost host)
    {
        PluginWorldTimeSnapshot Snapshot() => host.Automation.WorldTime.Snapshot;
        registry.Register("getgameyear", 0, 0, (_, _) =>
            ExpressionValue.Number(Snapshot().Year), "getgameyear[]");
        registry.Register("getgamemonth", 0, 0, (_, _) =>
            ExpressionValue.Number(Snapshot().Month), "getgamemonth[]");
        registry.Register("getgamemonthname", 1, 1, (_, args) =>
        {
            int index = args[0].AsInt32("getgamemonthname");
            return (uint)index < (uint)GameMonthNames.Length
                ? ExpressionValue.String(GameMonthNames[index])
                : ExpressionValue.String(string.Empty);
        }, "getgamemonthname[monthIndex]");
        registry.Register("getgameday", 0, 0, (_, _) =>
            ExpressionValue.Number(Snapshot().Day), "getgameday[]");
        registry.Register("getgamehour", 0, 0, (_, _) =>
            ExpressionValue.Number(Snapshot().Hour), "getgamehour[]");
        registry.Register("getgamehourname", 1, 1, (_, args) =>
        {
            int index = args[0].AsInt32("getgamehourname");
            return (uint)index < (uint)GameHourNames.Length
                ? ExpressionValue.String(GameHourNames[index])
                : ExpressionValue.String(string.Empty);
        }, "getgamehourname[hourIndex]");
        registry.Register("getminutesuntilday", 0, 0, (_, _) =>
            ExpressionValue.Number(Snapshot().MinutesUntilDay),
            "getminutesuntilday[]");
        registry.Register("getminutesuntilnight", 0, 0, (_, _) =>
            ExpressionValue.Number(Snapshot().MinutesUntilNight),
            "getminutesuntilnight[]");
        registry.Register("getgameticks", 0, 0, (_, _) =>
            ExpressionValue.Number(Snapshot().GameTicks), "getgameticks[]");
        registry.Register("getisday", 0, 0, (_, _) =>
            ExpressionValue.Boolean(Snapshot().IsDay), "getisday[]");
        registry.Register("getisnight", 0, 0, (_, _) =>
            ExpressionValue.Boolean(!Snapshot().IsDay), "getisnight[]");
    }

    private static void RegisterCombatAndMovement(
        ExpressionFunctionRegistry registry,
        IPluginHost host)
    {
        registry.Register("getcombatstate", 0, 0, (_, _) => ExpressionValue.String(
            host.Automation.Combat.Snapshot.Mode.ToString()), "getcombatstate[]");
        registry.Register("setcombatstate", 1, 1, (_, args) =>
        {
            if (!Enum.TryParse(
                args[0].AsString("setcombatstate"),
                ignoreCase: true,
                out PluginCombatMode mode)
                || mode == PluginCombatMode.Unknown)
            {
                return ExpressionValue.Zero;
            }
            return ExpressionValue.Boolean(
                host.Automation.Combat.EnterMode(mode).Accepted);
        }, "setcombatstate[state]");
        registry.Register("getbusystate", 0, 0, (_, _) => ExpressionValue.Number(
            host.Automation.Items.IsBusy
            || host.Automation.Equipment.IsBusy
            || host.Automation.Magic.IsCasting ? 1d : 0d), "getbusystate[]");
        registry.Register("getequippedweapontype", 0, 0, (_, _) =>
        {
            foreach (PluginEquipmentItem item in host.Automation.Equipment
                .CaptureOwnedEquipment().Where(static item => item.IsEquipped))
            {
                if ((item.EquippedLocation & 0x00400000u) != 0u)
                    return ExpressionValue.String("Missile");
                if ((item.EquippedLocation & 0x01000000u) != 0u)
                    return ExpressionValue.String("Wand");
                if ((item.EquippedLocation & 0x00100000u) != 0u)
                    return ExpressionValue.String("Melee");
            }
            return ExpressionValue.String("None");
        }, "getequippedweapontype[]");
        registry.Register("setmotion", 2, 2, (_, args) =>
        {
            string motion = args[0].AsString("setmotion");
            bool enabled = args[1].AsNumber("setmotion") != 0d;
            PluginNavigationSnapshot snapshot = host.Automation.Navigation.Snapshot;
            PluginMovementIntent intent = enabled
                ? MotionIntent(motion)
                : default;
            PluginNavigationCommandStatus result = enabled
                ? host.Automation.Navigation.SetMovementIntent(intent)
                : host.Automation.Navigation.ClearMovementIntent();
            return ExpressionValue.Boolean(result == PluginNavigationCommandStatus.Accepted);
        }, "setmotion[motion,state]");
        registry.Register("getmotion", 1, 1, (_, args) =>
        {
            string motion = args[0].AsString("getmotion");
            PluginNavigationSnapshot snapshot = host.Automation.Navigation.Snapshot;
            return ExpressionValue.Number(snapshot.IsMoving
                && motion is not null ? 2d : 0d);
        }, "getmotion[motion]");
        registry.Register("clearmotion", 0, 0, (_, _) => ExpressionValue.Boolean(
            host.Automation.Navigation.ClearMovementIntent()
                == PluginNavigationCommandStatus.Accepted), "clearmotion[]");
    }

    private static void RegisterObjectProperty(
        ExpressionFunctionRegistry registry,
        IPluginHost host,
        string name,
        PropertyKind kind)
    {
        registry.Register(name, 2, 2, (_, args) =>
        {
            uint objectId = args[0].AsObjectId(name);
            uint key = ToUInt(args[1], name);
            if (!host.Automation.Objects.TryCaptureProperties(
                objectId,
                out PluginItemProperties properties))
            {
                return ExpressionValue.Zero;
            }
            return kind switch
            {
                PropertyKind.Int => ExpressionValue.Number(Get(properties.Ints, key)),
                PropertyKind.Double => ExpressionValue.Number(Get(properties.Floats, key)),
                PropertyKind.Bool => ExpressionValue.Boolean(Get(properties.Bools, key)),
                PropertyKind.String => properties.Strings.TryGetValue(key, out string? value)
                    ? ExpressionValue.String(value)
                    : ExpressionValue.Zero,
                _ => ExpressionValue.Zero,
            };
        }, $"{name}[object,property]");
    }

    private static void RegisterFinder(
        ExpressionFunctionRegistry registry,
        IPluginHost host,
        string name,
        ObjectSet set,
        Func<PluginWorldObject, ExpressionValue, bool> predicate)
    {
        registry.Register(name, 1, 1, (_, args) => ObjectList(FilterSet(
            host.Automation.Objects.CaptureObjects(),
            set).Where(obj => predicate(obj, args[0]))), $"{name}[value]");
    }

    private static void RegisterRegexFinder(
        ExpressionFunctionRegistry registry,
        IPluginHost host,
        string name,
        ObjectSet set)
    {
        registry.Register(name, 1, 1, (_, args) =>
        {
            Regex regex = CreateRegex(args[0].AsString(name));
            return ObjectList(FilterSet(
                host.Automation.Objects.CaptureObjects(),
                set).Where(obj => regex.IsMatch(obj.Name)));
        }, $"{name}[pattern]");
    }

    private static void RegisterNearest(
        ExpressionFunctionRegistry registry,
        IPluginHost host,
        string name,
        Func<PluginWorldObject, IReadOnlyList<ExpressionValue>, bool> predicate,
        int argumentCount = 1)
    {
        registry.Register(name, argumentCount, argumentCount, (_, args) =>
        {
            PluginNavigationSnapshot player = host.Automation.Navigation.Snapshot;
            if (!player.IsAvailable)
                return ExpressionValue.Zero;
            PluginWorldObject? nearest = host.Automation.Objects.CaptureObjects()
                .Where(obj => obj.IsLandscape && obj.HasPosition && predicate(obj, args))
                .OrderBy(obj => player.Position.HorizontalDistanceMeters(obj.Position))
                .ThenBy(static obj => obj.ObjectId)
                .Cast<PluginWorldObject?>()
                .FirstOrDefault();
            return nearest is { } found
                ? ExpressionValue.WorldObject(found.ObjectId)
                : ExpressionValue.Zero;
        }, $"{name}[...]" );
    }

    private static ExpressionValue FirstObject(
        IPluginHost host,
        ObjectSet set,
        Func<PluginWorldObject, bool> predicate)
    {
        PluginWorldObject? found = FilterSet(
                host.Automation.Objects.CaptureObjects(), set)
            .Where(predicate)
            .OrderBy(static obj => obj.ObjectId)
            .Cast<PluginWorldObject?>()
            .FirstOrDefault();
        return found is { } value
            ? ExpressionValue.WorldObject(value.ObjectId)
            : ExpressionValue.Zero;
    }

    private static IEnumerable<PluginWorldObject> FilterSet(
        IEnumerable<PluginWorldObject> objects,
        ObjectSet set) => set switch
    {
        ObjectSet.Inventory => objects.Where(static obj => obj.IsOwned),
        ObjectSet.Landscape => objects.Where(static obj => obj.IsLandscape),
        _ => objects,
    };

    private static ExpressionValue ObjectList(IEnumerable<PluginWorldObject> objects) =>
        ExpressionValue.List(new ExpressionList(objects
            .OrderBy(static obj => obj.ObjectId)
            .Select(static obj => ExpressionValue.WorldObject(obj.ObjectId))));

    private static ExpressionValue NumberList(IEnumerable<uint> values) =>
        ExpressionValue.List(new ExpressionList(values.Select(
            static value => ExpressionValue.Number(value))));

    private static bool TryObject(
        IWorldObjectAutomation objects,
        in ExpressionValue value,
        string operation,
        out PluginWorldObject obj) => objects.TryGet(
        value.AsObjectId(operation),
        out obj);

    private static bool TryPlayerProperties(
        IPluginHost host,
        out PluginItemProperties properties) =>
        host.Automation.Objects.TryCaptureProperties(
            host.Automation.Character.ObjectId,
            out properties);

    private static double PlayerProperty(
        IPluginHost host,
        in ExpressionValue keyValue,
        PropertyKind kind)
    {
        if (!TryPlayerProperties(host, out PluginItemProperties properties))
            return 0d;
        uint key = ToUInt(keyValue, "character property");
        return kind switch
        {
            PropertyKind.Int => Get(properties.Ints, key),
            PropertyKind.Int64 => Get(properties.Int64s, key),
            PropertyKind.Double => Get(properties.Floats, key),
            PropertyKind.Bool => Get(properties.Bools, key) ? 1d : 0d,
            _ => 0d,
        };
    }

    private static double Attribute(
        ICharacterInfo character,
        in ExpressionValue id,
        bool buffed)
    {
        int kind = id.AsInt32("character attribute") - 1;
        foreach (PluginAttributeInfo attribute in character.Attributes)
        {
            if (attribute.Kind == kind)
                return buffed ? attribute.Current : attribute.Base;
        }
        return 0d;
    }

    private static double Skill(
        ICharacterInfo character,
        in ExpressionValue id,
        SkillRead read)
    {
        if (!character.TryGetSkill(ToUInt(id, "character skill"), out PluginSkillInfo skill))
            return 0d;
        return read switch
        {
            SkillRead.Base => skill.Base,
            SkillRead.Buffed => skill.Current,
            SkillRead.Training => skill.Training switch
            {
                PluginSkillTraining.Untrained => 1d,
                PluginSkillTraining.Trained => 2d,
                PluginSkillTraining.Specialized => 3d,
                _ => 0d,
            },
            _ => 0d,
        };
    }

    private static double Vital(
        ICharacterInfo character,
        in ExpressionValue id,
        VitalRead read)
    {
        (uint current, uint maximum) = id.AsInt32("character vital") switch
        {
            1 => (character.CurrentHealth, character.MaxHealth),
            2 => (character.CurrentStamina, character.MaxStamina),
            3 => (character.CurrentMana, character.MaxMana),
            _ => (0u, 0u),
        };
        return read == VitalRead.Current ? current : maximum;
    }

    private static double ObjectVital(
        IPluginHost host,
        in ExpressionValue objectValue,
        VitalObjectRead read)
    {
        uint id = objectValue.AsObjectId("object vital");
        ICharacterInfo character = host.Automation.Character;
        if (id == character.ObjectId)
        {
            return read switch
            {
                VitalObjectRead.Fraction => character.MaxHealth == 0u
                    ? -1d : character.CurrentHealth / (double)character.MaxHealth,
                VitalObjectRead.Health => character.CurrentHealth,
                VitalObjectRead.Stamina => character.CurrentStamina,
                VitalObjectRead.Mana => character.CurrentMana,
                _ => -1d,
            };
        }
        if (read is VitalObjectRead.Fraction or VitalObjectRead.Health)
        {
            foreach (PluginCombatTarget target in host.Automation.Combat
                .CaptureHostileTargets(float.MaxValue))
            {
                if (target.ObjectId != id || !target.IsHealthKnown)
                    continue;
                return read == VitalObjectRead.Fraction
                    ? target.HealthFraction
                    : target.MaximumHealth > 0
                        ? target.HealthFraction * target.MaximumHealth
                        : -1d;
            }
        }
        return -1d;
    }

    private static double FreeSlots(
        IPluginHost host,
        IReadOnlyList<ExpressionValue> args,
        bool containers)
    {
        uint containerId = args.Count == 0
            ? host.Automation.Character.ObjectId
            : args[0].AsObjectId("free slots");
        if (!host.Automation.Objects.TryGet(containerId, out PluginWorldObject container)
            || container.ObjectClass is not (PluginObjectClass.Container or PluginObjectClass.Player))
        {
            return -1d;
        }
        IReadOnlyList<PluginWorldObject> all = host.Automation.Objects.CaptureObjects();
        int used = all.Count(item => item.ContainerObjectId == containerId
            && (item.ObjectClass == PluginObjectClass.Container) == containers);
        int capacity = containers
            ? container.ContainersCapacity
            : container.ItemsCapacity;
        return Math.Max(0, capacity - used);
    }

    private static ExpressionValue CastResult(
        IMagicCommands magic,
        uint spellId,
        uint? target)
    {
        PluginCastGate gate = target is uint objectId
            ? magic.EvaluateGate(spellId, objectId)
            : magic.EvaluateGate(spellId);
        if (gate == PluginCastGate.Ready)
        {
            bool started = target is uint id
                ? magic.Cast(spellId, id)
                : magic.Cast(spellId);
            return ExpressionValue.Number(started ? 1d : 0d);
        }
        return ExpressionValue.Number(gate is PluginCastGate.NotKnown
            or PluginCastGate.Unavailable
            or PluginCastGate.Refused ? 2d : 0d);
    }

    private static ExpressionValue SpellProperty(
        in PluginSpellInfo spell,
        string property) => property.Trim().ToLowerInvariant() switch
    {
        "id" or "spellid" => ExpressionValue.Number(spell.SpellId),
        "name" => ExpressionValue.String(spell.Name),
        "family" => ExpressionValue.Number(spell.Family),
        "generation" or "tier" => ExpressionValue.Number(spell.Tier),
        "difficulty" => ExpressionValue.Number(spell.Difficulty),
        "quality" => ExpressionValue.Number(spell.Quality),
        "manacost" => ExpressionValue.Number(spell.ManaCost),
        "duration" => ExpressionValue.Number(spell.DurationSeconds),
        "school" => ExpressionValue.Number(spell.School),
        "description" => ExpressionValue.String(spell.Description),
        "isbeneficial" => ExpressionValue.Boolean(spell.IsBeneficial),
        "isoffensive" => ExpressionValue.Boolean(spell.IsOffensive),
        "isdebuff" => ExpressionValue.Boolean(spell.IsDebuff),
        "spelltype" => ExpressionValue.Number(spell.SpellType),
        "flags" => ExpressionValue.Number(spell.RawFlags),
        "targetmask" => ExpressionValue.Number(spell.TargetMask),
        _ => ExpressionValue.Zero,
    };

    private static double SpellExpiration(
        IReadOnlyList<PluginActiveEnchantment> enchantments,
        uint spellId)
    {
        foreach (PluginActiveEnchantment enchantment in enchantments)
        {
            if (enchantment.SpellId == spellId)
                return enchantment.SecondsRemaining;
        }
        return 0d;
    }

    private static PluginMovementIntent MotionIntent(string motion) =>
        motion.Trim().ToLowerInvariant() switch
        {
            "forward" => new PluginMovementIntent(Forward: true),
            "backward" or "backup" => new PluginMovementIntent(Backward: true),
            "turnright" => new PluginMovementIntent(TurnRight: true),
            "turnleft" => new PluginMovementIntent(TurnLeft: true),
            "straferight" => new PluginMovementIntent(StrafeRight: true),
            "strafeleft" => new PluginMovementIntent(StrafeLeft: true),
            "walk" => new PluginMovementIntent(Forward: true, Run: false),
            _ => throw new ExpressionEvaluationException(
                $"Invalid motion '{motion}'."),
        };

    private static ExpressionValue Coordinates(in PluginNavigationPosition position) =>
        ExpressionValue.Coordinates(new ExpressionCoordinates(
            position.EastWest,
            position.NorthSouth,
            position.Elevation / 240d));

    private static double HeadingTo(
        in PluginNavigationPosition from,
        in PluginNavigationPosition to)
    {
        double east = to.EastWest - from.EastWest;
        double north = to.NorthSouth - from.NorthSouth;
        return NormalizeHeading(Math.Atan2(east, north) * 180d / Math.PI);
    }

    private static double NormalizeHeading(double heading)
    {
        double result = heading % 360d;
        return result < 0d ? result + 360d : result;
    }

    private static Regex CreateRegex(string pattern) => new(
        pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static uint ToUInt(in ExpressionValue value, string operation) =>
        checked((uint)value.AsNumber(operation));

    private static double Get(IReadOnlyDictionary<uint, int> values, uint key) =>
        values.TryGetValue(key, out int value) ? value : 0d;

    private static double Get(IReadOnlyDictionary<uint, long> values, uint key) =>
        values.TryGetValue(key, out long value) ? value : 0d;

    private static double Get(
        IReadOnlyDictionary<uint, double> values,
        uint key,
        double fallback = 0d) =>
        values.TryGetValue(key, out double value) ? value : fallback;

    private static bool Get(IReadOnlyDictionary<uint, bool> values, uint key) =>
        values.TryGetValue(key, out bool value) && value;

    /// <summary>Legacy .NET Framework ordinal string hash used by UtilityBelt.</summary>
    private static int LegacyStringHash(string value)
    {
        unchecked
        {
            int hash1 = 5381;
            int hash2 = hash1;
            for (int index = 0; index < value.Length; index += 2)
            {
                hash1 = ((hash1 << 5) + hash1) ^ value[index];
                if (index == value.Length - 1)
                    break;
                hash2 = ((hash2 << 5) + hash2) ^ value[index + 1];
            }
            return hash1 + hash2 * 1566083941;
        }
    }

    private enum PropertyKind { Int, Int64, Double, Bool, String }
    private enum SkillRead { Base, Buffed, Training }
    private enum VitalRead { Current, Maximum }
    private enum VitalObjectRead { Fraction, Health, Stamina, Mana }
    private enum ObjectSet { All, Inventory, Landscape }
}
