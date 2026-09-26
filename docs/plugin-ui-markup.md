# Plugin UI markup

A plugin describes its windows in a small XML vocabulary and hands it to the
host through `AcDream.Plugin.Abstractions.IUiRegistry`. The host parses the
markup, builds the widgets, draws them in the game's own look, and re-reads
bound values every frame. Plugins never touch UI objects directly and never
depend on `AcDream.App`.

## Registering a panel

```csharp
host.Ui.AddPanel(
    new PluginPanelDescriptor("main", "My Plugin")
    {
        IconText = "MP",             // shelf button initials, the last resort
        IconSurfaceId = 0x06002C41,  // an icon id (see "Icon ids")
        StartVisible = true,
        ShowInSidePanel = true,      // default: a button in the plugin shelf
    },
    Path.Combine(pluginDirectory, "main.xml"),
    binding);
```

- `AddPanel(descriptor, markupPath, binding)` registers a window for the
  plugin's lifetime.
- `RegisterPanel` has the same signature and returns an `IDisposable` that
  removes the window on its own.
- `RegisterPanelContent` takes the markup as a string instead of a file path.

The plugin shelf picks a button's icon in order: the plugin's own `icon.png`
(one per plugin, at the root of its install folder), else `IconSurfaceId`,
else the initials from `IconText`.

Every window gets drag, an optional resize, the global UI lock, and a
persisted position keyed `plugin:{pluginId}:{windowId}`. Hiding a window never
pauses the plugin.

## Shared plugin appearance

Right-click the plugin shelf or an entry to open **Plugin appearance**. Classic
is the default. **Charcoal + moss** and **Warm graphite + brass** apply to
windows whose root uses `<panel theme="plugin" ...>`. The choice is saved in
the client's settings. Existing windows without this attribute keep their
original styling. Both modern themes use the bundled Noto Sans Regular font;
no operating-system font installation is required. Classic keeps its original
font. The font loader is shared client infrastructure (`BundledUiFont.Load`),
so other client panels can use it too; this preference currently changes only
opted-in plugin windows. The atlas covers Latin, Greek, Cyrillic and common
punctuation; unsupported characters display a question mark.

Modern shelves are 24 pixels wide; wheel scrolling reaches
entries that do not fit vertically. Icons keep their full-color composition.

Opted-in windows keep their authored layout and use themed labels, buttons,
tabs, fields, menus, lists, toggles and scrollbars. Literal and bound semantic
colors remain unchanged. To adapt a decorative color, use a token with its
original Classic fallback, for example `color="theme:text|#FFE8DEC3"` or
`background="theme:field|#FF0C0906"`. Tokens are `text`, `muted`, `field`,
`border`, `accent`, and `background`; the fallback uses `#AARRGGBB`.

Set `searchable="true"` on a menu to add an editable search band. Each typed
word must match its label, ignoring case. Filtering does not select an item;
click a result or press Enter to select it, use arrows to navigate, or Escape
to dismiss. Each opening refreshes the source list and clears the query.
Omitting the attribute retains existing menu behavior.

Fields support `oneline="false"` for wrapping and `editable="false"` for a
read-only description. Both default to true. A multiline field can scroll
through text longer than its viewport.

## Bindings

An attribute value is either a literal or a binding `{Name}`. A binding names
a public property, `Action`, or `Action<T>` on the binding object. It is
resolved once when the panel is built and then re-read every frame, so a
plugin updates its UI by assigning a property. Assign from the thread that
calls `Tick`.

A binding to a member that does not exist throws `FormatException` at build
time for every attribute except the ones marked *silent* below, which fall
back at runtime instead.

| Attribute | Type | Missing binding |
|---|---|---|
| `label text`, `field text`, `menu selected`, `tooltip` | `string` (via `ToString()`) | silent: the literal text is shown |
| `color`, `background`, `border` (every element that takes one) | `uint` or `int` holding `0xAARRGGBB`, or a `string` in the `#AARRGGBB` literal form | silent: opaque white, the same as an unparseable literal |
| `meter cur`, `meter max` | integral, nullable | silent: no value shown |
| `meter fill`, `slider value` | `float` | silent: 0 |
| `list items`, `menu items` | `IEnumerable<string>` | throws |
| `list colors` | `IEnumerable<uint>` or `IEnumerable<int>`, `0xRRGGBB` per row | silent when omitted; throws when mistyped |
| `list icons` | `IEnumerable<uint>` or `IEnumerable<int>` | silent when omitted; throws when mistyped; a negative element draws no icon |
| `icon did` / `spell` / `item`, `button icon` | any integral type | throws at build for a missing member; a negative or out-of-range value draws nothing |
| `list selected` | `int` | throws |
| `tab selected`, `toggle checked`, `visible`, `enabled` | `bool` | throws |
| `onclick` (button, tab, toggle) | `Action` | throws |
| `slider onchange` | `Action<float>` | throws |
| `field onchange`, `field onsubmit`, `menu onchange` | `Action<string>` | throws |
| `field onup`, `field ondown` | `Action` | throws |
| `list onchange` | `Action<int>` | throws |
| `column` attributes | see "Columns" | see "Columns" |

Hex literals need the `0x` prefix; `did="165"` is decimal 165, `did="0x165"`
is hex. `list colors` values are `0xRRGGBB`; every `color`, `background`, and
`border` attribute is `#AARRGGBB`.

## Bound colours

Every `color`, `background`, and `border` attribute takes a binding in place
of its literal, so a plugin that lets a player pick colours can show them.

```xml
<group x="8" y="8" w="404" h="304" background="{PanelColor}" border="{EdgeColor}">
  <label x="4" y="4" text="Monsters" color="{HeadingColor}"/>
  <meter x="4" y="24" w="200" h="14" fill="{HealthFraction}" color="{HealthBarColor}"/>
</group>
```

```csharp
public uint PanelColor { get; set; } = 0xC0101018;   // 0xAARRGGBB
public int EdgeColor { get; set; } = unchecked((int)0xFF4A3A14);
public string HeadingColor { get; set; } = "#FFE8D8B0";
```

- A bound colour resolves from a **`uint` or `int` holding `0xAARRGGBB`** --
  the same byte order as the literal, alpha in the top byte -- or from a
  **`string` in the `#AARRGGBB` literal form**. Store a colour setting as
  whichever of the three suits the plugin; all three mean the same colour.
- The top byte is real alpha, not padding: `0x00FF0000` is invisible, not
  opaque red, exactly as `background="#00FF0000"` is.
- The value is re-read every frame, so assigning the property from the thread
  that calls `Tick` is all it takes to repaint.
- A binding that names nothing, or a value that is neither of those forms
  (including text the literal parser rejects), is **silent**: the attribute
  falls back to opaque white, which is where an unparseable literal already
  landed. Colours never throw at build the way `onclick` or `selected` do.
- A colour attribute left out entirely still means what it always did: the
  element's own default, which for a `group` is no fill and no border.

Per-row colours are a separate thing and unchanged: `list colors` and
`<column type="text" colors>` take a list of `0xRRGGBB` values with no alpha.

## Elements

Unknown or miscased element names throw at build time.

| Element | Purpose | Attributes |
|---|---|---|
| `panel` (root) | The window | `x y w h title visible resizable minw minh resize` |
| `group` | Layout container | `x y w h background border` |
| `label` | Text | `x y text color` |
| `button` | Button with caption and optional icon | `x y w h text color background border onclick icon iconkind` |
| `icon` | An icon | `x y w h did` or `spell` or `item`, `tooltip` |
| `meter` | Nine-slice bar | `x y w h fill cur max color` plus the nine-slice `backleft backtile backright frontleft fronttile frontright` |
| `tab` | Tab button | `x y w h text selected onclick` |
| `toggle` | Checkbox | `x y w h text checked onclick color` |
| `slider` | Horizontal slider | `x y w h value onchange min max style` |
| `field` | Single-line text input | `x y w h text maxlength clearonsubmit onchange onsubmit onup ondown color background` |
| `menu` | Drop-down | `x y w h items selected onchange rows rowheight openupward style` |
| `list` | Scrolling rows | `x y w h selected onchange rowheight selectionband`, then either `items colors icons iconkind` or `<column>` children |

Every element except the root also accepts `name` (or `id`), `visible`,
`enabled`, `tooltip`, and `anchor`. The root `panel` accepts `visible` only as
a binding.

A `field` can bind `onup` and `ondown` to handle arrow keys while focused.
Fields without these callbacks retain their usual history navigation.

Clicking a `button`, `tab`, or `toggle` invokes its action without taking keyboard
focus from the game. Use Tab to focus these controls for Enter or Space activation.

A `field` shows its bound `text` and goes on following it: while nobody is
typing in the field, a value that changes behind it -- a profile loaded after
the panel was built -- replaces what the field shows. `onchange` reports what
the player types; it is not called for the field's own value being shown, at
build time or later.

`menu style` and `slider style` are `plain` (default: flat fill, one-pixel
border, no sprite art) or `retail` (the game's own pushbutton or scrollbar
art). A `menu` always opens as one scrolling column of at most `rows` entries;
when the entries overflow, the popup shows the game's scrollbar.

## Resizable panels and anchors

Panels are fixed-size unless the root declares `resizable="true"`. `minw` and
`minh` set the floor for a drag or a restored layout; they default to the
authored `w` and `h`. `resize="x|y|both|none"` limits the axes.

Children follow a resize through `anchor`, a subset of `left top right bottom`
naming the edges of the **direct parent** the element keeps a fixed margin to.
Separate the names with commas or spaces (`anchor="right,bottom"` and
`anchor="right bottom"` are the same thing); the names are case-insensitive
and their order does not matter. Leaving the attribute off gives the default,
`left top`; writing it and naming no edge (`anchor=""`, `anchor=" "`,
`anchor=","`) throws, because an attribute that is there was meant to say
something.

- `left top`: fixed position and size.
- `left,right`: stretches horizontally. `top,bottom`: stretches vertically.
- `left,top,right,bottom`: stretches both ways.
- `right` alone: fixed width, moves with the parent's right edge. Same for
  `bottom`.

```xml
<panel x="0" y="0" w="300" h="200" resizable="true" minw="200" minh="150">
  <label x="10" y="10" text="Title"/>                        <!-- stays put -->
  <field anchor="left,right" x="10" y="40" w="280" h="20"/>  <!-- widens -->
  <button anchor="right,bottom" x="250" y="170" w="40" h="20" text="OK"/>
</panel>
```

`anchor` applies to every element kind in the table above. A `group`
propagates a resize to its own children, so anchor the group to the panel and
the list to the group:

```xml
<panel x="0" y="0" w="420" h="320" title="My Plugin" resizable="true" minw="360" minh="260">
  <group anchor="left top right bottom" x="8" y="8" w="404" h="304" border="#FF4A3A14">
    <label x="4" y="4" text="Monsters"/>
    <list anchor="left top right bottom" x="4" y="24" w="396" h="276"
          items="{MonsterNames}" selected="{SelectedMonster}" onchange="{SelectMonster}"/>
  </group>
</panel>
```

Margins are measured once, from the layout you authored, when the panel is
built. A group that is hidden while the window is resized — one page of a
tabbed panel, say — therefore opens laid out exactly as it would have been had
it been on screen the whole time; whether and when an element was ever visible
never changes where it lands.

An unknown anchor token, or a value that is present and names no edge at all,
throws at build time naming the element and the value. Changing a panel's authored
size or limits in a later plugin version resets each user's stored size once;
their saved position is kept.

## Icon ids

An icon id is either a full data-file id (`0x06xxxxxx`) or a bare index below
`0x01000000`. The host normalizes both through
`AcDream.Plugin.Abstractions.PluginIcons.Normalize`, which adds the
`0x06000000` prefix to a bare index and passes a full id through unchanged.
Plugins never call it themselves; it applies wherever a `did` is accepted,
including `IconSurfaceId`.

Icon fields on host records (`PluginSpellInfo.IconId`, `PluginSkillInfo.IconId`,
`PluginInventoryItem.IconId`, `PluginWorldObject.IconId`) are already full ids.
Do not add the prefix to them.

## Icon sources

An icon comes from one of three sources. On `<icon>` the source is whichever
attribute is set; on `<button>` and `<list>` it is `iconkind` (default `did`).

| Source | Draws |
|---|---|
| `did` | The art at that id, with the art's pure-white key color replaced the way the inventory draws a plain item |
| `spell` | The composited spell icon for a spell id: power-level backing, art, tint, and the self/fellow overlay |
| `item` | The composited icon for a live object id, read from the same object table the inventory uses |

```xml
<icon x="8"  y="8" w="32" h="32" did="7735" tooltip="An icon"/>
<icon x="48" y="8" w="32" h="32" spell="{SpellId}" tooltip="{SpellName}"/>
<button x="12" y="68" w="120" h="24" text="Report" icon="0x06002D14" onclick="{Report}"/>
<list x="12" y="100" w="256" h="108" items="{SpellRows}" icons="{SpellIds}" iconkind="spell" selected="{SelectedIndex}"/>
```

Rules:

- Exactly one of `did`, `spell`, `item` on an `<icon>`; `iconkind` is not
  valid there.
- `w` and `h` default to 32. Art is drawn nearest-filtered, aspect-preserved,
  and centered. An id of 0 or an unresolvable id draws nothing.
- An `<icon>` with a `tooltip` is hit-testable; without one it is
  click-through.
- A `<list>` with `icons` draws one square icon column at the left, one
  icon per row, using one `iconkind` for the whole list. Rows without a
  matching icon draw text only.

## Columns

A `<list>` can declare typed columns instead of the single-column attributes.
The two forms cannot be mixed on one list.

```xml
<list x="8" y="24" w="256" h="120" rowheight="18"
      selected="{SelectedMonster}" onchange="{SelectMonster}">
  <column type="check" width="20"  values="{MonsterFlags}" onchange="{ToggleFlag}"/>
  <column type="text"  width="127" items="{MonsterNames}"  onclick="{PingMonster}"/>
  <column type="icon"  width="*"   iconkind="did" values="{MonsterIcons}" onclick="{MoveUp}"/>
</list>
```

| Attribute | Column type | Required | Binding |
|---|---|---|---|
| `type` | all | yes | `text`, `check`, or `icon` |
| `width` | all | see below | pixels, or `*` for auto |
| `items` | `text` | yes | `IReadOnlyList<string>` |
| `colors` | `text` | no | `IReadOnlyList<uint>` or `<int>`, `0xRRGGBB` per row |
| `onclick` | `text` | no | `Action<int>` with the row index; the click then does not select the row |
| `values` | `check` | yes | `IReadOnlyList<bool>` |
| `onchange` | `check` | yes | `Action<int>` with the row index; the plugin flips its own value |
| `values` | `icon` | yes | `IReadOnlyList<uint>` or `<int>` |
| `iconkind` | `icon` | no | `did` (default), `spell`, or `item` |
| `onclick` | `icon` | yes | `Action<int>` with the row index |

Widths: `*` shares the remaining width equally with every other `*` column.
The last column is always automatic; it takes the whole remainder unless
another column is also `*`. Every other column needs a positive `width` or
`*`. Columns that would overflow the list are clamped and later ones draw
nothing. Remainder pixels go to the last sharing column.

Rows: the row count is the longest bound column. A `text` or `icon` cell past
its own column's data draws nothing; a `check` cell draws unchecked. A click
in a `text` column without `onclick` selects the row and fires the list's
`onchange`; any other click fires the column's own callback and leaves the
selection alone. A click past a column's own data does nothing.

There is no header row; place `<label>` elements above the list. A `check`
cell draws the same lamp glyph as `<toggle>`. When rows overflow the list's
height it reserves a 16-pixel scrollbar at its right edge, drawn with the
game's scrollbar art; when they fit, no bar and no reservation. Row
selection draws no highlight band unless `selectionband="true"`.

## Slider range

`min` and `max` declare the range that `value` and `onchange` speak in; the
widget works in 0..1 internally. They default to 0 and 1.

```xml
<slider x="96" y="0" w="144" h="16" min="0" max="100"
        value="{HealPercent}" onchange="{SetHealPercent}"/>
```

## The plugin shelf

The shelf is the strip of plugin-window buttons at the right screen edge. It
is a normal window: draggable by the grip along its top, collapsible with
the `>`/`<` toggle at the grip's right end, and persisted like any other.
`Shift+Ctrl+F1` hides and shows it; hiding the shelf never disables a plugin
or touches a plugin window's own visibility.

## Showing and hiding your own window

A plugin window is on screen only while two things agree: the player's own
request (its shelf button and its close button) and, when the markup binds
the root's `visible`, the plugin's binding. Closing the window with its
close button clears the player's request, so setting the binding back to
true does not reopen it. `ShowPanel` and `HidePanel` make that request from
the plugin, exactly as the shelf button does:

```csharp
host.Ui.ShowPanel("main");          // by window id or title
host.Ui.HidePanel("main");
bool open = host.Ui.IsViewVisible("main");
```

They reach only the calling plugin's own windows and return false for a
window it does not have, before the window has been put on screen, and on a
client without a window. A window whose bound `visible` is false stays hidden
after `ShowPanel` until the binding is true again.

## Client windows

A plugin can also show, hide, toggle, or query one of the client's own
windows -- the same window a player opens with a keybind or a toolbar
button -- through `IUiRegistry`'s client-window methods:

```csharp
bool shown = host.Ui.ToggleClientWindow(PluginClientWindow.Inventory);
host.Ui.ShowClientWindow(PluginClientWindow.Character);
host.Ui.HideClientWindow(PluginClientWindow.Character);
bool isOpen = host.Ui.IsClientWindowVisible(PluginClientWindow.Spellbook);
```

`PluginClientWindow` lists the retained windows a player can open this way:
`Inventory`, `Character`, `CharacterInformation`, `Spellbook`, `Map`,
`Options`, `Social`, `Journal`, `PositiveEffects`, `NegativeEffects`,
`LinkStatus`, `Vitae`, and `Radar`. Every method returns `false` on a
no-window host or for a window this build does not mount --
there is no separate "unsupported" signal to check first.

This is unrelated to a plugin's own `AddPanel`/`RegisterPanel` windows: it
never creates, closes, or reaches into the plugin's own views, only the
client's pre-existing ones. Call it from the same thread that calls `Tick`,
same as any other UI call.

## Tests

Markup behavior is covered by `MarkupDocumentTests`, `MarkupIconTests`,
`MarkupListColumnsTests`, `MarkupColorBindingTests`,
`MarkupResizableAnchorTests`, and `PluginSidePanelTests` under
`tests/AcDream.App.Tests/UI/`, all against fake
resolvers rather than the game's data files. Client-window control is
covered by `BufferedUiRegistryTests` and `PluginClientWindowNamesTests` in
the same tree, and by `ScopedUiRegistryClientWindowTests` under
`tests/AcDream.Core.Tests/Plugins/` for the scoped forwarder. Plugin images
and canvases (see the plugin API guide) are covered by
`PluginImageTableTests`, `BufferedUiRegistryImagesTests`,
`BufferedUiRegistryCanvasTests` and `UI/Layout/PluginCanvasElementTests`
under `tests/AcDream.App.Tests/`, by `ScopedUiRegistryImagesTests` and
`ScopedUiRegistryCanvasTests` for the scoped forwarder, and by the contract
and headless suites for the inert answers a host without a window gives.

## Scrolling transcripts

`<log items="{Lines}" firstindex="{FirstIndex}" x="12" y="58" w="596" h="244" />`
renders a read-only, wrapped transcript with wheel and scrollbar navigation. It
follows appended entries while at the bottom. Scrolling back pauses following;
returning to the bottom resumes it. New entries never displace the reader's
anchor while paused. Resize and theme font changes rewrap text around the same
entry. Hidden windows retain their reading position.

`items` uses a cached `IReadOnlyList<string>` snapshot; replace the snapshot when
entries change. `firstindex` is an optional integer binding (default zero): the
sequence number of the first retained entry. Increment it by the number removed
from the front, including when clearing, so trimming retains the visible entry.
If that entry has been removed, the viewport stays at the oldest available entry
without resuming automatic following. The producer owns history retention limits.
The control participates in shared plugin themes and anchors.
