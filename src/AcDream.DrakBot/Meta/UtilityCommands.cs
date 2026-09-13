using System.Text.RegularExpressions;
using AcDream.DrakBot.Loot.Utl;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Meta;

/// <summary>
/// The MagTools / UtilityBelt chat verbs metas lean on, ported from
/// RynthAi's MagToolsCommands: <c>/mt</c> and <c>/ub</c> share one set
/// (<c>opt</c>, <c>combatstate</c>, <c>face</c>, <c>cast</c>, <c>use</c>,
/// <c>select</c>, <c>give</c>, <c>loot</c>, <c>drop</c>, <c>equip</c>,
/// <c>dequip</c>, <c>fellow</c>, <c>logoff</c>, <c>send</c>), each with a
/// <c>p</c> suffix for partial name matches and <c>i</c>/<c>l</c> for
/// inventory-only or landscape-only lookups. Names resolve through the
/// meta's view of the world, so what a meta sees is what these act on.
/// <c>/ra</c> adds RynthAi's own: the give family (<c>give[a][p|xp|pp|r]
/// [count] &lt;item&gt; to &lt;player&gt;</c>, <c>ig[p] &lt;profile&gt; to
/// &lt;player&gt;</c>, queued and handed over one stack at a time),
/// <c>mexec</c>, and start/stop/pause.
/// </summary>
public sealed class UtilityCommands(
    MetaWorld world,
    MetaEngine meta,
    IAutomationSurface surface,
    Func<string, VTankLootProfile?>? utlLoader = null,
    Func<string, bool>? botCommand = null)
{
    private const double PendingLootSeconds = 8d;
    private const double GiveIntervalSeconds = 0.25d;

    private readonly Dictionary<string, string> _remembered = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<(uint ItemId, uint TargetId, int Amount)> _gives = new();
    private double _lastGiveAt = double.NegativeInfinity;
    private string? _pendingLootName;
    private bool _pendingLootPartial;
    private double _pendingLootUntil;

    /// <summary>Stacks waiting to be handed over.</summary>
    public int QueuedGives => _gives.Count;

    /// <summary>Handles a <c>/mt</c> or <c>/ub</c> line; false when it is not one of these verbs.</summary>
    public bool TryHandle(string command)
    {
        string[] parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return false;
        string prefix = parts[0].ToLowerInvariant();
        if (prefix is not ("/mt" or "/ub" or "/ra"))
            return false;
        string verb = parts[1].ToLowerInvariant();
        switch (verb)
        {
            case "givexp":
                return Give(parts, GiveMatch.Exact, partialPlayer: true, all: false);
            case "givepp":
                return Give(parts, GiveMatch.Partial, partialPlayer: true, all: false);
            case "giver":
                return Give(parts, GiveMatch.Regex, partialPlayer: false, all: false);
            case "givea":
                return GiveAll(parts, GiveMatch.Exact, partialPlayer: false);
            case "giveap":
                return GiveAll(parts, GiveMatch.Partial, partialPlayer: false);
            case "giveaxp":
                return GiveAll(parts, GiveMatch.Exact, partialPlayer: true);
            case "giveapp" or "gap":
                return GiveAll(parts, GiveMatch.Partial, partialPlayer: true);
            case "givear":
                return GiveAll(parts, GiveMatch.Regex, partialPlayer: false);
            case "ig":
                return GiveProfile(parts, partialPlayer: false);
            case "igp":
                return GiveProfile(parts, partialPlayer: true);
            case "mexec" when parts.Length >= 3:
                Say(meta.Expressions.Evaluate(Rest(parts, 2)));
                return true;
            case "start" or "resume":
                return botCommand?.Invoke("start") == true;
            case "stop" or "pause":
                return botCommand?.Invoke("stop") == true;
            case "clearbusy" or "clearbugged":
                surface.Combat.AbortPhysicalAttack();
                surface.Navigation.ClearMovementIntent();
                return true;
            case "opt":
                return Opt(parts);
            case "combatstate":
                return CombatState(parts);
            case "face":
                return Face(parts);
            case "cast":
                return Cast(parts, partial: false);
            case "castp":
                return Cast(parts, partial: true);
            case "use":
                return Use(parts, inventory: true, landscape: true, partial: false);
            case "usep":
                return Use(parts, inventory: true, landscape: true, partial: true);
            case "usei":
                return Use(parts, inventory: true, landscape: false, partial: false);
            case "useip" or "usepi":
                return Use(parts, inventory: true, landscape: false, partial: true);
            case "usel":
                return Use(parts, inventory: false, landscape: true, partial: false);
            case "uselp" or "usepl":
                return Use(parts, inventory: false, landscape: true, partial: true);
            case "select":
                return Select(parts, partial: false);
            case "selectp":
                return Select(parts, partial: true);
            case "give":
                return Give(parts, partial: false);
            case "givep":
                return Give(parts, partial: true);
            case "loot":
                return Loot(parts, partial: false);
            case "lootp":
                return Loot(parts, partial: true);
            case "drop":
                return Drop(parts, partial: false);
            case "dropp":
                return Drop(parts, partial: true);
            case "equip":
                return Equip(parts, partial: false);
            case "equipp":
                return Equip(parts, partial: true);
            case "dequip":
                return Dequip(parts, partial: false);
            case "dequipp":
                return Dequip(parts, partial: true);
            case "fellow":
                return Fellow(parts);
            case "logoff" or "logout":
                world.InvokeChatParser("/logout");
                return true;
            case "quit" or "exit":
                world.InvokeChatParser("/quit");
                return true;
            case "send":
                if (parts.Length >= 3)
                    world.InvokeChatParser(Rest(parts, 2));
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Hands over the next queued stack, and retries a <c>loot</c> asked for
    /// before the corpse opened until it does or the wait runs out.
    /// </summary>
    public void Tick(double now)
    {
        if (_gives.Count > 0 && now - _lastGiveAt >= GiveIntervalSeconds)
        {
            (uint itemId, uint targetId, int amount) = _gives.Peek();
            PluginItemCommandResult give = surface.Items.Give(itemId, targetId, (uint)Math.Max(0, amount));
            if (give.Status != PluginItemCommandStatus.Busy)
            {
                _gives.Dequeue();
                _lastGiveAt = now;
                if (_gives.Count == 0)
                    Say("give queue complete");
            }
        }
        if (_pendingLootName is null)
            return;
        uint container = surface.Loot.CurrentContainerId;
        if (container == 0u)
            return;
        if (now > _pendingLootUntil)
        {
            Say($"loot: timed out waiting for '{_pendingLootName}'");
            _pendingLootName = null;
            return;
        }
        if (FindContained(container, _pendingLootName, _pendingLootPartial) is { } found)
        {
            world.SelectItem(found.Id);
            world.UseObject(found.Id);
            _pendingLootName = null;
        }
    }

    private bool Opt(string[] parts)
    {
        if (parts.Length < 3)
            return false;
        string sub = parts[2].ToLowerInvariant();
        switch (sub)
        {
            case "list":
                foreach ((string key, (Func<string> get, _)) in meta.Expressions.BuildSettingsMapPublic())
                    Say($"{key} = {get()}");
                return true;
            case "get" when parts.Length >= 4:
                Say($"{parts[3]} = {meta.GetOptionValue(parts[3])}");
                return true;
            case "set" when parts.Length >= 5:
                meta.SetOptionValue(parts[3], parts[4]);
                return true;
            case "remember" when parts.Length >= 4:
                _remembered[parts[3]] = meta.GetOptionValue(parts[3]);
                return true;
            case "restore" when parts.Length >= 4:
                if (_remembered.TryGetValue(parts[3], out string? saved))
                    meta.SetOptionValue(parts[3], saved);
                return true;
            default:
                return false;
        }
    }

    private bool CombatState(string[] parts)
    {
        if (parts.Length < 3)
            return false;
        PluginCombatMode? mode = parts[2].ToLowerInvariant() switch
        {
            "peace" => PluginCombatMode.Peace,
            "melee" => PluginCombatMode.Melee,
            "missile" => PluginCombatMode.Missile,
            "magic" => PluginCombatMode.Magic,
            _ => null,
        };
        if (mode is null)
        {
            Say($"unknown combat mode: {parts[2]}");
            return true;
        }
        surface.Combat.EnterMode(mode.Value);
        return true;
    }

    private bool Face(string[] parts)
    {
        if (parts.Length < 3 || !float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float degrees))
            return false;
        surface.Navigation.FaceHeading(degrees);
        return true;
    }

    private bool Cast(string[] parts, bool partial)
    {
        if (parts.Length < 3)
            return false;
        string args = Rest(parts, 2);
        uint target = world.GetSelectedItemId();
        string spellArg = args;
        int on = args.IndexOf(" on ", StringComparison.OrdinalIgnoreCase);
        if (on >= 0)
        {
            spellArg = args[..on].Trim();
            string targetName = args[(on + 4)..].Trim();
            MetaObject? found = Find(targetName, inventory: false, landscape: true, partial: true);
            if (found is null)
            {
                Say($"target not found: '{targetName}'");
                return true;
            }
            target = unchecked((uint)found.Id);
        }
        if (!int.TryParse(spellArg, out int spellId))
        {
            spellId = FindSpell(spellArg, partial);
            if (spellId == 0)
            {
                Say($"spell not found: '{spellArg}'");
                return true;
            }
        }
        if (target == 0u)
            target = world.GetPlayerId();
        world.CastSpell(target, spellId);
        return true;
    }

    private bool Use(string[] parts, bool inventory, bool landscape, bool partial)
    {
        if (parts.Length < 3)
            return false;
        string args = Rest(parts, 2);
        string lower = args.ToLowerInvariant();
        if (lower is "closestnpc" or "closestvendor" or "closestportal")
        {
            PluginObjectClass wanted = lower switch
            {
                "closestnpc" => PluginObjectClass.Npc,
                "closestvendor" => PluginObjectClass.Vendor,
                _ => PluginObjectClass.Portal,
            };
            MetaObject? best = null;
            double bestDistance = double.MaxValue;
            int player = unchecked((int)world.GetPlayerId());
            foreach (MetaObject candidate in world.GetLandscapeObjects())
            {
                if (candidate.ObjectClass != wanted)
                    continue;
                double distance = world.Distance(player, candidate.Id);
                if (best is null || distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }
            if (best is null)
            {
                Say($"no {lower["closest".Length..]} nearby");
                return true;
            }
            world.UseObject(unchecked((uint)best.Id));
            return true;
        }

        int on = args.IndexOf(" on ", StringComparison.OrdinalIgnoreCase);
        if (on >= 0)
        {
            string sourceName = args[..on].Trim();
            string targetName = args[(on + 4)..].Trim();
            MetaObject? source = Find(sourceName, inventory, landscape, partial);
            MetaObject? target = Find(targetName, inventory, landscape, partial);
            if (source is null || target is null)
            {
                Say($"not found: '{(source is null ? sourceName : targetName)}'");
                return true;
            }
            world.UseObjectOn(unchecked((uint)source.Id), unchecked((uint)target.Id));
            return true;
        }
        MetaObject? found = Find(args, inventory, landscape, partial);
        if (found is null)
        {
            Say($"not found: '{args}'");
            return true;
        }
        world.UseObject(unchecked((uint)found.Id));
        return true;
    }

    private bool Select(string[] parts, bool partial)
    {
        if (parts.Length < 3)
            return false;
        string name = Rest(parts, 2);
        MetaObject? found = Find(name, inventory: true, landscape: true, partial);
        if (found is null)
        {
            Say($"not found: '{name}'");
            return true;
        }
        world.SelectItem(unchecked((uint)found.Id));
        return true;
    }

    private enum GiveMatch
    {
        Exact,
        Partial,
        Regex,
    }

    private bool Give(string[] parts, bool partial) =>
        Give(parts, partial ? GiveMatch.Partial : GiveMatch.Exact, partialPlayer: true, all: false);

    private bool GiveAll(string[] parts, GiveMatch match, bool partialPlayer)
    {
        if (parts.Length >= 3 && parts[2].Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            int count = _gives.Count;
            _gives.Clear();
            Say(count > 0 ? $"give queue cancelled ({count} left)" : "give queue is empty");
            return true;
        }
        return Give(parts, match, partialPlayer, all: true);
    }

    /// <summary>
    /// <c>give [count] &lt;item&gt; to &lt;player&gt;</c>: one stack now, or with
    /// <paramref name="all"/> every matching stack queued for the tick.
    /// </summary>
    private bool Give(string[] parts, GiveMatch match, bool partialPlayer, bool all)
    {
        if (parts.Length < 3)
            return false;
        string args = Rest(parts, 2);
        int to = args.LastIndexOf(" to ", StringComparison.OrdinalIgnoreCase);
        if (to < 0)
        {
            Say("usage: give[a][p|xp|pp|r] [count] <item> to <player>");
            return true;
        }
        string itemPart = args[..to].Trim();
        string playerPart = args[(to + 4)..].Trim();
        if (itemPart.Length == 0 || playerPart.Length == 0)
            return true;
        int maxCount = all ? int.MaxValue : 1;
        string[] tokens = itemPart.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (!all && tokens.Length == 2 && int.TryParse(tokens[0], out int count) && count > 0)
        {
            maxCount = count;
            itemPart = tokens[1].Trim();
        }
        MetaObject? target = Find(playerPart, inventory: false, landscape: true, partialPlayer);
        if (target is null)
        {
            Say($"target not found: '{playerPart}'");
            return true;
        }
        Regex? pattern = null;
        if (match == GiveMatch.Regex)
        {
            try
            {
                pattern = new Regex(itemPart, RegexOptions.IgnoreCase);
            }
            catch (ArgumentException)
            {
                Say($"invalid pattern: {itemPart}");
                return true;
            }
        }
        var matches = new List<MetaObject>();
        foreach (MetaObject item in world.GetDirectInventory())
        {
            if (item.WieldedLocation != 0)
                continue;
            bool hit = match switch
            {
                GiveMatch.Exact => item.Name.Equals(itemPart, StringComparison.OrdinalIgnoreCase),
                GiveMatch.Partial => item.Name.Contains(itemPart, StringComparison.OrdinalIgnoreCase),
                _ => pattern!.IsMatch(item.Name),
            };
            if (hit)
                matches.Add(item);
            if (matches.Count >= maxCount)
                break;
        }
        if (matches.Count == 0)
        {
            Say($"no items matching '{itemPart}'");
            return true;
        }
        if (!all && matches.Count == 1)
        {
            world.MoveItemExternal(unchecked((uint)matches[0].Id), unchecked((uint)target.Id), Math.Max(1, matches[0].StackCount));
            return true;
        }
        foreach (MetaObject item in matches)
            _gives.Enqueue((unchecked((uint)item.Id), unchecked((uint)target.Id), Math.Max(1, item.StackCount)));
        Say($"queued {matches.Count} stack(s) for {target.Name}");
        return true;
    }

    /// <summary><c>ig &lt;profile&gt; to &lt;player&gt;</c>: every pack item a <c>.utl</c> profile would keep is queued for the player.</summary>
    private bool GiveProfile(string[] parts, bool partialPlayer)
    {
        if (parts.Length < 3)
            return false;
        string args = Rest(parts, 2);
        int to = args.LastIndexOf(" to ", StringComparison.OrdinalIgnoreCase);
        if (to < 0)
        {
            Say("usage: ig[p] <loot profile> to <player>");
            return true;
        }
        string profileName = args[..to].Trim();
        string playerPart = args[(to + 4)..].Trim();
        VTankLootProfile? profile = utlLoader?.Invoke(profileName);
        if (profile is null)
        {
            Say($"loot profile not found: {profileName}");
            return true;
        }
        MetaObject? target = Find(playerPart, inventory: false, landscape: true, partialPlayer);
        if (target is null)
        {
            Say($"target not found: '{playerPart}'");
            return true;
        }
        var context = new UtlLootContext(surface);
        int queued = 0;
        foreach (PluginInventoryItem item in surface.Items.CaptureOwnedItems())
        {
            if (item.IsEquipped || (item.ContainerObjectId != world.GetPlayerId() && !IsInSidePack(item)))
                continue;
            VTankLootRule? matched = null;
            foreach (VTankLootRule rule in profile.Rules)
            {
                if (rule.Enabled && UtlLootEvaluator.Match(rule, item, context))
                {
                    matched = rule;
                    break;
                }
            }
            if (matched is null || matched.Action is not (VTankLootAction.Keep or VTankLootAction.KeepUpTo))
                continue;
            _gives.Enqueue((item.ObjectId, unchecked((uint)target.Id), Math.Max(1, item.StackSize)));
            queued++;
        }
        Say(queued > 0 ? $"queued {queued} stack(s) from '{profileName}' for {target.Name}" : $"nothing in the pack matches '{profileName}'");
        return true;
    }

    private bool IsInSidePack(in PluginInventoryItem item)
    {
        uint player = world.GetPlayerId();
        foreach (PluginInventoryItem pack in surface.Items.CaptureOwnedItems())
        {
            if (pack.ObjectId == item.ContainerObjectId)
                return pack.ContainerObjectId == player;
        }
        return false;
    }

    private bool Loot(string[] parts, bool partial)
    {
        if (parts.Length < 3)
            return false;
        string name = Rest(parts, 2);
        uint container = surface.Loot.CurrentContainerId;
        if (container != 0u && FindContained(container, name, partial) is { } found)
        {
            world.SelectItem(found.Id);
            world.UseObject(found.Id);
            _pendingLootName = null;
            return true;
        }
        // The corpse may still be opening; the tick keeps looking for a while.
        _pendingLootName = name;
        _pendingLootPartial = partial;
        _pendingLootUntil = world.Now + PendingLootSeconds;
        return true;
    }

    private bool Drop(string[] parts, bool partial)
    {
        if (parts.Length < 3)
            return false;
        string name = Rest(parts, 2);
        MetaObject? item = Find(name, inventory: true, landscape: false, partial);
        if (item is null)
        {
            Say($"item not found in inventory: '{name}'");
            return true;
        }
        surface.Items.Drop(unchecked((uint)item.Id));
        return true;
    }

    private bool Equip(string[] parts, bool partial)
    {
        if (parts.Length < 3)
            return false;
        string name = Rest(parts, 2);
        MetaObject? item = Find(name, inventory: true, landscape: false, partial);
        if (item is null)
        {
            Say($"item not found: '{name}'");
            return true;
        }
        if (surface.Equipment.Equip(unchecked((uint)item.Id)).Status is PluginEquipmentCommandStatus.Unavailable or PluginEquipmentCommandStatus.InvalidItem)
            world.UseObject(unchecked((uint)item.Id));
        return true;
    }

    private bool Dequip(string[] parts, bool partial)
    {
        if (parts.Length < 3)
            return false;
        string name = Rest(parts, 2);
        PluginInventoryItem? worn = null;
        IReadOnlyList<PluginInventoryItem> owned = surface.Items.CaptureOwnedItems();
        foreach (PluginInventoryItem item in owned)
        {
            if (item.IsEquipped && Matches(item.Name, name, partial))
            {
                worn = item;
                break;
            }
        }
        if (worn is null)
        {
            Say($"wielded item not found: '{name}'");
            return true;
        }
        // Into a pack with a free slot, the main pack first.
        uint player = world.GetPlayerId();
        uint destination = 0u;
        int mainUsed = 0;
        foreach (PluginInventoryItem item in owned)
        {
            if (item.ContainerObjectId == player && !item.IsEquipped)
                mainUsed++;
        }
        if (mainUsed < 102)
            destination = player;
        else
        {
            foreach (PluginInventoryItem pack in owned)
            {
                if (pack.ContainerObjectId != player || pack.ItemsCapacity <= 0)
                    continue;
                int used = 0;
                foreach (PluginInventoryItem item in owned)
                {
                    if (item.ContainerObjectId == pack.ObjectId)
                        used++;
                }
                if (used < pack.ItemsCapacity)
                {
                    destination = pack.ObjectId;
                    break;
                }
            }
        }
        if (destination == 0u)
        {
            Say($"dequip: no room for '{worn.Value.Name}'");
            return true;
        }
        surface.Items.MoveToContainer(worn.Value.ObjectId, destination);
        return true;
    }

    private bool Fellow(string[] parts)
    {
        if (parts.Length < 3)
            return false;
        IFellowshipAutomation fellowship = surface.Fellowship;
        switch (parts[2].ToLowerInvariant())
        {
            case "create" when parts.Length >= 4:
                fellowship.Create(Rest(parts, 3), shareExperience: true);
                return true;
            case "open":
                fellowship.SetOpen(true);
                return true;
            case "close":
                fellowship.SetOpen(false);
                return true;
            case "disband":
                fellowship.Quit(disband: true);
                return true;
            case "quit":
                fellowship.Quit(disband: false);
                return true;
            case "recruit" when parts.Length >= 4:
            {
                string playerName = Rest(parts, 3);
                MetaObject? player = Find(playerName, inventory: false, landscape: true, partial: true);
                if (player is null)
                    Say($"player not found: '{playerName}'");
                else
                    fellowship.Recruit(unchecked((uint)player.Id));
                return true;
            }
            default:
                return false;
        }
    }

    /// <summary>An item by name: the pack first, then the nearest landscape object.</summary>
    private MetaObject? Find(string name, bool inventory, bool landscape, bool partial)
    {
        if (inventory)
        {
            foreach (MetaObject item in world.GetDirectInventory())
            {
                if (Matches(item.Name, name, partial))
                    return item;
            }
        }
        if (!landscape)
            return null;
        MetaObject? best = null;
        MetaObject? any = null;
        double bestDistance = double.MaxValue;
        int player = unchecked((int)world.GetPlayerId());
        foreach (MetaObject candidate in world.GetLandscapeObjects())
        {
            if (!Matches(candidate.Name, name, partial))
                continue;
            any ??= candidate;
            double distance = world.Distance(player, candidate.Id);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }
        return best ?? any;
    }

    /// <summary>An item in the open corpse: an exact name first, else the first containing it.</summary>
    private (uint Id, string Name)? FindContained(uint container, string name, bool partial)
    {
        if (surface.Loot.CurrentContainerId != container)
            return null;
        (uint, string)? substring = null;
        foreach (PluginInventoryItem item in surface.Loot.CaptureCurrentContents())
        {
            if (!partial && item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return (item.ObjectId, item.Name);
            if (substring is null && item.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                substring = (item.ObjectId, item.Name);
        }
        return substring;
    }

    private int FindSpell(string name, bool partial)
    {
        ISpellCatalog spells = surface.Spells;
        int found = 0;
        foreach (IReadOnlyList<PluginSpellInfo> list in new[] { spells.KnownSelfBuffs, spells.KnownAttackSpells, spells.KnownCombatSpells })
        {
            foreach (PluginSpellInfo spell in list)
            {
                if (spell.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return unchecked((int)spell.SpellId);
                if (partial && found == 0 && spell.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                    found = unchecked((int)spell.SpellId);
            }
        }
        return found;
    }

    private static bool Matches(string candidate, string name, bool partial) =>
        partial ? candidate.Contains(name, StringComparison.OrdinalIgnoreCase) : candidate.Equals(name, StringComparison.OrdinalIgnoreCase);

    private static string Rest(string[] parts, int from) => string.Join(' ', parts, from, parts.Length - from);

    private void Say(string text) => world.WriteToChat($"[DrakBot] {text}", 1);
}
