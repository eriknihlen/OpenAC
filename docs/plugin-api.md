# Plugin API: chat, lifecycle, spells, storage, clipboard, objects, confirmations, session and loot

Everything here lives in `AcDream.Plugin.Abstractions` and has a default
implementation, so a plugin written against an older build still compiles and
a host that cannot provide something returns an inert value rather than
throwing. `docs/plugin-ui-markup.md` covers the panel markup separately.

## Chat

### Reading lines

`host.Automation.Chat` offers two ways to read the client's text.

```csharp
host.Automation.Chat.Received += message =>
{
    // message.Kind, message.LogTextType, message.CombatKind, message.Received
};
```

`Received` fires once for every line the client takes delivery of, in
arrival order, on the same thread that raises `IEvents.Tick`. Nothing is
dropped: a handler sees every line while it is subscribed.

`CaptureMessages(afterSequence)` is the older poll. It keeps the last 512
lines, so a plugin that polls less often than that loses the overflow. Use
`Received` for anything that must be complete, such as a log.

`PluginChatMessage` carries:

| Member | Meaning |
|---|---|
| `Sequence` | Host-local, monotonic. Pass the last one back to `CaptureMessages`. |
| `SenderObjectId`, `Sender`, `Text`, `ChannelName` | Who said what, and where. |
| `Kind` | `0` local speech, `1` ranged speech, `2` channel, `3` tell, `4` system, `5` popup, `6` emote, `7` soul emote, `8` combat, `100` status notice (see below). |
| `LogTextType` | The text class the client colours the line by. |
| `CombatKind` | `0` when the line is not a combat line, `1` ordinary outgoing, `2` incoming, `3` failure. |
| `Received` | When the client took delivery of the line. |

### Dropping lines

```csharp
IDisposable filter = host.Automation.Chat.RegisterFilter(
    message => message.Text.Contains("Your spell burned"));
```

A filter is consulted *before* the line is shown. Returning true drops it
outright: it reaches neither the transcript, the chat windows,
`CaptureMessages`, `Received`, nor the chat log file.

- Filters run in registration order and stop at the first rejection.
- A filter that throws suppresses nothing; the host records the fault.
- Dispose the handle to remove one filter. The host removes every filter a
  plugin installed when that plugin unloads, so a plugin cannot leave the
  client permanently muted.
- A filter registered before login still applies to the next session.
- A dropped incoming tell never becomes the client's reply/retell target:
  the same append point that filters gate is where that target is recorded,
  so a suppressed tell leaves no trace to `/r` back to.
- Filters are client-wide, not per-plugin. A line one plugin drops is
  invisible to the client and to every other plugin, including one polling
  `CaptureMessages`. Match narrowly — a filter written for one plugin's own
  noise can silently blind every other plugin and the transcript itself.
- Do not post a message from inside a filter callback. The filter runs
  during line delivery, and posting there re-enters the same delivery path.

Short status notices — the ones shown over the world rather than written
into the transcript, such as "You're too busy!" — pass through the same
filters with `Kind == PluginChatMessage.StatusTextKind` (100). Check the
kind if a filter should treat them differently from transcript lines.

### Writing lines

`PostSystemMessage(text)` is unchanged. `PostMessage(text, logTextType)`
writes in one of the client's own text classes, so a plugin's own output can
use the colour the class carries. `Submit(text)` still runs the full chat
pipeline, commands included.

## Lifecycle

```csharp
host.Events.LoginComplete += () => { /* the local player is in the world */ };
host.Events.Logoff        += () => { /* the session is ending */ };
host.Events.LocalPlayerDied += deathMessage => { /* the server's message */ };
```

- `LoginComplete` fires once each time the local player enters the world.
  A reconnect does not reload plugins, so it fires again on the same
  instance — treat it as "there is a fresh world to work with", not as
  one-time setup.
- `Logoff` fires when the in-world session ends, before teardown, so a
  handler can still read gameplay state.
- `LocalPlayerDied` carries the server's death message. It comes from the
  death notification itself, so a plugin does not have to match chat text.

`host.Automation.Character.ServerPopulation` reports the players the server
reported connected in its login-time world-name message, or `-1` before that
message has arrived. The server sends this once, at login: it is a snapshot,
not a live count, and it does not change again for the rest of the session
even as players come and go.

## Spells

`host.Automation.Spells` gains the whole table, not just what the character
knows:

```csharp
IReadOnlyList<PluginSpellInfo> everySpell = host.Automation.Spells.All;

if (host.Automation.Spells.TryFindByName("Heal Self", partialMatch: true, out PluginSpellInfo spell))
{
    // ...
}
```

`All` is built on first use and cached. `TryFindByName` ignores case: an
exact match wins, and `partialMatch: true` falls back to the first name that
contains the text.

## Storage

`host.Storage.RootPath` is the absolute directory the plugin's keys are
written beneath, or `null` when the storage is not backed by files. It is
for telling a user where their data went — keys still go through
`ReadText` / `WriteText` / `List` / `Delete`.

## Clipboard

```csharp
if (!host.Clipboard.TrySetText(report))
    host.Log.Warn("Nothing was copied.");
```

The graphical client copies through the same device its own text controls
use. A host without a window has no clipboard and returns false, as does a
failed attempt, so always handle false rather than assuming the copy
happened.

`TrySetText` verifies the write by reading the clipboard back before
reporting success, so a silent platform failure (the graphical backend's
GLFW clipboard call can no-op without an exception) is reported as
`false` rather than a false `true`. That verification is only meaningful
on Windows: X11 and Wayland treat the clipboard as ownership-based, so as
long as this process still owns the selection, the getter just returns
its own last-set string back regardless of whether anything reached a
real system clipboard.

## Objects

```csharp
host.Events.ObjectChanged += change =>
{
    // change.ObjectId, change.Kind
};
host.Events.ContainerOpened += containerObjectId => { /* the corpse/chest/crate now open */ };
host.Events.ContainerClosed += containerObjectId => { /* it just closed */ };
```

`ObjectChanged` fires for every change to a world object the client is
tracking, on the same thread as `Tick`, in the host's own delivery order.
`PluginObjectChange.Kind` is one of:

| Kind | Meaning |
|---|---|
| `Created` | The object entered the client's object table for the first time. |
| `Updated` | A non-positional property or other field changed. |
| `IdentReceived` | The client took delivery of appraisal data for the object: the first reveal AND a later refresh of data already held (durability, stack count, and similar can change between requests). |
| `Moved` | The object's position changed enough to move it into a different cell. An in-cell position update that does not cross a cell boundary reports as `Updated` instead. |
| `Released` | The object left the client's object table (deleted, withdrawn, or an owned item leaving inventory). |

A bulk container reset carries no single object id and is not reported.

`host.Automation.Objects.Identify(objectId)` requests an appraisal of any
object present in the object table -- owned inventory, equipped,
landscape, a vendor listing, or an open container's content -- through
the same appraisal request the client's own assess uses, gated the same
way (`Busy` while another inventory request is in flight; `InvalidItem`
only for a guid the client has never seen). This is a different, wider
rule than `host.Automation.Loot.Identify`, which is deliberately scoped
to the currently open corpse/container's contents for a loot-sorting
plugin. Both report their result through the same `IdentReceived`
`ObjectChanged` event once the appraisal response lands -- `Identify`
itself only reports whether the request was accepted (`Started`) or
refused, not the appraisal outcome.

`IdentReceived` is reported from the appraisal response path; every other
kind is reported from the entity and inventory delta observers, which are
separate sources delivered in the same `Tick`-thread order but not
interleaved by a single shared sequence. An item held in inventory can
therefore raise two `ObjectChanged` calls for one underlying change (one
from the entity side, one from the inventory side); do not assume exactly
one call per change for such objects.

A plugin-driven `Identify` never touches the client's own examination
window. The object's properties/profiles update and `IdentReceived` fires
exactly as above regardless of what the window is showing, but the window
itself only opens, retargets, or comes to the front for the user's own
assess action (the assess keybind/click, or a headless bot's equivalent
"examine selected" command). Two exceptions follow directly from that
rule, not around it:

- If the object a plugin just identified happens to be the one already
  open in the window, that window's displayed numbers refresh in place
  (the user is already looking at it, so a durability tick or stack-count
  change should show up) -- but the window is never reopened or brought
  to the front for it.
- If the user assesses something while a plugin's `Identify` is still
  awaiting its response, the user's request wins the window: it opens (or
  retargets) for the user's object once that response lands, exactly as
  if no plugin request had been in flight.

The reverse never happens the other way: a plugin's `Identify` never
displaces a user assess that is already awaiting its response. It is
refused outright (`Refused`, or `Busy` if caught by the ordinary
busy-request gate first) rather than silently stealing the single
appraisal slot and making the user's own assess produce nothing.

`host.Automation.Loot.Appraisal` (`PluginAppraisalState`) reports the
shared appraisal slot's `Revision`/`AwaitingObjectId`/`CurrentObjectId` so
a plugin can poll for its own `Identify` to finish without waiting on
`IdentReceived`. `CurrentObjectId` is a completion signal, not the
window's displayed object -- it advances to whatever object last finished
an appraisal, of either origin, precisely because a plugin's Identify
must complete even while the examination window is showing something
else entirely (or nothing). Compare it against the id you passed to
`Identify`, together with `AwaitingObjectId` no longer matching that same
id, to know the response has landed.

`CurrentObjectId` is only meaningful for the request you yourself most
recently accepted -- it is not a history of every object ever appraised.
Issuing a new `Identify` for the same object id you previously saw
complete clears the signal for that id immediately (before the new
request is even sent), and cancelling the slot for a spell examine clears
it unconditionally. Do not compare `CurrentObjectId` against an id from
an earlier, already-consumed `Identify` call -- only against the id you
passed to the `Identify` call whose completion you are currently waiting
on.

`ContainerOpened` / `ContainerClosed` track the client's one open external
container — a corpse, a chest, a housing storage crate. A vendor's shop pane
is a separate surface (not covered by this event) and does not raise it.
Replacing one open container with another before it closes still reports a
`ContainerClosed` for the one that was open.

### Using a world object you don't own

```csharp
PluginItemCommandResult result = host.Automation.Items.Use(vendorObjectId);
// result.Status is Started once the walk begins; the vendor/corpse/chest
// panel (or IEvents.ContainerOpened) follows once the player arrives.
```

`Items.Use(objectId)` works for two different kinds of target, and picks
the right path automatically:

- An **owned item** (inventory, equipped, wielded) goes through the same
  inventory-use path as before — no movement, an immediate `Started` or
  `Refused`.
- A **world object** the plugin doesn't own — a vendor, a corpse, a chest,
  an NPC — walks to it first if it's out of range, the same way a
  double-click on it does, and dispatches the actual use once the player
  arrives. `Started` here means the walk (or the immediate use, if already
  in range) began, not that a container is open yet; watch
  `IEvents.ContainerOpened` or the vendor automation's own `Opened` event
  for that.

The world-object path is held to the same gates a click (or an owned
item's own automation) is held to, rather than bypassing them:

- `Refused` means the object isn't useable at all (for example, a target
  that requires being appraised first), or is another player — a
  player-to-player exchange goes through the Trade surface, not Use.
- `Busy` means the use-throttle refused it, an inventory request was
  already in flight, or an approach/use was already pending — the pending
  one is left alone rather than cancelled.
- `Unavailable` means the send itself was rejected by the transport,
  distinct from `Busy`'s "try again shortly".

`Apply(objectId, targetObjectId)` — using one item on another — is
unaffected by this: it still requires `objectId` to be an owned item.

Automation item commands (`Items.Use`/`Apply`/`MoveToContainer`/... and
this world-object path) are not thread-safe against each other or against
the client's own input: issue them from the same thread `IEvents.Tick`
fires on, exactly like every other automation entry point. A plugin that
calls them from its own background thread or an async continuation is
mutating movement/inventory/transport state the client's main thread also
touches, with no lock between the two.

### Weapon and armor profiles

```csharp
if (host.Automation.Objects.TryCaptureProperties(objectId, out PluginItemProperties properties))
{
    if (properties.WeaponProfile is { } weapon)
        Console.WriteLine($"Damage {weapon.Damage}, offense {weapon.WeaponOffense}");
    if (properties.ArmorProfile is { } armor)
        Console.WriteLine($"AL {armor.ArmorLevel}, slash {armor.SlashMod}");
}
```

An appraisal response carries two optional typed blobs alongside the
regular property tables: a weapon's real damage/offense numbers
(`PluginWeaponProfile`) and a piece of armor's per-damage-type protection
modifiers (`PluginArmorProfile`). Neither travels through
`PropertyInt`/`PropertyFloat` — the server does not populate those for
most weapons — so `TryCaptureProperties` (on `Objects`, the loot
surface's scoped `Identify`, and the item-automation surface) exposes them
as their own fields on `PluginItemProperties`:

- `WeaponProfile` is non-null only after the object has been successfully
  appraised AND its appraisal carried a WeaponProfile blob (i.e. it is a
  weapon). It stays null for a never-appraised object or a non-weapon.
- `ArmorProfile` is the armor equivalent, non-null only for an appraised
  piece of armor. `ArmorLevel` comes from the object's own
  `PropertyInt.ArmorLevel`, not the ArmorProfile blob itself, which does
  not carry it.
- A later, unrelated property update never clears an already-retained
  profile — only a fresh appraisal response does, and it fully replaces
  (or clears, if the new response omits the blob) whatever was there
  before.

`PluginInventoryItem`'s own `WeaponSkill`, `DamageType`, `Damage`, and
`DamageVariance` fields prefer the retained `WeaponProfile` when one is
present, falling back to the property table only for an unappraised item.
`Damage == -1` means the server's response left it unset (its own wire
sentinel is `uint.MaxValue`), not a real zero-or-negative damage value.

Units, since none of these read as plain integers or percentages:

- `WeaponOffense` and `DamageMod` are MULTIPLIERS centered on 1.0 — `1.05`
  means "+5%", `0.9` means "-10%", not an absolute offense/damage number.
- `DamageVariance` is a FRACTION of `Damage` describing the roll's floor:
  an actual hit rolls somewhere in
  `[(1 − DamageVariance) × Damage, Damage]`. `0.2` on a `Damage` of `12`
  means a real hit lands between `9.6` and `12`, never `0.2` itself.
- `WeaponTime` is a speed rating, not a duration in milliseconds or
  seconds — higher is slower, and it feeds the same attack-timing formula
  the assess window's own speed line uses.
- Every armor `*Mod` field (`SlashMod`, `PierceMod`, `BludgeonMod`,
  `ColdMod`, `FireMod`, `AcidMod`, `NetherMod`, `ElectricMod`) is also a
  MULTIPLIER applied to incoming damage of that type — `1.2` means that
  damage type does 20% MORE to the wearer, `0.8` means 20% less. It is not
  the flat armor-level number; `ArmorLevel` is the separate field for that.

`Objects.TryGet`/`CaptureObjects` are real on the headless host (see
[Headless](#headless)), but `Objects.TryCaptureProperties` and
`Objects.Identify` are not -- they need appraisal-wire and
external-container machinery no headless macro exercises yet, so they
always return `false`/`Unavailable` there regardless of whether the
object was ever appraised. The `Items`/`Loot` automation surfaces are
still entirely no-op on headless. `Vendor.TryCaptureProperties` is real on
both hosts -- the vendor automation adapter is shared verbatim between the
graphical and headless hosts, so a headless vendor-shopping plugin gets
the same `WeaponProfile`/`ArmorProfile` data a graphical one does.

## Confirmations

```csharp
host.Events.ConfirmationRequested += confirmation =>
{
    // confirmation.ContextId, confirmation.Type, confirmation.Text
    host.Automation.Dialogs.Answer(confirmation.ContextId, accept: true);
};
```

`ConfirmationRequested` fires whenever the server asks the client to show a
yes/no confirmation dialog. `Type` is the server's raw wire value; the only
one with fixed, known meaning is `5`, the crafting-percent confirmation
("this has a chance to fail, continue?"). Every other value is server-defined
and only distinguishable by `Text`.

`Dialogs.Answer(contextId, accept)` answers the dialog exactly as the
client's own Yes/No buttons would — it drives the same response builder, so
the server sees the identical reply. It returns `false` when there is no
outstanding dialog with that context id (already answered, timed out, or the
id does not match).

## Session

```csharp
if (!host.Automation.Login.Logout())
{
    // no in-world session to log out of
}
```

`Login.RequestLogout()` runs the client's own graceful logout — the same
route the UI's logout control uses. `Login.Logout()` is an alias that
forwards to it. Poll `Login.CanRequestLogout` first: it is `false` when the
surface is not `IsAvailable` (no in-world session) and while a teleport,
portal entry or earlier logout is already in flight, and `RequestLogout()`
returns `false` in the same cases. A `true` return means the logoff request
was sent; it does not report the outcome beyond that.

On the graphical host this returns to the character-select screen with the
process still running. On the headless host there is no character-select
screen to return to: the session sends the logoff, waits for the server's
confirmation, and then ends. If no confirmation arrives within 45 seconds
the session ends with a runtime error instead. Either way a headless plugin
that calls it should expect the session to end, not to see another
character list.

## Loot

A classifier is registered under `<pluginId>/<classifierId>` — the id a
plugin passes to `Register` is scoped by its own manifest id before other
plugins ever see it. MossTank, for example, registers `"moss-tank"` and is
visible to the rest of the client as `"<its plugin id>/moss-tank"`; use the
scoped id, not the bare one, when calling `TryNeedsIdentification` or
`TryClassifyWithProfile` from a different plugin.

Beyond the live-profile `Classify` a registered `IPluginLootClassifier`
already provides, two more members exist:

```csharp
bool blocked = host.LootClassifiers.TryNeedsIdentification(classifierId, context);

bool found = host.LootClassifiers.TryClassifyWithProfile(
    classifierId, "Vendor", context, out PluginLootClassification classification);
```

`NeedsIdentification` (and its registry forwarder `TryNeedsIdentification`)
reports whether an item cannot yet be classified with confidence: it lacks
appraisal data and at least one active rule needs an appraised property to
evaluate. A plugin can use this to hold off deciding until an identify
request completes.

`TryClassifyWithProfile` — both the classifier's own member and the
registry's forwarder of the same name — classifies against a *named, stored*
profile instead of the classifier's live one, such as VTank's "vendor" and
"trader" list files. It returns `false` when the named profile does not
exist; a classifier with no notion of named profiles defaults to the same.

`PluginLootAction` covers MossTank's full vocabulary, including its two
mana-transfer actions (`ManaStone`, `ManaTank`); a classifier reporting one
of those is a real match with `Matched` true and `RuleName` set, exactly
like any other action.

## Trade

```csharp
host.Automation.Trade.Opened += opened =>
{
    // opened.InitiatorObjectId (the local player), opened.PartnerObjectId
};
host.Automation.Trade.ItemAdded += added =>
{
    // added.ItemObjectId, added.Mine (true = staged on my side)
};
host.Automation.Trade.PartnerTradeAccepted += partnerId => { /* they hit accept */ };
host.Automation.Trade.Closed += () => { /* for any reason */ };

if (host.Automation.Trade.IsOpen)
{
    host.Automation.Trade.Add(itemObjectId);
    host.Automation.Trade.Accept();
}
```

`Trade` mirrors the retail-look secure-trade window one field at a time:
`IsOpen`, `PartnerObjectId`, `PartnerName`, `MyItems`, `PartnerItems`,
`MyAccepted`, `PartnerAccepted`. `Add`, `Accept`, `Decline`, `Reset`, and
`End` send the exact same wire commands the window's own buttons do, gated
the same way: each returns `PluginTradeCommandResult` with a
`PluginTradeCommandStatus` of `Unavailable` (no in-world session),
`NotOpen` (no trade window is open), `InvalidItem`, or `Sent`.

`Accept` is a no-op (returns `AlreadyAccepted` without touching the wire)
once `MyAccepted` is already true -- the same guard the window's own
Accept button has by disabling itself. `Decline`, `Reset`, and `End` carry
no such guard and always resend: a partner-declined round can be declined
again, and ending an already-closing trade is harmless.

The event named `PartnerTradeAccepted` — not `PartnerAccepted` — carries the
partner's object id when they accept. It could not be named `PartnerAccepted`
because that name is already the live acceptance flag; C# does not allow a
property and an event to share a name on one interface.

There is no wire bit for "who asked for this trade first": `Opened.
InitiatorObjectId` is always the local player's own object id, and
`PartnerObjectId` is always the other side, regardless of who actually sent
the open request.

A trade owner that registers a new partner while the window never closed
in between (one open trade replaced by another inside a single `Poll()`
interval) is reported as a `Closed` for the old partner immediately
followed by an `Opened` for the new one -- `Poll()` tracks the partner
guid, not just open/closed. Two or more such swaps landing inside the
same interval coalesce into a single close+open pair for the final
partner; an intermediate partner in that window is never individually
reported.

## Vendor

```csharp
host.Automation.Vendor.Opened += vendorId => { /* the shop pane just opened */ };
host.Automation.Vendor.TransactionCompleted += result =>
{
    // result.Kind (Buy/Sell), result.Success, result.Notice
};

foreach (PluginVendorItem item in host.Automation.Vendor.Items)
{
    // item.TemplateObjectId, item.Name, item.UnitPrice (retail sell-rate math -- the vendor's SellPrice, what it charges the player), item.StackSize
}

host.Automation.Vendor.AddToBuyList(templateObjectId, count: 1);
host.Automation.Vendor.BuyAll();

host.Automation.Vendor.AddToSellList(ownedItemObjectId);
host.Automation.Vendor.SellAll();
```

`Vendor.Items` lists what the shop currently has for sale, priced with the
same retail sell-rate formula the vendor window shows (quantity 1). Staging
is entirely local to this surface — `AddToBuyList` / `AddToSellList` and
their `Remove*` / `Clear*` counterparts never touch the wire — until
`BuyAll` or `SellAll` commits the staged list through the same builder the
window's own Buy All / Sell All buttons use, and clears the list on send. A
vendor selling a full stack sells however many of that item the character
currently owns, matching the window's own default. `TryCaptureProperties`
reads a listed item's already-materialized properties -- the data the
`ApproachVendor` listing itself carried, shaped like an appraisal but not a
live appraisal round trip -- by its `TemplateObjectId`, including the
`WeaponProfile`/`ArmorProfile` fields described under
[Weapon and armor profiles](#weapon-and-armor-profiles) when the listing
carries one.

`IsBusy` reports whether this adapter's own buy/sell is in flight -- it is
vendor-local, not the client-wide inventory-transaction busy state, which
a vendor transaction never touches. `BuyAll`/`SellAll` refuse with `Busy`
rather than queue behind an outstanding buy/sell of their own.

## Hotkeys

```csharp
IPluginHotkeyRegistration handle = host.Hotkeys.Register(
    "quick-heal",
    "Quick Heal",
    new PluginKeyChord(PluginKey.H, Ctrl: true),
    () => { /* Ctrl+H was pressed */ });

if (!handle.IsBound)
    host.Log.Warn("Quick Heal's default chord collided with a client binding.");
```

`Register` id is scoped by the plugin's own manifest id before the host ever
sees it, so two plugins registering `"quick-heal"` do not collide with each
other. A stored user override for the scoped id replaces the caller's
default chord at registration time; `handle.EffectiveChord` reports which
chord actually ended up bound. A chord that collides with an existing client
key binding is refused rather than silently stealing it: `handle.IsBound` is
`false` and the handler never fires. Disposing the handle revokes the
binding; the host also revokes every hotkey a plugin registered when that
plugin unloads.

A hotkey does not fire while the chat bar has keyboard focus unless Ctrl or
Alt is part of the chord — otherwise every letter typed into chat would also
be a candidate hotkey press.

Plugin hotkeys are a raw keyboard subscription, not a route through
InputDispatcher's action/scope engine (a dynamic per-plugin action space
large enough to fit that machinery would be a much bigger change than the
rest of this surface) -- documented deviation. Two dispatcher states still
suppress every hotkey, matching how the dispatcher itself would refuse to
route a client action in the same situations: a rebind capture in progress
(`InputDispatcher.BeginCapture`) and a modal `Dialog`/`EditField` scope
pushed on top (not just `Chat`, which has its own Ctrl/Alt carve-out
above).

The graphical host may receive a `Register` call before its keyboard and
input dispatcher exist yet (plugin loading is not strictly ordered against
input-dispatcher composition); the registration is queued and resolved the
moment the input layer comes up, so `IsBound` can flip from `false` to `true`
without the plugin doing anything further.

A chord that collides with another plugin's own already-bound hotkey is
refused the same way a client-binding collision is: first registered,
first bound. Registering the same scoped id a second time replaces the
first registration outright (the old handle's `IsBound` flips to false
and it stops firing) rather than adding a second live binding for that id.

`IPluginHotkeyRegistration.Rebind(chord)` stores a new chord as a user
override and re-resolves the registration immediately (headless treats it
as a no-op, matching its inert `Register`). Overrides persist to a
plugin-scoped `plugin-hotkeys.json`, keyed `<pluginId>:<hotkeyId>` --
sibling to, not inside, the client's own `keybinds.json` (the original
design sketch put overrides in `keybinds.json` itself; this was changed
so a corrupt or hand-edited plugin override file can never touch the
client's own binding schema). There is no in-client rebind UI yet; a
plugin (or a future Settings panel) calls `Rebind` directly.

## Host window

```csharp
if (host.Window.IsMinimized)
    host.Window.Restore();

HostWindowResult result = host.Window.Minimize();
if (!result.Succeeded)
    host.Log.Warn("could not minimize the client window.");
```

`host.Window` is one of the client's own OS window: minimize, restore, and
request-close, the same three controls the title bar already offers.

`Minimize()` sets the window to iconified; `Restore()` un-minimizes it if it
is currently minimized and is a no-op success otherwise -- it never forces
the window to a plain "Normal" state, because a window that was maximized
or fullscreen before it was minimized should come back maximized or
fullscreen, not windowed. Both report `HostWindowStatus.Done` only once the
window actually reports a state consistent with the request back, not just
because the call was made; a write that does not stick (no window focus, a
platform that refuses it) reports `Unavailable`.

That confirmation is not equally trustworthy on every platform. It is
synchronous on Windows. On X11 it arrives asynchronously over the window
manager's own state property, so a check immediately after `Minimize()` can
briefly still read the old state. On macOS the minimize animation means
there is a short window where the OS has not finished iconifying yet. On
Wayland the compositor protocol has no way to report iconification back to
the client at all, so `IsMinimized` never becomes `true` there and
`Minimize()` always reports `Unavailable` even when the window did minimize
-- treat `Unavailable` from `Minimize()` as "unknown", not as "definitely
still shown", and do not retry it in a loop on that signal alone.
`IsMinimized` itself reads a cached flag kept current by the window's own
state-change callback, not a live read of the window's state -- the same
window calls the writes above go through are documented main-thread-only,
so a live read from whatever thread a plugin happens to call this from
would carry the same silent-failure risk the write side already has to
guard against.

`RequestClose()` takes the exact route the window's own close button uses:
graceful logout, then teardown, then process exit. It never terminates the
process directly -- there is no `Environment.Exit`/`Process.Kill` on this
path, on either host. On a host with no window (headless), `Minimize`,
`Restore`, and `IsMinimized` stay at the interface's inert defaults
(`Unavailable`/`false`), but `RequestClose` still has somewhere real to go:
it ends the plugin's own session -- not the whole headless process -- the
same way a bot policy already ends its own session when it decides its job
is done. A second session hosted by the same process is untouched; only the
console's own `/quit` and a SIGINT/SIGTERM end every session in the process
at once. Without `--console` there is no `/quit` to type, so
`Window.RequestClose()` is the one graceful way a plugin has to end its own
headless session from the inside.

## Headless

A headless host implements this same contract, with a few members left as
placeholders rather than wired to real state:

- `Character` is real for identity: `Name`, `WorldName`, `AccountName`,
  `ObjectId`, `CharacterIndex`, `IsInWorld`, and `ServerPopulation` all come
  from the live runtime, exactly like the graphical host. Everything else on
  `Character` -- vitals (`CurrentHealth`/`MaxHealth`/etc.), `Skills`,
  `Attributes`, `ActiveEnchantments`, `Level`, `MainPackFreeSlots`, and
  `SummoningMastery` -- is still the interface's inert default; no headless
  macro reads them yet.
- `Spells` and `Magic` are entirely no-op: `Spells.All` / `TryFindByName`
  are always empty / always miss, and `Magic` never reports casting or
  accepts a cast request.
- `Objects.TryGet` and `Objects.CaptureObjects` are real, sourced from the
  same object table and entity directory the graphical host uses, through
  the same shared projection (name, weenie class id, item type,
  container/wielder ids, classification, ownership, position, appraisal
  data, capacities, stack size, door-open state, icon id all populate
  identically on both hosts). `Objects.TryCaptureProperties`,
  `Objects.Identify`, and `Objects.OpenContainerObjectId` remain the
  interface's inert defaults (`false`/`Unavailable`/`0`) -- they need
  appraisal-wire and external-container machinery no headless macro
  exercises yet. The one field the projection cannot populate identically
  on headless is `ActiveSpellIds` for the local player -- the graphical
  host tracks a live active-enchantment list this one doesn't, so it is
  always empty here.
- `Storage` is real: a headless plugin's settings persist to
  `<config>/plugins/<pluginId>/...`, the identical on-disk layout and root
  the graphical host uses, so hand-editing a settings file affects
  whichever host next loads that plugin. It is process-wide, not
  session-scoped -- every session hosted by one headless process shares
  the same `Storage` instance, so two bot sessions running the same
  plugin in one process share that plugin's one settings file (exactly as
  two plugin instances loaded into one graphical process would).
- `CaptureMessages` is unimplemented; use `Received` instead, which does
  work.
- `ContainerOpened`/`ContainerClosed`, `ConfirmationRequested`, and
  `Login.RequestLogout` (and its `Logout` alias) are real and wired to the
  same runtime state and session-command routes the graphical host uses;
  see the logout note above for how a headless session ends.
- `Dialogs.Answer` is real when the headless session was configured with a
  confirmation route; otherwise it returns `false` like any host with
  nothing bound.
- `Trade` and `Vendor` are real on both hosts: the same shared adapter binds
  over the same `GameRuntime`, so a headless bot sees identical state and
  sends the identical wire commands a graphical plugin would.
- `Hotkeys` is the inert no-op registry — there is no keyboard to bind to
  without a window. `Register` always returns a handle with `IsBound`
  `false` and the handler never fires.
- `Window.Minimize`/`Restore`/`IsMinimized` stay at the interface's inert
  defaults -- there is no OS window on a headless host.
  `Window.RequestClose` is real: it ends only this session, the same way
  a bot policy already ends its own session when it decides it is done;
  a second session hosted by the same process is untouched. Only the
  console's own `/quit` and a SIGINT/SIGTERM end every session in the
  process at once.
- Everything else on `IAutomationSurface` not named above --
  `Combat`/`Equipment`/`Items`/`Loot`/`Fellowship`/`Enchantments`/
  `Navigation`/`WorldTime`/`Network`/`Recovery`/`Projectile`/`Selection`
  automation -- is still the interface's inert `NoOp` default on headless.
