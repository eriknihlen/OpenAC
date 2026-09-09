using System.Globalization;
using System.Text;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.MossTank.Expressions;

namespace AcDream.Plugins.MossTank;

internal sealed partial class MossTankPanel
{
    private static readonly string[] VtankHelp =
    [
        "/vt commands (profiles): settings nav loot meta opt testitem propertydump addnavpt refresh getdb addnavjump addnavcheckpoint",
        "/vt commands (actions): start stop forcebuff cancelforcebuff setmetastate fakedeath deletemonster reverseroute reverseroutequery equipitemsfor mexec echo tapjump jump setattackbar",
        "/vt commands (game info): dumpspells dumpspecies dumpmats dumpskills",
        "/vt commands (debug): log testmonster lockdump dumptracker clearlocks clearbusy listmonstervariables dumpmetavars listmetafunctions metafunchelp fakeimp pscount testspell testpet",
    ];

    private readonly HashSet<string> _commandLogTypes =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _commandJumpActive;
    private bool _commandJumpReleased;
    private bool _commandJumpCharging;
    private double _commandJumpElapsed;
    private double _commandJumpTurnElapsed;
    private double _commandJumpChargeSeconds;
    private float _commandJumpHeading;
    private PluginMovementIntent _commandJumpIntent;
    private bool _commandPortalState;
    private int _commandPortalCount;

    internal void ExecuteVtankCommand(PluginCommand command)
    {
        try
        {
            ExecuteVtankCommandCore(command.Arguments);
        }
        catch (Exception error)
        {
            WriteVtank($"Command failed: {error.GetBaseException().Message}");
            _host.Log.Error("MossTank /vt command failed.", error);
        }
    }

    private void ExecuteVtankCommandCore(string input)
    {
        (string verb, string arguments) = SplitHead(input);
        switch (verb.ToLowerInvariant())
        {
            case "":
            case "help":
                foreach (string line in VtankHelp)
                    WriteVtank(line);
                return;

            case "start":
                if (!_combat.Enabled)
                    SetMacroRunning(true);
                else
                    WriteVtank("Macro is already running.");
                return;
            case "stop":
                if (_combat.Enabled)
                    SetMacroRunning(false);
                WriteVtank("Macro stopped.");
                return;
            case "forcebuff":
                _buffRule.StartForce();
                return;
            case "cancelforcebuff":
                _buffRule.CancelForce();
                return;
            case "settings":
                HandleSettingsCommand(arguments);
                return;
            case "nav":
                HandleRouteProfileCommand(arguments);
                return;
            case "loot":
                HandleLootProfileCommand(arguments);
                return;
            case "meta":
                HandleMetaProfileCommand(arguments);
                return;
            case "opt":
                HandleOptionCommand(arguments);
                return;
            case "setmetastate":
                SetMetaStateFromCommand(arguments);
                return;
            case "mexec":
                ExecuteExpression(arguments);
                return;
            case "echo":
                WriteVtank(arguments);
                return;
            case "setattackbar":
                SetAttackBar(arguments);
                return;
            case "tapjump":
                StartCommandJump(
                    _host.Automation.Navigation.Snapshot.Position.HeadingDegrees,
                    shift: false,
                    milliseconds: 100,
                    null);
                return;
            case "jump":
                HandleJumpCommand(arguments, addToRoute: false);
                return;
            case "addnavjump":
                HandleJumpCommand(arguments, addToRoute: true);
                return;
            case "addnavpt":
                AddCommandRoutePoint(arguments, checkpoint: false);
                return;
            case "addnavcheckpoint":
                AddCommandRoutePoint(arguments, checkpoint: true);
                return;
            case "reverseroute":
                _navigation.ToggleReverse();
                WriteVtank($"Setting nav backwards to: {_navigation.Reversing}");
                return;
            case "reverseroutequery":
                WriteVtank($"Nav backwards is: {_navigation.Reversing}");
                return;
            case "deletemonster":
                DeleteSelectedMonster();
                return;
            case "equipitemsfor":
                EquipItemsFor(arguments);
                return;
            case "testitem":
                TestSelectedItem();
                return;
            case "propertydump":
                DumpSelectedProperties();
                return;
            case "testmonster":
                TestSelectedMonster();
                return;
            case "testspell":
                TestSpell(arguments);
                return;
            case "testpet":
                TestPet();
                return;
            case "listmonstervariables":
                WriteVtank("Supported monster expression variables:");
                WriteVtank("true, false, name, typeid, species, maxhp, range, hasshield, metastate, setting_<OptionName>");
                return;
            case "dumpmetavars":
                DumpMetaVariables();
                return;
            case "listmetafunctions":
                ListMetaFunctions();
                return;
            case "metafunchelp":
                MetaFunctionHelp(arguments);
                return;
            case "fakedeath":
                _meta.TriggerFakeDeath();
                WriteVtank("Fake character death trigger fired.");
                return;
            case "pscount":
                WriteVtank($"Portal space toggle count: {_commandPortalCount}");
                return;
            case "refresh":
                EnsureDefaultMonsterRule();
                RefreshMonsterEditor();
                RefreshItemEditors();
                RefreshLootEditor();
                RefreshRouteEditor();
                RefreshMetaEditor();
                WriteVtank("Refreshed settings pages.");
                return;
            case "getdb":
                WriteVtank("Game information uses acdream's installed DAT catalog and bundled VTank tables; no remote database download is required.");
                return;
            case "log":
                HandleLogCommand(arguments);
                return;
            case "lockdump":
                WriteVtank($"Action busy: magic={_host.Automation.Magic.IsCasting}, items={_host.Automation.Items.IsBusy}, equipment={_host.Automation.Equipment.IsBusy}");
                return;
            case "dumptracker":
                DumpObjectTracker();
                return;
            case "clearlocks":
                ClearMossTankActionLocks();
                WriteVtank("Action locks cleared.");
                return;
            case "clearbusy":
                PluginRecoveryResult recovery = _host.Automation.Recovery
                    .ClearOneBusyReference();
                WriteVtank(recovery.Accepted
                    ? $"Action busy: {recovery.PreviousCount} -> {recovery.CurrentCount}."
                    : recovery.Message);
                return;
            case "fakeimp":
                FakeImperil();
                return;
            case "dumpspells":
                DumpSpells();
                return;
            case "dumpspecies":
                DumpSpecies();
                return;
            case "dumpmats":
                DumpMaterials();
                return;
            case "dumpskills":
                DumpSkills();
                return;
            default:
                WriteVtank("Unknown /vt command. Use /vt help.");
                return;
        }
    }

    private void HandleSettingsCommand(string arguments)
    {
        (string operation, string name) = SplitHead(arguments);
        operation = operation.ToLowerInvariant();
        if (name.Length == 0
            || operation is not ("save" or "load" or "savechar" or "loadchar"))
        {
            WriteVtank("Usage: /vt settings [save/load/savechar/loadchar] [filename]");
            return;
        }
        name = StripExtension(name, ".usd", ".settings");
        if (operation is "save" or "savechar")
        {
            if (operation == "savechar")
                _profiles.SetMineOnly(true);
            if (_profiles.Create(
                name,
                copyCurrent: true,
                _allSettings,
                _noBuffItemNames,
                _commandLogTypes,
                out string notice))
            {
                ResetProfileConsumers();
            }
            WriteVtank(notice);
            return;
        }
        if (!_profiles.Select(name))
        {
            WriteVtank($"Settings profile '{name}' was not found.");
            return;
        }
        LoadSelectedProfile();
        WriteVtank($"Loaded settings profile {_profiles.Selected}.");
    }

    private void HandleRouteProfileCommand(string arguments)
    {
        (string operation, string name) = SplitHead(arguments);
        operation = operation.ToLowerInvariant();
        if (name.Length == 0 || operation is not ("save" or "load"))
        {
            WriteVtank("Usage: /vt nav [save/load] [filename]");
            return;
        }
        name = StripExtension(name, ".nav", ".af");
        if (operation == "save")
        {
            _routeProfiles.Create(
                name,
                copyCurrent: true,
                _navigationSettings,
                out string notice);
            RefreshRouteEditor();
            WriteVtank(notice);
            return;
        }
        if (!_routeProfiles.Select(name))
        {
            if (!_routeProfiles.TryImportLegacy(
                    name,
                    _navigationSettings,
                    _host.Automation.Spells,
                    out string importNotice))
            {
                WriteVtank(importNotice);
                return;
            }
            _navigation.Reset();
            RefreshRouteEditor();
            WriteVtank(importNotice);
            return;
        }
        LoadRouteProfile();
        WriteVtank($"Loaded navigation profile {_routeProfiles.Selected}.");
    }

    private void HandleLootProfileCommand(string arguments)
    {
        (string operation, string name) = SplitHead(arguments);
        operation = operation.ToLowerInvariant();
        if (name.Length == 0 || operation is not ("new" or "save" or "load"))
        {
            WriteVtank("Usage: /vt loot [load/new] [filename]");
            return;
        }
        name = StripExtension(name, ".utl", ".json");
        if (operation is "new" or "save")
        {
            _lootProfiles.Create(
                name,
                copyCurrent: operation == "save",
                _inventorySettings.Loot.Rules,
                out string notice);
            LoadLootProfile();
            WriteVtank(notice);
            return;
        }
        if (!_lootProfiles.Select(name))
        {
            if (!_lootProfiles.TryImportLegacy(
                name,
                _inventorySettings.Loot.Rules,
                _inventorySettings.Loot,
                out string importNotice))
            {
                WriteVtank(importNotice);
                return;
            }
            _loot.Reset();
            RefreshLootEditor();
            WriteVtank(importNotice);
            return;
        }
        LoadLootProfile();
        WriteVtank($"Loaded loot profile {_lootProfiles.Selected}.");
    }

    private void HandleMetaProfileCommand(string arguments)
    {
        (string operation, string name) = SplitHead(arguments);
        operation = operation.ToLowerInvariant();
        if (name.Length == 0 || operation is not ("save" or "load"))
        {
            WriteVtank("Usage: /vt meta [save/load] [filename]");
            return;
        }
        name = StripExtension(name, ".met", ".json", ".af");
        if (operation == "save")
        {
            _metaProfiles.Create(
                name,
                copyCurrent: true,
                _metaProfile,
                out string notice);
            LoadMetaProfile();
            WriteVtank(notice);
            return;
        }
        if (!_metaProfiles.Select(name))
        {
            if (!_metaProfiles.TryImportLegacy(
                name,
                out MetaProfile imported,
                out string importNotice))
            {
                WriteVtank(importNotice);
                return;
            }
            _metaProfile = imported;
            _meta.ReplaceProfile(_metaProfile);
            if (_initialized)
                ApplyPersistedOptionOverrides();
            _selectedMetaRule = 0;
            RefreshMetaEditor();
            WriteVtank(importNotice);
            return;
        }
        LoadMetaProfile();
        WriteVtank($"Loaded Meta profile {_metaProfiles.Selected}.");
    }

    private void HandleOptionCommand(string arguments)
    {
        (string operation, string tail) = SplitHead(arguments);
        switch (operation.ToLowerInvariant())
        {
            case "list":
                if (tail.Length != 0)
                {
                    WriteVtank("Usage: /vt opt list");
                    return;
                }
                WriteVtank($"Available options: ({VtankOptionCatalog.Names.Length})");
                for (int index = 0; index < VtankOptionCatalog.Names.Length; index += 4)
                    WriteVtank("   " + string.Join("   ", VtankOptionCatalog.Names.Skip(index).Take(4)));
                return;
            case "get":
                if (!VtankOptionCatalog.IsKnown(tail))
                {
                    WriteVtank("Option get: Invalid option specified.");
                    return;
                }
                string canonical = VtankOptionCatalog.Canonical(tail);
                WriteVtank($"Option {canonical} = {GetMetaOption(canonical).ToDisplayString()}");
                return;
            case "set":
            case "setinall":
                (string name, string rawValue) = SplitHead(tail);
                if (!VtankOptionCatalog.IsKnown(name))
                {
                    WriteVtank("Option set: Invalid option specified.");
                    return;
                }
                canonical = VtankOptionCatalog.Canonical(name);
                VtankSettingValueType declaredType = VtankOptionCatalog.DeclaredType(canonical);
                if (rawValue.Length == 0 || !TryParseOptionValue(rawValue, declaredType, out ExpressionValue value))
                {
                    WriteVtank(
                        "Option set: Invalid value specified. Proper type of "
                        + canonical + " is " + DeclaredClrTypeName(declaredType) + ".");
                    return;
                }
                SetMetaOption(canonical, value);
                if (operation.Equals("setinall", StringComparison.OrdinalIgnoreCase))
                {
                    int count = _profiles.SetOptionInAll(canonical, _allSettings);
                    WriteVtank($"Done saving setting {canonical} to all profiles. (Changed {count} profiles)");
                }
                else
                {
                    WriteVtank($"Set option {canonical} = {GetMetaOption(canonical).ToDisplayString()}");
                }
                return;
            default:
                WriteVtank("Usage: /vt opt [list/get/set/setinall]");
                return;
        }
    }

    private void SetMetaStateFromCommand(string state)
    {
        if (state.Length == 0)
        {
            WriteVtank("Usage: /vt setmetastate [somestate]");
            WriteVtank("NOTE: States are case sensitive.");
            return;
        }
        string target = _meta.States.FirstOrDefault(value =>
            value.Equals(state, StringComparison.Ordinal)) ?? MetaEngine.DefaultState;
        if (!target.Equals(state, StringComparison.Ordinal))
            WriteVtank("Warning: Attempted to set an unused state. Setting to default instead.");
        _meta.Transition(target);
        _combatSettings.MetaState = _meta.CurrentState;
        WriteVtank($"Meta state is now {_meta.CurrentState}.");
    }

    private void ExecuteExpression(string source)
    {
        WriteVtank($"MExec evaluating expression: \"{source.Trim()}\"");
        try
        {
            WriteVtank("Result: " + _expressions.Evaluate(source).ToDisplayString());
        }
        catch (Exception error)
        {
            WriteVtank("Expression error: " + error.Message);
        }
    }

    private void SetAttackBar(string value)
    {
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float power)
            || power is < 0f or > 1f)
        {
            WriteVtank("Usage: /vt setattackbar [0 to 1]");
            return;
        }
        _combatSettings.AttackPower = power;
        SaveProfile();
        WriteVtank($"Attack bar set to {power.ToString("0.###", CultureInfo.InvariantCulture)}.");
    }

    private void HandleJumpCommand(string arguments, bool addToRoute)
    {
        string[] parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 3 or > 4
            || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float heading)
            || !bool.TryParse(parts[1], out bool shift)
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int milliseconds)
            || milliseconds is < 0 or > 5000
            || !TryParseJumpDirection(parts.Length == 4 ? parts[3] : null, out RouteJumpDirection direction))
        {
            WriteVtank(addToRoute
                ? "Usage: /vt addnavjump [heading] [shift: true or false] [milliseconds]"
                : "Usage: /vt jump [heading] [shift: true or false] [milliseconds]");
            return;
        }
        if (addToRoute)
        {
            PluginNavigationPosition position = _host.Automation.Navigation.Snapshot.Position;
            _navigationSettings.Waypoints.Add(new RouteWaypoint
            {
                Type = RouteWaypointType.Jump,
                Position = position,
                JumpHeadingDegrees = NormalizeHeading(heading),
                JumpRun = shift,
                JumpChargeMilliseconds = milliseconds,
                JumpDirection = direction,
            });
            SaveRouteProfile();
            RefreshRouteEditor();
            WriteVtank("Added jump to the current route.");
            return;
        }
        StartCommandJump(heading, shift, milliseconds, direction);
    }

    private void StartCommandJump(
        float heading,
        bool shift,
        int milliseconds,
        RouteJumpDirection? direction)
    {
        PluginNavigationSnapshot snapshot = _host.Automation.Navigation.Snapshot;
        if (!snapshot.IsAvailable || snapshot.IsPortalSpace)
        {
            WriteVtank("Jump unavailable outside the world.");
            return;
        }
        RouteJumpDirection resolved = direction ?? RouteJumpDirection.Forward;
        _commandJumpHeading = NormalizeHeading(heading);
        _commandJumpIntent = new PluginMovementIntent(
            Forward: resolved == RouteJumpDirection.Forward,
            StrafeLeft: resolved == RouteJumpDirection.StrafeLeft,
            StrafeRight: resolved == RouteJumpDirection.StrafeRight,
            Run: shift,
            Jump: true);
        _commandJumpChargeSeconds = Math.Clamp(milliseconds / 1000d, 0.05d, 5d);
        _commandJumpElapsed = 0d;
        _commandJumpTurnElapsed = 0d;
        _commandJumpReleased = false;
        _commandJumpCharging = false;
        _commandJumpActive = true;
        WriteVtank($"Turning to heading {_commandJumpHeading:0.#} for jump.");
    }

    private bool TickCommandJump(double elapsedSeconds)
    {
        if (!_commandJumpActive)
            return false;

        INavigationAutomation navigation = _host.Automation.Navigation;
        PluginNavigationSnapshot snapshot = navigation.Snapshot;
        if (!snapshot.IsAvailable || snapshot.IsPortalSpace)
        {
            navigation.ClearMovementIntent();
            _commandJumpActive = false;
            WriteVtank("Jump canceled because the character left the world.");
            return false;
        }

        if (!_commandJumpCharging)
        {
            _commandJumpTurnElapsed += Math.Max(0d, elapsedSeconds);
            float delta = NavigationController.SignedHeadingDelta(
                snapshot.Position.HeadingDegrees,
                _commandJumpHeading);
            if (Math.Abs(delta) > 4f)
            {
                if (_commandJumpTurnElapsed > 10d
                    || navigation.SetMovementIntent(new PluginMovementIntent(
                        TurnLeft: delta < 0f,
                        TurnRight: delta > 0f,
                        Run: _commandJumpIntent.Run))
                        != PluginNavigationCommandStatus.Accepted)
                {
                    navigation.ClearMovementIntent();
                    _commandJumpActive = false;
                    WriteVtank("Jump command could not align to the requested heading.");
                    return false;
                }
                return true;
            }

            _commandJumpCharging = navigation.SetMovementIntent(_commandJumpIntent)
                == PluginNavigationCommandStatus.Accepted;
            if (!_commandJumpCharging)
            {
                navigation.ClearMovementIntent();
                _commandJumpActive = false;
                WriteVtank("Jump command was refused by the host.");
                return false;
            }
            _commandJumpElapsed = 0d;
            WriteVtank($"Jump charging at heading {_commandJumpHeading:0.#}.");
        }

        _commandJumpElapsed += Math.Max(0d, elapsedSeconds);
        if (!_commandJumpReleased && _commandJumpElapsed >= _commandJumpChargeSeconds)
        {
            _commandJumpReleased = true;
            navigation.SetMovementIntent(_commandJumpIntent with { Jump = false });
        }
        if (_commandJumpElapsed < _commandJumpChargeSeconds + 0.25d)
            return true;
        navigation.ClearMovementIntent();
        _commandJumpActive = false;
        _commandJumpCharging = false;
        return false;
    }

    private void AddCommandRoutePoint(string coordinates, bool checkpoint)
    {
        PluginNavigationPosition position;
        if (coordinates.Length == 0)
        {
            position = _host.Automation.Navigation.Snapshot.Position;
        }
        else if (!TryParseCoordinates(coordinates, out position))
        {
            WriteVtank(checkpoint
                ? "Usage: /vt addnavcheckpoint [coords] OR /vt addnavcheckpoint"
                : "Usage: /vt addnavpt [coords] OR /vt addnavpt");
            return;
        }
        _navigationSettings.Waypoints.Add(new RouteWaypoint
        {
            Type = checkpoint ? RouteWaypointType.Checkpoint : RouteWaypointType.Point,
            Position = position,
        });
        SaveRouteProfile();
        RefreshRouteEditor();
        WriteVtank(checkpoint ? "Added navigation checkpoint." : "Added navigation point.");
    }

    private void DeleteSelectedMonster()
    {
        uint selected = _host.Selection.SelectedObjectId ?? 0u;
        PluginCombatTarget target = _host.Automation.Combat
            .CaptureHostileTargets(float.MaxValue)
            .FirstOrDefault(value => value.ObjectId == selected);
        if (target.ObjectId == 0u)
        {
            WriteVtank("Select a monster, then do /vt deletemonster");
            return;
        }
        PluginCombatCommandResult result =
            _host.Automation.Combat.DismissGhostTarget(selected);
        WriteVtank(result.Accepted
            ? $"Forcing the client to delete {target.Name} ({target.ObjectId})!!"
            : $"Unable to delete {target.Name}: {result.Status}");
    }

    private void EquipItemsFor(string monsterName)
    {
        if (monsterName.Length == 0)
        {
            WriteVtank("Usage: /vt equipitemsfor [monster name]");
            WriteVtank("NOTE: each use of this command invokes one equipment step; multiple calls may be required.");
            return;
        }
        bool ready = _combat.EquipOneStepForMonster(monsterName);
        WriteVtank($"Changing items for monster \"{monsterName}\", ready: {ready}");
    }

    private void TestSelectedItem()
    {
        if (!TryGetSelectedInventoryItem(out PluginInventoryItem item))
        {
            WriteVtank("TestItem: No item selected.");
            return;
        }
        if (!_host.Automation.Items.TryCaptureProperties(item.ObjectId, out PluginItemProperties properties))
        {
            _host.Automation.Objects.Identify(item.ObjectId);
            WriteVtank("TestItem: Waiting for appraisal data.");
            return;
        }
        LootDecision? decision = LootRuleEngine.Decide(
            item,
            properties,
            _inventorySettings.Loot.Rules,
            _host.Automation.Items.CaptureOwnedItems(),
            host: _host);
        WriteVtank(decision is { } match
            ? $"TestItem: {item.Name} => {match.Action} ({match.RuleName}, priority {match.Priority})."
            : $"TestItem: {item.Name} => NoLoot.");
    }

    private void DumpSelectedProperties()
    {
        uint selected = _host.Selection.SelectedObjectId ?? 0u;
        if (selected == 0u
            || !_host.Automation.Objects.TryGet(selected, out PluginWorldObject item)
            || !_host.Automation.Objects.TryCaptureProperties(selected, out PluginItemProperties properties))
        {
            WriteVtank("Propertydump: Either no object selected or current selection object is invalid or not appraised.");
            return;
        }
        WriteVtank($"Object 0x{item.ObjectId:X8}: {item.Name}, class={(int)item.ObjectClass}, WCID={item.WeenieClassId}");
        DumpPropertyTable("Int", properties.Ints);
        DumpPropertyTable("Int64", properties.Int64s);
        DumpPropertyTable("Bool", properties.Bools);
        DumpPropertyTable("Float", properties.Floats);
        DumpPropertyTable("String", properties.Strings);
        DumpPropertyTable("DataId", properties.DataIds);
        DumpPropertyTable("InstanceId", properties.InstanceIds);
    }

    private void TestSelectedMonster()
    {
        uint selected = _host.Selection.SelectedObjectId ?? 0u;
        PluginCombatTarget target = _host.Automation.Combat
            .CaptureHostileTargets(float.MaxValue)
            .FirstOrDefault(value => value.ObjectId == selected);
        if (target.ObjectId == 0u)
        {
            WriteVtank("TestMonster: No monster selected.");
            return;
        }
        ResolvedMonsterRule resolved = _combatSettings.ResolveRule(target);
        WriteVtank($"TestMonster: evaluating monster rules for monster {target.Name}, type {target.WeenieClassId}");
        WriteVtank($"Matched '{resolved.Rule.Expression}', priority {resolved.Priority}, damage {resolved.Actions.DamageType}, attack={resolved.Actions.UsesPrimaryAttack}.");
        if (resolved.EvaluationError is { Length: > 0 } error)
            WriteVtank("Expression warning: " + error);
    }

    private void TestSpell(string argument)
    {
        if (!uint.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint spellId))
        {
            WriteVtank("Usage: /vt testspell [spellid]");
            return;
        }
        if (!_host.Automation.Spells.TryGet(spellId, out PluginSpellInfo spell))
        {
            WriteVtank("Invalid spellid.");
            return;
        }
        WriteVtank("---------------------------------");
        WriteVtank($"Testing ability to cast spell {spell.Name}, family {spell.Family}, quality {spell.Quality}, diff {spell.Difficulty}");
        WriteVtank($"Known: {_host.Automation.Spells.IsKnown(spellId)}, cast gate: {_host.Automation.Magic.EvaluateGate(spellId)}");
        WriteVtank("---------------------------------");
    }

    private void TestPet()
    {
        IReadOnlyList<PluginCombatTarget> targets = _host.Automation.Combat
            .CaptureHostileTargets((float)(_combatSettings.PetRangeMode == PetRangeMode.Custom
                ? _combatSettings.PetCustomRange
                : _combatSettings.MaximumRange));
        PetAutomationChoice choice = PetAutomation.Select(
            _host.Automation.Items.CaptureOwnedItems(),
            targets,
            _host.Automation.Character,
            _combatSettings,
            _host.Automation.Items.ActiveOwnedPetCount,
            allowRefill: true,
            allowSummon: true);
        WriteVtank(choice.Kind == PetAutomationActionKind.None
            ? "Pet can spawn: False"
            : $"Pet can spawn: True, action: {choice.Kind}, device: {choice.Device.Name}");
    }

    private void DumpMetaVariables()
    {
        WriteVtank("Assigned meta variables:");
        foreach (ExpressionVariableScope scope in Enum.GetValues<ExpressionVariableScope>())
        {
            foreach ((string name, ExpressionValue value) in _expressions.State.Capture(scope))
                WriteVtank($"{scope}.{name} = {value.ToDisplayString()}");
        }
    }

    private void ListMetaFunctions()
    {
        WriteVtank("Available builtin meta functions:");
        WriteChunks(_expressions.Functions
            .OrderBy(static function => function.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static function => function.Name));
    }

    private void MetaFunctionHelp(string name)
    {
        ExpressionFunction? function = _expressions.Functions.FirstOrDefault(value =>
            value.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (function is null)
        {
            WriteVtank($"Function not defined \"{name}\"");
            return;
        }
        WriteVtank("-------------------------------");
        WriteVtank("Function: " + function.Name);
        WriteVtank("Signature: " + function.Signature);
        WriteVtank("Description: " + function.Description);
        string count = function.MinimumArguments == function.MaximumArguments
            ? function.MinimumArguments.ToString(CultureInfo.InvariantCulture)
            : $"{function.MinimumArguments}..{function.MaximumArguments}";
        WriteVtank("Parameter count: " + count);
        WriteVtank("-------------------------------");
    }

    private void HandleLogCommand(string arguments)
    {
        string[] parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            WriteVtank(_commandLogTypes.Count == 0
                ? "Not currently logging."
                : "Log state:  " + string.Join(' ', _commandLogTypes.Order(StringComparer.OrdinalIgnoreCase)));
            WriteVtank("Valid logtypes: ActiveRule SalvageList SpellCast RuleInfo Timers CastInfo DebuffChoice Loot CharProps Misc BusyState");
            return;
        }
        if (parts.Length == 2)
            parts[1] = parts[1].ToLowerInvariant();
        if (parts.Length != 2 || parts[1] is not ("on" or "off"))
        {
            WriteVtank("Usage: /vt log [type] [on/off]");
            return;
        }
        string type = parts[0];
        bool changed = parts[1] == "on"
            ? _commandLogTypes.Add(type)
            : _commandLogTypes.Remove(type);
        WriteVtank((parts[1] == "on" ? "Set " : "Reset ") + type);
        if (changed)
            SaveProfile();
    }

    private void EmitMacroLog(MacroLogChannel channel, string message) =>
        EmitMacroLog(channel, message, chat: true);

    private void EmitMacroLog(
        MacroLogChannel channel, string message, bool chat)
    {
        if (!_commandLogTypes.Contains(channel.ToString()))
            return;
        if (chat)
            _host.Automation.Chat.PostSystemMessage("[MossTank] " + message);
        _host.Log.Info("[vt log " + channel + "] " + message);
    }

    private void DumpObjectTracker()
    {
        IReadOnlyList<PluginWorldObject> objects = _host.Automation.Objects.CaptureObjects();
        WriteVtank($"Object tracker count: {objects.Count}");
        foreach (PluginWorldObject item in objects.Take(100))
            WriteVtank($"0x{item.ObjectId:X8} {item.ObjectClass} {item.Name}");
        if (objects.Count > 100)
            WriteVtank($"... {objects.Count - 100} more objects omitted from chat.");
    }

    private void DumpSpells()
    {
        PluginSpellInfo[] spells = _host.Automation.Spells.KnownCombatSpells
            .Concat(_host.Automation.Spells.KnownSelfBuffs)
            .GroupBy(static spell => spell.SpellId)
            .Select(static group => group.First())
            .OrderBy(static spell => spell.SpellId)
            .ToArray();
        WriteVtank($"Known spell table ({spells.Length}):");
        foreach (PluginSpellInfo spell in spells)
            WriteVtank($"{spell.SpellId}\t{spell.Name}\t{spell.Family}\t{spell.Difficulty}");
    }

    private void DumpSpecies()
    {
        var species = _host.Automation.Combat.CaptureHostileTargets(float.MaxValue)
            .Where(static target => target.SpeciesId != 0)
            .GroupBy(static target => target.SpeciesId)
            .Select(static group => (Id: group.Key, Name: group.First().SpeciesName))
            .OrderBy(static value => value.Id)
            .ToArray();
        WriteVtank($"Currently observed species ({species.Length}):");
        foreach (var entry in species)
            WriteVtank($"{entry.Id}\t{entry.Name}");
    }

    private void DumpMaterials()
    {
        var materials = _host.Automation.Items.CaptureOwnedItems()
            .Where(static item => item.MaterialType != 0u)
            .GroupBy(static item => item.MaterialType)
            .OrderBy(static group => group.Key);
        WriteVtank("Materials present in owned inventory:");
        foreach (IGrouping<uint, PluginInventoryItem> group in materials)
            WriteVtank($"{group.Key}\t{group.First().Name}");
    }

    private void DumpSkills()
    {
        WriteVtank($"Character skills ({_host.Automation.Character.Skills.Count}):");
        foreach (PluginSkillInfo skill in _host.Automation.Character.Skills.OrderBy(static value => value.SkillId))
            WriteVtank($"{skill.SkillId}\t{skill.Name}\t{skill.Base}\t{skill.Current}\t{skill.Training}");
    }

    private void ObserveCommandPortalState()
    {
        bool current = _host.Automation.Navigation.Snapshot.IsPortalSpace;
        if (current != _commandPortalState)
        {
            _commandPortalState = current;
            _commandPortalCount++;
        }
    }

    private void ResetCommandSession()
    {
        if (_commandJumpActive || _commandJumpCharging)
            _host.Automation.Navigation.ClearMovementIntent();
        _commandJumpActive = false;
        _commandJumpReleased = false;
        _commandJumpCharging = false;
        _commandJumpElapsed = 0d;
        _commandJumpTurnElapsed = 0d;
        _commandJumpChargeSeconds = 0d;
        _commandJumpHeading = 0f;
        _commandJumpIntent = default;
        _commandPortalState = _host.Automation.Navigation.Snapshot.IsPortalSpace;
        _commandPortalCount = 0;
    }

    private void WriteVtank(string text) =>
        _host.Automation.Chat.PostSystemMessage(text);

    private void WriteChunks(IEnumerable<string> values)
    {
        var line = new StringBuilder();
        foreach (string value in values)
        {
            int extra = line.Length == 0 ? value.Length : value.Length + 2;
            if (line.Length != 0 && line.Length + extra > 240)
            {
                WriteVtank(line.ToString());
                line.Clear();
            }
            if (line.Length != 0)
                line.Append(", ");
            line.Append(value);
        }
        if (line.Length != 0)
            WriteVtank(line.ToString());
    }

    private void DumpPropertyTable<T>(string kind, IReadOnlyDictionary<uint, T> values)
    {
        foreach ((uint key, T value) in values.OrderBy(static pair => pair.Key))
            WriteVtank($"{kind}[{key}] = {value}");
    }

    private static (string Head, string Tail) SplitHead(string value)
    {
        string trimmed = value.Trim();
        int separator = trimmed.IndexOfAny([' ', '\t']);
        return separator < 0
            ? (trimmed, string.Empty)
            : (trimmed[..separator], trimmed[(separator + 1)..].Trim());
    }

    private static string StripExtension(string name, params string[] extensions)
    {
        string result = name.Trim();
        foreach (string extension in extensions)
        {
            if (result.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                return result[..^extension.Length];
        }
        return result;
    }

    private static bool TryParseOptionValue(string source, out ExpressionValue value) =>
        TryParseOptionValue(source, declaredType: null, out value);

    private static bool TryParseOptionValue(
        string source,
        VtankSettingValueType? declaredType,
        out ExpressionValue value)
    {
        if (source.Length == 0)
        {
            value = default;
            return false;
        }
        switch (declaredType)
        {
            case VtankSettingValueType.Bool:
                if (bool.TryParse(source, out bool boolean))
                {
                    value = ExpressionValue.Boolean(boolean);
                    return true;
                }
                value = default;
                return false;
            case VtankSettingValueType.Int:
            case VtankSettingValueType.Enum:
                if (int.TryParse(
                    source, NumberStyles.Integer, CultureInfo.InvariantCulture, out int integer))
                {
                    value = ExpressionValue.Number(integer);
                    return true;
                }
                value = default;
                return false;
            case VtankSettingValueType.Double:
            case VtankSettingValueType.Single:
                if (double.TryParse(
                    source, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                {
                    value = ExpressionValue.Number(number);
                    return true;
                }
                value = default;
                return false;
            case VtankSettingValueType.String:
                value = ExpressionValue.String(source);
                return true;
            default:
                if (bool.TryParse(source, out bool freeBoolean))
                {
                    value = ExpressionValue.Boolean(freeBoolean);
                    return true;
                }
                if (double.TryParse(
                    source, NumberStyles.Float, CultureInfo.InvariantCulture, out double freeNumber))
                {
                    value = ExpressionValue.Number(freeNumber);
                    return true;
                }
                value = ExpressionValue.String(source);
                return true;
        }
    }

    private static string DeclaredClrTypeName(VtankSettingValueType type) => type switch
    {
        VtankSettingValueType.Bool => "System.Boolean",
        VtankSettingValueType.Double => "System.Double",
        VtankSettingValueType.Int => "System.Int32",
        VtankSettingValueType.Single => "System.Single",
        VtankSettingValueType.String => "System.String",
        VtankSettingValueType.Enum => "System.Int32",
        _ => "System.Object",
    };

    private bool TryParseCoordinates(string source, out PluginNavigationPosition position)
    {
        position = default;
        string[] parts = source.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2
            || !TryParseCompass(parts[0], northSouth: true, out double northSouth)
            || !TryParseCompass(parts[1], northSouth: false, out double eastWest))
        {
            return false;
        }
        double elevation = 0d;
        if (parts.Length >= 3
            && !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out elevation))
        {
            return false;
        }
        PluginNavigationPosition current = _host.Automation.Navigation.Snapshot.Position;
        position = new PluginNavigationPosition(
            current.CellId,
            eastWest,
            northSouth,
            elevation,
            current.HeadingDegrees,
            IsOutdoor: true);
        return true;
    }

    private static bool TryParseCompass(string source, bool northSouth, out double value)
    {
        value = 0d;
        string trimmed = source.Trim();
        if (trimmed.Length < 2)
            return false;
        char direction = char.ToUpperInvariant(trimmed[^1]);
        if (northSouth ? direction is not ('N' or 'S') : direction is not ('E' or 'W'))
            return false;
        if (!double.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double magnitude))
            return false;
        value = direction is 'S' or 'W' ? -Math.Abs(magnitude) : Math.Abs(magnitude);
        return true;
    }

    private static bool TryParseJumpDirection(string? value, out RouteJumpDirection direction)
    {
        direction = RouteJumpDirection.Forward;
        if (string.IsNullOrWhiteSpace(value) || value.Equals("forward", StringComparison.OrdinalIgnoreCase))
            return true;
        if (value.Equals("strafeleft", StringComparison.OrdinalIgnoreCase))
        {
            direction = RouteJumpDirection.StrafeLeft;
            return true;
        }
        if (value.Equals("straferight", StringComparison.OrdinalIgnoreCase))
        {
            direction = RouteJumpDirection.StrafeRight;
            return true;
        }
        return false;
    }

    private static float NormalizeHeading(float heading)
    {
        float result = heading % 360f;
        return result < 0f ? result + 360f : result;
    }
}
