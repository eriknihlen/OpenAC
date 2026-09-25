# Navigation

How the client walks, runs and jumps the character to a goal on its own: a grid built from the collision world, a route planned over it, and a motor that drives the route with scripted moves. It runs in the graphical client and in the headless host alike, and plugins reach it through `IPluginHost.Automation.Navigation`.

Layers, from the caller down: `AcDream.Plugin.Abstractions` → `AcDream.Runtime` (hosted by `AcDream.App` or `AcDream.Headless`) → `AcDream.Core`

## Contents

- [The idea](#the-idea)
- [Two ways to move](#two-ways-to-move)
- [The layers](#the-layers)
- [How a walk flows](#how-a-walk-flows)
- [Design decisions](#design-decisions)
- [Chat commands](#chat-commands)
- [Calling it from a plugin](#calling-it-from-a-plugin)
- [Reading the reports](#reading-the-reports)
- [What a walk does](#what-a-walk-does)
- [Arriving](#arriving)
- [Following](#following)
- [The grid and the router](#the-grid-and-the-router)
- [Leaps](#leaps)
- [Driving a route](#driving-a-route)
- [Headless](#headless)
- [MossTank](#mosstank)
- [Seeing it in the client](#seeing-it-in-the-client)
- [Known limits](#known-limits)
- [Tests](#tests)
- [Where it lives](#where-it-lives)

## The idea

A caller never holds keys. It asks for a move, a jump or a walk, and gets an answer at once: `Accepted`, `Rejected` or `Unavailable`. The client then carries the request out one frame at a time and publishes how it is going in a report.

- **Every request gets a sequence number.** A caller keeps the number it started with and reads the report until it shows a final state, or a later number that means another request replaced its own.
- **Routes come from the client's own collision world:** terrain, dungeon cells, building shells, placed objects, and the objects the server placed that stand where they are, whose tops are floor a walk stands on and a jump lands on. Walks steer around doors, corpses, players and creatures as they stand now, since those move or come and go. Grids are built on demand from the landblocks in memory. Nothing is precomputed or shipped.
- **No GPU.** Planning, walking and jumping are plain C# over the physics world. Only the debug overlay draws.
- **Moves combine the way held movement keys do.** Travel, strafe and turn are separate channels, so a caller can run while it turns, and a new move replaces only the move on its own channel.
- **The player always wins.** The player's own movement keys end every move and every walk at once, whatever the walk is doing: planning, walking, waiting on a plugin, waiting on a door, or settling after a landing. Portal space ends them too.

## Two ways to move

The motor described here is added beside the steering that was already there, and replaces none of it.

| | Steering | Motor |
|---|---|---|
| Calls | `SetMovementIntent`, `ClearMovementIntent`, `FaceHeading` | `Move`, `StopMoving`, `Jump`, `GoTo`, `StandOn`, `Follow`, `StopGoTo` |
| How it moves | Holds movement the way keys are held; the caller steers every frame. | The client carries out a move, or plans and walks a whole route, and reports how it ended. |
| Used by | MossTank's own route following, by default | `/nav`, `/motor`, MossTank when its straight walk is stuck, or with **Client pathing: Always** |

A scripted move overrides a held intent while it lasts, and a walk waits while an intent is held. Mixing the two on purpose, such as a `/motor` move while a plugin steers, is not arbitrated.

## The layers

| Layer | Project | What it does | Key types |
|---|---|---|---|
| Callers | consumers | Ask for moves and walks, and read the reports. | `/nav` and `/motor`, MossTank's route legs, any plugin |
| Contract | `AcDream.Plugin.Abstractions` | What any plugin can call. | `INavigationAutomation`, `PluginGoToReport`, `PluginMoveReport` |
| Navigation API | `AcDream.Runtime` | One implementation of the contract, handed out by both hosts, and the chat commands over it. | `RuntimeNavigationAutomation`, `NavigationChatCommands` |
| Walks | `AcDream.Runtime` | Turns a walk, a stand-on or a follow into grids, a route and a drive; recovers and plans again when stuck, opens doors, passes crowds, waits, and goes in stages. | `NavigationWalkController`, `RuntimeNavigationWalkBody`, `RuntimeNavigationGoalSource`, `RuntimeNavigationDoors` |
| Driving | `AcDream.Runtime` | Walks a route's legs and flies its leaps with scripted moves, frame by frame. | `RuntimeRouteDriver`, `RuntimeScriptedMovement` |
| Planning | `AcDream.Core.Navigation` | Geometry with no client state: the grid, route search and leaps. | `NavGeometry`, `NavGrid`, `NavRouter`, `NavRoute`, `NavLeapFinder` |
| Hosts | `AcDream.App`, `AcDream.Headless` | Construct the same Runtime objects over their own physics world, tick walks before the player's frame, and register the chat commands. Only the graphical client draws. | `NavigationWalkFramePhase`, `NavMeshDebugOverlay`, `HeadlessSessionHost` |

Each layer knows only the one below it. The contract is plain .NET so plugins build against it alone; Core has no client state, so the planner runs in tests from shapes built in code; Runtime owns the moving parts, so both hosts get one implementation.

## How a walk flows

```text
  /nav go to, /nav follow, a plugin's GoTo / StandOn / Follow
          |
          v
  RuntimeNavigationAutomation ---- validates, answers Accepted / Rejected / Unavailable
          |
          v
  NavigationWalkController  (update thread, ticked before the player's frame)
     |  1. locate the goal (object, place, top, player)
     |  2. capture geometry: NavGeometry.Capture reads the physics world  (update thread)
     |  3. build the grid:   NavGrid.Build                               (worker task)
     |  4. search a route:   NavRouter.Find / FindOnto / FindToward      (worker task)
     |  5. collect the route next frame, narrate it, hand it to a driver
     v
  RuntimeRouteDriver  -- each frame: sample the body, return moves to begin or stop, a jump to charge
          |
          v
  RuntimeLocalPlayerMovementState  -- scripted moves on travel / strafe / turn, charged jumps
          |
          v
  RuntimeScriptedMovementInputSource  -- swaps scripted input in for the frame, or passes the
          |                              player's own input through untouched when nothing runs
          v
  player frame  ->  physics
```

**Threads.** The physics world belongs to the update thread, so everything that reads it (capturing geometry, locating objects, sampling the body, driving) happens there. Building a grid and searching it touch only the captured copy, so they run on worker tasks and never hold up a frame. A finished build or search is taken up on the next tick; a search's own notes for narration are queued and said then.

**Reports.** Every request gets a sequence number and a report the controller publishes under a lock, so plugins and chat commands read it from any thread. A newer request replaces the one under way, and its report carries the higher number.

**Lifetimes.** A grid is kept for the next walk nearby and let go once what it was built over unloads, or once 30 seconds pass with no walk needing it (the debug view pins it while it is on); the next walk rebuilds one in well under a second. A walk ends on arrival, when it cannot finish, when something stops it, or when the player's own movement input takes the character. A follow never ends by itself.

## Design decisions

**A grid of standing points, not polygons or physics sweeps.** The planner samples the collision world into 0.25 m columns and keeps the tops a body can stand on, with the headroom, wall clearance and step links a body needs. That turns floors, stairs, roofs, dungeon levels and object tops into one searchable graph, and the same columns answer the questions leaps and sight need. A search over it is fast enough to plan across a town or a whole dungeon. The price is that it is an approximation: a gap a quarter column narrower than the body can look walkable, and bugs in how columns are read show up as routes the real body cannot follow. Checking planned legs and leaps against the physics engine before walking them is the intended next step, not a replacement for the grid.

**The walk layer lives in Runtime.** The headless host depends on Runtime, not on the graphical client. Walks, their sources, the plugin navigation API and the chat commands sit in Runtime so both hosts construct the same objects and plugins behave the same in either. The graphical client adds only drawing. For a grid to come out the same in both hosts, both must load the same collision, so the loading of objects placed inside cells is shared from `LandblockPhysicsContentBuilder`; the headless host did not load their collision before. A parity test builds grids from either host's collision and compares them.

**The motor is additive.** Held-intent steering (`SetMovementIntent`, `FaceHeading`) and plugins that steer themselves are unchanged and remain the default. The motor wraps the player's input source and does nothing until a move is asked for. A plugin opts in by calling the new members, and MossTank opts in per profile.

**The player always wins.** Every frame of the player's own movement input is counted, and any walk sees the count grow whatever it is doing, including planning, waiting and settling after a landing. There is no arbitration between the player and a bot; the bot yields.

**Arrival means something specific.** A walk to an object ends beside it in sight of its side; a place ends on that place's floor; a stand-on ends on the object's top; a follow ends on the player's floor or holds. Routes that only get near are reported as such (`ArrivedWithoutSight`, `NoRoute`) rather than as arrivals, because a bot acting on "arrived" must be able to trust it.

**Recover before giving up.** Planning again from the spot a body is pressed against rarely frees it, so a walk that stops making progress tries a different way off what it met at each stop (back, the roomiest spot nearby, a sidestep each way, a hop) before planning again. A walk ends blocked only after the whole ladder, so its caller gets an answer; a follow starts over instead, because bots play for hours. Keep-out spots are added only for the first stops, and dropped when keeping out of them leaves no route, so a body stuck on the only way on, such as a narrow stair, does not ring itself in.

**Leaps come from the character.** Jump height, run speed, the safe drop and the physics step all come from the character and the client's physics, and a leap's reach is how far that character actually carries. Jumps are aimed to come to rest with room to spare even when flown a little off, so the plan is one the character can repeat.

**Follow chases where the player is, not where they went.** A follow plans the way to the player's current position and replans as they move. It copies what cannot be planned from positions alone: a portal the player entered, a jump they are still in the air from. It does not retrace the player's trail of positions: walking back through where the player has been goes wrong on multi-level terrain, where old positions can be on another floor.

**The debug narration tells the whole story.** `/nav debug` and its file are meant to let a developer replay a player's report without being there: where a walk started, the grid and search inputs, every route point and leap, what hid a goal from each spot around it, where and how a walk stuck against how near the walls were, and why a follow held or slept. When a playtest bug could not be explained from the narration, the missing detail was added to it.

## Chat commands

`/nav` and `/motor` register through the same command registry plugins use, so they work in the client's chat and on the headless console.

### `/nav`

| Command | What it does |
|---|---|
| `/nav go to target` | Walks to the selected object. `/nav go to` alone does the same. |
| `/nav go to 0x7A000123` | Walks to an object by id. |
| `/nav go to Town Network Portal` | Walks to the nearest object with that name, within 1,000 m. |
| `/nav go to 0x7E0307A3 [310.353577 -312.687317 6.005000] 1.000000 0.000000 0.000000 0.000000` | Walks to a place given as `/loc` prints it: a cell and a point in its landblock, and a heading that is ignored. `0x`, the brackets, the heading and a leading `Your location is:` are all optional. |
| `/nav go to [68.145149 -60.000000 0.005000]` | A bracketed point alone lies in the landblock of the cell the character stands in. |
| `/nav go to 24.3N 101.1W` | Walks to map coordinates, standing on the ground there. |
| `/nav stand on target` | Walks onto an object and stands on its top. Takes a target, an id or a name, like `go to`. |
| `/nav follow Bob 5` | Follows a player, keeping within 5 m behind them (3 m when no number is given), until stopped. Takes a target, an id or a player's name. |
| `... within 4` | Ends any `go to` or `stand on` within that many meters instead of 2.5. |
| `/nav stop` | Ends the walk under way. |
| `/nav status` | How the latest walk stands. |
| `/nav grid` | Shows or hides the walkability grid. |
| `/nav route [target \| 0xID \| name]` | Plans and draws a route without walking it. |
| `/nav debug [on \| off]` | Narrates walks in chat, each line starting `[nav]`: where a walk starts and heads, the grids it builds, every route point and leap, and what happens along the way. |
| `/nav debug file PATH` | Writes the same narration to a file, one timestamped line each. `/nav debug file off` stops. `~/` means the home folder. |

Walks, routes and moves say nothing in chat about how they start and end unless `/nav debug` is on; answers to what was typed, such as a status, usage, or a problem with the command, always show. `/nav grid` and `/nav route` draw, so in the headless host they answer that there is nothing to draw on.

### `/motor`

| Command | What it does |
|---|---|
| `/motor walk\|run [forward\|backward] [AMOUNT]` | Travels on the travel channel. |
| `/motor strafe left\|right [AMOUNT]` | Strafes. |
| `/motor turn left\|right [DEGREES]` | Turns in place, to the exact angle. |
| `/motor turn to HEADING` | Turns to a compass heading. |
| `/motor jump [POWER]` | Charges and releases a jump; power above 0 and at most 1. |
| `/motor stop [travel\|strafe\|turn]` | Stops every move, or one channel. |
| `/motor status` | The latest move on each channel and the latest jump. |
| `/motor run forward 20s + turn left 90` | Starts moves on different channels in the same frame. Two moves on one channel are refused. |

An AMOUNT is meters, degrees for a turn, or seconds written as `20s`. With no amount a move keeps going until stopped, for at most 30 s. With `/nav debug` on, chat says how each move ended.

## Calling it from a plugin

Everything is on `host.Automation.Navigation`. A host without navigation answers `Unavailable`, so the same plugin runs on any host.

### Where things are

| Member | What it does |
|---|---|
| `Snapshot` | The character's cell, map position and heading; whether it is moving, in the air or in portal space; and the last position the server confirmed. |
| `TryGetObject(id, out obj)` | An object's position. Doors also say whether they are open or locked. |
| `TryFindObject(name, near, meters, out obj)` | An object found by name near a position. |
| `CaptureObjects()` | A detached list of world objects, for a plugin's own rules. |
| `CheckRoomAhead(meters)` | Whether a body the size of the character fits that far straight ahead, as a summoned pet needs: `Clear` with the spot, `Blocked`, or `Unknown` when the client cannot look (not in the world, spot not loaded, distance outside 0 to 10 m). Walls, buildings, terrain and solid objects count; creatures and players do not. Ground up to 70 cm higher or 66 cm lower than the feet is tried too. Nothing moves. |

### Moves

| Member | What it does |
|---|---|
| `Move(direction, pace, amount, unit)` | A move on one channel, in meters or degrees, or in seconds. An amount of 0 keeps going until stopped, for at most 30 s. A move ends by itself once it covers its amount, stops making progress or runs out of time. |
| `StopMoving()`, `StopMoving(channel)` | Ends every move, or the move on one channel. |
| `Jump(power)` | Charges a jump and releases it. Power is above 0 and at most 1; a full charge takes 1 s. |
| `MoveReport` | The latest move on each channel, with its state, how far it got and how long it took, and the latest jump. |

### Walks

| Member | What it does |
|---|---|
| `GoTo(objectId, arrivalMeters)` | Walks to an object and ends beside it, facing it, within `arrivalMeters` (above 0, at most 50) at a spot with a line of sight to it. |
| `GoTo(position, arrivalMeters)` | Walks to a place and ends on the floor that place stands on. Faces nothing on arrival. A position with no cell, such as one read from a route file, is placed by its map coordinates alone, and an elevation of NaN stands it on the ground there. |
| `PreviewPathAsync(objectId, arrivalMeters)` | Plans a path to an object and returns ordered cell-aware positions with elevation. Does not start or replace a walk. |
| `PreviewPathAsync(position, arrivalMeters)` | Plans to a place on its specified floor, including inside a dungeon. Does not start or replace a walk. |
| `StandOn(objectId, arrivalMeters)` | Walks onto an object and ends on its top, jumping up where the character can. |
| `Follow(playerId, bufferMeters)` | Follows a player until stopped: see [Following](#following). Only players can be followed. |
| `StopGoTo()` | Ends the walk under way; `Rejected` when there is none. |
| `GoToReport` | The latest walk: its state, meters left, how often it planned again, a reason, and what blocked it. |
| `PauseGoToWhile(need)` | Registers a callback asked every frame a walk is under way. While it returns a reason, such as `"fighting a monster"`, the walk stops and waits. Dispose the result to unregister. |

### Path previews

`PreviewPathAsync` returns a `Task<PluginNavigationPlan>`. Start the call on the thread that raises `host.Events.Tick`, including after an `await`: an async continuation may run on a worker and must wait for a later Tick to request another preview. The method reads the character and world and captures collision before it returns. Await the returned task outside the tick handler, or poll `IsCompleted` on later ticks. Grid building and route search run on a worker; a large dungeon can take time. The returned `Path` is a detached sequence of route leg points from the character toward the goal. Each `PluginNavigationPosition` includes a cell id, map coordinates and elevation; heading is zero because a route point does not face a direction. `LengthMeters` is the planned route length.

`Status` is `Routed`, `NoRoute`, `Unavailable`, `InvalidTarget` or `Failed`. `Failed` means the grid build or route search threw an error; `Reason` contains its message. For any result other than `Routed`, `Path` is empty. A preview covers one planning region: a far outdoor destination beyond 320 m returns `NoRoute`, while a sealed dungeon can use a grid up to 2,048 m wide. Plans use collision and nearby objects as they stood when requested. They do not open doors, cross portals, reserve a walk or promise that the path will stay clear. The graphical and headless hosts expose the same API; drawing remains specific to `/nav route` in the graphical host.

### Who is driving

One walk runs at a time, and it belongs to whoever asked for it: a plugin, or the
player through a `/nav` command. While a plugin's walk is under way, another plugin's
`GoTo`, `StandOn` or `Follow` answers `Held` instead of taking the character, and a
plugin's `StopGoTo` ends only the walk it started. The player's own commands always
win: a `/nav go to` replaces any plugin's walk, `/nav stop` ends any, and the player's
movement keys interrupt any. `GoToReport.Owner` says who owns the walk under way, so a
plugin that was refused can see why.

A plugin's walks and pauses are its own. When it is disabled or unloaded, the walk it
started stops and every `PauseGoToWhile` it registered is dropped, whether or not it
disposed them. A plugin that wants the character while another plugin's walk is under
way asks the player, or waits for the report to show that walk ended; it cannot take it.

### Statuses

- `Accepted`: the request was taken. Watch its report.
- `Rejected`: the arguments are wrong: an object id of 0, a position that isn't finite, an arrival distance outside 0 to 50 m, a jump's power outside 0 to 1, a move past its limits, or nothing to stop.
- `Unavailable`: there is no character in the world, or the host has no navigation.
- `Held`: another plugin's walk is under way; see [Who is driving](#who-is-driving).

### Walking to an object

```csharp
INavigationAutomation navigation = host.Automation.Navigation;

if (navigation.GoTo(vendorId, arrivalMeters: 2.5f) != PluginNavigationCommandStatus.Accepted)
    return;
long walk = navigation.GoToReport.Sequence;

// On each plugin tick:
PluginGoToReport report = navigation.GoToReport;
if (report.Sequence != walk)
    return; // a later walk replaced this one

switch (report.State)
{
    case PluginGoToState.Planning:
    case PluginGoToState.Walking:
    case PluginGoToState.Waiting:
        break; // under way; RemainingMeters is the route left
    case PluginGoToState.Arrived:
    case PluginGoToState.ArrivedWithoutSight:
        break; // Reason notes an arrival that ended short of the object
    default:
        break; // NoRoute, Blocked (BlockedByObjectId), Stopped, Interrupted, Lost
}
```

### Moving, and making walks wait

```csharp
// Run for a minute, and steer the run without stopping it.
navigation.Move(PluginMoveDirection.Forward, PluginMovePace.Run, 60f, PluginMoveUnit.Seconds);
navigation.Move(PluginMoveDirection.TurnLeft, PluginMovePace.Run, 5f);

// Walks stop while this returns a reason, and plan on once it returns null.
IDisposable pause = navigation.PauseGoToWhile(() => fighting ? "fighting a monster" : null);

// When the plugin unloads:
pause.Dispose();
```

## Reading the reports

Reports are snapshots, safe to read on any plugin tick. Check that `Sequence` is still the one you started, then read `State`.

### Walks: `PluginGoToState`

| State | Kind | Meaning |
|---|---|---|
| `Planning` | under way | Building a grid, or searching it for a route. |
| `Walking` | under way | Following the route. `RemainingMeters` is the length of route left. |
| `Waiting` | under way | Stopped where the character stands while something else needs it. Plans on from there. |
| `Arrived` | finished | Within reach with a line of sight. `Reason` says when the walk ended at the nearest spot that could see the goal instead. |
| `ArrivedWithoutSight` | finished short | The walk ended at the nearest spot it could reach, but no spot it could reach sees the goal, as when the goal is shut behind a wall or a window. |
| `NoRoute` | could not finish | Nothing joins the character to the goal, or to the floor or top it must end on. |
| `Blocked` | could not finish | Stopped making progress even after planning again. `BlockedByObjectId` names what stood beside the spot, such as a door that would not open. |
| `Stopped` | ended by something else | `StopGoTo`, or a later walk. |
| `Interrupted` | ended by something else | The player moved the character. |
| `Lost` | ended by something else | Portal space, or the character left the world. |

### Moves: `PluginMoveState`

| State | Kind | Meaning |
|---|---|---|
| `Moving` | under way | The move is being carried out. |
| `Completed` | finished | It covered its distance, angle or time. |
| `TimeLimit` | could not finish | A move with no amount reached 30 s, or a move was too slow to cover its amount. |
| `Blocked` | could not finish | It stopped making progress. |
| `Stopped` | ended by something else | A stop ended it. |
| `Interrupted` | ended by something else | The player moved the character. |
| `Lost` | ended by something else | Portal space, or the character left the world. |

`Covered` is always meters, or degrees for a turn, whatever unit the move was given in.

## What a walk does

`NavigationWalkController` owns walks. Its host ticks it on the update thread before the player's frame advances; it builds grids and searches routes off that thread, and hands each route to a `RuntimeRouteDriver`.

| Topic | Behavior |
|---|---|
| Planning grid | A square around the character and the goal with 24 m to spare, 96 to 320 m a side. In a sealed dungeon one grid covers every cell, up to 2,048 m a side, and serves every walk there. |
| Far goals | Walked in stages, each planned toward the goal over its own grid, each at least 16 m nearer, 12 at most. |
| Getting stuck | When progress stops, the walk plans again. The first two stops keep later plans out of a spot 0.6 m across, 0.75 m ahead, while a route still arrives without it; where it was the only way on, the walk plans through it again. Each stop after the first also tries a different way off what the character met before planning: stepping back 1 m, walking to the roomiest clear spot within 2.5 m (the middle of a narrow staircase), sidestepping 1 m toward the more open side, sidestepping the other way, and a small hop forward. After 6 new plans a walk ends blocked, naming the object beside the spot; a follow starts over, going on round those ways off, not back to the first stop's none. Narration says where each stop happened, facing which way, on which leg, and how near the walls were. |
| Doors | A closed door on the route is opened first, as a player's click would open it, and the walk goes on once it is open. The walk runs the last of the way up to the door itself and uses it from inside the client's use range. A door not yet appraised is appraised quietly, with nothing opening on screen; a locked one is walked around, and with no other way it ends the walk blocked, naming the door. |
| Portals | Routes never pass through a portal other than the one a walk goes to. |
| Objects | Routes keep out of what the server placed and doesn't move, such as ore deposits, whenever a route still arrives. So do creatures that can't be attacked and follow no one, such as vendors. Corpses are crossed. |
| Creatures and players | Passed around where there is room, and through where going around would scrape walls. A walk looks ahead for one stepping onto its route and plans around it without stopping. One that stops the walk gets two chances to move aside before the walk plans around it. A hostile monster is planned around at once. |
| Waiting | While a plugin's `PauseGoToWhile` names a need, an attack is under way, or a movement intent is held, the walk stops where it stands. Once nothing has needed the character for 1.5 s, it plans again from there. |
| In the air | A walk asked for while the character is jumping or falling waits for it to land, then plans from where it came down. |
| Memory | A grid is kept between walks so the next walk nearby reuses it, and let go once none of what it was built over is loaded, as when the character portals away, or after 30 seconds without a walk; a headless session collects the moment it is let go. |

## Arriving

| Goal | Where the walk ends |
|---|---|
| An object (`go to`) | Beside it, within the arrival distance of its side, at a spot that can see that side, facing it. An object whose collision is in the grid, such as a life stone or a sign, hides its own middle, so its side is what counts. Failing that, the nearest spot that can see it, up to 10 m away, as `Arrived` with a reason. Failing that, the nearest spot a walk reaches, as `ArrivedWithoutSight`. |
| A place (`go to` a cell or map point) | On the floor the place stands on, within the arrival distance. A place on a rock top is arrived at on the rock, or not at all: a route that only reaches the ground beside it ends `NoRoute`. A place joined to the ground by a ramp or stairs is reached up them. |
| A player (`follow`) | Never ends by itself: see [Following](#following). |
| An object's top (`stand on`) | On the highest floor over the object's own collision that a body stands on, within the arrival distance of that top's middle. A ramp or stair up to a deck is not the deck. An object with nothing on top to stand on, such as a creature or a thin wall, or no way up, ends `NoRoute`. |

## Following

`Follow(playerId, bufferMeters)`, or `/nav follow`, keeps the character with a player until `StopGoTo`, `/nav stop`, a later walk, the player's own movement keys, or leaving the world ends it. `GoToReport` shows it walking or waiting for as long as it lasts.

| Situation | What the follow does |
|---|---|
| Reaching the player | Walks up behind them to within the buffer, on the floor they stand on when a route reaches it, jumping onto a platform after them where the character can. It never settles for another spot near them: on a top it can neither walk nor jump onto, it holds where it stands and tries again. Holds behind the player facing them. |
| The player moves | While holding, goes on once they have moved more than 1.5 m from where they stood and are beyond the buffer, or have climbed or dropped more than 1.5 m. While walking, plans again toward them without stopping once they have moved 2 m, at most twice a second. |
| The player jumps | While the player has no floor under them, waits up to 2 s for them to land before planning, so it aims at where they come down. |
| No route, or blocked | A blocked follow works through the same recovery ladder as any walk, then starts it over instead of ending. With no route, it tries again a second later, as often as it takes. Stranded on a top too small to walk on, where no leap down is planned, it first steps 1.5 m off toward the player, up to 3 times running. |
| The player out of sight | Waits where it stands until they are seen again. |
| The player goes through a portal | A player who vanishes within 6 m of a portal, or whom the client moves more than 20 m at once from beside one, went through it: the follow walks into that portal, uses it if walking in did not take the character, waits out portal space, and looks for the player on the other side. A player moved that far from nowhere near a portal, as by a recall, is followed to where they are now if that is within 100 m; farther, the follow waits where it stands until the player is within 100 m again. |
| Anything but a player | Ends at once with no route. |

## The grid and the router

The planning layer, `AcDream.Core.Navigation`, is plain C# with no client state, so it runs in tests from shapes built in code or from the installed game files.

`NavGeometry.Capture` → `NavGrid.Build` → `NavRouter.Find` → `NavRoute`

| Type | What it does |
|---|---|
| `NavGeometry` | Copies the fixed collision of a square region on the physics thread, so a grid can be built from it on another: terrain, dungeon cells, building shells, placed objects, and the objects the server placed that a body meets. It also records what those objects came to, so a grid is known to be out of date once one arrives, leaves or moves. `Capture` takes a region, `CaptureDungeon` a sealed dungeon without terrain, and `CaptureLandblock` one landblock. `SurfacesOf` takes one object's collision. |
| `NavGrid.Build` | Turns that geometry into 0.25 m columns of solid spans. A node is the top of a walkable span with headroom for the whole body. A link joins nodes in neighbouring columns within the body's step-up and step-down heights. A node is clear when no wall comes nearer than the body's radius less a quarter column, and it is not on the very edge of a ledge. Tops too small to walk on are perches a jump can land on. |
| `NavRouter.Find` | A* from a node the start can walk to, within 1.5 m, to a clear node near the goal that can see it, or on the goal's own floor. It plans three ways in parallel, from the shortest to one well clear of walls, and keeps the tidiest route that arrives. It takes spots to keep out of and creatures to pass, and plans leaps only when no walked route arrives. It gives up at 4,000,000 expanded nodes. |
| `NavRouter.FindOnto` | The same, ending on an object's top. |
| `NavRouter.FindToward` | The same, toward a goal beyond the grid: to the grid's edge nearest the goal, for staged walks. |
| `NavRoute` | `Outcome` (`Routed`, `NoStart`, `NoGoal` or `NoPath`) with a `Reason`; the `Path` of nodes; straight `Legs`, with corners moved up to 1.5 m to keep 1 m from walls; `Leaps`; `Length`; `EndsInSight`; `Scrape`; `Crowding`; and `Milliseconds`. |

The physics world gains two read accessors for this, `TryGetLandblockCollision` and `LandblockIds`, and the shadow object registry gains `CaptureEntries` and `CaptureEntriesNear`, which copy collision parts out in one pass instead of the debug enumeration.

## Leaps

Where no walk reaches, a route can jump. The walk controller builds a `NavLeapAbility` from the character's own jump skill, run skill and burden: walk and run speed, the height of a full-power jump, the deepest drop that does no damage (12 m), and a `NavLeapPhysics` for how the physics carries a body through a jump, its step, its bounce and its ground friction.

`NavLeapFinder` treats every piece of floor no walk joins as a place to land:

- **Where it aims.** At the middle of each piece: the centre of a small platform, and a band 3 m in on a larger floor, with bands 1 m and 0.5 m in as riskier aims.
- **Takeoffs.** For each spot within reach, every takeoff on the line back toward the character's floor is solved for the power that arcs onto it, at a walk and at a run. Reach is as far as the character carries at a run on a full-power jump before it comes back down to the height it left from, so a strong jumper plans the long running jumps it can make. A flight is stopped only by what it really meets: the ceiling of a room it passes high over, as the room under a roof's edge, does not count.
- **Physics.** Flight follows the physics step the client is running at. Landing throws the body back up at 5% of its falling speed, and friction slows it step by step, so a walk jump comes to rest about 1.4 m past where its arc comes down and a run jump about 4.5 m. Each jump is aimed to come to rest on its spot.
- **What it keeps.** A jump must clear the lip of any higher floor by 0.15 m across the body's whole width, and keep 0.5 m from the landing floor's edge, still holding when charged a frame more or less, faced 2° off, or taken from 0.35 m short or past its takeoff.
- **Risks.** Aiming short of the middle, landing near an edge when flown a little off, a close lip, a landing that moves, a long flight, or a takeoff near an edge each cost a route 6 m, so easy jumps win whenever there is a choice.

A jump is taken standing: the driver runs or walks up to the takeoff so the character comes to rest on it, waits until it has stood still for 0.25 s, turns to face the landing, charges in place, presses forward at the leap's pace as the charge releases, and lets go once the body has risen 0.05 m. After it lands and slides to a stop, the walk goes on if it came to rest near the spot, plans on from where it stands if it landed elsewhere on that level, and ends blocked otherwise.

## Driving a route

### `RuntimeScriptedMovement`

Carries out moves frame by frame on three channels that combine like held keys. It wraps the player's input source and passes the player's own input through untouched while no move is under way.

| Limit | Value |
|---|---|
| Longest move | 500 m, 3,600° or 300 s |
| Move with no amount | 30 s |
| Progress check | 0.3 m or 5° every 1.5 s, else blocked |
| Slowest pace | 1 m/s or 30°/s, else out of time |
| Full jump charge | 1 s |

### `RuntimeRouteDriver`

Turns a route's legs into scripted moves. Each frame it takes a sample of the body and returns one step: moves to begin or stop, and a jump to charge with the pace to leave at.

| Topic | Behavior |
|---|---|
| Legs | A leg's end counts as reached within 0.5 m, or once the body has passed it. |
| Steering | Small exact turns toward a point 2.5 m along the leg, plus 2.75 m for every meter the body is off it. |
| Corners | Runs around corners wherever a running turn's arc stays on the floor and off ledges. Otherwise it runs up to the corner, stops and turns in place. It never slows to a walk. |
| States | `Driving`, `Arrived`, `Blocked`, `LandedElsewhere`, `Interrupted`, `Lost` |

## Headless

The headless host hands plugins the same `RuntimeNavigationAutomation`, so moves and jumps work in every session. Walks need collision: a session with a prepared content package loads the 3x3 landblock neighbourhood around the character through the same content builder the client uses, objects inside cells included, and gets a walk controller that opens doors through the runtime's own use and appraisal commands. The host ticks walks before each player frame advances, and `/nav` and `/motor` answer on the console. A walk plans only over what is loaded, so a far goal is reached in stages as the neighbourhood follows the character.

## MossTank

[MossTank](https://github.com/eriknihlen/openac-mosstank) is an external
automation plugin, in its own repository and installed through the launcher.
It is written up here because it is the heaviest caller of this API and shows
what the two ways to move look like side by side in one plugin.

MossTank walks its routes the reference way: straight at each point with held keys, and its own corpse and monster walks the same. Its per-profile **Client pathing** choice on the Route tab says when the client's navigation walks a leg for it instead. **When stuck**, the default, hands a leg to `GoTo` once per visit to a waypoint when the straight walk has held the forward key for three seconds without covering three quarters of a metre; the clock runs only while MossTank's route rule has the character, so a fight or a corpse walk never counts. While the client walks, the route keeps its turn and watches the report: arriving advances the route; a walk that ends any other way returns the keys to the straight walk, and a second stall at the same waypoint is said once in chat. Losing the turn to combat or loot stops the client's walk with the rest of the movement. **Never** keeps the straight walk and only says in chat when it stalls. **Always** sends every leg to `GoTo`; a leg the client cannot walk pauses the route where it stands and says so once, until the route is reset or the choice changes. MossTank registers `PauseGoToWhile`, so a client walk it asked for waits while it buffs. A profile written with the older "walk legs with client pathing" checkbox on reads as Always.

## Seeing it in the client

`/nav grid` shows the grid around the character, or over the whole dungeon inside one; `/nav route` draws a route without walking it.

- Green points: clear floor, every 0.5 m.
- Red points: beside a ledge or too near a wall.
- Magenta line: the route being walked.

The grid and the route line are hidden behind walls, floors and ceilings, as the world is. `/nav debug` narrates the rest.

## Known limits

- Route legs and leaps are not checked against the physics engine before they are walked. A route through a staircase narrower than a body comfortably fits can hug its walls and rely on the recovery ladder.
- Jump puzzles are climbed but not descended: nothing aims a leap down at some small tops.
- A thin post about 1.4 m tall plans no jump onto it.
- A follow takes the shortest way to the player, not the way the player went, so it may use another door or the long way down.
- Door use in the headless host is covered offline but has not been run against a live server.
- A grid over a whole dungeon holds 46 to 149 MB while it is in use, and planning with leaps over a large grid can take a couple of seconds.

## Tests

| Where | What it covers |
|---|---|
| `tests/AcDream.Core.Tests/Navigation/NavGridTests.cs` | Grids built from shapes in code: ledges, gaps, stairs, rooms and pillars, the open sea, routes, goal floors, object tops, open air over a room's ceiling, and leaps, including reach from the character's own run and jump. |
| `tests/AcDream.Core.Tests/Physics/ShadowObjectRegistryCaptureTests.cs` | Collision parts copied out by object and near a point, in the debug enumeration's order. |
| `tests/AcDream.Runtime.Tests/Gameplay/RuntimeRouteDriverTests.cs`, `RuntimeScriptedMovementTests.cs` | The driver and the motor against a simulated body: corners, legs it cannot move along, leaps that land on their spot or elsewhere, and move limits. |
| `tests/AcDream.Runtime.Tests/Navigation/` | The navigation API's projection and pauses, and every `/nav` and `/motor` command. |
| `tests/AcDream.App.Tests/Navigation/NavigationWalkControllerTests.cs` | Walk requests against a simulated body and world: planning again and the recovery ladder, doors, creatures, waiting, player input in every state, grids let go, narration, place floors, object tops, and following: holding, replanning, portals, landings, sleeping and stepping off. |
| `tests/AcDream.Headless.Tests/HeadlessSessionNavigationTests.cs` | A headless session's navigation, walk and console commands. |
| `NavigationWalkCorpusTests`, `RockJumpPuzzleInstalledDatTests` | Fixed walks through real dungeons, Holtburg and a staged walk across the 3x3 landblocks around it, over the collision the client loads and over the collision the headless host loads, and the rock jump puzzle, from the installed game files (`InstalledDat` lane). `ACDREAM_UPDATE_WALK_CORPUS=1` records the corpus again, from the client's walks. |
| `HeadlessCollisionParityInstalledDatTests` | Grids over a town, dungeons and the example landblocks come out identical from the collision either host loads. |
| `HoltburgRoofJumpInstalledDatTests` | A 26 m running jump from a Holtburg porch onto a roof over a room plans at jump skill 443. |

## Where it lives

| Layer | Files |
|---|---|
| Contract | `src/AcDream.Plugin.Abstractions/NavigationAutomation.cs` |
| Navigation API and commands | `src/AcDream.Runtime/Navigation/RuntimeNavigationAutomation.cs`, `NavigationChatCommands.cs` |
| Walks | `src/AcDream.Runtime/Navigation/NavigationWalkController.cs`, `RuntimeNavigationWalkSources.cs`, `SealedDungeonCells.cs` |
| Driving | `src/AcDream.Runtime/Gameplay/RuntimeRouteDriver.cs`, `RuntimeScriptedMovement.cs` |
| Planning | `src/AcDream.Core/Navigation/NavGeometry.cs`, `NavGrid.cs`, `NavRoute.cs`, `NavLeaps.cs`, `NavColumnTiles.cs` |
| Client | `src/AcDream.App/Navigation/NavigationWalkFramePhase.cs`, `src/AcDream.App/Rendering/NavMeshDebugOverlay.cs`, `DebugLineRenderer.cs` |
| Headless | `src/AcDream.Headless/Hosting/HeadlessSessionHost.cs`; shared collision loading in `src/AcDream.Content/LandblockPhysicsContentBuilder.cs` |
| MossTank | [its own repository](https://github.com/eriknihlen/openac-mosstank) |
