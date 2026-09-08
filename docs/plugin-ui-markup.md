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
        IconText = "MP",             // shelf button initials, used when IconSurfaceId is 0
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

Every window gets drag, an optional resize, the global UI lock, and a
persisted position keyed `plugin:{pluginId}:{windowId}`. Hiding a window never
pauses the plugin.

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
| `list onchange` | `Action<int>` | throws |
| `column` attributes | see "Columns" | see "Columns" |

Hex literals need the `0x` prefix; `did="165"` is decimal 165, `did="0x165"`
is hex. `list colors` values are `0xRRGGBB`; every `color`, `background`, and
`border` attribute is `#AARRGGBB`.

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
| `field` | Single-line text input | `x y w h text maxlength clearonsubmit onchange onsubmit color background` |
| `menu` | Drop-down | `x y w h items selected onchange rows rowheight openupward style` |
| `list` | Scrolling rows | `x y w h selected onchange rowheight selectionband`, then either `items colors icons iconkind` or `<column>` children |

Every element except the root also accepts `name` (or `id`), `visible`,
`enabled`, `tooltip`, and `anchor`. The root `panel` accepts `visible` only as
a binding.

`menu style` and `slider style` are `plain` (default: flat fill, one-pixel
border, no sprite art) or `retail` (the game's own pushbutton or scrollbar
art). A `menu` always opens as one scrolling column of at most `rows` entries;
when the entries overflow, the popup shows the game's scrollbar.

## Resizable panels and anchors

Panels are fixed-size unless the root declares `resizable="true"`. `minw` and
`minh` set the floor for a drag or a restored layout; they default to the
authored `w` and `h`. `resize="x|y|both|none"` limits the axes.

Children follow a resize through `anchor`, a space-separated subset of
`left top right bottom` naming the edges of the **direct parent** the element
keeps a fixed margin to. The default is `left top`.

- `left top`: fixed position and size.
- `left right`: stretches horizontally. `top bottom`: stretches vertically.
- `left top right bottom`: stretches both ways.
- `right` alone: fixed width, moves with the parent's right edge. Same for
  `bottom`.

A `group` propagates a resize to its own children, so anchor the group to the
panel and the list to the group:

```xml
<panel x="0" y="0" w="420" h="320" title="My Plugin" resizable="true" minw="360" minh="260">
  <group anchor="left top right bottom" x="8" y="8" w="404" h="304" border="#FF4A3A14">
    <label x="4" y="4" text="Monsters"/>
    <list anchor="left top right bottom" x="4" y="24" w="396" h="276"
          items="{MonsterNames}" selected="{SelectedMonster}" onchange="{SelectMonster}"/>
  </group>
</panel>
```

An unknown anchor token throws at build time. Changing a panel's authored
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

## Tests

Markup behavior is covered by `MarkupDocumentTests`, `MarkupIconTests`,
`MarkupListColumnsTests`, `MarkupResizableAnchorTests`, and
`PluginSidePanelTests` under `tests/AcDream.App.Tests/UI/`, all against fake
resolvers rather than the game's data files.
