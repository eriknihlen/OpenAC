using System;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Xml.Linq;
using AcDream.Plugin.Abstractions;

namespace AcDream.App.UI;

public static class MarkupDocument
{
    private const uint RuntimeTooltipRootElementId = 0x10000397u;
    private const uint RuntimeTooltipLayoutDid = 0x21000041u;

    public static UiNineSlicePanel Build(
        string xml, object binding, Func<uint, (uint, int, int)> resolve,
        ControlsIni? style = null, UiDatFont? datFont = null,
        IMarkupIconResolver? icons = null)
    {
        var root = XDocument.Parse(xml).Root ?? throw new FormatException("empty markup");
        if (root.Name.LocalName != "panel")
            throw new FormatException($"root must be <panel>, got <{root.Name.LocalName}>");

        var panel = new UiNineSlicePanel(resolve)
        {
            Left   = F(root, "x"),
            Top    = F(root, "y"),
            Width  = F(root, "w"),
            Height = F(root, "h"),
        };

        bool resizable = B(root, "resizable", false);
        panel.Resizable = resizable;
        panel.MinWidth = FOr(root, "minw", panel.Width);
        panel.MinHeight = FOr(root, "minh", panel.Height);
        panel.ResizeX = resizable;
        panel.ResizeY = resizable;

        string? resize = (string?)root.Attribute("resize");
        if (resize is not null)
        {
            panel.ResizeX = resize is "x" or "both";
            panel.ResizeY = resize is "y" or "both";
        }

        string? visible = (string?)root.Attribute("visible");
        if (visible is not null && IsBinding(visible))
        {
            PropertyInfo? flag = binding.GetType().GetProperty(visible[1..^1]);
            if (flag is null || flag.PropertyType != typeof(bool))
            {
                throw new FormatException(
                    $"<panel visible=\"{visible}\"> did not resolve to a bool property "
                    + $"on {binding.GetType().Name}");
            }
            panel.VisibleSource = () => flag.GetValue(binding) is true;
        }

        string? title = (string?)root.Attribute("title");
        if (!string.IsNullOrEmpty(title))
        {
            Vector4 tc = style is not null && style.TryColor("title", "color", out var c) ? c : Vector4.One;
            panel.AddChild(new UiLabel
            {
                Text = title, Left = 8, Top = 4, TextColor = tc, DatFont = datFont,
            });
        }

        foreach (var el in root.Elements())
            AddElement(panel, el, binding, resolve, datFont, icons);
        return panel;
    }

    private static void AddElement(
        UiElement parent,
        XElement el,
        object binding,
        Func<uint, (uint, int, int)> resolve,
        UiDatFont? datFont,
        IMarkupIconResolver? icons)
    {
        switch (el.Name.LocalName)
        {
            case "group":
                var group = new UiPanel
                {
                    Left = F(el, "x"),
                    Top = F(el, "y"),
                    Width = F(el, "w"),
                    Height = F(el, "h"),
                    BackgroundColor = el.Attribute("background") is null
                        ? Vector4.Zero
                        : Color((string?)el.Attribute("background")),
                    BorderColor = el.Attribute("border") is null
                        ? Vector4.Zero
                        : Color((string?)el.Attribute("border")),
                    BorderThickness = el.Attribute("border") is null ? 0f : 1f,
                    ClickThrough = true,
                };
                ApplyCommon(group, el, binding);
                parent.AddChild(group);
                foreach (XElement child in el.Elements())
                    AddElement(group, child, binding, resolve, datFont, icons);
                break;

            case "meter":
                    var cur = BindUint((string?)el.Attribute("cur"), binding);
                    var max = BindUint((string?)el.Attribute("max"), binding);
                    var meter = new UiMeter
                    {
                        Left          = F(el, "x"),
                        Top           = F(el, "y"),
                        Width         = F(el, "w"),
                        Height        = F(el, "h"),
                        BarColor      = Color((string?)el.Attribute("color")),
                        Fill          = BindFloat((string?)el.Attribute("fill"), binding),
                        Label         = () => (cur(), max()) is (uint c, uint m) ? $"{c}/{m}" : null,
                        // anchor= is applied uniformly for every element by
                        // ApplyCommon below; no per-element handling needed here.
                        SpriteResolve = resolve,
                        BackLeft      = Hex((string?)el.Attribute("backleft")),
                        BackTile      = Hex((string?)el.Attribute("backtile")),
                        BackRight     = Hex((string?)el.Attribute("backright")),
                        FrontLeft     = Hex((string?)el.Attribute("frontleft")),
                        FrontTile     = Hex((string?)el.Attribute("fronttile")),
                        FrontRight    = Hex((string?)el.Attribute("frontright")),
                    };
                    ApplyCommon(meter, el, binding);
                    parent.AddChild(meter);
                    break;

            case "label":
                    // Text may be a literal or a {Binding}. Bound labels re-read
                    // their property every frame through the Func, so a plugin
                    // updates its status line by assigning a property rather
                    // than by touching UI objects from its own thread.
                    var label = new UiLabel
                    {
                        Left = F(el, "x"),
                        Top = F(el, "y"),
                        TextSource = BindString((string?)el.Attribute("text"), binding),
                        DatFont = datFont,
                    };
                    if (el.Attribute("color") is not null)
                        label.TextColor = Color((string?)el.Attribute("color"));
                    ApplyCommon(label, el, binding);
                    parent.AddChild(label);
                    break;

            case "button":
                    string? clickName = (string?)el.Attribute("onclick");
                    Action? onClick = BindAction(clickName, binding);
                    if (clickName is not null && onClick is null)
                    {
                        throw new FormatException(
                            $"<button onclick=\"{clickName}\"> did not resolve to an "
                            + $"Action property on {binding.GetType().Name}");
                    }
                    var button = new UiSimpleButton
                    {
                        Left = F(el, "x"),
                        Top = F(el, "y"),
                        Width = F(el, "w"),
                        Height = F(el, "h"),
                        Text = (string?)el.Attribute("text") ?? string.Empty,
                        DatFont = datFont,
                    };
                    string? caption = (string?)el.Attribute("text");
                    if (caption is not null && IsBinding(caption))
                        button.TextSource = BindString(caption, binding);
                    if (el.Attribute("color") is not null)
                        button.TextColor = Color((string?)el.Attribute("color"));
                    if (el.Attribute("background") is not null)
                        button.BackgroundColor = Color(
                            (string?)el.Attribute("background"));
                    if (el.Attribute("border") is not null)
                        button.BorderColor = Color(
                            (string?)el.Attribute("border"));
                    string? buttonIcon = (string?)el.Attribute("icon");
                    if (buttonIcon is not null)
                    {
                        string? buttonIconKind = (string?)el.Attribute("iconkind");
                        ValidateIconKind(buttonIconKind);
                        Func<uint> buttonIconReader =
                            BindUintLiteralOrBinding(buttonIcon, binding, "button icon");
                        if (icons is not null)
                        {
                            button.IconSource = BuildIconSource(
                                buttonIconKind,
                                buttonIconReader,
                                icons);
                        }
                    }
                    ApplyCommon(button, el, binding);
                    if (onClick is not null)
                        button.Click += onClick;
                    parent.AddChild(button);
                    break;

            case "icon":
                {
                    if (el.Attribute("iconkind") is not null)
                    {
                        throw new FormatException(
                            "iconkind applies to button and list; icon derives its kind from did/spell/item");
                    }

                    string? didAttr = (string?)el.Attribute("did");
                    string? spellAttr = (string?)el.Attribute("spell");
                    string? itemAttr = (string?)el.Attribute("item");
                    int sourceCount = (didAttr is not null ? 1 : 0)
                        + (spellAttr is not null ? 1 : 0)
                        + (itemAttr is not null ? 1 : 0);
                    if (sourceCount != 1)
                    {
                        throw new FormatException(
                            "<icon> requires exactly one of did/spell/item");
                    }

                    string iconKind = didAttr is not null ? "did"
                        : spellAttr is not null ? "spell"
                        : "item";
                    string iconExpression = didAttr ?? spellAttr ?? itemAttr!;
                    Func<uint> iconReader = BindUintLiteralOrBinding(
                        iconExpression, binding, $"icon {iconKind}");
                    var icon = new UiMarkupIcon
                    {
                        Left = F(el, "x"),
                        Top = F(el, "y"),
                        Width = FOr(el, "w", 32f),
                        Height = FOr(el, "h", 32f),
                        IconSource = BuildIconSource(iconKind, iconReader, icons),
                    };
                    ApplyCommon(icon, el, binding);
                    string? iconTooltip = (string?)el.Attribute("tooltip");
                    if (!string.IsNullOrWhiteSpace(iconTooltip))
                        icon.ClickThrough = false;
                    parent.AddChild(icon);
                    break;
                }

            case "tab":
                string? tabClickName = (string?)el.Attribute("onclick");
                Action? tabClick = BindAction(tabClickName, binding);
                if (tabClickName is not null && tabClick is null)
                {
                    throw new FormatException(
                        $"<tab onclick=\"{tabClickName}\"> did not resolve to an "
                        + $"Action property on {binding.GetType().Name}");
                }

                var tab = new UiMarkupTabButton
                {
                    Left = F(el, "x"),
                    Top = F(el, "y"),
                    Width = F(el, "w"),
                    Height = F(el, "h"),
                    Text = (string?)el.Attribute("text") ?? string.Empty,
                    DatFont = datFont,
                    SelectedSource = BindRequiredBoolReader(
                        (string?)el.Attribute("selected"),
                        binding,
                        "tab selected"),
                };
                ApplyCommon(tab, el, binding);
                if (tabClick is not null)
                    tab.Click += tabClick;
                parent.AddChild(tab);
                break;

            case "toggle":
                string? toggleClickName = (string?)el.Attribute("onclick");
                Action? toggleClick = BindAction(toggleClickName, binding);
                if (toggleClickName is not null && toggleClick is null)
                {
                    throw new FormatException(
                        $"<toggle onclick=\"{toggleClickName}\"> did not resolve to an "
                        + $"Action property on {binding.GetType().Name}");
                }

                string? toggleCaption = (string?)el.Attribute("text");
                var toggle = new UiMarkupToggle
                {
                    Left = F(el, "x"),
                    Top = F(el, "y"),
                    Width = F(el, "w"),
                    Height = F(el, "h"),
                    Text = toggleCaption ?? string.Empty,
                    TextSource = BindString(toggleCaption, binding),
                    CheckedSource = BindRequiredBoolReader(
                        (string?)el.Attribute("checked"),
                        binding,
                        "toggle checked"),
                    DatFont = datFont,
                    Toggle = toggleClick,
                };
                if (el.Attribute("color") is not null)
                    toggle.TextColor = Color((string?)el.Attribute("color"));
                ApplyCommon(toggle, el, binding);
                parent.AddChild(toggle);
                break;

            case "slider":
                string? changeName = (string?)el.Attribute("onchange");
                Action<float>? changed = BindFloatAction(changeName, binding);
                if (changeName is not null && changed is null)
                {
                    throw new FormatException(
                        $"<slider onchange=\"{changeName}\"> did not resolve to an "
                        + $"Action<float> property on {binding.GetType().Name}");
                }

                // KB 08 §3 gap: VVS's HudHSlider exposes an arbitrary Min/Max
                // range (VTank's own Vitals sliders are minimum="0"
                // maximum="100"); acdream's <slider> historically only ever
                // bound a fixed 0.0-1.0 value. Omitting both attributes keeps
                // that exact identity range so every pre-existing <slider>
                // (which never sets min/max) is byte-for-byte unaffected.
                float sliderMin = FOr(el, "min", 0f);
                float sliderMax = FOr(el, "max", 1f);
                if (sliderMax <= sliderMin)
                {
                    throw new FormatException(
                        $"<slider min=\"{sliderMin}\" max=\"{sliderMax}\"> must have max > min");
                }
                float sliderRange = sliderMax - sliderMin;

                Func<float?> sliderValueSource = BindFloat(
                    (string?)el.Attribute("value"),
                    binding);

                bool sliderRetailArt = ValidateArtStyle("slider", (string?)el.Attribute("style"));

                var slider = new UiScrollbar
                {
                    Left = F(el, "x"),
                    Top = F(el, "y"),
                    Width = F(el, "w"),
                    Height = F(el, "h"),
                    Horizontal = true,
                    SpriteResolve = resolve,
                    RetailArt = sliderRetailArt,
                    ScalarPositionSource = () =>
                        sliderValueSource() is { } declaredValue
                            ? Math.Clamp(
                                (declaredValue - sliderMin) / sliderRange, 0f, 1f)
                            : (float?)null,
                    ScalarChanged = changed is null
                        ? null
                        : normalized => changed(sliderMin + normalized * sliderRange),
                };
                if (sliderRetailArt)
                    RetailScrollbarChrome.ApplyHorizontal(slider);
                ApplyCommon(slider, el, binding);
                parent.AddChild(slider);
                break;

            case "field":
                string? fieldChangeName = (string?)el.Attribute("onchange");
                Action<string>? fieldChanged = BindStringAction(
                    fieldChangeName,
                    binding);
                if (fieldChangeName is not null && fieldChanged is null)
                {
                    throw new FormatException(
                        $"<field onchange=\"{fieldChangeName}\"> did not resolve to an "
                        + $"Action<string> property on {binding.GetType().Name}");
                }
                string? submitName = (string?)el.Attribute("onsubmit");
                Action<string>? submitted = BindStringAction(submitName, binding);
                if (submitName is not null && submitted is null)
                {
                    throw new FormatException(
                        $"<field onsubmit=\"{submitName}\"> did not resolve to an "
                        + $"Action<string> property on {binding.GetType().Name}");
                }

                var field = new UiField
                {
                    Left = F(el, "x"),
                    Top = F(el, "y"),
                    Width = F(el, "w"),
                    Height = F(el, "h"),
                    DatFont = datFont,
                    BackgroundColor = el.Attribute("background") is null
                        ? new Vector4(0f, 0f, 0f, 0.9f)
                        : Color((string?)el.Attribute("background")),
                    TextColor = el.Attribute("color") is null
                        ? new Vector4(0.91f, 0.87f, 0.76f, 1f)
                        : Color((string?)el.Attribute("color")),
                    MaxCharacters = Math.Max(1, I(el, "maxlength", 128)),
                    ClearOnSubmit = B(el, "clearonsubmit", false),
                    RecordHistory = false,
                    OnTextChanged = fieldChanged,
                    OnSubmit = submitted,
                };
                field.SetText(BindString((string?)el.Attribute("text"), binding)());
                ApplyCommon(field, el, binding);
                parent.AddChild(field);
                break;

            case "menu":
                string? menuChangeName = (string?)el.Attribute("onchange");
                Action<string>? menuChanged = BindStringAction(
                    menuChangeName,
                    binding);
                if (menuChangeName is not null && menuChanged is null)
                {
                    throw new FormatException(
                        $"<menu onchange=\"{menuChangeName}\"> did not resolve to an "
                        + $"Action<string> property on {binding.GetType().Name}");
                }
                Func<IReadOnlyList<string>> menuItems = BindStringList(
                    (string?)el.Attribute("items"),
                    binding,
                    "menu items");
                Func<string?> menuSelected = BindString(
                    (string?)el.Attribute("selected"),
                    binding);
                bool menuRetailButtonArt = ValidateArtStyle("menu", (string?)el.Attribute("style"));
                var menu = new UiMenu
                {
                    Left = F(el, "x"),
                    Top = F(el, "y"),
                    Width = F(el, "w"),
                    Height = F(el, "h"),
                    DatFont = datFont,
                    SpriteResolve = resolve,
                    RowsPerColumn = Math.Max(1, I(el, "rows", 7)),
                    RowHeight = Math.Max(12f, FOr(el, "rowheight", 18f)),
                    ColumnWidth = Math.Max(20f, F(el, "w")),
                    OpenUpward = B(el, "openupward", false),
                    ScrollTrackSprite = 0x06004C5Fu,
                    ScrollThumbSprite = 0x06004C63u,
                    ScrollUpSprite = RetailScrollbarChrome.UpNormal,
                    ScrollDownSprite = RetailScrollbarChrome.DownNormal,
                    TextIndent = 6f,
                    ButtonTextIndent = 6f,
                    NormalSprite = 0x06004D65u,
                    PressedSprite = 0x06004D66u,
                    PopupBgSprite = 0x0600124Cu,
                    ItemNormalSprite = 0x0600124Eu,
                    ItemHighlightSprite = 0x0600124Du,
                    RetailButtonArt = menuRetailButtonArt,
                    Scrollable = true,
                    PopupScrollbarHideWhenDisabled = true,
                    ButtonLabelProvider = () => menuSelected() ?? string.Empty,
                    OnSelect = payload =>
                    {
                        if (payload is string value)
                            menuChanged?.Invoke(value);
                    },
                };
                RetailScrollbarChrome.ApplyToMenuPopup(menu);
                void RefreshMenu()
                {
                    menu.Items = menuItems()
                        .Select(static value => new UiMenu.MenuItem(value, value))
                        .ToArray();
                    menu.Selected = menuSelected();
                }
                RefreshMenu();
                menu.BeforeOpen = RefreshMenu;
                ApplyCommon(menu, el, binding);
                parent.AddChild(menu);
                break;

            case "list":
                string? listChangeName = (string?)el.Attribute("onchange");
                Action<int>? listChanged = BindIntAction(listChangeName, binding);
                if (listChangeName is not null && listChanged is null)
                {
                    throw new FormatException(
                        $"<list onchange=\"{listChangeName}\"> did not resolve to an "
                        + $"Action<int> property on {binding.GetType().Name}");
                }

                var listChildren = el.Elements().ToList();
                foreach (var child in listChildren)
                {
                    if (child.Name.LocalName != "column")
                    {
                        throw new FormatException(
                            $"<list> children must all be <column>, got <{child.Name.LocalName}>");
                    }
                }
                bool listUsesColumns = listChildren.Count > 0;
                if (listUsesColumns
                    && (el.Attribute("items") is not null
                        || el.Attribute("icons") is not null
                        || el.Attribute("colors") is not null))
                {
                    throw new FormatException(
                        "<list> with <column> children cannot also use the "
                        + "items/icons/colors attributes (the single-column "
                        + "form) — express every row source as a <column> instead");
                }

                var list = new UiMarkupList
                {
                    Left = F(el, "x"),
                    Top = F(el, "y"),
                    Width = F(el, "w"),
                    Height = F(el, "h"),
                    RowHeight = Math.Max(12f, FOr(el, "rowheight", 18f)),
                    DatFont = datFont,
                    SpriteResolve = resolve,
                    SelectedIndexSource = BindRequiredIntReader(
                        (string?)el.Attribute("selected"),
                        binding,
                        "list selected"),
                    SelectionChanged = listChanged,
                    SelectionBandEnabled = B(el, "selectionband", false),
                };

                if (listUsesColumns)
                {
                    int lastColumnIndex = listChildren.Count - 1;
                    list.Columns = listChildren
                        .Select((columnEl, index) => BuildListColumn(
                            columnEl, binding, icons, index, index == lastColumnIndex))
                        .ToList();
                }
                else
                {
                    list.ItemsSource = BindStringList(
                        (string?)el.Attribute("items"),
                        binding,
                        "list items");
                    list.ItemColorsSource = BindUintList(
                        (string?)el.Attribute("colors"),
                        binding,
                        "list colors");
                    string? listIcons = (string?)el.Attribute("icons");
                    if (!string.IsNullOrWhiteSpace(listIcons))
                    {
                        string? listIconKind = (string?)el.Attribute("iconkind");
                        ValidateIconKind(listIconKind);
                        Func<IReadOnlyList<uint>> listIconIdsReader =
                            BindUintList(listIcons, binding, "list icons");
                        if (icons is not null)
                        {
                            list.IconIdsSource = listIconIdsReader;
                            list.IconResolve = BuildRowIconResolve(listIconKind, icons);
                        }
                    }
                }
                ApplyCommon(list, el, binding);
                parent.AddChild(list);
                break;

            default:
                throw new FormatException($"unknown element <{el.Name.LocalName}>");
        }
    }

    private static string ValidateIconKind(string? iconKind, string context = "iconkind") =>
        (iconKind ?? "did") switch
        {
            "did" or "spell" or "item" => iconKind ?? "did",
            var other => throw new FormatException(
                $"{context} must be did, spell, or item (got \"{other}\")"),
        };

    private static bool ValidateArtStyle(string elementName, string? style) => style switch
    {
        null or "plain" => false,
        "retail" => true,
        var other => throw new FormatException(
            $"<{elementName} style=\"{other}\"> must be plain or retail"),
    };

    private static Func<(uint tex, int w, int h)> BuildIconSource(
        string? iconKind, Func<uint> idReader, IMarkupIconResolver? icons)
    {
        string kind = ValidateIconKind(iconKind);
        if (icons is null)
            return static () => (0u, 0, 0);
        return kind switch
        {
            "did" => () => icons.ResolveDid(
                PluginIcons.Normalize(idReader())),
            "spell" => () => icons.ResolveSpell(idReader()),
            "item" => () => icons.ResolveItem(idReader()),
            _ => throw new InvalidOperationException(
                "unreachable — ValidateIconKind already rejected anything else"),
        };
    }

    private static Func<uint, (uint tex, int w, int h)> BuildRowIconResolve(
        string? iconKind, IMarkupIconResolver icons)
    {
        string kind = ValidateIconKind(iconKind);
        return kind switch
        {
            "did" => id => icons.ResolveDid(
                PluginIcons.Normalize(id)),
            "spell" => icons.ResolveSpell,
            "item" => icons.ResolveItem,
            _ => throw new InvalidOperationException(
                "unreachable — ValidateIconKind already rejected anything else"),
        };
    }

    private static Func<uint> BindUintLiteralOrBinding(
        string expression, object binding, string context)
    {
        if (!IsBinding(expression))
        {
            uint literal = ParseUintLiteral(expression, context);
            return () => literal;
        }
        PropertyInfo? property = binding.GetType().GetProperty(expression[1..^1]);
        if (property is null)
        {
            throw new FormatException(
                $"{expression} did not resolve to a property on "
                + binding.GetType().Name + $" ({context})");
        }
        return () => property.GetValue(binding) switch
        {
            uint u => u,
            null => 0u,
            var v => ToUintOrZero(v),
        };
    }

    private static uint ToUintOrZero(object value)
    {
        try
        {
            return Convert.ToUInt32(value, CultureInfo.InvariantCulture);
        }
        catch (OverflowException)
        {
            return 0u;
        }
    }

    private static uint ParseUintLiteral(string text, string context)
    {
        string trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (uint.TryParse(trimmed.AsSpan(2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out uint hex))
                return hex;
        }
        else if (uint.TryParse(trimmed, NumberStyles.Integer,
                     CultureInfo.InvariantCulture, out uint dec))
        {
            return dec;
        }
        throw new FormatException($"{context}=\"{text}\" is not a valid uint literal");
    }

    private static Func<string?> BindString(string? attribute, object binding)
    {
        if (attribute is null)
            return static () => null;
        if (!IsBinding(attribute))
            return () => attribute;

        string name = attribute[1..^1];
        PropertyInfo? property = binding.GetType().GetProperty(name);
        if (property is null)
            return () => attribute;
        return () => property.GetValue(binding)?.ToString();
    }

    private static Action? BindAction(string? attribute, object binding)
    {
        if (attribute is null || !IsBinding(attribute))
            return null;

        string name = attribute[1..^1];
        PropertyInfo? property = binding.GetType().GetProperty(name);
        if (property is null || !typeof(Action).IsAssignableFrom(property.PropertyType))
            return null;

        return () => (property.GetValue(binding) as Action)?.Invoke();
    }

    private static Action<float>? BindFloatAction(
        string? attribute,
        object binding)
    {
        if (attribute is null || !IsBinding(attribute))
            return null;

        string name = attribute[1..^1];
        PropertyInfo? property = binding.GetType().GetProperty(name);
        if (property is null
            || !typeof(Action<float>).IsAssignableFrom(property.PropertyType))
            return null;
        return value => (property.GetValue(binding) as Action<float>)?.Invoke(value);
    }

    private static Action<string>? BindStringAction(
        string? attribute,
        object binding)
    {
        if (attribute is null || !IsBinding(attribute))
            return null;

        string name = attribute[1..^1];
        PropertyInfo? property = binding.GetType().GetProperty(name);
        if (property is null
            || !typeof(Action<string>).IsAssignableFrom(property.PropertyType))
        {
            return null;
        }
        return value => (property.GetValue(binding) as Action<string>)?.Invoke(value);
    }

    private static Action<int>? BindIntAction(string? attribute, object binding)
    {
        if (attribute is null || !IsBinding(attribute))
            return null;
        PropertyInfo? property = binding.GetType().GetProperty(attribute[1..^1]);
        if (property is null
            || !typeof(Action<int>).IsAssignableFrom(property.PropertyType))
        {
            return null;
        }
        return value => (property.GetValue(binding) as Action<int>)?.Invoke(value);
    }

    private static Func<IReadOnlyList<string>> BindStringList(
        string? expression,
        object binding,
        string context)
    {
        if (string.IsNullOrWhiteSpace(expression) || !IsBinding(expression))
            throw new FormatException($"{context} must be a string-list binding");
        PropertyInfo? property = binding.GetType().GetProperty(expression[1..^1]);
        if (property is null
            || !typeof(IEnumerable<string>).IsAssignableFrom(property.PropertyType))
        {
            throw new FormatException(
                $"{expression} did not resolve to an IEnumerable<string> property on "
                + binding.GetType().Name);
        }
        return () => property.GetValue(binding) is IEnumerable<string> values
            ? values.ToArray()
            : Array.Empty<string>();
    }

    private static Func<IReadOnlyList<uint>> BindUintList(
        string? expression,
        object binding,
        string context)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return static () => Array.Empty<uint>();
        if (!IsBinding(expression))
            throw new FormatException($"{context} must be a uint-list binding");
        PropertyInfo? property = binding.GetType().GetProperty(expression[1..^1]);
        if (property is null)
        {
            throw new FormatException(
                $"{expression} did not resolve to an IEnumerable<uint> or "
                + "IEnumerable<int> property on " + binding.GetType().Name);
        }
        if (typeof(IEnumerable<uint>).IsAssignableFrom(property.PropertyType))
        {
            return () => property.GetValue(binding) is IEnumerable<uint> values
                ? values.ToArray()
                : Array.Empty<uint>();
        }
        if (typeof(IEnumerable<int>).IsAssignableFrom(property.PropertyType))
        {
            return () => property.GetValue(binding) is IEnumerable<int> values
                ? values.Select(static v => v < 0 ? 0u : (uint)v).ToArray()
                : Array.Empty<uint>();
        }
        throw new FormatException(
            $"{expression} did not resolve to an IEnumerable<uint> or "
            + "IEnumerable<int> property on " + binding.GetType().Name);
    }

    private static Func<IReadOnlyList<bool>> BindBoolList(
        string? expression, object binding, string context)
    {
        if (string.IsNullOrWhiteSpace(expression) || !IsBinding(expression))
            throw new FormatException($"{context} must be a bool-list binding");
        PropertyInfo? property = binding.GetType().GetProperty(expression[1..^1]);
        if (property is null
            || !typeof(IEnumerable<bool>).IsAssignableFrom(property.PropertyType))
        {
            throw new FormatException(
                $"{expression} did not resolve to an IEnumerable<bool> property on "
                + binding.GetType().Name + $" ({context})");
        }
        return () => property.GetValue(binding) is IEnumerable<bool> values
            ? values.ToArray()
            : Array.Empty<bool>();
    }

    private static Func<IReadOnlyList<uint>> BindRequiredUintList(
        string? expression, object binding, string context)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new FormatException($"{context} must be a uint-list binding");
        return BindUintList(expression, binding, context);
    }

    private static Action<int> BindRequiredIntAction(
        string? attribute, object binding, string context)
    {
        if (attribute is null || !IsBinding(attribute))
            throw new FormatException($"{context} must be an Action<int> binding");
        PropertyInfo? property = binding.GetType().GetProperty(attribute[1..^1]);
        if (property is null || !typeof(Action<int>).IsAssignableFrom(property.PropertyType))
        {
            throw new FormatException(
                $"{attribute} did not resolve to an Action<int> property on "
                + binding.GetType().Name + $" ({context})");
        }
        return value => (property.GetValue(binding) as Action<int>)?.Invoke(value);
    }

    private static UiMarkupListColumn BuildListColumn(
        XElement columnEl, object binding, IMarkupIconResolver? icons, int index, bool isLast)
    {
        string? type = (string?)columnEl.Attribute("type");
        (float width, bool isAutoWidth) = ParseColumnWidth(columnEl, index, type, isLast);
        switch (type)
        {
            case "text":
            {
                var textSource = BindStringList(
                    (string?)columnEl.Attribute("items"), binding, ColumnContext(index, "text", "items"));
                string? colorsAttr = (string?)columnEl.Attribute("colors");
                Func<IReadOnlyList<uint>>? colorsSource = colorsAttr is null
                    ? null
                    : BindUintList(colorsAttr, binding, ColumnContext(index, "text", "colors"));
                string? textOnClickAttr = (string?)columnEl.Attribute("onclick");
                Action<int>? textOnClick = BindIntAction(textOnClickAttr, binding);
                if (textOnClickAttr is not null && textOnClick is null)
                {
                    throw new FormatException(
                        $"{ColumnContext(index, "text", "onclick")} did not resolve to an "
                        + $"Action<int> property on {binding.GetType().Name}");
                }
                return UiMarkupListColumn.Text(width, textSource, colorsSource, textOnClick, isAutoWidth);
            }
            case "check":
            {
                var checkSource = BindBoolList(
                    (string?)columnEl.Attribute("values"), binding, ColumnContext(index, "check", "values"));
                var onChange = BindRequiredIntAction(
                    (string?)columnEl.Attribute("onchange"), binding, ColumnContext(index, "check", "onchange"));
                return UiMarkupListColumn.Check(width, checkSource, onChange, isAutoWidth);
            }
            case "icon":
            {
                var valuesSource = BindRequiredUintList(
                    (string?)columnEl.Attribute("values"), binding, ColumnContext(index, "icon", "values"));
                string? iconKind = (string?)columnEl.Attribute("iconkind");
                ValidateIconKind(iconKind, ColumnContext(index, "icon", "iconkind"));
                var onClick = BindRequiredIntAction(
                    (string?)columnEl.Attribute("onclick"), binding, ColumnContext(index, "icon", "onclick"));
                Func<uint, (uint, int, int)>? resolve = icons is not null
                    ? BuildRowIconResolve(iconKind, icons)
                    : null;
                return UiMarkupListColumn.Icon(width, valuesSource, resolve, onClick, isAutoWidth);
            }
            default:
                throw new FormatException(
                    $"column[{index}] has unknown type=\"{type}\" (expected text, check, or icon)");
        }
    }

    private static string ColumnContext(int index, string type, string attribute) =>
        $"column[{index}] type=\"{type}\" {attribute}";

    private static (float width, bool isAutoWidth) ParseColumnWidth(
        XElement columnEl, int index, string? type, bool isLast)
    {
        string? raw = (string?)columnEl.Attribute("width");
        if (raw == "*")
            return (0f, true);
        if (isLast)
            return (F(columnEl, "width"), false);
        if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float width)
            || width <= 0f)
        {
            throw new FormatException(
                $"{ColumnContext(index, type ?? "(missing)", "width")} must be a positive "
                + "number or \"*\", got " + (raw is null ? "(missing)" : $"\"{raw}\""));
        }
        return (width, false);
    }

    private static bool IsBinding(string value) =>
        value.Length > 2 && value[0] == '{' && value[^1] == '}';

    private static void ApplyCommon(
        UiElement element,
        XElement source,
        object binding)
    {
        element.Name = (string?)source.Attribute("name")
            ?? (string?)source.Attribute("id");

        element.Anchors = ParseAnchor((string?)source.Attribute("anchor"), source);

        BindBool((string?)source.Attribute("visible"), binding,
            value => element.Visible = value,
            sourceReader => element.VisibleSource = sourceReader);
        BindBool((string?)source.Attribute("enabled"), binding,
            value => element.Enabled = value,
            sourceReader => element.EnabledSource = sourceReader);

        string? tooltip = (string?)source.Attribute("tooltip");
        if (!string.IsNullOrWhiteSpace(tooltip))
        {
            element.RuntimeTooltipTextSource = BindString(tooltip, binding);
            element.AuthoredTooltipRootElementId = RuntimeTooltipRootElementId;
            element.AuthoredTooltipLayoutDid = RuntimeTooltipLayoutDid;
            element.AuthoredTooltipEnabled = true;
        }
    }

    private static void BindBool(
        string? expression,
        object binding,
        Action<bool> setLiteral,
        Action<Func<bool>> setSource)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return;
        if (!IsBinding(expression))
        {
            if (bool.TryParse(expression, out bool literal))
                setLiteral(literal);
            return;
        }

        PropertyInfo? property = binding.GetType().GetProperty(expression[1..^1]);
        if (property is null || property.PropertyType != typeof(bool))
        {
            throw new FormatException(
                $"{expression} did not resolve to a bool property on "
                + binding.GetType().Name);
        }
        setSource(() => property.GetValue(binding) is true);
    }

    private static Func<bool> BindRequiredBoolReader(
        string? expression,
        object binding,
        string context)
    {
        if (string.IsNullOrWhiteSpace(expression) || !IsBinding(expression))
            throw new FormatException($"{context} must be a bool binding");

        PropertyInfo? property = binding.GetType().GetProperty(expression[1..^1]);
        if (property is null || property.PropertyType != typeof(bool))
        {
            throw new FormatException(
                $"{expression} did not resolve to a bool property on "
                + binding.GetType().Name);
        }
        return () => property.GetValue(binding) is true;
    }

    private static Func<int> BindRequiredIntReader(
        string? expression,
        object binding,
        string context)
    {
        if (string.IsNullOrWhiteSpace(expression) || !IsBinding(expression))
            throw new FormatException($"{context} must be an int binding");
        PropertyInfo? property = binding.GetType().GetProperty(expression[1..^1]);
        if (property is null || property.PropertyType != typeof(int))
        {
            throw new FormatException(
                $"{expression} did not resolve to an int property on "
                + binding.GetType().Name);
        }
        return () => property.GetValue(binding) is int value ? value : -1;
    }

    private static float F(XElement e, string attr)
        => float.TryParse((string?)e.Attribute(attr), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var v) ? v : 0f;

    private static float FOr(XElement e, string attr, float fallback)
        => float.TryParse((string?)e.Attribute(attr), NumberStyles.Float,
            CultureInfo.InvariantCulture, out float value) ? value : fallback;

    private static int I(XElement e, string attr, int fallback)
        => int.TryParse((string?)e.Attribute(attr), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int value) ? value : fallback;

    private static bool B(XElement e, string attr, bool fallback)
        => bool.TryParse((string?)e.Attribute(attr), out bool value)
            ? value
            : fallback;

    private static Vector4 Color(string? hex)
    {
        if (hex is { Length: 9 } && hex[0] == '#'
            && uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber,
                             CultureInfo.InvariantCulture, out uint argb))
            return new Vector4(
                ((argb >> 16) & 0xFF) / 255f,
                ((argb >> 8)  & 0xFF) / 255f,
                (argb         & 0xFF) / 255f,
                ((argb >> 24) & 0xFF) / 255f);
        return Vector4.One;
    }

    private static Func<float?> BindFloat(string? expr, object binding)
    {
        var pi = Prop(expr, binding);
        if (pi is null) return () => 0f;
        return () => pi.GetValue(binding) switch
        {
            float f  => f,
            null     => (float?)null,
            var v    => Convert.ToSingle(v, CultureInfo.InvariantCulture),
        };
    }

    private static Func<uint?> BindUint(string? expr, object binding)
    {
        var pi = Prop(expr, binding);
        if (pi is null) return () => null;
        return () => pi.GetValue(binding) switch
        {
            uint u => u,
            null   => (uint?)null,
            var v  => Convert.ToUInt32(v, CultureInfo.InvariantCulture),
        };
    }

    private static PropertyInfo? Prop(string? expr, object binding)
    {
        if (expr is null || expr.Length < 3 || expr[0] != '{' || expr[^1] != '}') return null;
        return binding.GetType().GetProperty(expr[1..^1]);
    }

    private static uint Hex(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        var t = s.Trim();
        if (t.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase)) t = t[2..];
        return uint.TryParse(t, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0u;
    }

    private static AnchorEdges ParseAnchor(string? tokens, XElement source)
    {
        if (string.IsNullOrWhiteSpace(tokens))
            return AnchorEdges.Left | AnchorEdges.Top;

        var edges = AnchorEdges.None;
        foreach (string token in tokens.Split(
            (char[]?)null, System.StringSplitOptions.RemoveEmptyEntries))
        {
            edges |= token.ToLowerInvariant() switch
            {
                "left"   => AnchorEdges.Left,
                "top"    => AnchorEdges.Top,
                "right"  => AnchorEdges.Right,
                "bottom" => AnchorEdges.Bottom,
                _ => throw new FormatException(
                    $"{ElementIdentity(source)} anchor=\"{tokens}\" has unknown token "
                    + $"\"{token}\" (expected left, top, right, bottom)"),
            };
        }
        return edges;
    }

    private static string ElementIdentity(XElement source)
    {
        string? name = (string?)source.Attribute("name") ?? (string?)source.Attribute("id");
        return name is null
            ? $"<{source.Name.LocalName}>"
            : $"<{source.Name.LocalName} name=\"{name}\">";
    }
}
