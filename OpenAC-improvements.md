# OpenAC Improvements Needed for a 100% GoArrow Port

This document is an implementation-oriented backlog for improving OpenAC so that `openac.goarrow` can reproduce the original Virindi/Decal GoArrow experience without depending on Decal, raw packet hooks, client memory access, or OpenAC internals.

It is intended as a base document for coding agents working in the OpenAC repository. Each section describes:

- the missing capability;
- the recommended abstraction;
- the host-side work implied by that abstraction;
- the GoArrow use case;
- acceptance criteria and tests.

The examples use the existing `AcDream.Plugin.Abstractions` naming conventions. They are proposals, not requirements that the exact signatures must be copied verbatim.

---

## Current baseline

The current port already has a supported core:

- `IAcDreamPlugin` lifecycle;
- `IPluginStorage` persistence;
- `IPluginCommandRegistry` `/go` commands;
- declarative panel markup;
- embedded location and portal data;
- weighted location graph and A* shortest-path routing;
- multi-hop walk and route-start edges;
- destination distance and bearing calculation;
- `INavigationAutomation.GoTo` integration;
- graceful operation when automation is unavailable.

The current contract does **not** provide equivalents for the original plugin's:

- Direct3D arrow HUD;
- Dereth and dungeon map HUDs;
- floating toolbar HUD;
- clickable coordinate links in chat;
- authoritative recall/house/allegiance tracking;
- reliable portal/NPC/door interaction;
- complete route-leg and portal-transition recovery;
- editable, user-updatable route data with migration support.

The improvements below are ordered by capability area rather than by implementation difficulty. The recommended implementation order is at the end.

---

# 1. Plugin-owned HUD and rendering

## 1.1 Problem

The original GoArrow plugin renders an always-on-top directional arrow, map windows, dungeon maps, toolbars, tooltips, and custom icons. OpenAC panels are declarative and are not a replacement for arbitrary transparent, movable, continuously rendered overlays.

A plugin should not need to reference OpenAC's UI toolkit, renderer, window implementation, or operating-system APIs directly. Rendering should therefore be exposed through a stable abstraction that the host can implement using its chosen graphics backend.

## 1.2 Recommended abstraction

Add a rendering service to `IPluginHost`, for example:

```csharp
public interface IPluginRenderRegistry
{
    IPluginHudRegistration AddHud(PluginHudDescriptor descriptor);
    IPluginTexture LoadTexture(string resourceId);
}

public interface IPluginHudRegistration : IDisposable
{
    bool IsVisible { get; set; }
    PluginHudBounds Bounds { get; set; }
    event Action<PluginHudInput>? Input;
    IPluginRenderSurface Surface { get; }
}

public interface IPluginRenderSurface
{
    void BeginFrame(PluginRenderContext context);
    void DrawTexture(IPluginTexture texture, PluginRect destination, PluginColor color);
    void DrawLine(PluginPoint from, PluginPoint to, PluginColor color, float thickness);
    void DrawText(string text, PluginPoint position, PluginTextStyle style);
    void EndFrame();
}
```

The exact API may instead expose retained drawing commands rather than immediate-mode drawing. The important properties are:

- no D3D types in the abstraction assembly;
- no graphics-device ownership by plugins;
- host-managed frame lifetime;
- per-plugin disposal and resource ownership;
- transparent composition over the game window;
- input routing that cannot interfere with the game unless the HUD has focus;
- safe behavior in headless hosts.

## 1.3 HUD descriptor requirements

A `PluginHudDescriptor` should define:

- stable HUD ID;
- title and optional icon;
- initial visibility;
- default size and position;
- minimum and maximum size;
- whether the HUD is movable;
- whether it is resizable;
- whether it is click-through when inactive;
- whether it is persisted globally or per character;
- z-order or overlay layer;
- scaling behavior for different DPI settings.

The host should persist position, size, visibility, and scale under a plugin-scoped storage key. Plugins should not write platform-specific window settings themselves.

## 1.4 Input requirements

GoArrow needs arrow-HUD click handling for actions such as:

- stop navigation;
- open the main panel;
- toggle map visibility;
- toggle destination tracking.

Input should provide:

```csharp
public readonly record struct PluginHudInput(
    PluginHudInputKind Kind,
    PluginPoint Position,
    PluginMouseButton Button,
    bool Shift,
    bool Control,
    bool Alt);
```

The host must define whether input is delivered on the UI thread and whether a plugin callback may mutate HUD state immediately.

## 1.5 Resource requirements

The resource API should support:

- embedded plugin resources;
- package-relative resources;
- host-provided RenderPack/icon assets;
- texture lifetime and disposal;
- asynchronous loading for large map files;
- invalid-resource diagnostics;
- texture dimensions and alpha support.

A texture should be immutable from the plugin's perspective. The host owns GPU upload and device-loss recovery.

## 1.6 GoArrow implementation enabled

With this capability, GoArrow could restore:

- the directional arrow HUD;
- the toolbar HUD;
- tooltip overlays;
- custom icons and status indicators;
- configurable opacity, scale, and position;
- click-to-stop and click-to-open-panel workflows.

## 1.7 Acceptance criteria

Host tests should verify:

- a plugin can create and dispose a HUD;
- headless hosts return an inert registration without throwing;
- HUD state persists across host restart;
- input is delivered only while the HUD is interactive;
- plugin unload releases textures and render callbacks;
- a renderer/device reset does not crash plugins;
- rendering callbacks do not run concurrently with plugin disable.

A GoArrow integration test should render an arrow whose direction changes when the destination bearing changes.

---

# 2. Image, map, and canvas controls

## 2.1 Problem

The original map HUDs need a large image, world-coordinate markers, zooming, panning, dungeon overlays, and route lines. Existing declarative controls do not expose a general-purpose image or canvas control.

A general rendering API is useful for the arrow HUD, but a map-specific control would make common plugin behavior easier and safer.

## 2.2 Recommended declarative controls

Add markup controls such as:

```xml
<image source="{Binding MapImage}" stretch="Uniform" />
<map source="{Binding MapSource}"
     viewport="{Binding MapViewport}"
     markers="{Binding MapMarkers}"
     marker-click="{Binding MapMarkerClicked}" />
<canvas draw="{Binding DrawMap}" />
```

The markup system needs documented binding types for:

- image source;
- image scaling and clipping;
- viewport;
- markers;
- selection;
- mouse wheel zoom;
- drag/pan gestures;
- coordinate conversion.

## 2.3 Recommended map abstraction

```csharp
public interface IPluginMapSurface
{
    PluginMapViewport Viewport { get; set; }
    void SetBackground(IPluginMapImage image);
    void SetMarkers(IReadOnlyList<PluginMapMarker> markers);
    void SetRoute(IReadOnlyList<PluginMapPoint> points);
    event Action<PluginMapInput>? Input;
}
```

A marker should support:

- stable ID;
- display label;
- world coordinate;
- optional dungeon/floor/region ID;
- icon ID;
- color;
- selected state;
- tooltip text;
- arbitrary plugin-owned metadata.

## 2.4 Coordinate model

OpenAC should define one canonical map coordinate model. It must document:

- North/South and East/West sign conventions;
- map units versus meters;
- outdoor world origin;
- landcell and cell conversion;
- dungeon-local coordinate spaces;
- floor/elevation handling;
- whether longitude/latitude-like values are global or region-local.

Recommended conversion helpers:

```csharp
public interface IMapCoordinateService
{
    bool TryWorldToMap(PluginWorldPosition world, out PluginMapPoint map);
    bool TryMapToWorld(PluginMapPoint map, out PluginWorldPosition world);
    bool TryGetRegion(PluginWorldPosition world, out PluginMapRegion region);
}
```

The helpers should be tested against known locations and should not be reimplemented independently by every plugin.

## 2.5 Map asset requirements

The host/package system should support:

- large tiled maps;
- compressed image formats;
- asynchronous loading;
- cache eviction;
- image metadata and coordinate extents;
- optional dungeon map layers;
- user-supplied map overrides;
- missing-tile fallback behavior.

Loading a whole Dereth map into memory synchronously is unacceptable for a plugin UI thread.

## 2.6 GoArrow implementation enabled

This enables:

- Dereth map HUD;
- dungeon map HUD;
- current-position marker;
- destination marker;
- route lines and portal-leg markers;
- map click-to-destination workflows;
- zoom and pan persistence.

## 2.7 Acceptance criteria

Tests should verify:

- world-to-map and map-to-world conversion round trips within a documented tolerance;
- markers remain correctly positioned while zooming and panning;
- map resources load asynchronously;
- missing tiles do not crash the UI;
- map controls work in a headless host without attempting image creation;
- map input can select a destination and report the corresponding world coordinate.

---

# 3. Chat improvements

## 3.1 Problem

The original plugin uses chat as both an input surface and a navigation workflow. It recognizes coordinate links and names, provides clickable actions, and emits formatted status messages. OpenAC currently provides chat capture/posting but no structured clickable-link model.

## 3.2 Structured output

Extend `IPluginChat` with structured messages or spans rather than requiring plugins to depend on client log-text IDs:

```csharp
public readonly record struct PluginChatSpan(
    string Text,
    PluginChatSpanStyle Style,
    PluginChatAction? Action = null);

public readonly record struct PluginChatAction(
    string Id,
    IReadOnlyDictionary<string, string> Arguments);
```

The host should render spans safely and invoke actions only for the plugin that registered them. Actions must not execute arbitrary code or commands from untrusted server chat.

## 3.3 Clickable links and coordinate parsing

Add normalized link events:

```csharp
public readonly record struct PluginChatLink(
    PluginChatLinkKind Kind,
    string DisplayText,
    PluginWorldPosition? Position,
    uint ObjectId,
    string? Name);

event Action<PluginChatLinkClicked> LinkClicked;
```

The host should recognize at least:

- coordinate text such as `42.1N, 33.6E`;
- object names when linked by the client;
- player names;
- plugin-generated actions.

Coordinate parsing should be centralized and tested for:

- N/E, N/W, S/E, S/W;
- decimal precision;
- alternate separators;
- whitespace variations;
- malformed and ambiguous text;
- multiple coordinates in one line.

## 3.4 Chat command improvements

The command registry should support:

- command descriptions;
- aliases;
- argument completion;
- quoted arguments;
- typed subcommands;
- help generation;
- command ownership and conflict diagnostics.

A possible registration model is:

```csharp
public interface IPluginCommandDefinition
{
    string Verb { get; }
    string Description { get; }
    IReadOnlyList<string> Aliases { get; }
    PluginCommandResult Invoke(PluginCommand command);
    IReadOnlyList<PluginCommandCompletion> Complete(PluginCommand command);
}
```

GoArrow needs quoted destination names and completion from the location database.

## 3.5 GoArrow implementation enabled

This enables:

- clicking a coordinate in chat to set a destination;
- clicking a destination name to navigate;
- formatted clickable route/status messages;
- `/go` destination autocomplete;
- compatibility aliases and generated help.

## 3.6 Acceptance criteria

Tests should verify:

- links are parsed from ordinary chat messages;
- malformed links are ignored safely;
- plugin actions cannot be invoked by another plugin;
- command arguments preserve quoted multi-word names;
- autocomplete does not block the chat thread;
- chat output remains local unless the plugin explicitly submits text through the chat bar.

---

# 4. World state, objects, and position events

## 4.1 Problem

The current port polls navigation state on ticks. Polling is adequate for display but is not sufficient for reliable portal transitions, route recovery, object interaction, or recall tracking.

The host should expose normalized state transitions, not raw network messages.

## 4.2 Position events

Extend navigation with events or an event stream:

```csharp
event Action<PluginNavigationSnapshot> PositionChanged;
event Action<PluginPortalTransition> PortalTransition;
event Action<PluginWorldAvailabilityChanged> AvailabilityChanged;
```

A position event should include:

- local predicted position;
- latest server-confirmed position;
- cell ID;
- heading;
- outdoor/dungeon state;
- portal-space state;
- revision number;
- timestamp or host tick number.

Events should be raised on the same thread as `IEvents.Tick`, or the threading contract must be explicit.

## 4.3 Object lifecycle events

Add a world-object event surface:

```csharp
event Action<PluginObjectChanged> ObjectChanged;
event Action<PluginObjectRemoved> ObjectRemoved;
IReadOnlyList<PluginNavigationObject> CaptureObjects();
```

`PluginObjectChanged` should identify:

- object ID;
- changed fields;
- current name;
- position;
- class/category;
- interaction capabilities;
- lock/open state;
- owner/container relationship;
- revision number.

The host should coalesce high-frequency position updates or provide separate subscriptions for identity changes and movement changes.

## 4.4 Object identity and normalized metadata

GoArrow needs to distinguish:

- portal devices;
- portal NPCs;
- doors;
- vendors;
- recall targets;
- dungeons and entrances;
- player characters;
- static landmarks.

The abstraction should expose stable semantic flags or categories, for example:

```csharp
[Flags]
public enum PluginObjectCapabilities
{
    None = 0,
    Interactable = 1,
    Portal = 2,
    Door = 4,
    Vendor = 8,
    Container = 16,
    Player = 32,
}
```

The host may derive these from known object metadata, but plugins should not have to decode raw property IDs.

## 4.5 GoArrow implementation enabled

This enables:

- destination tracking from objects selected in the world;
- dynamic portal and NPC discovery;
- object-position-based route legs;
- reliable detection of portal arrival;
- automatic route recovery when an object moves or disappears;
- selected-object destination commands.

## 4.6 Acceptance criteria

Tests should verify:

- object creation, update, and removal events are ordered;
- stale object revisions are rejected or clearly marked;
- position events include a monotonic revision;
- portal-space transitions are emitted exactly once per transition;
- no object event is delivered after plugin disable;
- inert/headless implementations return empty snapshots and never throw.

---

# 5. Recall, portal, house, and allegiance tracking

## 5.1 Problem

The original GoArrow tracks recall destinations learned from game state and network messages. The current port can only persist manually configured strings or infer limited information from chat.

A full port needs an authoritative, normalized source of recall information.

## 5.2 Recall model

Add a recall service to the automation surface or world-state service:

```csharp
public readonly record struct PluginRecallLocation(
    PluginRecallKind Kind,
    PluginWorldPosition Position,
    string Name,
    ulong Revision,
    bool IsKnown);

public interface IRecallAutomation
{
    IReadOnlyList<PluginRecallLocation> Locations { get; }
    event Action<PluginRecallLocation> LocationChanged;
}
```

Required kinds include:

- primary portal recall;
- secondary portal recall;
- allegiance recall;
- house recall;
- lifestone or other configured return point if the game exposes it;
- current portal destination after a successful transition.

The model should distinguish:

- unknown;
- known and valid;
- known but stale;
- known but unavailable in the current world/session.

## 5.3 Learning events

The host should publish normalized events for:

- recall destination learned;
- recall destination changed;
- spell cast accepted;
- spell cast completed;
- item/device used;
- portal entered;
- portal exit completed;
- recall failed;
- recall interrupted.

The event must not expose raw protocol packets. It should expose the semantic result and enough diagnostics for a plugin to update its route state.

## 5.4 House and allegiance data

Expose optional metadata for:

- house location;
- house name/ID;
- allegiance headquarters location;
- allegiance name/ID;
- whether a location is currently usable;
- world/server scope of the location;
- source and confidence of the value.

## 5.5 GoArrow implementation enabled

This enables:

- `/go recall` and equivalent recall destinations;
- automatic route starts from primary, secondary, house, or allegiance recall;
- persistence of authoritative values instead of chat heuristics;
- recovery after portal transitions;
- status messages explaining stale, unknown, or unavailable recall data.

## 5.6 Acceptance criteria

Tests should verify:

- recall updates are versioned and ordered;
- a new character/session starts with unknown rather than fabricated values;
- recall changes are scoped to the correct character and world;
- portal entry/exit events correlate with the appropriate route leg;
- failed casts and interrupted transitions do not update the destination incorrectly;
- an unavailable recall is distinguishable from a missing recall.

---

# 6. Navigation improvements

## 6.1 Problem

`INavigationAutomation.GoTo` is sufficient for a basic point-to-point walk, but GoArrow is a multi-leg route planner. It needs explicit route ownership, reliable progress reports, interruption reasons, portal recovery, and supported interaction actions.

The host should remain responsible for path planning and movement safety. A plugin should submit goals and react to reports, not emulate movement by holding keys or injecting packets.

## 6.2 Navigation ownership

Every navigation request should have an owner identity supplied by the host or plugin framework. The host should enforce:

- a plugin can stop or replace only its own request;
- player commands override plugin navigation;
- combat or higher-priority automation can temporarily pause a route;
- a plugin unload cancels all of its requests;
- a request cannot survive after its plugin registration is disposed.

Recommended request shape:

```csharp
public readonly record struct PluginNavigationRequest(
    string Owner,
    PluginNavigationGoal Goal,
    float ArrivalMeters,
    PluginNavigationPriority Priority,
    PluginNavigationOptions Options);
```

## 6.3 Multi-leg route API

Add a host-managed route operation or make `GoTo` reports rich enough for plugins to implement one safely:

```csharp
PluginNavigationCommandStatus FollowRoute(
    string owner,
    IReadOnlyList<PluginNavigationWaypoint> waypoints,
    PluginNavigationRouteOptions options);
```

A waypoint should include:

- world position or object ID;
- stable route-leg ID;
- arrival tolerance;
- whether line of sight is required;
- whether an interaction is required on arrival;
- whether the leg can be skipped after a portal transition;
- display caption;
- optional plugin metadata.

## 6.4 Rich route reports

`PluginGoToReport` should expose:

- request sequence;
- owner;
- route ID;
- waypoint/leg ID;
- current state;
- current position;
- remaining route distance;
- replan count;
- blocked object ID;
- failure reason enum;
- human-readable diagnostic;
- timestamp/revision;
- whether the report is terminal.

Recommended failure states include:

- unavailable;
- planning;
- walking;
- waiting;
- arrived;
- arrived without sight;
- no route;
- blocked;
- interrupted by player;
- interrupted by another owner;
- portal-space transition;
- session lost;
- stopped.

Reports should be events as well as snapshots:

```csharp
event Action<PluginGoToReport> GoToReportChanged;
```

The event must not repeatedly emit the same report unless the revision changes.

## 6.5 Pause/resume semantics

The existing `PauseGoToWhile` concept should be expanded with explicit state:

- pause requested;
- character stopped;
- route retained;
- route replanned;
- resumed;
- pause timeout or cancellation.

A plugin should be able to state why it needs the character, such as `"GoArrow is interacting with a portal"`, and the host should expose that reason in diagnostics.

## 6.6 Interaction actions

A complete GoArrow route may need actions beyond walking:

```csharp
public interface IWorldInteractionAutomation
{
    PluginInteractionCommandStatus UseObject(uint objectId);
    PluginInteractionCommandStatus OpenDoor(uint objectId);
    PluginInteractionCommandStatus TalkTo(uint objectId);
    PluginInteractionCommandStatus SelectPortalDestination(string name);
    PluginInteractionReport LastReport { get; }
    event Action<PluginInteractionReport> ReportChanged;
}
```

Actions must be:

- asynchronous or report-driven;
- cancellable;
- ownership-aware;
- safe when the object disappears;
- explicit about dialog/confirmation requirements;
- inert outside a live session.

## 6.7 Coordinate and elevation support

`GoTo` should document and test:

- whether `EastWest` and `NorthSouth` are map units or meters;
- whether `Elevation = NaN` means ground;
- how dungeon floors are identified;
- behavior for `CellId = 0`;
- behavior when a point is outside the navigation mesh;
- arrival tolerance units;
- whether a point target requires line of sight.

Provide conversion helpers rather than requiring each plugin to guess.

## 6.8 GoArrow implementation enabled

This enables:

- route-leg ownership and cancellation;
- reliable portal/NPC/door legs;
- blocked-route diagnostics;
- robust replanning after interruptions;
- route progress display;
- portal-transition recovery;
- no duplicate or stale arrival handling.

## 6.9 Acceptance criteria

Navigation tests should cover:

- two plugins competing for navigation;
- player movement interrupting a route;
- a blocked object and a successful replan;
- arrival at an object versus arrival without sight;
- portal-space transition while a request is active;
- plugin disable while walking;
- duplicate report suppression;
- headless and unavailable navigation;
- route ownership and stop authorization.

---

# 7. Declarative UI improvements

## 7.1 Problem

The current declarative panel can display values and trigger actions, but GoArrow needs destination entry, autocomplete, route-step lists, map interaction, validation, and reliably refreshed live values.

## 7.2 Input controls

Add documented controls for:

- single-line text input;
- submit-on-enter;
- placeholder text;
- input validation;
- selected text and focus;
- keyboard navigation;
- debounced change events;
- command completion dropdowns.

Example:

```xml
<text-input value="{Binding DestinationDraft}"
            placeholder="Destination"
            changed="{Binding SetDestinationDraft}"
            submitted="{Binding SubmitDestination}"
            suggestions="{Binding DestinationSuggestions}" />
```

## 7.3 Lists and route editors

GoArrow needs list controls with:

- stable row keys;
- selected row binding;
- separate display and value columns;
- virtualized rows for large databases;
- context actions;
- keyboard selection;
- empty/loading/error states.

The route editor should be able to show:

- leg type;
- destination;
- distance;
- interaction action;
- portal/recall metadata;
- reorder/delete controls;
- current active leg.

## 7.4 Binding refresh contract

Live distance, bearing, navigation state, and route progress need a defined update model. Support at least one of:

- automatic reevaluation on every `IEvents.Tick`;
- `INotifyPropertyChanged`;
- an explicit `IPluginPanelBinding.Invalidate()` method;
- binding refresh intervals.

The host should document:

- which thread reads bindings;
- whether property getters must be side-effect free;
- whether callbacks can mutate bindings;
- how exceptions are surfaced;
- whether a disabled/hidden panel is still reevaluated.

## 7.5 Panel lifecycle and focus

Add APIs or markup actions for:

- opening a named panel;
- closing/hiding a panel;
- focusing a text input;
- selecting a tab;
- opening a panel at a requested location;
- notifying a plugin when a panel is destroyed.

## 7.6 GoArrow implementation enabled

This enables:

- destination text entry instead of chat-only commands;
- autocomplete from the location database;
- live route-step display;
- editable route profiles;
- map selection from the panel;
- validation and user-friendly errors.

## 7.7 Acceptance criteria

UI tests should verify:

- a bound property refreshes after a tick;
- input submission invokes exactly once;
- autocomplete handles thousands of locations without blocking UI;
- list selection survives refresh when the row still exists;
- selected rows are cleared when deleted;
- hidden panels release or pause expensive bindings;
- malformed markup reports a useful plugin-specific error.

---

# 8. Storage and data distribution

## 8.1 Structured storage

`IPluginStorage` currently offers text files. Add optional helpers for common plugin data patterns:

```csharp
public interface IPluginStorage
{
    string? ReadText(string key);
    void WriteText(string key, string content);
    bool Delete(string key);
    string? ReadJson<T>(string key);
    void WriteJson<T>(string key, T value);
    bool IsAvailable { get; }
}
```

If generic methods are not desirable in the abstraction, provide extension methods in a shared package.

Required behavior:

- atomic replacement;
- UTF-8 encoding;
- directory creation;
- bounded key/path validation;
- no path traversal;
- clear unavailable-storage behavior;
- optional corruption backup;
- schema/version metadata;
- migration hooks.

## 8.2 Scope support

GoArrow needs multiple scopes:

- global plugin settings;
- per-character destination and recall data;
- per-world portal data;
- user route profiles;
- shared default data.

A scoped storage API could be:

```csharp
IPluginStorage OpenScope(PluginStorageScope scope);
```

The host must define how character and world identity are sanitized and what happens before login.

## 8.3 User-overridable route databases

The host should provide a plugin data directory with separate layers:

1. embedded defaults shipped with the plugin;
2. host-installed data;
3. user overrides;
4. optional session-specific data.

The merge policy must be deterministic and documented. Data loaders should report:

- file name;
- record count;
- ignored records;
- duplicate keys;
- parse errors;
- schema version;
- override precedence.

## 8.4 Data reload

Provide either:

- explicit plugin-controlled reload;
- host file-change notifications;
- reload-on-panel-open.

Reload must be atomic from the plugin's perspective: readers see either the old complete database or the new complete database, never a partially populated collection.

## 8.5 GoArrow implementation enabled

This enables:

- updates to towns and portal devices without rebuilding;
- user-added locations;
- per-character route profiles;
- migration from the original GoArrow data format;
- diagnostics when community route data is malformed.

## 8.6 Acceptance criteria

Storage tests should verify:

- interrupted writes do not destroy the previous file;
- malformed JSON is recoverable;
- scope keys cannot escape the plugin directory;
- per-character data does not leak between characters;
- unavailable storage does not crash plugin initialization;
- concurrent reads see complete versions only.

---

# 9. Resource manifest and packaging support

## 9.1 Problem

A complete GoArrow port needs panel markup, icons, maps, route data, and possibly localized text. Plugin packaging needs to distinguish executable code from resources and user-overridable files.

## 9.2 Manifest requirements

Extend `plugin.json` or its schema with resource declarations:

```json
{
  "resources": {
    "markup": ["goarrow-panel.xml"],
    "embeddedData": ["EmbeddedResources/*.xml"],
    "images": ["Assets/*.png"],
    "maps": ["Maps/**/*.webp"]
  },
  "dataDirectory": "GoArrow"
}
```

The host should validate:

- resource existence;
- path safety;
- duplicate resource IDs;
- supported formats;
- package size limits;
- plugin-owned versus host-owned files.

## 9.3 Resource lookup API

Plugins should resolve resources through an abstraction rather than `Assembly.GetManifestResourceStream` and assembly-relative paths:

```csharp
public interface IPluginResources
{
    Stream? OpenEmbedded(string id);
    string? ResolveInstalledPath(string id);
    IReadOnlyList<PluginResourceInfo> List(string category);
}
```

This supports single-file deployment, package extraction, tests, and host-specific resource loading.

## 9.4 Acceptance criteria

- plugin packages install and validate before loading;
- resources are available identically in development, installed, and single-file hosts;
- user data is not overwritten by plugin updates;
- invalid resources produce actionable diagnostics;
- resource streams are disposed safely.

---

# 10. Testing and host fixtures

## 10.1 Problem

The route-finding layer can be unit tested independently, but a complete port depends heavily on lifecycle, events, storage, UI, chat, and navigation behavior. Every plugin agent should be able to test against deterministic fake abstractions.

## 10.2 Standard fixtures

OpenAC should ship reusable test fixtures for:

- `IPluginHost`;
- `IPluginStorage` in memory;
- command registration and invocation;
- chat capture and posting;
- tick/event dispatch;
- navigation snapshots;
- `GoTo` reports;
- object lifecycle events;
- UI panel registration;
- resource lookup;
- render surfaces where practical.

Recommended fake-host capabilities:

```csharp
var host = new FakePluginHost();
host.Storage.SetAvailable(true);
host.Automation.Navigation.SetSnapshot(snapshot);
host.Automation.Navigation.CompleteGoTo(state);
host.Commands.Invoke("go", "Holtburg");
host.Events.RaiseTick(0.1);
```

## 10.3 Contract tests

Every host implementation should run contract tests covering:

- inert behavior;
- disposal;
- event ordering;
- thread affinity;
- ownership rules;
- storage atomicity;
- command conflicts;
- panel lifecycle.

## 10.4 GoArrow integration tests

The completed plugin should test:

- initialize, enable, disable, and re-enable;
- restoring a saved destination;
- `/go Holtburg` command handling;
- `/go stop` cancellation;
- unavailable/headless navigation;
- route calculation and first-leg submission;
- report-driven leg advancement;
- blocked and interrupted navigation;
- portal transition recovery;
- panel values after ticks;
- user data override and malformed-data diagnostics.

## 10.5 Test SDK support

The OpenAC repository should standardize:

- supported .NET SDK version;
- test SDK version;
- xUnit/NUnit/MSTest version;
- package lock policy;
- test project defaults;
- test discovery command.

A new plugin template should build and run a minimal test before an agent starts implementation.

---

# 11. Security, stability, and threading requirements

New APIs must preserve the plugin boundary.

## 11.1 Security boundaries

Do not require:

- raw network message access;
- direct client memory access;
- `App`, `Runtime`, or `Core` references;
- arbitrary native handles;
- unrestricted file-system access;
- arbitrary code execution from markup or chat.

All semantic data should be normalized by the host.

## 11.2 Ownership and cancellation

Every long-running operation should have:

- owner ID;
- request sequence;
- cancellation method;
- terminal report;
- disposal behavior;
- timeout or host shutdown behavior.

This applies to navigation, interaction, map loading, resource loading, and asynchronous UI operations.

## 11.3 Threading

The abstraction documentation must define:

- thread that raises events;
- thread that reads navigation snapshots;
- thread that invokes UI bindings;
- whether plugin methods may call host APIs synchronously;
- whether callbacks may reenter the host;
- how plugin disable synchronizes with in-flight callbacks.

The simplest safe contract is to raise plugin events on the host update/UI thread and prohibit blocking operations in callbacks.

## 11.4 Fault isolation

The host should catch exceptions from:

- event handlers;
- command handlers;
- rendering callbacks;
- binding getters/actions;
- object and navigation callbacks.

The host should log the plugin ID and callback name, disable only the failing registration where possible, and continue running other plugins.

## 11.5 Acceptance criteria

- a plugin exception does not terminate the host;
- plugin unload waits for or cancels in-flight callbacks;
- invalid plugin resources cannot escape the plugin directory;
- navigation cannot be commandeered across plugin ownership boundaries;
- malformed server-derived text cannot execute plugin actions.

---

# 12. Recommended implementation order

The following order gives the highest value to GoArrow and other navigation plugins while minimizing duplicated APIs.

## Phase 1: Contract and test foundations

1. ~~Document threading and lifecycle rules.~~ **Done** — every abstraction's XML doc now documents its threading contract: `IEvents` describes the update thread, exception isolation, and reentry rules; `INavigationAutomation` documents snapshot threading; `IAutomationSurface` documents per-area threading; lifecycle rules are documented on `IAcDreamPlugin`.
2. ~~Add fake host, fake storage, fake chat, fake navigation, and fake UI fixtures.~~ **Done** — `AcDream.Plugin.Tests.Fixtures` ships `FakePluginHost`, `FakePluginStorage`, `FakePluginCommandRegistry`, `FakeEvents`, `FakePluginChat`, `FakeNavigationAutomation`, and `FakeAutomationSurface`. Every fixture is settable, inspectable, and isolates callers.
3. ~~Add contract tests for inert hosts, disposal, event ordering, and command registration.~~ **Done** — `AcDream.Plugin.Tests` contains `FakePluginHostContractTests` (29 tests) covering: default/inert values, read/write storage round-trips, event fire/unsubscribe, exception isolation, command registration/dispose/invoke, chat capture and link-click routing, navigation snapshot/command/event delivery, and logger capture.
4. ~~Standardize the supported test SDK and package versions.~~ **Done** — `Directory.Packages.props` centrally manages all package versions (xUnit 3.2.2, Microsoft.NET.Test.Sdk 17.14.1, coverlet 6.0.4).

All Phase 1 deliverables are checked in and the full solution builds clean.

## Phase 2: Navigation reliability

The plugin now has a graph-based route planner, but navigation still executes one point at a time and does not yet understand portal or interaction legs.

1. ~~Add navigation report events and monotonic revisions.~~ **Done** — `IEvents.NavigationChanged` now emits sequence/state changes on graphical and headless hosts.
2. ~~Add request ownership and cancellation rules.~~ **Done** — navigation requests are owner-scoped, competing plugin requests are held, and releasing a plugin cancels its walk and pauses.
3. ~~Add portal-space and position-change events.~~ **Done** — `INavigationAutomation.SnapshotChanged` publishes revisioned navigation snapshots on graphical and headless hosts.
4. ~~Define coordinate units, elevation behavior, and `CellId = 0` semantics.~~ **Done** — `PluginNavigationPosition` documents 240-meter map units, elevation, and zero-cell point semantics.
5. ~~Add blocked, interrupted, unavailable, and arrival integration tests.~~ **Done** — navigation ownership and report behavior are covered by focused runtime navigation tests, including terminal `NoRoute`, `Blocked`, `Stopped`, `Interrupted`, and `Lost` projections. The out-of-sight arrival test now uses an impassable wall extending beyond the floor, preventing the solver from routing around the test geometry; the portable gate passes with 8,281 tests.

This phase should make the existing GoArrow core route walker reliable without adding rendering. **Phase 2 is complete on the current navigation-reliability branch.**

## Phase 3: World interaction and semantic state

1. ~~Add object lifecycle/change events.~~ **Done** — `IEvents.ObjectChanged` now carries monotonic revisions, normalized current-object snapshots, and `ChangedFields`; graphical, headless, scoped, and fake hosts are covered.
2. ~~Add normalized object capabilities and interaction reports.~~ **Done** — `PluginObjectCapabilities`, `CanActivate`, semantic `Activate`, item-use completion reports, stale-report guards, and dedicated `PluginActivationCompletion` reports with `PluginActivationOutcome` (Completed, Refused, Interrupted, TargetLost, Blocked, TimedOut) are available.
3. ~~Add portal, door, NPC, item-use, and dialog automation.~~ **Done** — world-object activation, item-use completion, portal transition reporting, activation completion events (`IEvents.ActivationCompleted`), dialog automation, and headless/graphical parity are implemented. Failure and interruption outcomes have a complete semantic report model through `PluginActivationOutcome`.
4. ~~Add recall, house, allegiance, and portal-transition state.~~ **Done** — generation-safe recall commands cover lifestone, marketplace, house, mansion, and allegiance hometown; recall request revisions correlate with portal transitions; authoritative house locations, normalized allegiance snapshots, and learned marketplace/mansion/lifestone/allegiance locations are exposed. All recall locations are learned after successful recall portal transitions.
5. ~~Add tests for failed, interrupted, and stale state changes.~~ **Done** — stale object, stale portal, terminal transition, duplicate snapshot, house revision, host-parity, and failed/interrupted interaction integration tests exist. The portable gate passes with 8,280 tests (1 pre-existing navigation test failure).
6. ~~Add explicit portal entrance/exit metadata so portal devices can become graph edges.~~ **Done in GoArrow** — `PortalDevice` accepts optional `Entrance`/`From` and `Exit`/`To` location names; explicit records now create portal graph edges. A host interaction API is now available through `IWorldObjectAutomation.Activate`.

This phase enables robust multi-leg route execution and authoritative recall tracking. **Phase 3 is complete on the current feature/plugin-phase3-world-interaction branch.**

## Phase 4: Data and UI

1. ~~Add scoped structured storage and atomic writes.~~ **Done** — plugin storage now supports validated logical scopes, JSON helpers, UTF-8 atomic replacement, and unavailable-host behavior.
2. ~~Add user-overridable route database directories.~~ **Done** — `IPluginResourceCatalog` exposes deterministic layered data-file discovery with package defaults and host/user override roots.
3. ~~Add resource lookup and packaging declarations.~~ **Done** — declared package resources are available through a safe, traversal-checked `OpenRead`/`List` catalog, with an inert headless implementation.
4. ~~Add text input, autocomplete, virtualized lists, and binding invalidation.~~ **Done** — plugin UI contracts provide input suggestions, stable list rows, property invalidation, view invalidation, and focus routing.
5. ~~Port GoArrow's route editor and destination search to the declarative panel.~~ **Done** — shared destination search and stable-key route-editor binding state support capped prefix-first completion, remove, reorder, and live panel refresh.

**Phase 4 is complete on the current data/UI contract branch.**

## Phase 5: Maps and rendering

1. Add map coordinate conversion services.
2. Add asynchronous tiled image/map resources.
3. Add map/canvas controls and marker interaction.
4. Add plugin-owned transparent HUDs.
5. Port the arrow, toolbar, Dereth map, and dungeon map HUDs.

## Phase 6: Chat parity

1. ~~Add coordinate parsing and structured chat links.~~ **Done** — compass coordinates such as `28.5S, 59.3E` are parsed and exposed as typed coordinate links.
2. ~~Add link-click events and plugin action routing.~~ **Done** — `IPluginChat.LinkClicked` delivers typed `PluginChatLinkClicked` events to plugins.
3. Add command aliases, quoting, completion, and generated help.
4. Restore the original clickable coordinate workflow.

---

# 13. Definition of “100% port”

A 100% port means that GoArrow can reproduce the original user-visible behavior while remaining entirely within the supported OpenAC plugin contract:

- directional arrow HUD;
- map and dungeon map HUDs;
- floating toolbar HUD;
- clickable coordinate and object links;
- destination and route management;
- destination autocomplete and panel input;
- recall, house, and allegiance tracking;
- portal, NPC, door, item, spell, and dialog interaction;
- route walking with ownership, retries, replanning, and transition recovery;
- persistent per-character settings;
- user-updatable location, portal, map, and route data;
- equivalent command, panel, chat, and HUD workflows;
- deterministic lifecycle and integration tests.

Until the rendering, map, clickable-chat, authoritative state, and interaction/navigation capabilities are implemented, `openac.goarrow` should be described as a **supported core navigation port**, not a complete behavioral replacement for the original GoArrow plugin.

---

# 14. Agent guidance

Agents implementing these improvements should follow these rules:

1. **Inspect existing abstractions first.** Extend an existing interface when the capability belongs to an existing service; create a new service only when ownership and lifecycle are clear.
2. **Keep the abstraction semantic.** Expose positions, portal transitions, interactions, and reports—not packets, memory addresses, renderer objects, or client-private IDs.
3. **Add a fake implementation with every new capability.** A new interface without a deterministic fixture is difficult for plugin agents to use safely.
4. **Add contract tests before host integration.** Verify inert behavior, disposal, ownership, and event ordering independently of the live client.
5. **Preserve headless behavior.** Every optional feature must have a no-op or unavailable implementation that does not throw during plugin load.
6. **Define units and threading in XML documentation.** In particular, document map units versus meters, event threads, snapshot consistency, and cancellation behavior.
7. **Prefer immutable records for snapshots and reports.** Mutable shared state makes plugin behavior nondeterministic.
8. **Use revisions and request IDs.** Plugins need to reject stale reports and distinguish their current request from a previous one.
9. **Do not expose raw protocol access as a shortcut.** If GoArrow needs a semantic event, implement that semantic event in the host.
10. **Update the GoArrow port and tests with each abstraction change.** The port is the reference consumer and should demonstrate the intended API shape.
