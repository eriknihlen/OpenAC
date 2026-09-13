# The built-in bot

`src/AcDream.Bot` is the automation that ships inside the client. It is
written from scratch against the public plugin contract and nothing else.

## Provenance rule

The bot is a clean-room design. It is built from knowledge of the game as a
player sees it and from `AcDream.Plugin.Abstractions`. Contributions must not:

- take code, tables, defaults, option names or file layouts from VirindiTank
  or any other closed-source automation tool, whether by decompiling, by
  memory, or by "translating" a listing;
- cite such a tool's internals in comments or commit messages;
- copy `.met`, `.nav`, `.utl` or `.usd` fixture files out of it.

Spell names, spell families, object classes, material ids and the like come
from the game's own data files through the host's spell catalog and object
projections; that is game data, not anyone's code. Reading an open-source
project (MIT/Apache) to learn a *file format* is fine if its license is
honoured; importing its code needs a NOTICE entry like the rest of the repo.

If in doubt, describe the behavior you want and write it fresh.

## How it is hosted

The bot is a gameplay plugin (`BotPlugin : IAcDreamPlugin`) compiled into the
client. `PluginSession.AddBuiltIn` gives it the same scoped host a discovered
plugin gets - its own storage folder, `/bot` command lease, UI owner and event
subscriptions - but no assembly load context, so it lives and dies with the
session. `AcDream.App` and `AcDream.Headless` each register it with one line;
delete that line and add a `plugin.json` and it becomes an external plugin
without any code change.

`AcDream.Bot` references `AcDream.Plugin.Abstractions` and ImGui.NET only. It never sees the
renderer, the wire protocol, or `GameRuntime`. Everything it does goes through
`IAutomationSurface`, which is what makes it testable against a fake.

## Architecture

```
BotPlugin            IAcDreamPlugin: wires host, /bot, windows, Tick
  BotController      the operations both /bot and the windows perform
  Ui/                BotDashboard, BotSettingsWindow, NavBuilderWindow (ImGui)
  BotEngine          priority arbiter; one Blackboard snapshot per tick
    Blackboard       vitals, enchantments, hostiles, corpses, position, casting
    IBehavior        WantsControl(board) / Execute(context) / Interrupt(context)
      vitals         Survival    heal / revitalize / mana; healing-kit fallback
      buffs          Buffing     keep configured self-buff families up
      combat         Combat      target selection, mode, swing or war spell
      loot           Looting     open corpse, appraise on demand, pick up by rule
      nav            Navigation  follow a route, turn-then-walk, stuck recovery
  Spells/            SpellSelector (name -> best known tier), CastTracker
  Combat/            TargetSelector
  Loot/              LootRule / LootRuleSet (own JSON format)
  Navigation/        Route / Waypoint, RouteFollower, StuckDetector
  Profiles/          BotProfile (JSON), BotStore (plugin storage)
```

Arbitration: behaviors are ordered by `BehaviorPriority`. Each tick the
engine asks, in order, whether anything strictly higher than the running
behavior wants control; if so the running one is `Interrupt`ed and loses the
tick. A running behavior otherwise keeps control while its `Execute` returns
`Continue`. `Done` and `Failed` release control and re-arbitrate next tick.
Every behavior is written so that one step is one action (one cast, one
swing, one pickup), which is what lets a heal land between two swings.

Timing comes from `IBotClock`, advanced by the host's tick delta, so timeouts
and back-offs are deterministic in tests.

## Windows

The bot's windows are Dear ImGui, drawn by the client's immediate-mode
overlay (`ACDREAM_IMGUI=0` turns the overlay off; the bot then falls back to
a small retail-look status panel). The dashboard opens on start; `Settings`
and `Nav builder` open from it.

- **Dashboard** - start/stop, activity and reason, profile and route pickers,
  the four subsystem toggles, force rebuff, player and target vitals.
- **Settings** - one tab per subsystem (Recharge, Combat, Buffs, Loot,
  Navigation). Every widget edits the live profile; `Save` persists it under
  the name in the box.
- **Nav builder** - record the current position as a waypoint, add pauses,
  remove steps, follow the draft, save it by name.

Plugins get the same facility through `IPluginHost.ImmediateUi`: register a
draw callback and call `ImGui.*` inside it (ImGui.NET is shared from the
host, so a plugin's calls land in the host's context). ImGui is for
editor-shaped tooling; player-facing windows stay markup so they wear the
game's own look.

## Commands

```
/bot start | stop | status | rebuff
/bot profile list | load <name> | save [name] | reset
/bot nav add | pause <seconds> | clear | use | save <name> | load <name> | list
/bot style melee | missile | magic
/bot buffs|combat|loot on|off
```

`nav add` records the character's current position as a waypoint on the
draft route; `nav use` starts following the draft; `nav save` names it.
Profiles and routes are JSON under the plugin's storage folder
(`<config>/plugins/acdream.bot/profiles/*.json` and `routes/*.json`) and can
be edited by hand. `BotProfile` in `Profiles/BotProfile.cs` is the schema.

## Testing

`tests/AcDream.Bot.Tests` drives every behavior through
`FakeAutomationSurface`, which records the commands the bot issued and lets
a test play the server's answers back (`CompleteCast`, `CompleteSwing`,
`CompletePickup`). `BotPluginHostingTests` runs the real `PluginSession`
built-in path end to end.

## What is not there yet

In rough priority order:

- **Combat range and approach.** The bot swings at whatever is in range; it
  does not walk toward a target or back off a caster. Needs a "close to
  target" behavior between combat and navigation.
- **Routes with doors and portals.** `WaypointKind` has `Point` and `Pause`;
  `Portal`/`UseObject`/`Chat` steps are the next additions and the follower is
  shaped to take them.
- **Missile ammo and weapon swapping** via `IEquipmentAutomation`.
- **Debuffs, rings and DoTs** - `ISpellCatalog.KnownCombatSpells` already
  projects them; the combat behavior only uses direct bolts.
- **Fellowship helpers** (heal a fellow, follow the leader) via
  `IFellowshipAutomation`.
- **Meta state machine.** RynthScript is the intended language; the engine
  needs an "expression surface" that exposes the blackboard to it.
- **Loot rule editing in the settings window.** Rules can be removed there
  but are authored in the profile JSON.
