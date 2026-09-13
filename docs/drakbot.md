# DrakBot, the built-in bot

`src/AcDream.DrakBot` is DrakBot, the automation that ships inside the client. It is
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

The bot is a gameplay plugin (`DrakBotPlugin : IAcDreamPlugin`) compiled into the
client. `PluginSession.AddBuiltIn` gives it the same scoped host a discovered
plugin gets - its own storage folder, `/drakbot` command lease (`/bot` is an alias), UI owner and event
subscriptions - but no assembly load context, so it lives and dies with the
session. Built-ins enable when the session starts, before discovered plugins,
so a host's own `started` status always precedes the bot's `pluginLoaded`.
`AcDream.App` and `AcDream.Headless` each register it with one line; delete
that line and add a `plugin.json` and it becomes an external plugin without
any code change. The headless host treats it like a discovered plugin for
the session's `plugins` list: absent, everything loads; present, the bot
loads only when `acdream.drakbot` is named, so a launcher probe or a session
with `"plugins": []` runs without it.

`AcDream.DrakBot` references `AcDream.Plugin.Abstractions` only. It never sees
the renderer, the wire protocol, or `GameRuntime`. Everything it does goes
through `IAutomationSurface`, which is what makes it testable against a fake.
The ImGui windows are a second assembly, `AcDream.DrakBot.Ui`, that only the
graphical client references: it hands `DrakBotPlugin` a
`DrakBotWindowsFactory`, and the headless host, which has no presentation
layer, hands it nothing.

## Architecture

```
DrakBotPlugin      IAcDreamPlugin: wires host, /drakbot, windows, Tick
  BotController      the operations both /drakbot and the windows perform
  ..DrakBot.Ui/      BotDashboard, BotSettingsWindow, NavBuilderWindow (ImGui, App only)
  BotEngine          priority arbiter; one Blackboard snapshot per tick
    Blackboard       vitals, enchantments, hostiles, corpses, position, casting
    IBehavior        WantsControl(board) / Execute(context) / Interrupt(context)
      vitals         Survival    heal / revitalize / mana; healing-kit fallback
      buffs          Buffing     keep configured self-buff families up
      combat         Combat      target selection, line of sight, approach, swing or war spell
      loot           Looting     open corpse, appraise on demand, pick up by rule
      nav            Navigation  follow a route through the Walker
  Spells/            SpellSelector (name -> best known tier), CastTracker
  Combat/            TargetSelector, LineOfSightService
  Loot/              LootRule / LootRuleSet (own JSON format)
  Navigation/        Route / Waypoint, NavFile (.nav), RouteFollower, Walker, RouteActionRunner
  Meta/              MetaEngine (states/rules), ExpressionEngine, MetaWorld, .af/.met parsers
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

## The monster list

The Combat tab's element keyword fights everything the same way. The
Monsters tab is the VTank-style list instead: one rule per kind of monster
(a regular expression over the name, or a substring when it is not one),
with a priority (higher first, zero never fought), an element (or Auto for
the profile's), the war spell's shape (bolt, arc, streak) and whether to
ring instead once the profile's minimum number of hostiles stand within
ring range, and the debuffs to land first - imperil, the element's
vulnerability, a second vulnerability, fester, yield, broadside, gravity
well. A rule named `Default` covers what nothing else matches; without one
an unmatched monster is left alone. Debuffs are cast in that order until
the client's record of what the character landed shows them on the
target; spells are chosen by family, so a lore-named top tier (Outlander's
Insolence for Force Streak VII) is reached through its lower tiers. The
tables of spell names are in `Spells/WarSpellNames.cs`; void magic stands
in for war when only it is known.

## Line of sight, obstacle sense and approach

The client's collision is the bot's one obstacle sense, for shots and for
walking alike. `LineOfSightService` wraps two host sweeps and the bot never
sees geometry - it only sees `Clear`, `Blocked` (with the blocking object)
or "no answer":

- `IProjectileAutomation` sweeps a missile-flagged sphere through the
  client's own `PhysicsEngine` from the shooter's hands to a point on the
  target's body. Walls, doors, hills and scenery block it the way they block
  a real shot; other creatures, ethereal objects and the shooter do not.
- `IMovementProbeAutomation.ProbeWalk` walks the character's own collision
  body (the movement controller's spheres, scale and step heights) along a
  compass heading, one grounded step at a time: knee-high steps are climbed,
  ledges are stepped off and dropped from within a safe fall, and a wall, a
  fence, a closed door or a cliff stops it. Creatures are ignored; they move.

All walking - a route's next waypoint or a hostile to close on - goes
through `Walker`, which drives the character the way a player does: a held
run, steered with the turn keys while it moves (with a little hysteresis so
the key does not chatter), and a stop to turn in place only when the
destination is more than the profile's turn-in-place angle off (20 by
default; the run resumes at half that, so the gate does not flap). Time
spent turning never reads as being stuck. When a run makes no progress for
a few seconds the walker backs up, then sidesteps left, then right; it
never jumps. The route follower adds two habits that stop a runner circling
a waypoint: passing within 2.5 arrival distances of a point and then
drawing away counts as reaching it, and inside the corner lookahead the aim
point blends toward the next point so corners are cut. The angles and
distances are the ones RynthSuite's navigation engine settled on in play.

Ranged styles do not fire blind, and nobody walks into a wall:

- **Trajectory.** Missile style sweeps a `Missile` arc (an arrow under
  gravity). Magic sweeps `Straight` for bolts and streaks, or `Arc` when the
  profile says the character casts arc spells; arcs are tested as flat paths
  indoors by default because they meet ceilings. Launch speeds default to the
  client's figures and can be overridden per profile (a slower launch lobs
  higher).
- **Aim heights.** The configured attack height is tried first, then the
  other two. A missile attack uses whichever height was clear.
- **Choosing a target.** Hostiles are ranked as before; the first one with a
  clear path wins. A blocked target inside the approach range earns a
  strike and is passed over; after N strikes in a row it is blacklisted for
  a while and drops out of selection until the blacklist lapses or a later
  sweep finds it clear. Strikes are counted once per fresh sweep, so a
  cached verdict re-read every tick does not stack them.
- **Approach.** When nothing can be shot from where the bot stands, it
  walks toward the best hostile that is blocked beyond the approach range:
  hold a run toward it, re-check the path every tick, stop when it clears
  or the approach range is met, give up after a timeout. Melee
  uses the same step to close to within its reach before pressing the
  attack. The walk is one step like everything else: a heal interrupts it
  and the movement intent is dropped.
- **Walking with eyes open.** Before a hostile is chosen as something to
  walk to - melee out of reach, or ranged and blocked beyond the approach
  range - the service walks the body toward it; if the direct heading is
  blocked it tries a fan of headings (30, 60 and 90 degrees to either side)
  out to the steering look-ahead. A hostile no heading reaches earns a
  strike and is passed over, so a monster behind a fence is blacklisted the
  same way one behind a pillar is for a caster. While walking, the direct
  heading is re-probed every tick; the bot steers along the first open fan
  heading and comes back to the direct one when it clears. When nothing in
  the fan is open it tries the navigation recoveries one at a time (back
  up, strafe left, strafe right) and looks again; the stuck detector covers
  whatever the probes did not model. Strikes are kept per sense: an open
  walk forgives walk strikes, an open shot forgives shot strikes, and the
  blacklist counts both.
- **Fail open.** No projectile collision on the host, a spent collision
  budget or an error all count as "no answer" and the shot goes ahead.
- **Diagnostics.** The Combat tab's "Draw the swept path" asks the client to
  mark every collision check in the world for the current target.

The sweeps live in the client (`AcDream.App/Plugins/ProjectilePathProbe.cs`
and `WalkPathProbe.cs`) and are covered by synthetic-landblock tests (walls,
steps, ledges, cliffs, bystanders, arcs) plus installed-dat tests against a
real dungeon's walls and ceiling.

## Metas

A meta is the VTank-style state machine that sits above the behaviors: a
set of states, each a list of rules `IF condition DO action`, evaluated
every tick in the current state, first match wins, each rule firing once
per visit to its state. It does not fight or walk itself; it flips the
profile's switches, loads routes, changes its own state, and talks in
chat, and the behaviors do the work. Ported from RynthSuite's MetaManager
and ExpressionEngine, so a meta written for RynthAi or VTank runs here.

- **Files.** `.af` (metaf text, what RynthScript compiles to) and `.met`
  (VTank binary) load by name from the VTank profiles folder
  (`<data>/vtank/<name>.af|.met`): `/drakbot meta load <name>`, the Meta
  tab, or `/vt meta load <name>` from a rule. Embedded `NAV:` blocks travel
  with the meta. The profile remembers the meta's name and loads it with
  the profile.
- **Conditions.** Never, Always, All, Any, Not, ChatMatch and ChatCapture
  (regex over the last second of chat; capture groups fill `{1}`.. in the
  action), MainSlotsLE, SecsInStateGE, Death, ItemCountLE/GE, MobsInDist
  by name and by count, NoMobsInDist, NeedToBuff, BlockE, CellE,
  IntoPortal, ExitPortal, SecsOnSpellGE/LE, BuPercentGE, DistToRteGE,
  NavEmpty, the typed vitals, and `Expr`. Vendor and vitae conditions are
  not carried by the plugin contract and never fire.
- **Actions.** Chat (a `/vt` line is translated, a `/drakbot` line handled,
  anything else sent), SetState, CallState / Return (a stack), EmbedNav
  (an embedded route or one by name), DoAll, SetWatchdog / ClearWatchdog
  (no progress in N meters for M seconds sends the meta to a state),
  SetOpt / GetOpt, DoExpr, ChatExpr. VTank views are ignored with a
  notice.
- **Expressions.** The full VTank/UtilityBelt expression language: infix
  operators, `funcname[args]`, `$var`, session, persistent (per character)
  and global variables kept in the plugin's storage, lists, dicts,
  stopwatches, coordinates, `wobject*` queries over the object table,
  character properties, spell timers, quest flags (from `/myquests`),
  fellowship, game time, `exec` / `delayexec`. `docs/drakbot-expressions.txt`
  is the reference. `/drakbot meta eval <expr>` (and the Meta tab) evaluate
  one in place. Functions that need the client's memory - creature
  profiles, salvage panel, vitae - answer "0".
- **`/vt` translation.** `opt set` maps VTank option names onto the
  profile (enablecombat, enablenav, attackdistance, the recharge
  thresholds, ...), `meta load`, `nav load`, `setmetastate`, `echo`,
  `reverseroute`; other `/vt` verbs are tried as `/drakbot` verbs.

## Windows

The bot's windows are Dear ImGui (`AcDream.DrakBot.Ui`), drawn by the
client's immediate-mode overlay (`ACDREAM_IMGUI=0` turns the overlay off; the
bot then falls back to a small retail-look status panel). Nothing is drawn
until the character is in the world; the dashboard then opens, and `Settings`
and `Nav builder` open from it.

- **Dashboard** - start/stop, activity and reason, profile and route pickers,
  the four subsystem toggles, force rebuff, player and target vitals, and the
  current target's line-of-sight state (clear, blocked by what, strikes,
  blacklisted) and, while walking, which heading is open.
- **Settings** - one tab per subsystem (Recharge, Combat, Monsters, Buffs,
  Loot, Navigation, Meta). Every widget edits the live profile; `Save` persists it under
  the name in the box. The Combat tab carries reach (melee reach, approach
  range, walk timeout) and the line-of-sight options (on/off, war spell
  path, launch speeds, blacklist strikes and duration, walk checks and
  steering look-ahead, path drawing).
- **Nav builder** - record the current position as a waypoint, add pauses,
  remove steps, follow the draft, save it by name.

Plugins get the same facility through `IPluginHost.ImmediateUi`: register a
draw callback and call `ImGui.*` inside it (ImGui.NET is shared from the
host, so a plugin's calls land in the host's context). ImGui is for
editor-shaped tooling; player-facing windows stay markup so they wear the
game's own look.

## Commands

```
/drakbot start | stop | status | rebuff
/drakbot profile list | load <name> | save [name] | reset
/drakbot nav add | pause <seconds> | chat <text> | recall <spell id> | portal <name> | npc <name>
/drakbot nav clear | use | save <name> | load <name> | list | import <file.nav> | export <file.nav>
/drakbot style melee | missile | magic
/drakbot buffs|combat|loot on|off
/drakbot los on|off | debug on|off
/drakbot meta load <name> | clear | on | off | state <name> | states | debug on|off | eval <expr> | status
/vt <anything VTank>      (translated by the meta engine)
```

`nav add` records the character's current position as a waypoint on the
draft route; `nav use` starts following the draft; `nav save` names it.
Profiles and routes are JSON under the plugin's storage folder
(`<config>/plugins/acdream.drakbot/profiles/*.json` and `routes/*.json`) and can
be edited by hand. `BotProfile` in `Profiles/BotProfile.cs` is the schema.

### Route steps

A route is the VTank set of steps, numbered the way a `.nav` file numbers
them, so a route recorded with VTank or RynthSuite walks here unchanged:
`nav import <file.nav>` reads one (and `nav save` keeps it as JSON), a
`.nav` dropped into the routes folder loads by name, `nav export` writes one
back. A route carries its own mode (`.nav` circular/linear/once map to
loop/ping-pong/once); a hand-built one follows the profile's.

- **Point** - walk there. A run of points already inside the arrival
  distance, or a point lying within 2 m of the straight line to the farther
  point after it, is skipped when the cursor lands on it, so a dense
  path-finder route or an over-recorded corridor does not stall the walk.
- **Pause** - stand for the seconds given.
- **Chat** - submit the line; a slash command runs, anything else is said.
- **Recall** - self-cast the spell, again every 4 s until the teleport
  shows, then stand for the profile's post-portal settle.
- **Portal** - find the named portal (the recorded position picks between
  same-named ones, else the nearest within 250 m), use it once, wait for the
  teleport, settle. The object is looked for again every 1.5 s until it is
  in the object table, since the world around a fresh arrival fills in over
  a moment.
- **NPC / Vendor** - walk to the point, use the named object, move on after
  a moment.

A teleport is recognised three ways: the character passed through portal
space, jumped more than 50 m, or changed landblock (a short-hop dungeon
portal moves less than 50 m). A teleporting step gives up after 60 s and
the route moves on. A teleport the route did not ask for - a portal walked
into, a recall cast by hand - is noticed the same way; the walk stops,
settles, and carries on from the current step.

## Testing

`tests/AcDream.DrakBot.Tests` drives every behavior through
`FakeAutomationSurface`, which records the commands the bot issued and lets
a test play the server's answers back (`CompleteCast`, `CompleteSwing`,
`CompletePickup`). Its `IProjectileAutomation` is scriptable per target and
aim height (`BlockPath`, `ClearPath`, `PathStatuses`) and records every sweep
asked for in `PathQueries`, separately from `Commands`; its
`IMovementProbeAutomation` is scripted by heading (`BlockedWalkHeadings`,
`DefaultWalkStatus`, recorded in `WalkQueries`) and `ObjectPositions` feeds
the approach step. `DrakBotPluginHostingTests` runs the real
`PluginSession` built-in path end to end.

## What is not there yet

In rough priority order:

- **Backing off.** The bot walks toward targets it cannot reach or shoot
  but never retreats from a melee monster while casting.
- **Doors on routes.** Portals, NPCs, recalls and chat lines are route
  steps now; a closed door in the way is still only handled by the stuck
  recoveries.
- **Missile ammo and weapon swapping** via `IEquipmentAutomation`.
- **DoTs and life magic in combat** - the monster list covers war
  shapes, rings and the creature debuffs; drains and DoTs are not cast.
- **Fellowship helpers** (heal a fellow, follow the leader) via
  `IFellowshipAutomation`.
- **Meta state machine.** RynthScript is the intended language; the engine
  needs an "expression surface" that exposes the blackboard to it.
- **Loot rule editing in the settings window.** Rules can be removed there
  but are authored in the profile JSON.
