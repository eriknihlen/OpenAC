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
      vitals         Survival    heal / revitalize / mana, idle top-off, heal fellows; healing-kit fallback
      buffs          Buffing     keep configured self-buff families, weapon auras and armor spells up
      pets           Buffing     summon a combat pet from an essence when hostiles crowd in
      manastones     Buffing     recharge worn items from stones; drain surplus loot into empty ones
      combat         Combat      target selection, line of sight, approach, swing or war spell; fletches ammo
      loot           Looting     open corpse, appraise on demand, pick up by rule or VTank .utl
      salvage        Salvage     salvage what was looted under a salvage rule; merge partial bags
      doors          Doors       open a closed door in the way of the walk (closed = the walk probe runs into it; the host's open flag alone is not trusted)
      nav            Navigation  follow a route through the Walker, or a player
    InventoryTidy    beside the behaviors: autostack and autocram
  Spells/            SpellSelector (name -> best known tier), SpellTierGate, CastTracker
  Combat/            TargetSelector, LineOfSightService, WeaponReadiness, AmmoCrafter
  Loot/              LootRule / LootRuleSet (own JSON format); Utl/ for VTank .utl
  Navigation/        Route / Waypoint, NavFile (.nav), RouteFollower, Walker, RouteActionRunner,
                     DungeonPathfinder (A*, patrols), DungeonHazards, Jumper
  Meta/              MetaEngine (states/rules), ExpressionEngine, MetaWorld, .af/.met parsers
  Profiles/          BotProfile (JSON), BotStore (plugin storage)
DrakBotRemotePlugin  ..DrakBot.Remote/: the phone's listener (status, feed, commands), off unless configured
```

Arbitration: behaviors are ordered by `BehaviorPriority`. Each tick the
engine asks, in order, whether anything strictly higher than the running
behavior wants control; if so the running one is `Interrupt`ed and loses the
tick. A running behavior otherwise keeps control while its `Execute` returns
`Continue`. `Done` and `Failed` release control and re-arbitrate next tick.
The Navigation tab's two boosts lift navigation or looting above combat
(VTank's navpriorityboost / lootpriorityboost); survival and buffing stay
on top, and a fight already under way is not left for a corpse.
Every behavior is written so that one step is one action (one cast, one
swing, one pickup), which is what lets a heal land between two swings.

Timing comes from `IBotClock`, advanced by the host's tick delta, so timeouts
and back-offs are deterministic in tests.

Every behavior waits while the host is mid-action (`Blackboard.IsActionPending`:
the client's busy count, a combat request or a server reply in flight). The
busy count is raised when an action is sent and lowered by the server's
reply; a reply that never comes (a door use cut short by a fight) would
leave the character busy for good - the bot standing in peace mode while
monsters circle - so the engine watches it: busy for ten seconds straight
and it clears one busy reference through `IRecoveryAutomation`
(`BotEngine.BusyStuckSeconds`, logged as a warning), and another every ten
seconds while it stays stuck, what `/ub clearbusy` does by hand. An
attack request the server never answered (a swing cut off by an
interrupt just as it went out) is aborted the same way after ten
seconds, and if the abort itself goes unanswered the stance is dropped
to peace ten seconds later - the one thing that resets the client's
attack state; combat takes the stance up again for its next target. The vitals and buff behaviours themselves wait at most eight
seconds on a pending action before giving the tick back, so nothing
else starves meanwhile; and any behaviour that holds control for a
minute is named in the log, with whether an action or a cast is pending.

## The monster list

The Combat tab's element keyword fights everything the same way. The
Monsters tab is the VTank-style list instead: one rule per kind of monster
(a regular expression over the name, or a substring when it is not one),
with a priority (higher first, zero never fought), an element (or Auto for
the profile's), the war spell's shape (bolt, arc, streak) and whether to
ring instead once the profile's minimum number of hostiles stand within
ring range, and the debuffs to land first - imperil, the element's
vulnerability, a second vulnerability, fester, yield, broadside, gravity
well, and a weapon to wield for it. The list always begins with
`Default`, which cannot be deleted: it is every monster no other rule
names, fought with the profile's settings. The rules below it are the
exceptions - a monster added to the list is fought its own way, and
priority zero (on a rule, or on Default itself) leaves it alone. Debuffs are cast in that order until
the client's record of what the character landed shows them on the
target; spells are chosen by family, so a lore-named top tier (Outlander's
Insolence for Force Streak VII) is reached through its lower tiers. The
tables of spell names are in `Spells/WarSpellNames.cs`; void magic stands
in for war when only it is known.

## Combat pets

The Monsters tab's pet section names the essence devices to summon from.
When at least the configured number of hostiles is within range and no
pet named "<character>'s ..." stands in the world, the first essence with
charges left is used; an empty one is refilled by applying an
Encapsulated Spirit from the pack, when that is on. Summoning must be
trained. VTank's summonpets, petmonsterdensity and petcustomrange
options map onto these.

## Mana stones

Below the pets on the Monsters tab. When on, a worn item under a quarter
of its mana has a charged stone from the pack used on the character
(once per five minutes at most, since the stone may not reach every
item). When the drain threshold is above zero, an unworn item in the
pack carrying at least that much mana - never a wand - is drained into an
empty stone, destroying the item. The looter then keeps mana stones from
corpses up to the keep count whatever the loot rules say. Nothing
happens while hostiles are in range. VTank's enablemanatapping,
manatapminmana and manastonelootcount options map onto these.

## Spell tiers by skill

Every cast goes through the tier gate: a spell's tier is allowed once
the character's skill in its school reaches that tier's minimum, on one
ladder for combat casts and another for buffs (both `0, 85, 135, 185,
235, 285, 335, 435` by default; the Buffs tab edits them). A spell whose
school is unknown, or a skill the host cannot read, is not gated. The
selector then takes the highest allowed known tier, so a low-skill
character casts what lands instead of fizzling on the top tier.

Spells are named the way the game names them, without a tier:
"Strength Self". The name finds the family - any tier of it the
character knows - and the family's top allowed tier is what is cast,
whatever it is called, so the lore-named sevenths and the "Incantation
of" eighths are reached without naming them. A tier without its
components in the pack is skipped for the next one down. Self and
Other tiers share a family in the spell table, so a name ending in
Self or Other keeps to its side: "Strength Self" is never Strength
Other VI, which wants a target and never lands on the caster. The
book's elements are spelt either way - Fire or Flame, Cold or Frost,
Pierce or Piercing, Bludgeon or Bludgeoning - and so may the name. A buff
the host calls cast three times running that is still due straight
after is rested for two minutes rather than cast for ever. `/drakbot
spells` shows, for every configured buff, the tier that would be cast
and whether it is up or due, or what stands in the way.

The engine watches the client's busy state as well: an inventory that
stays busy for ten seconds with no action of the bot's in flight - a
request the server never answered, or a busy count left over from one -
has its busy count cleared and, failing that, its pending request given
up (`IRecoveryAutomation.AbandonPendingInventoryRequest`), since it
blocks every wield, use and loot.

Before any cast - a buff or a vital - the bot puts a caster in hand and
the character in magic mode (`MagicModeGate`): the server drops a cast
sent from melee, missile or peace mode with a use-done that looks like
success, and will not enter magic mode with no wand, orb or staff
wielded. The caster is the one the Combat tab names for the magic
style when the character has it, else whatever caster is in hand, else
the first in the pack; the combat behaviour swaps the fighting weapon
back for the next fight. A heal the host calls a success that moved the
vital by less than a hundredth of its maximum - a heal of a hundred on
a hundred thousand - puts that vital's spell aside for five minutes,
and a kit takes its place: with kits on, the vitals behaviour applies
the first kit in the pack that restores the vital (health, stamina or
mana kinds, by the kit's booster) whenever the spell cannot be cast. A
kit heals by the Healing skill, which is what a character with such
vitals has.

## Weapons and ammunition

The Combat tab names a weapon per style (melee, missile, wand) and a
monster rule may name another; before an attack the bot wields the named
one through `IEquipmentAutomation`, dropping to peace mode while the swap
lands, and with the missile style keeps a stack of the wielded bow's
ammunition wielded (the largest matching stack, by the bow's ammo type).
A named weapon that is not in the inventory fails the step rather than
swinging bare-handed. Empty names leave the hands alone.

A bow with nothing left to fire is fed from wrapped bundles: the best
recipe the character can fletch (Fletching trained; Deadly and Lethal
Prismatic need it specialized) is combined twice in peace mode, a head
bundle applied to a shaft bundle, each combine confirmed by the item use
completion or the ammunition turning up, and the result is wielded. No
bundles, or a combine that never lands, fails the step as before.

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
- **Seeing it first.** Before anything else, and for every style, a
  hostile out of reach is looked at: a straight, flat sweep to it at the
  middle height, then the high and the low, cached like a shot. When the
  world blocks all three - the floor above, the far side of a wall, round
  a corner - it is *hidden*: not a candidate, no strike, no blacklist to
  wait out; it is looked at again each cache period and fought the moment
  it comes into view. A creature in the way is not a wall, but it is not
  a sighting either - the sweep says nothing about what lies past it -
  so for a melee walk a hostile *covered* by another creature is passed
  over and the creature in front, in plain sight, is the one fought (the
  ranged styles let their own sweeps decide; a shot may still arc over
  it). While walking at a target the bot keeps looking for it, twice a
  second: one that goes round a corner or through a door on the way is
  let go there and then, not steered after until the walk times out.
  The strikes and the blacklist below are for what can be seen but not
  shot or reached, for the host refusing to swing, and for a swing the
  server never finishes.
- **The server's reach.** *Melee range* is where the bot starts a swing;
  the server itself swings from about two metres and walks the character
  in for a farther one. Hemmed in - a swarm, a doorway - that walk never
  comes, so a swing still unanswered after three seconds with the target
  beyond two metres is abandoned and the bot walks in to two metres
  itself before swinging again.
- **Choosing a target.** Hostiles are ranked as before (distance is the
  straight line, height included; one more than *Ignore above/below*
  metres up or down is not a hostile at all); the first one with a
  clear path wins. A blocked target is never walked to: it earns a strike
  and is passed over, whatever its distance; after N strikes in a row it
  is blacklisted for a while and drops out of selection until the
  blacklist lapses or a later sweep finds it clear. Strikes are counted
  once per fresh sweep, so a cached verdict re-read every tick does not
  stack them. Combat only claims control for a hostile it can actually
  fight - one it can shoot, or one it can walk to - so a blocked or
  too-distant monster in view does not pre-empt navigation every tick.
- **A refused swing.** When the host will not start an attack on the
  chosen target (`Refused`: not attackable just then, the character
  mid-something the flags do not show), the target is held and tried
  again 0.3 s later rather than dropped for the next one - which the host
  refused the same way, fifteen targets in a third of a second. Three
  refusals in a row blacklist the target for the blacklist period.
- **Approach.** Melee only: a hostile out of reach is walked up to when it
  is within the walk-up range (*Walk up to* under Ranges, `ApproachRange`
  in a meta); one farther away is left alone until it comes closer or the
  route brings the bot to it, so a monster seen down a long hall does not
  drag the bot off its route. The walk holds a run toward the target,
  re-checks the way every tick, stops within reach and gives up after a
  timeout (three timeouts blacklist the target). The walk is one step like
  everything else: a heal interrupts it and the movement intent is dropped.
- **Walking with eyes open.** Before a melee hostile is chosen as
  something to walk to, the service walks the body straight toward it. A
  direct walk the world itself blocks - a wall, the floor of the room
  above, a door frame (the probe says "environment") - makes it no target:
  a strike per fresh sweep and passed over, blacklisted after three, so a
  monster on the floor overhead or behind a wall is left alone in a couple
  of seconds instead of being run at sideways; the steering fan is not
  consulted for it, because something is always open sideways. Only a
  direct walk blocked by something *in* the way - another creature, a
  door - is approached, along the first open heading of the fan (30, 60
  and 90 degrees to either side) out to the steering look-ahead. While walking, the direct
  heading is re-probed every tick; the bot steers along the first open fan
  heading and comes back to the direct one when it clears. When nothing in
  the fan is open it tries the navigation recoveries one at a time (back
  up, strafe left, strafe right) and looks again; the stuck detector covers
  whatever the probes did not model. Strikes are kept per sense: an open
  walk forgives walk strikes, an open shot forgives shot strikes, and the
  blacklist counts both.
- **Backing off.** With a ranged style and a back-off distance set, a
  hostile that gets that close is walked away from - straight away from
  it, steering round what is behind - until it is at the back-off range
  or the timeout passes, and then the next shot goes out.
- **Fail open.** No projectile collision on the host, a spent collision
  budget or an error all count as "no answer" and the shot goes ahead.
- **Diagnostics.** The Combat tab's "Draw the swept path" asks the client to
  mark every collision check in the world for the current target.

The sweeps live in the client (`AcDream.App/Plugins/ProjectilePathProbe.cs`
and `WalkPathProbe.cs`) and are covered by synthetic-landblock tests (walls,
steps, ledges, cliffs, bystanders, arcs) plus installed-dat tests against a
real dungeon's walls and ceiling.

## VTank loot profiles

The Loot tab's rules are the bot's own JSON. Name a VTank `.utl` profile
(from the bot's `loot/` folder, see *Files*; the client's shared `vtank`
folder is the fallback) and it decides
instead: the file is read and written as VTank writes it (`Loot/Utl`,
RynthSuite's parser), and every condition kind VTank evaluates is
evaluated here against the item record and its appraisal - name and
string matches, long and double property tests by the game's property
ids, spell name and count matches, damage figures, ratings, character
level, skill and pack room. The keys Decal numbered for its own fields
(type, icon, stack, slots, category, max damage, icon overlay, the armor
protections, variance, ...) are answered from the item record and its
appraisal. Colour rules pass optimistically, as they did in RynthAi.
Rules that judge by name and class decide before an appraisal; an
appraisal is asked for only when a rule that needs one could still
match. Keep, Salvage, Sell and KeepUpTo (up to the count already
carried) pick the item up; Read leaves it. `/vt loot load <name>` from a
meta selects a profile.

## Salvage

A loot rule whose action is Salvage (a `.utl` rule, or a profile rule
with `"Action": "Salvage"`) still picks the item up; once the pickup
lands the item is queued, and when no hostile is near and the corpses
are done, the queued items go to the Ust in one request. With **merge
partial bags** on, every half minute the under-full salvage bags of one
material and workmanship band (the `.utl`'s SalvageCombine block, else
1-6, 7-8, 9, 10) are salvaged together, which merges them. An item still
in the pack six seconds after the request is retried up to three times.

## Pack housekeeping

Two switches on the Loot tab, run beside whatever the bot is doing when
no action is pending and no corpse is open, one move per half second:
**merge partial stacks** puts the smallest partial of an item onto the
largest, and **move loose items into side packs** crams unworn items
(never packs or foci) out of the main pack into the fullest side pack
that still has two free slots, so new loot always has a slot. A move
that has not landed ten seconds later is backed off, doubling to five
minutes. VTank's autostack and autocram options map onto these.

## Dungeon patrols and paths

`IDungeonAutomation` hands the bot the loaded dungeon's cell graph: every
environment cell's centre and the cells it shares a doorway with (the
client's portal records - never what a cell can see, so no edge cuts
through a wall). `DungeonPathfinder` plans on it the way RynthAi's does:

- an edge that climbs or falls steeper than 45 degrees, centre to centre,
  is a drop the character cannot walk and is never taken;
- A* finds the shortest doorway-to-doorway path, routing around cells the
  operator marked as hazards (`/drakbot hazard add` marks the cell the
  character stands in; the marks are kept per landblock in the plugin's
  storage). A patrol never *tours* a hazard cell - none of its corridors
  is walked for its own sake - but it may *cross* one on the way from one
  safe part of the dungeon to another, at a stiff path cost, so an acid
  corridor between two halves does not confine the patrol to one half;
  the crossing takes seconds and combat never fights from inside a
  marked cell;
- a path is walked through the doorways themselves - the host reports
  each opening's polygon centre at floor level (`PluginDungeonCell.Doorways`),
  since a cell's origin is its model anchor rather than a point between its
  doors; a host that knows only the adjacency gets 30% and 50% of the way
  between origins instead - and ends at the exact destination; points
  within 1.5 m of the segment between their neighbours are dropped, but the
  far end of an out-and-back spur is kept;
- `/drakbot patrol` builds a looping patrol over the dungeon's main route
  (the cells on a cycle or between junctions, dead-end spurs stripped, or
  everything reachable in a small or linear dungeon): a closed walk that
  covers every corridor once and takes loop-closing edges, so a loop is
  walked round rather than in and out, closed back to the start. Standing
  off the main route (a dead-end spur, the entrance corridor), the route
  begins with a one-time lead-in along the doorways to the loop
  (`Route.LoopStart`), and the loop comes back to its own start, not the
  lead-in. A loaded loop or ping-pong route is joined at its nearest step,
  as VTank joins a circular route. Standing in a hazard, the walk starts
  from the nearest safe cell. The Navigation
  window's **Dungeon Patrol** button does the same; `/drakbot patrol stop`
  clears it.
- a route is rejoined by a path, not a straight line: when navigation gets
  control back after a fight or a heal and the walk probe says the step it
  was heading for is walled off (the fight dragged the character into
  another room), or when it stalls against a wall, it asks for a lead-in -
  the dungeon path through the doorways from the character's cell to the
  step's cell - and splices it in (`DungeonPathfinder.Rejoin`): a loop is
  rotated so the lap continues from that step and wraps through the ones
  before it. At most once every five seconds. When no lead-in helps (the
  path already ends in this cell: a ramp that does not start where the
  straight line meets the wall) the walk detours instead - the first open
  heading of a fan round the target (30, 60, 90, 120, 150 degrees either
  side, nearest first), walked for a second and a half before the route
  is aimed at again; the plain back-up and strafe recoveries are the
  fallback when nothing is open. A step detoured six times without being
  reached is given up and the next one aimed for - a point on the floor
  above with no ramp from here is not reached by walking at the wall all
  night - and it is remembered, with the cell it was given up from, per
  landblock in the plugin's storage (`givenup/<landblock>.json`), so the
  next lap and the next session skip it at once. Route points are simplified with height
  in mind: a point on the line on the map but off it in height (a ramp's
  landing) is kept, or the walk would go from one floor to the next
  through the wall.
- hazards are also sighted: once a second the bot looks at the objects in
  view and any named like a hotspot (lava, pool of acid, magma, cesspool,
  hot spring, pool of fire/cold) marks its cell; a new mark during a
  patrol rebuilds the patrol around it, resumed at the nearest step.
  Server-side invisible hotspots are not seen this way, but they are
  felt: "You suffer 47 damage from acid!" twice in the same cell within
  ten seconds marks that cell too, and combat never fights from inside a
  marked cell - the walk out comes first. The manual mark stays (the
  Navigation window has Mark / Unmark / Clear buttons).
- **Patrol on login** (Settings > Navigation) starts a patrol, and the bot,
  as soon as the character appears in the world inside a dungeon. The
  Marketplace and the Town Network are built of cells like any dungeon
  but are not dungeons to patrol; they are on the profile's no-patrol
  list (`Navigation.NoPatrolLandblocks`, hex landblock ids) and a patrol
  is refused there. A character teleported out of its dungeon has its
  patrol put down (or rebuilt, in another dungeon), and the login patrol
  re-arms for the next dungeon it enters.
- `/drakbot goto 41.5N 34.2E` plans a route to a coordinate and follows it.

`/drakbot follow <name>` (or `leader`, for the fellowship's leader) walks
after that player's live position instead of the route - setting off
beyond the resume distance, stopping within the stop distance, holding
when the player is not loaded - until `/drakbot follow off`.

While a route is followed, a closed door within the Navigation tab's
door range is opened before the walk goes on - used, watched, picked
with a lockpick from the pack when that is allowed, and given up on
after three tries for a while; a door just opened is not re-targeted
for a minute. VTank's opendoors and dooropenrange options map onto it.

A UtilityBelt-style jump - `/drakbot jump[w|x|z|c|s] [heading] [ms]`, also
as `/ub jump...` so UB metas work - faces the heading, holds the jump key
with the named movement keys for the given time, lets go, and takes the
character over from the behaviors until it lands.

## Metas

A meta is the VTank-style state machine that sits above the behaviors: a
set of states, each a list of rules `IF condition DO action`, evaluated
every tick in the current state, first match wins, each rule firing once
per visit to its state. It does not fight or walk itself; it flips the
profile's switches, loads routes, changes its own state, and talks in
chat, and the behaviors do the work. Ported from RynthSuite's MetaManager
and ExpressionEngine, so a meta written for RynthAi or VTank runs here.

- **Files.** `.af` (metaf text, what RynthScript compiles to) and `.met`
  (VTank binary) load by name from the bot's `metas/` folder (see
  *Files*; the client's shared `vtank` folder is the fallback):
  `/drakbot meta load <name>`, the Meta
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

The tool windows never hold the keyboard for having focus: the client's
overlay hands the keys to the game unless a text field in a window is
being typed into, so Enter still opens chat and the movement keys still
move with a bot panel in front. Mouse clicks on a window are the window's.

The bot's windows are Dear ImGui (`AcDream.DrakBot.Ui`), drawn by the
client's immediate-mode overlay (`ACDREAM_IMGUI=0` turns the overlay off; the
bot then falls back to a small retail-look status panel). Nothing is drawn
until the character is in the world; the dashboard then opens and the
other windows open from its launcher grid, laid out the way RynthAi's
dashboard is.

- **Dashboard** - RynthAi's dashboard, drawn the same way: a title bar
  with Lock, opacity -/+, minimise and close; RUNNING/STOPPED with its
  light, the meta state and the bot's activity (hover for the reason) on
  the left, the Profile / Nav / Loot / Meta pickers on the right; the
  combat panel with the square toggles (combat, buffing, navigation,
  looting - left-click toggles, right-click opens the settings or window),
  the MACRO toggle and FR (force rebuff) beside the target's name and
  segmented health bar and the player's HP/ST/MN rows; and the launcher
  grid: Macro Rules, Monsters, Settings, Navigation, Items, Log, Dungeon
  Patrol, Save Profile. Minimised, only the combat panel and a small
  ON/OFF button remain; lock, collapse and opacity are saved with the
  profile (`DashboardSettings`) so the window comes back as it was left. The target's line-of-sight detail lives in the Log.
  The icons are [Phosphor](https://phosphoricons.com) glyphs (MIT,
  `assets/fonts/`): the client merges the Phosphor font into the overlay's
  UI font at the web font's code points (U+E000-U+F8FF), so any plugin
  draws an icon as text; `PhosphorIcons` in `AcDream.DrakBot.Ui` names
  the ones the windows use and `DashboardDrawing.DrawIcon` draws one at
  its own size and colour.
- **Macro Rules** - RynthAi's meta editor: the loaded rules grouped by
  state, the row that just fired flashing red, up/down/delete per row, a
  two-pane editor (nested All/Any/Not conditions on the left, the action
  or an All body on the right, with pickers for states, routes, watchdogs
  and options), a pop-out editor for long expressions (right-click a
  field), a Source view that round-trips the `.af` text, load/save over
  the bot's `metas/` folder, and the current-state picker. Edits to a
  meta loaded from an `.af` are written back to it.
- **Monsters** - the monster list as a grid: toggle lights for the debuffs
  (Fester, Broadside, Gravity Well, Imperil, Yield, Vulnerability) and the
  war spell shape (Arc, Bolt, Ring, Streak), then name, priority, element,
  second vulnerability, weapon and delete; add by name, from the current
  target, or the Default rule.
- **Settings** - a list of sections down the left (Recharge, Combat, Ranges,
  Buffing, Looting, Navigation, Pets & Doors, Priorities), the section's
  controls on the right. Every widget edits the live profile; `Save`
  persists it under the name in the box. Combat carries the line-of-sight
  options; Ranges the engage, ring and reach distances.
- **Navigation** - the active route and what the walk is doing, Start/Stop,
  route type and where new steps go (end, above or below the selected
  step), buttons that record a waypoint, portal, NPC or vendor (with the
  nearby ones listed), recall, pause or chat step, the step list with the
  current step marked, and a load/save bar over saved routes and `.nav`
  files. Edits to the route being walked take effect at once, resuming at
  the nearest step.
- **Items** - the weapon each style wields (typed, or taken from the item
  selected in the inventory), the ammunition switch, mana stone tapping,
  and what is wielded now with its mana.
- **Log** - the bot's own log ring (4000 lines): what it decided (Info:
  behavior changes, targets, steps, casts, corpses, patrol builds), why
  (Debug: heading errors, walker state, loot decisions, every step of a
  planned route with its cells and doorways, meta rules fired, commands)
  and, at Trace, everything every tick. A level picker, text filter,
  pause/follow, Copy to the clipboard, Dump to `logs/drakbot-log.txt` in
  the bot's folder and an Open folder button;
  `/drakbot log quiet|info|debug|trace | tail [n] | dump |
  clear` does the same from chat. Everything at or above the level also
  goes to the client's log file (`<data>/logs/client-<date>.log`, rolled
  daily, 14 kept), so a session can be read back afterwards.
- **Route markers in the world** - the route being walked (or the draft)
  is drawn on the ground: a cyan ring at every travel point within 150 m,
  amber for NPC and vendor steps, red for the step being walked, and a
  strip from each point to the next (a loop closes back on its start).
  They are geometry in the scene, as RynthAi's Nav3D rings are: a flat
  band with a short wall standing on it, drawn after the world with its
  depth, so a wall hides them, the character stands on them and the UI
  sits over them (`IImmediateUiHost.AddWorldRing` / `AddWorldLine`; the
  client's `WorldMarkerRenderer` draws what was queued in the next frame's
  world pass). Settings > Navigation turns them off and sets the ring
  radius, the band width and a height offset. On a host without world
  geometry the markers fall back to lines projected onto the overlay
  (`TryProjectToScreen`), with the step numbers.

Plugins get the same facility through `IPluginHost.ImmediateUi`: register a
draw callback and call `ImGui.*` inside it (ImGui.NET is shared from the
host, so a plugin's calls land in the host's context). ImGui is for
editor-shaped tooling; player-facing windows stay markup so they wear the
game's own look.

## Commands

```
/drakbot start | stop | status | rebuff
/drakbot spells        (what the character can cast; each configured buff: what would be cast, up or due, or what is in the way)
/drakbot spells <name> (one spell by name - "Heal Self" - and why it is or is not castable)
(a file named commands.txt in the bot's folder, one command per line, is read and deleted once a second and each line run as if typed)
/drakbot folder [profiles|routes|loot|metas|logs]     (where the bot's files are; opens it)
/drakbot log quiet|info|debug|trace | tail [n] | dump | clear
/drakbot profile list | load <name> | save [name] | reset
/drakbot nav add | pause <seconds> | chat <text> | recall <spell id> | portal <name> | npc <name>
/drakbot nav clear | use | save <name> | load <name> | list | import <file.nav> | export <file.nav>
/drakbot style melee | missile | magic
/drakbot buffs|combat|loot on|off
/drakbot los on|off | debug on|off
/drakbot meta load <name> | clear | on | off | state <name> | states | debug on|off | eval <expr> | status
/drakbot patrol | goto <NS> <EW> | hazard add|remove|clear
/drakbot follow <name> | leader | off
/drakbot jump[w|x|z|c|s] [heading] [ms]      (also /ub jump...)
/vt <anything VTank>      (translated by the meta engine)
/mt | /ub opt list | get <name> | set <name> <value> | remember <name> | restore <name>
/mt | /ub combatstate peace|melee|missile|magic | face <degrees> | cast[p] <id|name> [on <target>]
/mt | /ub use[i|l][p] <name> [on <name>] | use closestnpc|closestvendor|closestportal | select[p] <name>
/mt | /ub give[p] <item> to <target> | loot[p] <name> | drop[p] <name> | equip[p] <name> | dequip[p] <name>
/mt | /ub fellow create <name> | open | close | disband | quit | recruit <player> | send <chat> | logoff
/ra give[p|xp|pp|r] [count] <item> to <player> | givea[p|xp|pp|r] <item> to <player> | givea stop
/ra ig[p] <loot profile> to <player> | mexec <expression> | start | stop | clearbusy   (plus every /mt verb)
```

`/mt` and `/ub` are the MagTools / UtilityBelt verbs metas lean on
(RynthAi's set). A `p` suffix matches part of a name; `i`/`l` look only
in the pack or only on the landscape; `loot` waits up to eight seconds
for the corpse to open. Names resolve through the meta's view of the
world, the nearest landscape match winning. `/ra` adds RynthAi's give
family: `givea...` queues every matching pack stack (`r` takes a regular
expression, `xp`/`pp` match part of the player's name) and `ig` queues
whatever a `.utl` profile would keep; the queue hands over one stack a
quarter second, `givea stop` empties it.

`nav add` records the character's current position as a waypoint on the
draft route; `nav use` starts following the draft; `nav save` names it.
Profiles and routes are JSON in the bot's folder (see *Files*) and can be
edited by hand. `BotProfile` in `Profiles/BotProfile.cs` is the schema.
Every change made in a window, by a command or by a meta is saved under
the profile's own name a second after the last change (a dragged slider is
one write) and on shutdown; the profile in use is recorded
(`last-profile.txt`) and is the one the bot starts with next time, along
with the route it names (`Navigation.RouteName`, set whenever a route is
loaded by name) and its meta. **Save Profile** is only needed to save
under another name.

## Files

Everything the bot reads or writes is in one folder, laid out by kind, so a
player never has to know the client's conventions: **Open folder** buttons
sit beside the loot profile field (Settings > Looting), the route picker
(Navigation) and the log's Dump button, and `/drakbot folder
[profiles|routes|loot|metas|logs]` says where it is and opens it.
`BotFiles` in `Profiles/BotFiles.cs` is the layer.

```
<config>/plugins/acdream.drakbot/        Windows: %AppData%\acdream\plugins\acdream.drakbot
  profiles/   name.json                  bot profiles (auto-saved; last-profile.txt names the one in use)
  routes/     name.json, name.nav        bot routes, and VTank .nav routes dropped in
  loot/       name.utl                   VTank loot profiles
  metas/      name.af, name.met          VTank metas (saved metas are written here as .af)
  hazards/    XXXX.json                  marked hazard cells, per landblock
  meta/       gvars.txt, pvars/*.txt     meta variables
  logs/       drakbot-log.txt            log dumps
```

The folders are created on start so an empty install shows where things
go. Names are file names without the extension: a loot profile saved by
VTank as `--+Buffy_Aeshnidae.utl` is `--+Buffy_Aeshnidae` in the profile
field. VTank-format files a player already has in the client's shared
folder (`<data>/vtank`, Windows `%LocalAppData%\acdream\vtank`: its root,
or its `metas` and `navs` subfolders, since VTank kept everything flat and
RynthAi sorted them) are still found, as a fallback, and listed in the
pickers; a file of the same name in the bot's folder wins, and anything
the bot saves goes to its own folder. `.usd` files are VTank's settings
and are not read.

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
- **NPC** - walk to the point, use the named object, move on after a
  moment.
- **Vendor** - walk to the point, open the shop, and sell every unworn
  pack item the loot rules mark Sell (a `.utl` Sell rule, or a profile
  rule with `"Action": "Sell"`), one every half second; move on when
  nothing is left, or after eight seconds if the shop never opens.

A teleport is recognised three ways: the character passed through portal
space, jumped more than 50 m, or changed landblock (a short-hop dungeon
portal moves less than 50 m). A teleporting step gives up after 60 s and
the route moves on. A teleport the route did not ask for - a portal walked
into, a recall cast by hand - is noticed the same way; the walk stops,
settles, and carries on from the current step.

## The phone remote

`src/AcDream.DrakBot.Remote` is DrakBot Remote, a second built-in plugin
(`acdream.drakbot.remote`) registered after the bot that lets a phone watch
and drive one client: the [DrakRemote](https://github.com/tombohar/DrakRemote)
iOS app is its client, ported from RynthSuite's RynthRemote. It is an
HTTP + WebSocket listener inside the client, its own small server over a
plain socket (no http.sys reservation on Windows, the same on Linux), that
serves what the bot and the character are doing and takes commands. It
never touches the game from a request thread: the plugin tick builds the
documents it serves and applies the commands it queued, so a phone tap
lands the way a chat command does.

**It is off unless configured.** No socket is opened without a port. Turn
it on with `/remote setup <port> [token] [lan]`, which writes
`remote.json` in the plugin's folder
(`<config>/plugins/acdream.drakbot.remote/`) and starts listening:

```
/remote setup 8740                  this PC only, no token
/remote setup 8740 mytoken lan      every interface, token required
/remote status | on | off
```

`remote.json` is `{"enabled":true,"port":8740,"bind":"any","token":"..."}`;
a headless session sets the same keys in `pluginSettings` under
`acdream.drakbot.remote` and names the plugin in `plugins`; the
environment overrides both (`ACDREAM_REMOTE=1`, `ACDREAM_REMOTE_PORT`,
`ACDREAM_REMOTE_TOKEN`, `ACDREAM_REMOTE_BIND=any`). Listening on every
interface without a token is refused. The token travels as
`Authorization: Bearer` or `?token=`; `/healthz` alone is open. A
multi-box needs no configuration per client: a client whose port is taken
walks up to the next free one (ten ports from the configured one), and the
app looks for its siblings the same way.

What it serves, in the shape RynthCore's StatusAgent served so the app's
screens carry over:

- `GET /status` - one document with this client: state (loading, idle,
  botting, wedged, hung), vitals, the bot's activity and reason, the meta
  state, the target and its health, the four module switches and the meta
  switch, the profile / route / loot / meta pickers with their lists, kills
  and rates, XP and deaths this session (from the character's own
  properties), burden, free slots, scarabs by tier and tapers, the worn
  gear with its appraisal, position and area, the last sixty chat lines,
  the last warning, every enchantment in force (`enchantments`, soonest
  to lapse first, named through the spell catalog) and the bot's own buff
  list as it stands (`buffPlan`: each configured self buff, weapon aura
  and armor spell per piece, what it resolved to, time left, whether it
  is due by the profile's threshold, and why a name could not be cast -
  `SelfBuffBehavior.Report`). `GET /statusfeed` is the same over a WebSocket,
  pushed within ~150 ms of a change - and only of a change: a document
  that differs from the last only by its timestamp is held back, going
  out every three seconds so the feed still reads as alive. The document also states the
  server's `capabilities` so the app hides what a host cannot do.
- `POST /command` `{"action":..,"value":..}` - `macro`, `combat`,
  `buffing`, `navigation`, `looting`, `meta` (on/off), `navProfile`,
  `lootProfile`, `metaProfile`, `settingsProfile` (an index into the lists
  the status carried, or a name; -1 or `none` clears), `forceRebuff`,
  `cancelRebuff`, `clearBusy`, `sendChat`, `botCommand` (a `/drakbot`
  verb), `moveStart` / `moveStop` (`forward`, `back`, `left`, `right`,
  `strafeleft`, `straferight`, `stop`), `assess` (an item id),
  `setSetting` (`{"key":"vitals.healBelow","value":0.5}`), `closeClient`,
  `hideUi`. A held direction is a dead-man's switch: the app re-sends the
  start every third of a second and a hold that goes quiet for two
  seconds is let go of, so a dropped connection never leaves the character
  running.
- `GET /inventory` - the whole pack: worn, main pack, side packs, with
  the appraisal of anything already assessed and an `appraised` flag, so
  the app can offer a tap-to-assess; re-read every second, its `version`
  moves only when something changed.
- `GET /settings` - the live profile as a flat form (`vitals.healBelow`,
  `combat.style`, ...; lists of names read-only, the monster and loot
  rules left out), and `setSetting` writes one key back, refused unless
  the whole profile still parses.
- `GET /icon?did=N` - an item's icon as a PNG, rendered by the graphical
  client from its data files on the tick; a headless host has none.
- `GET /frame?q=55&w=720` and `GET /stream?fps=5&q=55&w=720` - the live
  view: the frame the client just presented as a JPEG, or an MJPEG stream
  of them (`multipart/x-mixed-replace`) at the asked rate, quality and
  width. The frames are the renderer's own: while a phone watches, the
  GPU keeps a host-readable copy of each presented frame
  (`IGpuDevice.RetainBackbufferCapture`, the screenshot path's copy),
  `RemoteFrameCapture` in the client reads it on the frame thread right
  after the frame closes and encodes on the pool, and requests that
  arrive together share one capture; five seconds after the last request
  the copy is released, so an idle client pays nothing. A minimized
  window has no surface to present to, so while a phone is watching the
  client draws each requested frame into offscreen images at the size the
  window last had (`VulkanGraphicsContext.PrepareOffscreenFrame`, an
  `IVulkanBackbuffer` that never presents) - and only then, so a
  minimized client nobody watches still renders nothing; the swapchain
  comes back when the window does. The status still says `isMinimized`.
- `/runs`, `/maps` and `/video` answer as absent: the run archive, dungeon
  maps and the H.264 stream of the RynthCore agent are not here yet, nor
  is tap-to-click in the live view.

`tests/AcDream.DrakBot.Remote.Tests` drives the document builders and the
command routing against the bot's fake surface, and the listener with a
real `HttpClient` and `ClientWebSocket`, including the whole hosted round
trip: a session port, a `POST /command`, the next tick applying it, the
feed pushing the change.

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

- **DoTs and life magic in combat** - the monster list covers war
  shapes, rings and the creature debuffs; drains and DoTs are not cast.
- **Learned resistances.** RynthAi remembers each creature's appraised
  resistances and picks the weakest element under `Auto`; the client
  keeps the appraisal armor profile in the UI only, so the surface has
  nothing to learn from yet.
- **Loot rule editing in the settings window.** Rules can be removed there
  but are authored in the profile JSON.
- **Terrain passability overlays and the radar wall renderer** - the
  route markers are drawn now; those two need cell surface data the
  contract does not carry.
- **The remote's H.264 stream, tap-to-click, run archive and dungeon
  maps** - the RynthCore agent had them; the remote serves an MJPEG live
  view and answers the rest as absent for now.
