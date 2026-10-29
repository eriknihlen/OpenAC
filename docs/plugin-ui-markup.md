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

Canvases are not restyled: a plugin reads the theme and its palette from
`host.Ui.Theme` and repaints on `host.Ui.ThemeChanged`. See "Plugin theme"
in the plugin API guide.

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
| `list icons` | `IEnumerable<uint>` or `IEnumerable<int>`; `IEnumerable<string>` paths with `iconkind="file"` | silent when omitted; throws when mistyped; a negative element, or a null or blank path, draws no icon |
| `icon did` / `spell` / `item`, `button icon` | any integral type | throws at build for a missing member; a negative or out-of-range value draws nothing |
| `icon file`, `button icon` with `iconkind="file"` | `string`, a path in the plugin folder | throws at build for a missing or non-`string` member; a null or blank path draws nothing |
| `list selected` | `int`; with `selectmode="multi"`, `IEnumerable<int>` of plugin rows | throws |
| `tab selected`, `toggle checked`, `visible`, `enabled` | `bool` | throws |
| `onclick` (button, tab, toggle) | `Action` | throws |
| `slider onchange` | `Action<float>` | throws |
| `field onchange`, `field onsubmit`, `field onblur`, `menu onchange` | `Action<string>` | throws |
| `field onup`, `field ondown` | `Action` | throws |
| `list onchange` | `Action<int>`; with `selectmode="multi"`, `Action<IReadOnlyList<int>>` | throws |
| `checkbox checked` | `bool`, or `bool?` where null is indeterminate | throws |
| `checkbox onchange` | `Action<bool>`, the new value | throws |
| `number value` | `int`, `long`, `float`, `double` or `decimal` | throws |
| `number onchange` | `Action<T>` of the same type as `value` | throws |
| `hotkey value` | `string`, `Ctrl+Alt+Shift+Key`; empty is unbound | throws |
| `hotkey onchange` | `Action<string>` | throws |
| `color value` | `uint`, `int` or `string` `#AARRGGBB` | throws |
| `color onchange` | `Action<T>` of the same type as `value` | throws |
| `radio items` | `IEnumerable<string>`, or a literal list `a\|b\|c` | throws |
| `radio selected` | `int`, the chosen option (−1 for none) | throws |
| `radio onchange` | `Action<int>`, the option picked | throws |
| `expander expanded` | `bool`; when bound, the plugin owns the state | throws |
| `expander onchange` | `Action<bool>`, the new state | throws |
| `split position` | `float`, the first pane's size; when bound, the plugin owns it | throws |
| `split onchange` | `Action<float>`, the size asked for | throws |
| `contextmenu items` | `IEnumerable<string>`, or a literal list `a\|-\|b`; `-` is a separator | throws |
| `contextmenu enabled` | `IEnumerable<bool>`, one per item | throws |
| `contextmenu onselect` | `Action<int>`, the item index; separators count | throws |
| `contextmenu onopen` | `Action<int>`, the plugin row on a list, otherwise −1 | throws |
| `list sortcolumn` | `int`, the sorted column (−1 for none); bound together with `sortdescending` | throws |
| `list sortdescending` | `bool` | throws |
| `list onsort` | `Action<int, bool>`, the column and direction asked for | throws |
| `list widths` | `IReadOnlyList<float>`, one per column (0 = automatic) | throws |
| `list onresize` | `Action<IReadOnlyList<float>>`, every column's width | throws |
| `list levels` | `IEnumerable<int>`, each row's depth (0 = top level) | throws |
| `list keys` | `IEnumerable<string>`; a list or array is read as is, other collections are copied each frame | throws |
| `list expanded` | `IReadOnlySet<string>` (best: `HashSet<string>`), or any `IEnumerable<string>`, which is copied each frame; when bound, the plugin owns it | throws |
| `list onexpand` | `Action<string, bool>`, the key and the state asked for | throws |
| `column` attributes | see "Columns" | see "Columns" |

Inside `<repeat items="{X}">`, names resolve on the element type of
property `X` of the enclosing scope, and nothing else is visible. The window's
own binding object cannot be reached from inside a repeat. See
[Repeat](#repeat).

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
| `panel` (root) | The window | `x y w h title visible resizable minw minh resize titlebar scroll`, and the flex container attributes |
| `group` | Layout container | `x y w h background border scroll`, and the flex container attributes |
| `label` | Text | `x y text color` |
| `button` | Button with caption and optional icon | `x y w h text color background border onclick icon iconkind` |
| `icon` | An icon | `x y w h did` or `spell` or `item` or `file`, `tooltip` |
| `meter` | Nine-slice bar | `x y w h fill cur max color` plus the nine-slice `backleft backtile backright frontleft fronttile frontright` |
| `tab` | Tab button with optional icon | `x y w h text selected onclick icon iconkind` |
| `toggle` | On/off lamp | `x y w h text checked onclick color` |
| `checkbox` | Tick box, optionally indeterminate | `x y w h text checked onchange color` |
| `number` | Number field with spin buttons | `x y w h value onchange min max step decimals color background` |
| `hotkey` | A box that records a key chord | `x y w h value onchange` |
| `color` | A colour swatch that opens a picker | `x y w h value onchange alpha` |
| `radio` | One choice of several, as round buttons or a segmented bar | `x y w h items selected onchange orientation style color` |
| `separator` | A 1 px dividing line | `x y w h color` |
| `expander` | A header that folds away the content below it | `name x y w h text expanded onchange open color` |
| `split` | Two panes with a bar between them the player drags | `name x y w h orientation position onchange at min max` |
| `contextmenu` | Right-click menu for the element it is inside | `items enabled onselect onopen` |
| `slider` | Horizontal slider | `x y w h value onchange min max style` |
| `field` | Single-line text input | `x y w h text maxlength clearonsubmit onchange onsubmit onblur onup ondown color background` |
| `menu` | Drop-down | `x y w h items selected onchange rows rowheight openupward style` |
| `list` | Scrolling rows | `x y w h selectmode selected onchange rowheight selectionband`, then either `items colors icons iconkind` or `<column>` children (which also take `header resizable sortcolumn sortdescending onsort widths onresize tree levels keys expanded onexpand collapsed`; column form only) |
| `repeat` | One copy of its children per item of a list (flex containers only) | `items key` |

Every element except the root also accepts `name` (or `id`), `visible`,
`enabled`, `tooltip`, and `anchor`. The root `panel` accepts `visible` only as
a binding. A `contextmenu` takes none of them, and a `repeat` takes only
`items` and `key` (see [Repeat](#repeat)). Inside a flex container
elements take the flex item attributes instead of `x`, `y` and `anchor` (see
[Flex layout](#flex-layout)).

A `field` can bind `onup` and `ondown` to handle arrow keys while focused.
Fields without these callbacks retain their usual history navigation.

Clicking a `button`, `tab`, `toggle`, `checkbox`, `radio`, or `expander` (its
header) invokes its action without taking keyboard focus from the game. A `hotkey` is the exception: clicking it takes focus, while it records. A `color` swatch doesn't take focus on click either, but its open picker holds the keyboard until it closes. Use Tab to focus these controls:
Enter or Space then activates a `button`, `tab`, `toggle`, `checkbox` or
`expander` (its header). A focused `radio` uses the arrow keys instead; Enter
and Space do nothing on it. A focused `split` bar moves 8 px with the arrow
keys along its axis (Left/Right side by side, Up/Down stacked).

A `field` shows its bound `text` and goes on following it: while nobody is
typing in the field, a value that changes behind it -- a profile loaded after
the panel was built -- replaces what the field shows. `onchange` reports what
the player types; it is not called for the field's own value being shown, at
build time or later.

A `field` has three ways to hand its text back, each an `Action<string>`:

- `onchange` fires on every edit, with the whole text.
- `onsubmit` fires when Enter is pressed on a one-line field with text that is
  not blank. Enter then ends the edit, and with `clearonsubmit="true"` the field
  empties.
- `onblur` fires when the field loses keyboard focus, with the text it has at
  that moment: the player clicks another control or empty space, presses Escape
  or Tab, or the panel hides or closes while the field is focused. Use it to
  apply a value the way an ordinary text box does, without requiring Enter.
  It fires once per focus loss, even when the callback itself hides or
  removes the panel.

The focus loss that Enter causes does not fire `onblur` when `onsubmit` has
just received the text or `clearonsubmit` has just emptied the field, so a
field that binds both never applies the same text twice and never reports the
cleared empty text. A field with `onblur` and no `onsubmit` still applies its
text when Enter ends the edit.

`menu style` and `slider style` are `plain` (default: flat fill, one-pixel
border, no sprite art) or `retail` (the game's own pushbutton or scrollbar
art). A `menu` always opens as one scrolling column of at most `rows` entries;
when the entries overflow, the popup shows the game's scrollbar.

## Title bar

`titlebar="true"` on the root `<panel>` gives the window a host title bar
(a root with `layout` has it unless it says `titlebar="false"`; see
[Flex layout](#flex-layout)):
the window's name on the left and a close button on the right, drawn in the
window's look (the game's close art in Classic; in the shared themes a plain
palette button with an "x"). The name is the panel's `title`, or the title the
window was registered with when the markup has none. `titlebar` takes
`true` or `false`; anything else is ignored, with a warning in the client
log.

With the bar on, the window has a **content area** inside the chrome:

- children's `x`/`y` are measured from the content area's top-left corner,
  so nothing you place can sit under the title;
- `w`, `h`, `minw` and `minh` describe the content area, and the host adds
  the chrome: 5 points left, right and bottom, and the 24-point bar on top
  (the frame is 10 points wider and 29 points taller);
- the root's `x`/`y` still position the window's outer frame, while children
  are placed from the content area's corner;
- dragging the bar moves the window;
- the frame is never narrower than 96 points, so the close button always
  leaves room for the start of the title; a narrower window opens at 96.

```xml
<panel x="120" y="120" w="300" h="160" title="Buff Bot" titlebar="true">
  <label x="8" y="8" text="Ready" />
  <button x="212" y="128" w="80" h="24" text="Start" onclick="{Start}" anchor="right bottom" />
</panel>
```

The close button does what the window's shelf button does: it closes the
window for the player and the plugin keeps running; the shelf button (or
`ShowPanel`) opens it again. A window with a bar does not get the shelf's
"–" button, since its X does the same thing; the X's tooltip says so. A window
kept out of the shelf (registered with `ShowInSidePanel` off) has no shelf
button, so after the player closes it only the plugin's `ShowPanel` opens it
again; give players a way to call it, such as a command or a button. Turning
the bar on for an existing window changes its size, so a size the player
saved is reset once (the position is kept).

The `samples/AcDream.Plugins.ControlsDemo` plugin opens one window per group
of the controls below (inputs, keys and colours, layout, table, tree, and
dialogs), each with a status line that shows what its callbacks receive.
Every window uses `theme="plugin"`, so switching **Plugin appearance** shows
each control in Classic and in both modern themes. Its markup files are
working examples of each of the new controls, from `<checkbox>` to trees.

## Checkbox

```xml
<checkbox x="8" y="8" w="140" h="16" text="Loot rares"
          checked="{LootRares}" onchange="{SetLootRares}"/>
```

A `<checkbox>` is a tick box; a `<toggle>` is an on/off lamp. Both stay
available: use whichever reads right for the setting.

- `checked` is required and binds a `bool`, or a `bool?` whose `null` draws the box
  indeterminate (a dash), e.g. a "select all" over a mix of rows.
- `onchange` receives the value the click asks for, not a bare "clicked":
  unchecked and indeterminate become `true`, checked becomes `false`. The
  plugin stores it; the box shows whatever `checked` returns next frame.
  Without `onchange` the box only displays.
- Tab focuses it; Space or Enter activates it, as for `<toggle>`.

In a list, `<column type="checkbox">` takes the same `values` and
`onchange` as `type="check"` and draws tick boxes instead of lamps.
On a host older than checkboxes, a `<checkbox>` element or `type="checkbox"`
column fails the build of its window as unknown.

## Number field

```xml
<number x="8" y="8" w="80" h="20" value="{HealAt}" min="0" max="100" step="5"
        onchange="{SetHealAt}"/>
```

A `<number>` is a one-line field that only ever hands back a valid number,
with spin buttons at its right edge.

- `value` binds an `int`, `long`, `float`, `double` or `decimal`; `onchange`
  is an `Action<T>` of the same type. Both are required.
- `min` and `max` default to the type's range (for `float` and `double`,
  ±7.9×10^28). `step` defaults to 1 and must be above 0. `decimals` defaults
  to 0 for `int` and `long` (and must stay 0) and to 2 otherwise, at most 10.
  `step` must not have more decimal places than `decimals`, so a fractional
  `step` on an `int` or `long` fails the build.
- Typing accepts digits, a minus sign when `min` is below 0, and a point
  when `decimals` is above 0. Text that isn't a number, or is out of range,
  shows in red.
- Enter, or leaving the field, commits: the value is rounded to `decimals`,
  clamped to `min`..`max`, and `onchange` is called if it changed. Leaving the
  field without changing the text reports nothing. Text that isn't a number
  puts the bound value back. Escape puts it back too.
- The spin buttons step once per press and repeat while held. Pressing one ends typing: the
  typed text is committed and focus leaves the field. Up and Down
  step while the field has focus, and so does the mouse wheel; the wheel
  never steps an unfocused field, so scrolling a list past it is safe.
  Up, Down and the wheel step from the number being typed, if it's a valid
  number: typing 42 then pressing Up gives 42 plus `step`. Hold Shift for ten
  steps. (On macOS, Shift with a mouse wheel arrives as a sideways step; see
  the wheel in [Scrolling groups](#scrolling-groups).)
- While nobody is typing in it, the field follows `value`.

On a host older than number fields, a `<number>` fails the build of its
window as unknown.

## Hotkey

```xml
<hotkey x="8" y="8" w="140" h="20" value="{ToggleNavKey}" onchange="{SetToggleNavKey}"/>
```

A `<hotkey>` shows a key chord and lets the player record a new one. The
plugin decides what the chord does; the box only edits the string.

- `value` binds a `string` and `onchange` an `Action<string>`; both are
  required. The string is `Ctrl+Alt+Shift+Key`: the modifiers that are held,
  in that order, then the key's `PluginKey` name (`F`, `F12`, `Number1`,
  `Numpad5`, `Grave`, `Enter`). Empty means unbound. When reading `value`
  the box also accepts modifiers in any order and any case
  (`shift+ctrl+f`), and the key in any case, but `onchange` always hands
  back the exact form.
- The key names are the members of `PluginKey`:
  - letters `A` to `Z`; the number row `Number0` to `Number9`; `F1` to `F12`
  - `Space`, `Enter`, `Escape`, `Tab`, `Backspace`, `Delete`, `Insert`,
    `Home`, `End`, `PageUp`, `PageDown`, `Up`, `Down`, `Left`, `Right`
  - `Minus`, `Equal`, `LeftBracket`, `RightBracket`, `BackSlash`,
    `Semicolon`, `Apostrophe`, `Comma`, `Period`, `Slash`, `Grave`
  - the keypad: `Numpad0` to `Numpad9`, `NumpadDecimal`, `NumpadDivide`,
    `NumpadMultiply`, `NumpadSubtract`, `NumpadAdd`, `NumpadEnter`
  - `PrintScreen`, `Pause`

  The box records only these keys, apart from Escape, Tab, Backspace and
  Delete, which work the box (below); a `value` can still name them. Any
  other key (F13, Caps Lock) doesn't end a recording, and a `value` naming
  one shows `None`. So a recorded chord can always be registered:
  read it with `PluginKeyChord.TryParseHotkeyText(text, out PluginKeyChord chord)`,
  and show a hotkey's chord in a box by binding `value` to
  `chord.ToHotkeyText()`. See [Hotkeys](plugin-api.md#hotkeys).
- The box shows the modifiers as Ctrl, Alt and Shift (never left or right,
  since the string doesn't record a side) and the key by the game's own key
  names. An empty value, or one it
  can't read, shows `None`.
- Clicking the box, or Enter or Space while it has focus, starts recording:
  the box shows `Press a key...` and takes every key press for itself, so
  neither the game's keys nor plugin hotkeys act while it records. Holding
  Shift, Ctrl or Alt alone keeps it waiting; the first other key ends it and
  `onchange` gets the chord. Escape stops without changing anything, and
  Backspace or Delete unbinds (`onchange` gets `""`), even with modifiers
  held. Keys pressed while the Windows/Command key is held are ignored. Tab
  can't be recorded.
- `onchange` is called only when the new string differs from `value`.
- After a recording started by a click, the box gives the keyboard back to
  the game when the key that ended it is released. One started with Enter or
  Space keeps the focus, so Tab carries on to the next control.
- Left-clicking elsewhere, the window hiding, or the box being disabled stops
  a recording without changing anything. After a click-started recording
  that is stopped by disabling the box, the focus is released at once.
- Mouse buttons are never recorded.

In Classic the box is a dark field with a thin border that turns gold while
it has focus; under `theme="plugin"` it takes the palette's field, border,
text, muted and accent colours and the panel's font.

A `<hotkey>` without `value` or `onchange`, or with either bound to the wrong
type, fails the build. On a host older than hotkey boxes, a `<hotkey>` fails
the build of its window as unknown.

## Colour picker

```xml
<color x="8" y="8" w="40" h="18" value="{BarColor}" onchange="{SetBarColor}" alpha="true"/>
```

A `<color>` shows a colour as a swatch. Clicking it, or Enter or Space while
it has focus, opens a picker below it (above it near the bottom of the
screen).

- `value` binds a `uint`, an `int` or a `string`, and `onchange` an `Action`
  of the same type; both are required. Numbers are `0xAARRGGBB`, the byte
  order of the `#AARRGGBB` literal; a string must be exactly `#AARRGGBB`
  (any case), and anything else shows as white. `onchange` hands strings back
  as `#AARRGGBB` in upper case.
- `alpha="true"` adds an alpha strip. Without it (the default) every colour
  the picker reports is opaque, and a translucent `value` opens in the picker
  (and is reported, if the player changes it) as the same colour made opaque.
- The picker has a saturation/value square, a hue strip, the alpha strip, a
  hex field, a row of palette colours and a row of recently picked colours.
  The palette row is the theme's colours under `theme="plugin"`, and a fixed
  set of basic colours in Classic.
- `onchange` is called live while the player drags, at most once per frame,
  and only when the colour differs from `value`. The swatch always shows
  `value`, so a plugin that ignores the call keeps its colour.
- The hex field takes `#RRGGBB` or `#AARRGGBB` (the `#` is optional). Six
  digits keep the current alpha. Clicking the field starts editing; the text
  applies on Enter, when the player clicks elsewhere in the picker, or when
  the picker closes. Text it can't read changes nothing.
- Clicking outside the picker, Enter (outside the hex field), Tab or
  Shift+Tab, anything else that takes the keyboard focus, or the swatch being
  hidden or disabled closes it and keeps the colour. Tab moves the focus to the
  control after the swatch and Shift+Tab to the swatch itself; in a window with
  no other control, Tab also lands on the swatch. Escape closes it and puts back the colour it
  opened with, calling `onchange` with that colour if it had already reported
  another. The click outside that closes the picker is swallowed: neither its
  press nor its release reaches the game or the window under it.
- The recent row holds the last 8 colours picked with any `<color>`, newest
  first, remembered for the character and shared by every picker. A colour is
  added when the picker closes with a colour different from the one it opened
  with (not on Escape).
- Clicking the swatch doesn't take keyboard focus from the game. The open
  picker holds the keyboard, and gives it back when it closes.

In Classic the swatch has a thin border that turns gold while it has focus or
is open, with a checkerboard behind translucent colours; under
`theme="plugin"` the swatch and picker take the palette's colours and the
panel's font.

A `<color>` without `value` or `onchange`, with either bound to an
unsupported type, with `onchange` of a different type from `value`, or with
`alpha` other than `true` or `false`, fails the build. On a host older than
colour pickers, a `<color>` fails the build of its window as unknown.

## Radio group

```xml
<radio x="8" y="8" w="200" h="54" items="Melee|Missile|Magic"
       selected="{Mode}" onchange="{SetMode}"/>
<radio x="8" y="70" w="210" h="20" items="{Ranges}" selected="{Range}"
       onchange="{SetRange}" orientation="horizontal" style="segmented"/>
```

One `<radio>` is the whole group: the player picks one of its options.

- `items` and `selected` are required. An `orientation` other than
  `vertical` or `horizontal`, or a `style` other than `radio` or
  `segmented`, fails the build.
- `items` is a literal list separated by `|`, or a bound
  `IEnumerable<string>` that the group re-reads every frame. A literal list
  can't have an empty option: `a||b`, `a|` and `|` fail the build. A literal
  that starts with `{` and ends with `}` is read as a binding. A `null` entry
  in a bound list shows as a blank option.
- `selected` binds an `int`, the chosen option; −1, or any number past the
  last option, draws none chosen.
- `onchange` receives the option the player picks. It isn't called for the
  option already chosen. The plugin stores the choice; the group shows
  whatever `selected` returns next frame. Without `onchange` the group
  only displays.
- `orientation` is `vertical` (the default, options stacked) or
  `horizontal` (side by side). The box is shared equally between options.
- `style` is `radio` (the default: a round button and caption per option)
  or `segmented` (a joined bar of buttons, the chosen one lit, captions
  centred).
- A click picks an option when the button comes up over the option it went
  down on.
- The group is one Tab stop. While it has focus, Up or Left picks the
  previous option and Down or Right the next, wrapping round; with nothing
  chosen, Down or Right picks the first and Up or Left the last. A click
  picks an option but doesn't keep keyboard focus, as with `<toggle>`:
  after clicking, Tab to the group from another focused control in the
  window to use the arrow keys again.

On a host older than radio groups, a `<radio>` fails the build of its
window as unknown.

## Separator

```xml
<separator x="8" y="40" w="200" h="1"/>
<separator x="110" y="48" w="1" h="120" color="theme:border|#FF61573B"/>
```

Write `color="theme:border|#FF61573B"` when you want one colour pinned in both
looks: the theme's border colour in the plugin theme, and the given colour in
Classic. It is useful when other separators on the window use a custom colour.

A `<separator>` draws a 1 px line across the middle of its box: horizontal,
or vertical when the box is taller than it is wide. In Classic it is a
fixed muted brown-olive (`#FF61573B`, the unlit lamp's colour); under
`theme="plugin"` it is the theme's border colour. `color` gives it any other
colour, literal or bound. A separator with zero width or height draws
nothing. It takes no input, and a click on it reaches whatever lies beneath.
Because clicks and hovering pass through a separator, a `tooltip` on it never
shows.

On a host older than separators, a `<separator>` fails the build of its
window as unknown.

## Expander

```xml
<expander name="advanced" x="8" y="60" w="240" h="120" text="Advanced">
  <toggle x="4" y="4" w="140" h="16" text="Loot everything" checked="{LootAll}" onclick="{ToggleLootAll}"/>
</expander>
<label x="8" y="190" text="Below the expander"/>
```

An `<expander>` is a header (an arrow and its `text`) with content beneath
it. `h` includes the 18 px header and must be at least 18. Its children are
placed below the header: a child at `y="0"` sits right under it.

Clicking the header, or pressing Enter or Space while it has focus, folds
the content away or brings it back. While it is folded, every element in the
same parent whose top is at or below the expander's bottom moves up by the
height it gave up (so a bar anchored to the window's bottom moves up too;
stretched elements grow instead, see below), and moves back when it opens. Elements beside it, whose
top is above the expander's bottom, stay put.

- Anchored `top,bottom`, it shrinks to its 18 px header while folded and gets
  its stretched height back when it opens, even if the window was resized in
  between.
- An expander inside another expander does not resize the outer one when it
  folds.
- `enabled="false"` disables everything inside it as well.
- A `tooltip` on an `<expander>` shows on its header.
- `color` sets the header caption's colour; without it the caption uses the
  theme's text colour under `theme="plugin"`.
- `open` other than `true` or `false`, and an `h` under 18, fail the build of
  the window.
- Elements below the expander that are anchored to both top and bottom
  (stretched) grow into the freed space instead of moving, and shrink back when
  it opens; their bottom margin stays as authored.
- An expander without a `name` starts from `open` every time the window is built.
  Two remembered controls with the same `name` in one window share what they
  remember, so give each its own name.
- A hidden expander applies its state when it is shown; while hidden it still
  occupies its space as last laid out (in an absolute window; a hidden flex
  item takes no space).
- Without `expanded` the client keeps the state. It starts from `open`
  (`true` by default, or `false`). If the expander has a `name`, the client
  remembers the player's choice per character with the window's other
  settings, saving it as soon as the player makes it, and forgets it again
  when it matches `open`. `onchange`, if given, is told each new state.
- With `expanded="{Bool}"` the plugin keeps the state: a click only calls
  `onchange` with the state asked for, and the expander follows the property.
  Without `onchange` it only displays. Bound state is never remembered by the
  client.

Inside a flex container an expander is a flex item: its `h` is its preferred
height while expanded (across a row it stretches like any item unless
`alignself` says otherwise), and collapsed it is just its 18-point header.
The container lays out again when it folds, so the items after it move up
and `grow` items take the space, and the expander doesn't move its siblings
itself as it does in an absolute window. A collapsed expander never grows or
stretches. Without a `w` it starts 160 wide, its content size, and right
anchors inside it are measured from that width.

On a host older than expanders, an `<expander>` fails the build of its
window as unknown.

## Split

```xml
<split name="main" x="0" y="0" w="400" h="300" min="80" max="320">
  <group><list x="0" y="0" w="197" h="300" anchor="left top right bottom" items="{Names}"/></group>
  <group><label x="8" y="8" text="Details"/></group>
</split>
```

A `<split>` holds exactly two `<group>` elements with a 5 px bar between
them. With `orientation="horizontal"` (the default) the groups sit side by
side; with `vertical` they are stacked. The split sizes and places its
groups, so their own `x y w h` and `anchor` are ignored; anchor the elements
inside them to fill a group. They still honour `visible`, `enabled`,
`background` and `border`, and each clips what it holds to its own size.

The groups' contents are laid out against the starting group sizes: `at`, or
half of what is left after the 5 px bar (197 in the example above), whatever
position the client remembers or the plugin binds. So author and anchor the
children to fill a group at that size.

A pane may be a flex container: `<group layout="column">` (or `row`) as a
pane lays out its children in whatever size the split gives it, and again
whenever the bar moves. The pane's content minimum doesn't limit the bar; set
the split's `min` and `max` for that.

A split inside a flex container without `w` or `h` starts at its content
size, 160 × 80 (its bar at 77 unless `at` says otherwise), and right and
bottom anchors in its groups are measured from that size.

The player drags the bar to share the space out. Its position is the first
group's size in pixels, kept between `min` (default 0) and `max` (default
all of it). When the split itself resizes, the first group keeps its size
and the second takes the rest.

- Without `position` the client keeps the position. It starts at `at`
  (default half). If the split has a `name`, the client remembers where the
  player left the bar per character, when they let go of it, and forgets it
  again when it is back at `at`.
- With `position="{Float}"` the plugin keeps it: dragging and the arrow
  keys only call `onchange` with the size asked for, and the split follows
  the property.
  Without `onchange` it only displays. Bound positions are never remembered
  by the client.
- `onchange` is called for every pixel the bar moves during a drag, not
  only when the player lets go.

A focused bar moves 8 px with the arrow keys along its axis, as described
under focus above.

A disabled split can't be dragged, ignores the arrow keys, shows no resize
cursor and draws its bar faded. On a host older
than splits, a `<split>` fails the build of its window as unknown.

## Context menu

```xml
<button x="8" y="8" w="80" h="20" text="Profile">
  <contextmenu items="{ProfileMenu}" enabled="{ProfileMenuEnabled}" onselect="{OnProfileMenu}"/>
</button>
<list x="8" y="36" w="300" h="200" header="true" selected="{Sel}" onchange="{Pick}">
  <column type="text" header="Name" items="{Names}" sort="text"/>
  <contextmenu items="{RowMenu}" onopen="{OnRowMenuOpen}" onselect="{OnRowMenu}"/>
</list>
```

A `<contextmenu>` inside an element gives that element a right-click menu.
An element has at most one. The menu isn't drawn in place: a right click on
the element (or on anything inside it that has no menu of its own) opens it
at the cursor, flipped or shifted so it stays on the screen. A drag with the
right button doesn't open it. The innermost menu wins. A `<group>` lets
clicks on its empty space through, so a group's menu opens over its children
only.

`items` is a literal list (`Copy|-|Paste`) or a bound `IEnumerable<string>`,
and `-` is a separator line. `enabled` optionally binds an
`IEnumerable<bool>`, one flag per item; disabled items draw dimmed and can't
be picked, and an item past the end of the list is enabled. Both are read
once, when the menu opens. `onselect` gets the index of the item picked,
counting separators, after the menu has closed. An empty item list doesn't
open a menu, and the right click goes on to any enclosing element's menu.

Hovering an item highlights it, and clicking it picks it; clicking a
separator or a disabled item does nothing. While the menu is open it has
the keyboard: Up and Down move between the items that can be picked
(wrapping), Enter picks, Escape closes, and other keys do nothing, so the
game doesn't act on them. A focused text field loses the keyboard while the
menu is open (its `onblur` fires). When the menu closes the keyboard goes back
to whatever had it before, unless something else has taken it since. A click outside the menu closes it (and does nothing else),
as does opening another menu or drop-down, the element being hidden,
disabled or leaving the window, or something else taking the keyboard focus
(then the focus is not handed back).

`onopen` is called as the menu is about to open, before `items` is read, so a
plugin can change the items to suit; it runs even when the items then turn
out empty and no menu opens (on a list the row is still selected). On a `<list>`, the right click first selects
the row under the cursor (unless it is already one of the rows selected in a
`selectmode="multi"` list) and `onopen` gets that row, as the plugin's own
row index; over the header, the scrollbar or empty space it gets −1 and the
selection stays as it is. On other elements `onopen` gets −1.

In Classic the menu has the plain drop-down's colours; under
`theme="plugin"` it takes the palette's field, border, text, muted and
selected colours and the panel's font.

A `<contextmenu>` without `items` or `onselect`, with an empty literal item,
with `items`, `enabled`, `onselect` or `onopen` of the wrong type, with
children, a second one on the same element, or one directly in `<panel>` or
inside a `<column>` fails the build. A host older than context menus
ignores the menu inside an element that has no children of its own (such as
a `button`), and fails the build when it is inside a `<list>` or `<group>`.

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

## Flex layout

`layout="row"` or `layout="column"` on a `<group>` or on the root `<panel>`
makes it a **flex container**: it places its children itself, one after
another along its main axis, sized to their content and sharing out the
space that is left. A container without `layout` keeps absolute placement.
The rules are a practical subset of CSS flexbox. The
`samples/AcDream.Plugins.FlexDemo` plugin has four flex windows to resize
and compare: a finder, a settings form, a scrolling gallery, and the newer
controls inside flex.

```xml
<panel title="Buff Bot" layout="column" padding="8" gap="6" resizable="true">
  <group layout="row" gap="4">
    <field grow="1" text="{Search}" onchange="{SetSearch}" />
    <button text="Find" onclick="{Find}" />
  </group>
  <list grow="1" items="{Results}" selected="{Selected}" onchange="{Select}" />
  <group layout="row" justify="end" gap="4">
    <button text="OK" onclick="{Ok}" />
    <button text="Cancel" onclick="{Cancel}" />
  </group>
</panel>
```

### Container attributes

| Attribute | Values | Default |
|---|---|---|
| `layout` | `row`, `column` | absent: absolute placement |
| `gap` | points between items, and between wrapped lines | `0` |
| `padding` | 1, 2 or 4 numbers in CSS order: all; vertical horizontal; top right bottom left | `0` |
| `justify` | `start`, `center`, `end`, `space-between` | `start` |
| `align` | `start`, `center`, `end`, `stretch` (across the line) | `stretch` |
| `wrap` | `true`, `false` (rows only) | `false` |

`gap`, `padding`, `justify`, `align` and `wrap` need `layout` on the same
element; without it they are ignored, with a warning in the client log, and
so is a `layout` other than `row` or `column` (the element stays absolute).
Only rows wrap in this version: `wrap="true"` on a column fails the build.

### Item attributes

Every child of a flex container may carry:

| Attribute | Meaning | Default |
|---|---|---|
| `grow` | share of the space left over on the main axis | `0` |
| `shrink` | share of the space missing, weighted by the item's basis | `1` |
| `basis` | `auto` or points: the item's starting size on the main axis | `auto`: its `w`/`h` on that axis, else its content size |
| `w`, `h` | preferred size, in place of the content size | the content size |
| `minw`, `maxw`, `minh`, `maxh` | limits | min: the content minimum; max: none |
| `alignself` | `start`, `center`, `end`, `stretch` | the container's `align` |

An item never shrinks below its content (a button below its caption) unless
`minw`/`minh` allow it; a label is the exception (see below). `x`, `y` and
`anchor` on a flex item fail the build. `grow`, `shrink`, `basis`,
`alignself` and the limits on a child of a container without `layout` are
ignored, with a warning in the client log. A `<contextmenu>` belongs to the
element it is inside and is never a flex item.

Markup that uses flex fails the build on a bad value (an unknown keyword, a
negative or non-finite number, inverted limits), as the rest of the markup
does. The warnings are for markup that does not use flex: hosts before flex
ignored these attributes, so a window with a stray one still opens.

### Content sizes

| Element | Size |
|---|---|
| `label` | its text, one line tall; it may shrink to nothing (no `minw` of its own) |
| `button`, `tab` | its caption with 12 points either side (and a square for its icon); at least 24 points tall, taller when the font's line plus 8 is more |
| `toggle` | its lamp, then its caption; at least 20 points tall, taller when the font's line plus 4 is more |
| `checkbox` | box, then its caption; tall as a toggle |
| `radio` | one cell per option along its `orientation`, each as wide as the widest caption (box, caption and 8 points; segmented: the caption with 12 points either side) and as tall as a toggle (segmented: as a button) |
| `number` | 120 wide (54 at least: a field and its spin); tall as a field |
| `hotkey` | 120 wide (40 at least); tall as a field |
| `color` | 40 × 24 |
| `separator` | 9 points thick, stretched along the container's cross axis (across a column, down a row) unless `align`/`alignself` says otherwise |
| `split` | 160 × 80 (60 × 40 at least) |
| `expander` | its `h` while expanded, 18 while collapsed; 160 wide (its caption and arrow at least) |
| `icon` | 32 × 32 |
| `field`, `menu` | 120 wide (40 at least); tall as a button (at least 24, or the font's line plus 8) |
| `slider` / `meter` | 120 wide (40 at least), 16 / 12 tall (fixed) |
| `list`, `log` | 160 × 80 (60 × 40 at least) |
| `group` with `layout` | what its own items need |
| `group` without `layout` | its `w` × `h` |

Sizes follow what is shown: a bound caption that gets longer, a hidden item
(which takes no space), or a switch of the shared appearance lays the
container out again on the next frame, but only when a size really changed:
a caption that changes and keeps its width, or a control whose `w`, `h`,
`minw` and `minh` are all stated, lays nothing out. A label inside a flex
container draws in the box it is given, its line centred top to bottom, and
text wider than the box ends in "...". Give a label a `minw` to keep it from
shrinking below that.

### Mixing absolute and flex

The two nest freely. A group without `layout` inside a flex container is an
item of its authored `w` × `h`; when the container gives it another size, its
own children follow their `anchor`s exactly as in a resized window. A
`<group layout>` inside an absolute container is placed by its `x`, `y`,
`w`, `h` and `anchor` like any element, and lays out its children in
whatever size that gives it. It needs its own `w` and `h` for that: without
them it is empty and shows nothing.

### Flex windows

`layout` on the root `<panel>` lays out the window's content area:

- the window gets the [title bar](#title-bar) unless the root says
  `titlebar="false"` (without it the content area is inset by the 5-point
  border on every side);
- `w` and `h` may be left out: the window then opens at the size its
  content needs. A size below what the content needs opens at the latter;
- `minw` and `minh` default to what the content needs, so a window never
  shrinks (by dragging, or by a restored layout) to a size that cuts its
  content off. When the content needs more later (a longer caption), the
  window grows to fit, even when it is not resizable, and stays on screen.
  A long label never needs more: it is cut short instead;
- the opening size and minimum follow bound `visible` (and `enabled`) as the
  bindings read when the window is built: a tabbed window whose pages are
  sibling groups with `visible="{XTabSelected}"` opens sized for the shown
  page, not every page stacked. A binding that throws at that point counts
  as visible until the first frame reads it;
- as with the title bar, the window's saved size is reset once when its
  markup's `w`, `h`, `minw`, `minh`, `resizable`, `resize`, `layout`,
  `titlebar` or `scroll` change. Captions and appearance never reset it.

When the content needs less room again, a window that grew to fit goes back
toward the size it had before (never below the new minimum), unless the
player resized it in between; a window that never grew keeps its size. A
wrapping root's minimum height is the height of its
narrowest layout, so a wide wrapping window cannot be made shorter than that;
give it `scroll="y"` (see [Scrolling groups](#scrolling-groups)) to let it.

A layout root with `titlebar="false"` shows no title text in the window (its
`title` still names it in the shelf), and gets no shelf "–" button either,
since its content reaches the corner where the button would sit: the player
closes it from its shelf button, the plugin with `HidePanel`.

A root that wraps should state its `w`: without it the window opens one
line wide.

## Scrolling groups

`scroll="y"`, `scroll="x"` or `scroll="both"` on a `<group>` makes it scroll
along those axes when its content does not fit; on the root `<panel>` it
scrolls the window's content area. Without `scroll` content that does not fit
is cut off, as before; any other value is ignored, with a warning in the
client log.

```xml
<panel title="Spells" layout="column" padding="8" gap="6" w="240" h="200" resizable="true">
  <label text="Known spells" />
  <group layout="row" wrap="true" gap="4" scroll="y" grow="1">
    <icon spell="{Spell1}" />
    <icon spell="{Spell2}" />
    <!-- ... -->
  </group>
</panel>
```

- **Flex groups** lay their items out without a limit along the scrolling
  axis: a scrolling column keeps every item at its size instead of shrinking
  it, and a wrapping row with `scroll="y"` keeps its width and grows downward.
  Items do not grow to fill a scrolling axis either; `grow` there has no space
  to share.
- **Groups without `layout`** scroll over their children as placed: the
  scrolled area reaches the right and bottom edges of the furthest visible
  child, measured from the group's top-left corner. A child at negative
  coordinates is not reachable; leave your own margin after the last child if
  you want one.
- A **bar** (16 points) appears on each scrolling axis only while the content
  overflows it, beside the content rather than over it: the content is laid
  out (or its anchors applied) in the room left, so a right-anchored child
  moves in beside the bar. When both bars show, the corner square stays empty.
  Classic uses the game's scrollbar art; in the shared themes a themed bar
  draws a flat thumb in the palette's muted colour.
- **In a flex container** a scrolling group may shrink to a 40-point view
  along each axis it scrolls (plus the other bar's 16 points), so it never
  holds its window open. A scrolling wrapping row in a column shows all its
  lines when the window has room and scrolls when it does not. On the root,
  the window's minimum along a scrolling axis is that small view too.
- The root needs a content area to scroll: `scroll` on a root `<panel>`
  without `layout` and without `titlebar="true"` is ignored, with a warning
  in the client log.

**The wheel** scrolls the innermost thing under the pointer that can scroll:
a `list` or `log` inside a scrolling group scrolls itself, and the group
scrolls everywhere else. A group, list or log with nothing to scroll lets
the wheel pass to a group around it. A wheel step that anything takes, a
built-in window included, closes an open menu or popup, unless the pointer
is over the popup. A sideways swipe, a tilt wheel, or
Shift with the wheel scrolls horizontally. Only `scroll="x"` and
`scroll="both"` groups take a horizontal step; a Shift step that no such
group takes scrolls vertically as before. macOS sends Shift with a mouse
wheel as a sideways step, and the same rule applies: it scrolls a horizontal
group under the pointer, else it is the wheel, so Shift+wheel still steps a
focused `<number>` by ten. A focused number inside a horizontally scrolling
group loses Shift+wheel to the group. One wheel step moves 48 points (three
16-point lines); the bar's arrows move 16 and a click in its track a page.

**Focus** moves into view: when Tab (or a click) gives a control keyboard
focus, each scrolling group around it scrolls just far enough to show it.

The scroll position survives hiding and showing the group and a change of
appearance, is pulled back when the group grows or its content shrinks, and
is not saved between sessions. Plugins cannot read or set it in this version.

## Repeat

`<repeat>` builds one copy of its children for each item of a list, and keeps
the copies in step as the list changes. Use it for rows you cannot list in
the markup, because the plugin decides how many there are.

```xml
<repeat items="{Rows}" key="{Id}">
  <group layout="row" gap="6" h="22">
    <checkbox checked="{On}" onchange="{SetOn}" w="20"/>
    <label text="{Label}" grow="1"/>
    <number value="{Value}" onchange="{SetValue}" w="80"/>
  </group>
</repeat>
```

The children of `<repeat>` are the template. `<repeat>` takes only `items`
and `key`. `x`, `y`, `w`, `h`, `anchor` and the flex item attributes on
`<repeat>` fail the build. Put them on the template's top-level elements
instead: each copy's top-level elements are items of the surrounding flex
container, in list order, where the `<repeat>` stands. A `<repeat>` takes no
space of its own. An empty or null list builds nothing.

### Items

`items` is required. It must be a binding to a property declared as
`IReadOnlyList<T>`, `IList<T>` or `IEnumerable<T>` (or a type that implements
exactly one `IEnumerable<T>`). `T` is the declared type, whatever the list
holds at run time. A `string`, a non-generic `IEnumerable`, a literal, or a
missing property fails the build with `FormatException`.

The client reads `items` and every key each tick. When the count, the keys
and every item reference are all the same as the last tick, it does nothing,
even when the list is a new instance: a property that builds its list on
every read (`=> _all.Where(...).ToList()`) costs only that read. Change a
list by assigning a new list, or by changing the one you have; both are seen
on the next tick. Edit an item's own properties in place when only its values
change: the bindings inside the copy read them every frame.

### Which names a template can see

Inside `<repeat items="{X}">`, names resolve on the element type of property
`X` of the enclosing scope, and nothing else is visible. Names follow the
same rules and types as in a window binding (see [Bindings](#bindings)), and
are checked at build time even when the list is empty. A member that exists
only on a subtype of `T` is not visible. An attribute that falls back
silently in a window binding still falls back silently here.

A plugin that checks its own markup can follow the same rule: the window's
binding resolves the names outside every `<repeat>`, and for each `<repeat>`,
`T` resolves the names inside it.

### Keys

`key` is optional. It is a binding to a property of `T`. Keys are compared
with `Equals`.

With `key`, a copy follows its item. When the list changes, a copy whose key
is still in the list is kept and moved to its new place. It keeps its focus,
the half-typed text in a field, an open menu, an expander's state and a
group's scroll position. Its bindings then read the new item object with that
key. A new key gets a new copy. A key that is gone loses its copy, and the
copy's focus, popups and theme hooks go with it.

Without `key`, copies match by position: the first copy shows the first item,
and so on. A copy keeps what the player did to it, so after a shuffle each
row shows another item with the same half-typed text.

Keys must differ and must not be null. When two keys are equal, or a key is
null, the repeat writes one line to the console (`duplicate or missing keys;
rows are matched by position`) and matches by position, as if it had no
`key`. It goes back to matching by key when the keys are good again.

A copy built during a tick starts following its item from the next tick.

### Where it can go

`<repeat>` must be a direct child of a flex container: a root `<panel>` or a
`<group>` with `layout`. Anywhere else fails the build. In particular it does
not work directly inside an `<expander>` or a `<split>` pane, because those
are not flex containers. Put a `<group layout="column">` inside the pane and
the repeat in that.

A `<contextmenu>` goes inside the template element it belongs to. One placed
directly in `<repeat>` fails the build.

### Nesting

Repeats nest to any depth. The inner `items` is read on the outer item:

```xml
<repeat items="{Sections}" key="{Id}">
  <label text="{Title}"/>
  <repeat items="{Rows}" key="{Id}">
    <label text="{Name}"/>
  </repeat>
</repeat>
```

Here `{Rows}` is a property of the section type, and `{Id}` in the inner
repeat is a property of the row type. The inner copies sit in the same flex
container as the outer ones, after their section's label.

### Remembered names

A `name` (or `id`) inside a template is remembered per item. The client
stores it as `name#k1/k2`: the name, then the key of each enclosing repeat,
outermost first (`Convert.ToString` of the key, culture-invariant). Keys are
joined with `/`; a `/` or `%` inside a key is stored as `%2F` or `%25`, so
any key text is safe. Each item keeps its own expander, split or list state,
and it follows the item when the list is reordered. Two repeats in one
window whose templates use the same name and whose items share a key share
that remembered value, so give each template its own names.

When an item's key leaves the list, the client forgets what that item's
controls remembered (and what the copies of repeats nested in it
remembered), so short-lived keys such as item or monster ids don't pile up
in the settings file. Only the names in that repeat's template are
forgotten: another repeat keyed by the same ids keeps its state. An item
that comes back starts from its markup defaults. Closing the window forgets
nothing.

The by-name calls on `IUiRegistry` (`ControlExists`, `SetControlLabel`,
`SetControlVisible`) don't reach inside a `<repeat>`: a template name is on
every row, so it names no single control. Bind the row's own properties
instead.

An element with a `name` or `id` inside any `<repeat>` without `key` fails
the build, since it would have no per-item name. In a nested repeat, every
enclosing repeat needs `key`.

### Other attributes

Everything else works inside a template: `visible`, `enabled` and `tooltip`
bindings, bound colours, `<contextmenu>`, scrolling groups and the plugin
theme. Copies pick up a change of appearance with the rest of the window.

About 150 copies of a five-control row in a scrolling group build and lay out
without a visible pause.

### Older hosts

A host that does not know `<repeat>` fails on the unknown element when it
builds the panel. A plugin that needs it should say so in its
`minHostVersion` in [the plugin manifest](plugin-manifest.md), or the player
only sees the error in the log.

`samples/AcDream.Plugins.ControlsDemo` has a "Controls: Repeat" window with
rows of a checkbox, a name, a menu and a number, and buttons to add a row,
remove the last, and shuffle.

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

An icon comes from one of four sources. On `<icon>` the source is whichever
attribute is set; on `<button>`, `<tab>`, `<list>` and an icon `<column>` it is
`iconkind` (default `did`).

| Source | Draws |
|---|---|
| `did` | The art at that id, with the art's pure-white key color replaced the way the inventory draws a plain item |
| `spell` | The composited spell icon for a spell id: power-level backing, art, tint, and the self/fellow overlay |
| `item` | The composited icon for a live object id, read from the same object table the inventory uses |
| `file` | A PNG or JPEG the plugin ships, by its path relative to the plugin's install folder (see "File icons") |

```xml
<icon x="8"  y="8" w="32" h="32" did="7735" tooltip="An icon"/>
<icon x="48" y="8" w="32" h="32" spell="{SpellId}" tooltip="{SpellName}"/>
<button x="12" y="68" w="120" h="24" text="Report" icon="0x06002D14" onclick="{Report}"/>
<tab x="140" y="68" w="90" h="24" text="Spells" icon="{SpellId}" iconkind="spell" selected="{SpellsTab}" onclick="{ShowSpells}"/>
<list x="12" y="100" w="256" h="108" items="{SpellRows}" icons="{SpellIds}" iconkind="spell" selected="{SelectedIndex}"/>
```

Rules:

- Exactly one of `did`, `spell`, `item`, `file` on an `<icon>`; `iconkind` is not
  valid there.
- `w` and `h` default to 32. Art is drawn nearest-filtered, aspect-preserved,
  and centered. An id of 0 or an unresolvable id draws nothing.
- An `<icon>` with a `tooltip` is hit-testable; without one it is
  click-through.
- A `<button>` or `<tab>` with `icon` draws it in a square column at the
  left and centers the caption in the rest. Leave out `text` for an
  icon-only control.
- A `<list>` with `icons` draws one square icon column at the left, one
  icon per row, using one `iconkind` for the whole list. Rows without a
  matching icon draw text only.

## File icons

A plugin can draw its own art: a PNG or JPEG in its install folder, named by
a path relative to that folder, written literally or bound to a `string`
property. On `<button>`, `<list>` and an icon `<column>`, set
`iconkind="file"` and give paths where the other kinds take ids.

```xml
<icon x="8" y="8" w="24" h="24" file="icons/sword.png" tooltip="Melee"/>
<icon x="40" y="8" w="24" h="24" file="{StatusIcon}"/>
<button x="12" y="40" w="120" h="24" text="Loot" icon="icons/bag.png" iconkind="file" onclick="{Loot}"/>
<list x="12" y="72" w="256" h="108" items="{Rows}" icons="{RowIconPaths}" iconkind="file" selected="{Selected}"/>
```

- **Paths** use `/` or `\`, must end in `.png`, `.jpg` or `.jpeg`, and must stay
  inside the plugin folder once links are followed. A file icon in a window
  registered without a plugin folder draws nothing.
- **Limits:** at most 1 MiB per file and 512 pixels on a side; the file's
  own bytes must be a PNG or a JPEG, whatever its name.
- **Budget:** file icons are the plugin's own images, held in the same table
  as the ones it asks for through `IUiRegistry.Images`, and count against the
  same limits: 256 images and 32 MB of the plugin's own art, smaller copies
  (mipmaps, about a third more) included. A file past them draws nothing, and
  the log says why; it is tried again once the plugin lets go of another image.
- **Drawing:** the element's `w` and `h` set the size; the image keeps its
  aspect ratio, is centred in the element, and is scaled smoothly, unlike
  client art. The client keeps smaller copies of every image (mipmaps), so a
  large image in a small icon shrinks cleanly: a 512 x 512 file drawn at 24 x 24
  looks right. Detail finer than the drawn size is still lost, so art made for
  its size, at twice that for high-density displays (48 x 48 for a 24-point
  icon), stays the sharpest. PNG transparency is kept, without dark fringes
  where it shrinks; the image's own colours are drawn as they are.
- **Bindings** are read every draw, so a window can switch icons by changing
  the path it returns. A null or blank path draws nothing.
- **Loading:** a file is read and decoded in the background the first time a
  window draws it, so the icon appears a frame or so after the window does.
  Each file is held once, however many elements and windows show it. A window
  lets go of a file it has not drawn for 5 seconds (a hidden window or tab, a
  bound path that moved on), and reads it again if it draws it
  later. Everything is given back when the window is closed for good, rebuilt,
  or unloaded with its plugin.
- **Reloading:** a file that changed on disk is loaded again when its window
  is built again, for example on a plugin hot reload. Another window still
  showing the old image keeps it until that window is built again too.

A file that cannot be used draws nothing, and the client log says why, once
per file:

```
[UI] plugin image 'acme.hello/icons/sword.png' ignored: is wider or taller than 512 pixels
```

## Columns

A `<list>` can declare typed columns instead of the single-column attributes.
The two forms cannot be mixed on one list.

```xml
<list x="8" y="24" w="256" h="120" rowheight="18"
      selected="{SelectedMonster}" onchange="{SelectMonster}">
  <column type="check" width="20"  values="{MonsterFlags}" onchange="{ToggleFlag}"/>
  <column type="text"  width="127" items="{MonsterNames}"  onclick="{PingMonster}"/>
  <column type="icon"  width="20"  iconkind="did" values="{MonsterIcons}" onclick="{MoveUp}"/>
  <column type="button" width="*"  items="{MonsterActions}" onclick="{ActOnMonster}"/>
</list>
```

| Attribute | Column type | Required | Binding |
|---|---|---|---|
| `type` | all | yes | `text`, `check`, `checkbox`, `icon`, or `button` |
| `width` | all | see below | pixels, or `*` for auto |
| `items` | `text` | yes | `IReadOnlyList<string>` |
| `colors` | `text` | no | `IReadOnlyList<uint>` or `<int>`, `0xRRGGBB` per row |
| `onclick` | `text` | no | `Action<int>` with the row index; the click then does not select the row |
| `values` | `check`, `checkbox` | yes | `IReadOnlyList<bool>` |
| `onchange` | `check`, `checkbox` | yes | `Action<int>` with the row index; the plugin flips its own value |
| `values` | `icon` | yes | `IReadOnlyList<uint>` or `<int>`; `IReadOnlyList<string>` paths with `iconkind="file"` |
| `iconkind` | `icon` | no | `did` (default), `spell`, `item`, or `file` |
| `onclick` | `icon` | yes | `Action<int>` with the row index |
| `items` | `button` | yes | `IReadOnlyList<string>`, each row's caption; an empty caption draws no button |
| `header` | all | no | literal or bound string, the caption; `all` on a `checkbox` column is a select-all box |
| `onall` | `checkbox` with `header="all"` | no | `Action<bool>`, the value every row should take |
| `sort` | all | no | `text` or `number` (text and button columns, or any column with `sortkeys`); `bool` (check and checkbox) |
| `sortkeys` | all with `sort` | no | `IReadOnlyList<double>` or `IReadOnlyList<string>`, one key per row |
| `onclick` | `button` | yes | `Action<int>` with the row index; the click does not select the row |
| `buttonw`, `buttonh` | `button` | no | pixels; the button's size, centred in its cell (default: the whole cell) |

Widths: `*` shares the remaining width equally with every other `*` column.
The last column is always automatic; it takes the whole remainder unless
another column is also `*`. Every other column needs a positive `width` or
`*`. Columns that would overflow the list are clamped and later ones draw
nothing. Remainder pixels go to the last sharing column.

Rows: the row count is the longest bound column. A `text`, `icon` or `button`
cell past its own column's data draws nothing; a `check` cell draws unchecked. A click
in a `text` column without `onclick` selects the row and fires the list's
`onchange`; any other click fires the column's own callback and leaves the
selection alone. A click past a column's own data does nothing.

A `button` cell is drawn as a button one pixel inside its cell, or at
`buttonw` by `buttonh` centred in the cell (never larger than the cell; set
either alone to size only that side), with its caption centred in the list's
text colour and cut off at the button's edges. Without a theme its face and
border are those of an unthemed `<button>`. On a themed window the face is the
theme's background colour (the list itself is drawn in the field colour that
a themed `<button>` uses) and the border is the theme's border colour. Unlike
a `<button>`, it lightens while hovered and darkens while pressed. A disabled
list draws its buttons' face, border and caption faded and fires none of them.

It fires like a `<button>`, when the left button comes up over the button it
went down on, so a press dragged off the button cancels it; a click beside a
smaller button does nothing. Because `onclick` gets a row index, a press is
also cancelled if, before the release, the list scrolls, its row count changes,
or the pressed button's own caption changes or becomes empty. Changes in the
row's other columns, such as a distance that updates every frame, do not
cancel it, so a plugin that reorders rows with the same count should change
the caption too, or the click fires for whatever item is at the pressed row.
A cell with an empty caption is not a button and ignores clicks.
On a host older than button columns, a `type="button"` column fails the build
of its window as an unknown type.

With `header="true"` a list draws a header row; see below. A `check`
cell draws the same lamp glyph as `<toggle>`; a `checkbox` cell draws the
`<checkbox>` tick box. When rows overflow the list's height it reserves a
16-pixel scrollbar at its right edge, drawn with the game's scrollbar art;
when they fit, no bar and no reservation. Row selection draws no highlight
band unless `selectionband="true"`.

### Header, sorting and column widths

```xml
<list name="mobs" x="8" y="24" w="320" h="200" header="true" resizable="true"
      selected="{Sel}" onchange="{Pick}">
  <column type="text" header="Name" width="140" items="{Names}" sort="text"/>
  <column type="text" header="Dist" width="80" items="{Dists}" sort="number" sortkeys="{DistKeys}"/>
  <column type="checkbox" header="all" width="40" values="{On}" onchange="{Toggle}"
          onall="{ToggleAll}"/>
  <column type="button" items="{Acts}" onclick="{Act}"/>
</list>
```

`header="true"` draws an 18 px row of column captions above the rows; the
rows and the scrollbar start under it. A column's `header` is a literal or a
bound string. Classic draws the header on the list's own dark background; a
themed window uses its field colour. Both draw a line under the header and
between cells.

A column with `sort` can be sorted by clicking its header: the first click
sorts ascending, the second descending, the third goes back to the plugin's
order. The sorted column shows a small arrow. `text` compares the cells
ignoring case; `number` reads them as numbers (invariant culture); cells
that aren't numbers sort last in either direction; `bool` sorts a check or
checkbox column unticked first. `sortkeys` replaces the cells as the keys,
for a column that shows something other than what it sorts by, such as
"12.3 m"; a row past the end of the keys sorts last. Ties keep the plugin's
order.

Sorting only changes what is drawn where. Every row index the list hands
the plugin (`selected`, `onchange`, every column callback) is still the
plugin's own index, the selection stays on the same row, and a button press
in progress is cancelled if a sort moves a different row under it before the
release. The list scrolls to keep the selected row in view when the
selection or the sort changes, but not when live data (a distance, say)
reorders the rows under a sort, so the player can scroll away from the
selection.

- Without `sortcolumn` the client keeps the sort and, for a list with a
  `name`, remembers it per character; `onsort` is told each change.
- With `sortcolumn="{Int}" sortdescending="{Bool}"` (always both) the plugin
  keeps it: a header click only calls `onsort` with the column (−1 for none)
  and direction asked for. Without `onsort` it only displays.

With `resizable="true"` (which needs a header) the player drags the line
between two header cells to resize the column to its left, down to 16 px
and only as wide as leaves the columns to its right their own widths (16 px
for an automatic one), and double-clicks it to put the column back to its markup width. A
double-click counts only when both its clicks started from rest; the release
of a drag is not the first click. The resize cursor shows over those lines.

- Without `widths` the client keeps the widths and, for a named list,
  remembers them per character when the player lets go (a remembered width
  below 16 px is raised to 16); `onresize` is told every column's width for
  each pixel the line moves and after a double-click reset.
- With `widths="{Floats}"` the plugin keeps them: one entry per column in
  pixels, 0 for automatic, the last entry ignored (the last column always
  takes the rest). A list of the wrong length is ignored. A drag or a
  double-click reset only calls `onresize`; without it the widths only
  display, and the lines show no resize cursor and can't be grabbed.

A `checkbox` column with `header="all"` draws a select-all box in its
header: ticked when every row is ticked, clear when none is, indeterminate
otherwise. Clicking it calls `onall` once with the value every row should
take: true, unless every row is already ticked. The plugin sets its own
values. Without `onall` the header cell is blank. `header="all"` on any
other column is just the caption "all". A select-all column can't also
`sort`.

`header`, `resizable`, `sortcolumn`, `sortdescending`, `onsort`, `widths`
and `onresize` need `<column>` children. `resizable` without a header,
`widths` or `onresize` without `resizable`, `sortcolumn` without
`sortdescending` (or the reverse), `sort="bool"` on a column that isn't a
check or checkbox, `sort="text"` or `"number"` on a check, checkbox or icon
column without `sortkeys`, `sortkeys` without `sort="text"` or `"number"`,
`onall` without `header="all"` on a checkbox column, and `header="all"` with
`sort` fail the build. These are attributes on existing elements, and a
host older than list headers ignores attributes it doesn't know: it draws
the list without a header and ignores sorting and widths.

### Multi-select

```xml
<list name="loot" x="8" y="24" w="320" h="200" header="true" selectmode="multi"
      selected="{Picked}" onchange="{SetPicked}">
  <column type="text" header="Item" items="{Names}" sort="text"/>
  <column type="text" header="Value" width="60" items="{Values}" sort="number"/>
</list>
```

With `selectmode="multi"` the player can select any number of rows.
`selected` binds the selected rows (any `IEnumerable<int>`; a list or array
is read as is, other collections are copied each frame) and `onchange`
is an `Action<IReadOnlyList<int>>` that gets the new selection, as the
plugin's own row indices in ascending order. The plugin owns the selection:
the list draws whatever `selected` returns each frame and only asks for a
change, so set the property in `onchange`. Without `onchange` the list only
displays. `onchange` fires on every click or key the list handles, even if
the selection comes out the same.

Rows are picked where a single-select list picks them: anywhere in the
single-column form, and on a text column without `onclick` in the column
form. Check, icon and button cells keep their own actions.

- Click selects only that row.
- Ctrl-click (Cmd-click on macOS) adds or removes that row.
- Shift-click selects every row from the last plain or Ctrl click to this
  one, as they are shown, so a sorted list selects what the player sees
  between the two. Ctrl+Shift-click adds that range to the selection.
  With no earlier click, Shift-click acts as a plain click and
  Ctrl+Shift-click as a Ctrl-click.

The list takes keyboard focus with Tab from another focused control in the
same window, such as a field or a checkbox. Clicking a row does not focus
the list (and clears any keyboard focus, as for other controls that don't
take text), so the game keeps its keys. While focused it draws the gold
focus outline. Up and Down select the row above or below the last one
picked (the first row if none) and move the Shift anchor to that row,
Shift+Up and Shift+Down extend the range from the anchor instead, and
Ctrl+A (Cmd+A) selects every row. With no rows, Up and Down are still taken
but do nothing and `onchange` doesn't fire. The list keeps the last-picked
row in view. Other keys go on to the game.

Every selected row draws the selection band; `selectionband` is implied,
and `selectionband="false"` fails the build. Other build errors:
`selectmode` other than `single` or `multi`, `selected` that isn't bound to
a list of ints, and `onchange` that isn't an `Action<IReadOnlyList<int>>`.
On a host older than multi-select, `selected` must be an `int`, so a
multi-select list fails the build of its window there.

### Trees

```xml
<list name="rules" x="8" y="24" w="320" h="200" tree="true" levels="{Depths}" keys="{Ids}"
      header="true" selected="{Sel}" onchange="{Pick}">
  <column type="checkbox" width="20" values="{On}" onchange="{ToggleRule}"/>
  <column type="text" header="Rule" items="{Names}" sort="text"/>
  <column type="text" header="Value" width="80" items="{Values}"/>
</list>
```

With `tree="true"` a column list shows its rows as a tree. The rows are the
tree flattened parent-first (each row followed by its children), and
`levels` gives each row's depth, 0 for the top level. A negative level counts
as 0, a row more than one level deeper than the row before it is treated as
one deeper, and a row past the end of `levels` is top level. `keys` gives
each row a stable id that the expanded state is kept by (a list or array is
read as is, other collections are copied each frame); a row past the end of
`keys` gets the key `#<row>`, for example `#7`.
Keys should be stable across sessions: the client remembers changed keys per
character and keeps them even when a key no longer appears, so volatile ids
(for example item GUIDs) make the remembered value grow. Rows that share a key
expand and collapse together. A `levels` that is not a list or array is copied
each frame, like `keys`.

The first `text` column draws the tree: each level indents 12 pixels, and a
row with children shows an arrow in a 12 pixel slot, pointing right when
collapsed and down when expanded. Clicking the slot collapses or expands the
row without selecting it, and does not fire the column's `onclick`. The list
takes keyboard focus with Tab from another focused control in the same
window; clicking a row does not focus it. While focused, Right expands the
selected row or, if it is already expanded, moves to its first child, and
Left collapses it or moves to its parent.
In a single-select tree, Up and Down move the selection to the row above or
below (the first row when none is selected), and Left or Right with no row
selected selects the first row; none of these keys reach the game while the
tree has focus. If the selected row is hidden inside a collapsed parent, the
keys start from that parent. In a multi-select list, Left and Right act on
the last row picked or stepped to, so they do nothing until the player has
picked one.

- Without `expanded` the client keeps the expanded state: every row starts
  expanded, or collapsed with `collapsed="true"`, and a list with a `name`
  remembers the rows the player changed, per character. `onexpand`, if bound,
  is told each change.
- With `expanded="{Keys}"` the plugin keeps it: a row is expanded when its
  key is in the set. Bind an `IReadOnlySet<string>` such as a
  `HashSet<string>`; any other `IEnumerable<string>` is copied into a new
  set every frame. Clicking an arrow or Left/Right calls `onexpand` with the
  key and the state asked for, so change the set there. A bound set that
  returns null counts as empty, so every parent shows collapsed. Without
  `onexpand` nothing can expand or collapse rows, but Left/Right still move
  the selection (Right to the first child of an expanded row, Left to the
  parent), and in a multi-select list a collapse request still removes the
  hidden children from the selection.

Sorting a tree sorts each row's children among themselves and keeps them
under their parent. Collapsed rows' children are left out of what is drawn,
but every row index the plugin sees is still its own index, so `selected`,
`onchange` and the column callbacks are unchanged. A multi-select list
selects only rows that are shown (Shift ranges and Ctrl+A). A checkbox
column's select-all header (`header="all"`) counts every row, hidden ones
included, matching what `onall` changes. When the player asks for a collapse
(an arrow click or Left), the list calls `onchange` with the row's hidden
children removed from the selection, and moves the Shift anchor and
last-picked row up to the collapsed row. This happens straight after
`onexpand`, in bound mode too, before the plugin applies the collapse and
whether or not it ever does. A collapse the plugin makes itself, by editing
its `expanded` set, leaves the selection alone: hidden selected rows stay
selected and show again on expand. A single-select list keeps its selection
when the selected row is hidden.

`tree` needs `<column>` children with at least one `text` column. A tree
without `levels` or `keys`, `expanded` that isn't bound to a collection of
strings, `onexpand` that isn't an `Action<string, bool>`, `collapsed`
together with `expanded`, and `levels`, `keys`, `expanded`, `onexpand` or
`collapsed` without `tree="true"` fail the build. A host older than trees
ignores these attributes and draws every row as a flat list, with no indent.

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
the root's `visible`, the plugin's binding. The close button is the title
bar's X (see [Title bar](#title-bar)), or, without a bar, the shelf's "–" on
the window's corner (absolute windows only). Closing the window with it
clears the player's request, so setting the binding back to true does not
reopen it. `ShowPanel` and `HidePanel` make that request from
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
The theme plugins read is covered by `BufferedUiRegistryThemeTests` under
`tests/AcDream.App.Tests/`, by `ScopedUiRegistryThemeTests` for the scoped
forwarder, and by `PluginThemeContractTests` and the headless suite for the
Classic answer.

The flex engine is covered by `FlexLayoutMeasureTests`,
`FlexLayoutArrangeTests`, `FlexLayoutNestingTests`, `FlexLayoutScalingTests`,
`FlexLayoutScrollbarTests`, `FlexLayoutWrapScrollTests`, `FlexFitTests` and
`FlexLayoutFuzzTests` under `tests/AcDream.App.Tests/UI/Layout/Flex/`. Flex
markup, title bars and scrolling groups are covered by `MarkupFlexTests`,
`MarkupFlexAttributesTests`, `MarkupContentSizeTests`,
`MarkupFlexControlsTests`, `MarkupFlexWindowTests`, `MarkupTitleBarTests`,
`MarkupScrollWindowTests`, `MarkupWindowMountTests`, `PluginTitleBarTests`,
`PluginWindowChromeTests`, `UiContentOffsetTests`,
`RetailWindowManagerMinimumTests`, `UiScrollGroupTests`,
`UiRootWheelRoutingTests` and `FlexDemoSampleTests` under
`tests/AcDream.App.Tests/UI/`.

Repeat is covered by `MarkupBindingSourceTests`, `UiElementChildOrderTests`,
`UiFlexBoxStructureTests`, `PluginThemeScopeTests`, `KeyedControlStateTests`,
`MarkupRepeatBuildTests`, `MarkupRepeatReconcileTests` and
`MarkupRepeatScaleTests` under `tests/AcDream.App.Tests/UI/` (the sample's
window is in `ControlsDemoSampleTests`).

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
