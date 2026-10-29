using System;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Xml.Linq;
using AcDream.App.UI.Layout.Flex;
using AcDream.App.UI.Markup;
using AcDream.Plugin.Abstractions;
using AcDream.UI.Abstractions.Input;

namespace AcDream.App.UI;

public static class MarkupDocument
{
    internal const uint RuntimeTooltipRootElementId = 0x10000397u;
    internal const uint RuntimeTooltipLayoutDid = 0x21000041u;

    /// <summary>
    /// What separates the edge names in an anchor attribute. An author writes
    /// either "right bottom" or "right,bottom"; both read the same aloud.
    /// </summary>
    private static readonly char[] AnchorSeparators = [',', ' ', '\t', '\r', '\n'];

    public static UiNineSlicePanel Build(
        string xml, object binding, Func<uint, (uint, int, int)> resolve,
        ControlsIni? style = null, UiDatFont? datFont = null,
        IMarkupIconResolver? icons = null, PluginUiThemeSettings? themes = null,
        IMarkupControlState? controlState = null,
        Func<KeyChord, string>? describeKey = null,
        IMarkupTextureService? textures = null, IMarkupControlState? sharedControlState = null)
        => BuildWindow(xml, binding, resolve, style, datFont, icons, themes, fallbackTitle: null,
            controlState, describeKey, textures, sharedControlState).Frame;

    /// <summary>
    /// Builds a markup window and what mounting it needs (see
    /// <see cref="MarkupWindow"/>). <paramref name="fallbackTitle"/> names the
    /// title bar when the markup has no <c>title</c>: the registration's title.
    /// </summary>
    internal static MarkupWindow BuildWindow(
        string xml, object binding, Func<uint, (uint, int, int)> resolve,
        ControlsIni? style = null, UiDatFont? datFont = null,
        IMarkupIconResolver? icons = null, PluginUiThemeSettings? themes = null,
        string? fallbackTitle = null,
        IMarkupControlState? controlState = null,
        Func<KeyChord, string>? describeKey = null,
        IMarkupTextureService? textures = null, IMarkupControlState? sharedControlState = null)
    {
        var root = XDocument.Parse(xml).Root ?? throw new FormatException("empty markup");
        var source = MarkupBindingSource.For(binding);
        if (root.Name.LocalName != "panel")
            throw new FormatException($"root must be <panel>, got <{root.Name.LocalName}>");
        if (root.Element("contextmenu") is not null)
            throw new FormatException("<contextmenu> goes inside the element it belongs to, not directly in <panel>");
        MarkupRepeat.RejectUnkeyedNames(root);

        bool themed = (string?)root.Attribute("theme") == "plugin";
        if (themed) themes ??= new PluginUiThemeSettings();
        UiNineSlicePanel panel = themed
            ? new UiPluginMarkupPanel(resolve, themes ?? new PluginUiThemeSettings())
            : new UiNineSlicePanel(resolve);

        // A window with chrome or a flex layout has a content area: its authored
        // sizes are the area inside the border (and title bar), and the frame
        // adds the chrome. A root with layout gets the title bar by default.
        var warnings = new List<string>();
        bool hasLayout = MarkupFlexAttributes.IsContainer(root, warnings);
        bool hasTitleBar = TitleBar(root, hasLayout, warnings);
        bool hasContentArea = hasLayout || hasTitleBar;
        var (rootScrollX, rootScrollY) = MarkupFlexAttributes.Scroll(root, warnings);
        if ((rootScrollX || rootScrollY) && !hasContentArea)
        {
            warnings.Add(MarkupFlexAttributes.Ignored(root, "scroll", (string?)root.Attribute("scroll") ?? string.Empty,
                "scrolls the window's content area, which needs layout=\"row|column\" or titlebar=\"true\" on the panel"));
            (rootScrollX, rootScrollY) = (false, false);
        }
        float chromeW = hasContentArea ? PluginWindowChrome.HorizontalInsets : 0f;
        float chromeH = hasContentArea ? PluginWindowChrome.VerticalInsetsFor(hasTitleBar) : 0f;
        if (!hasLayout) MarkupFlexAttributes.RejectContainer(root, warnings);

        // A layout root may leave its size to its content; it is measured once
        // its children exist (below), so these are provisional for it.
        float? authoredW = hasLayout ? MarkupFlexAttributes.RootSize(root, "w") : F(root, "w");
        float? authoredH = hasLayout ? MarkupFlexAttributes.RootSize(root, "h") : F(root, "h");
        float? authoredMinW = hasLayout ? MarkupFlexAttributes.RootSize(root, "minw") : null;
        float? authoredMinH = hasLayout ? MarkupFlexAttributes.RootSize(root, "minh") : null;
        float contentW = authoredW ?? 0f, contentH = authoredH ?? 0f;
        panel.Left = F(root, "x"); panel.Top = F(root, "y");
        panel.Width = contentW + chromeW; panel.Height = contentH + chromeH;

        bool resizable = B(root, "resizable", false);
        panel.Resizable = resizable;
        panel.MinWidth = hasContentArea
            ? PluginWindowChrome.FrameMinimumWidth(FOr(root, "minw", contentW), hasTitleBar)
            : FOr(root, "minw", contentW);
        panel.MinHeight = FOr(root, "minh", contentH) + chromeH;
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
            PropertyInfo? flag = source.Property(visible[1..^1]);
            if (flag is null || flag.PropertyType != typeof(bool))
            {
                throw new FormatException(
                    $"<panel visible=\"{visible}\"> did not resolve to a bool property "
                    + $"on {source.TypeName}");
            }
            panel.VisibleSource = () => source.Read(flag) is true;
        }

        string? title = (string?)root.Attribute("title");
        Vector4 titleColor = style is not null && style.TryColor("title", "color", out var c) ? c : Vector4.One;
        UiElement contentParent = panel;
        UiPluginContentHost? content = null;
        PluginTitleBar? titleBar = null;
        if (hasTitleBar)
        {
            titleBar = new PluginTitleBar(resolve, panel.Width)
            {
                Title = string.IsNullOrEmpty(title) ? fallbackTitle ?? string.Empty : title,
                DatFont = datFont,
                TextColor = titleColor,
            };
            titleBar.Close.AuthoredTooltipRootElementId = RuntimeTooltipRootElementId;
            titleBar.Close.AuthoredTooltipLayoutDid = RuntimeTooltipLayoutDid;
            titleBar.Close.AuthoredTooltipEnabled = true;
            panel.AddChild(titleBar);
            if (panel is UiPluginMarkupPanel barPanel)
                PluginMarkupTheme.RegisterTitleBar(barPanel, titleBar);
        }
        else if (!hasLayout && !string.IsNullOrEmpty(title))
        {
            panel.AddChild(new UiLabel
            {
                Text = title, Left = 8, Top = 4, TextColor = titleColor, DatFont = datFont,
            });
            if (panel is UiPluginMarkupPanel titlePanel)
                PluginMarkupTheme.Register(titlePanel, panel.Children[^1], new XElement("label"));
        }
        if (hasContentArea)
        {
            content = new UiPluginContentHost
            {
                Left = PluginWindowChrome.Border,
                Top = PluginWindowChrome.TopInset(hasTitleBar),
                Width = contentW,
                Height = contentH,
            };
            panel.AddChild(content);
            contentParent = content;
        }

        UiFlexBox? rootFlex = null;
        if (hasLayout)
        {
            rootFlex = content!.UseFlex();
            MarkupFlexAttributes.ReadContainer(root, rootFlex.Node);
        }
        if (rootScrollX || rootScrollY)
        {
            content!.UseScroll(rootScrollX, rootScrollY, resolve);
            if (panel is UiPluginMarkupPanel scrollPanel) content.ScrollArea!.RegisterTheme(scrollPanel);
        }

        var recentColors = new MarkupRecentColors(sharedControlState);
        foreach (var el in root.Elements())
            AddElement(contentParent, el, source, resolve, datFont, icons, panel as UiPluginMarkupPanel, themes,
                controlState, describeKey, textures, recentColors, warnings, rootFlex);

        if (rootFlex is not null)
        {
            // The window's first measurement, with the fonts and theme known
            // now: it sizes a root that states no w/h, and sets the minimum, so
            // mounting and registration see real geometry. An authored size
            // below the content's minimum opens at the minimum.
            FlexMeasurement measured = rootFlex.MeasureNow();
            float minW = authoredMinW ?? measured.Minimum.Width;
            float minH = authoredMinH ?? measured.Minimum.Height;
            contentW = MathF.Max(authoredW ?? measured.Preferred.Width, minW);
            contentH = MathF.Max(authoredH ?? measured.Preferred.Height, minH);
            panel.Width = contentW + chromeW; panel.Height = contentH + chromeH;
            panel.MinWidth = PluginWindowChrome.FrameMinimumWidth(minW, hasTitleBar); panel.MinHeight = minH + chromeH;
            content!.Width = contentW; content.Height = contentH;
        }
        if (hasTitleBar && panel.Width < PluginWindowChrome.MinimumBarFrameWidth)
        {
            // A window narrower than its title bar needs opens at the bar's minimum.
            panel.Width = panel.MinWidth;
            content!.Width = panel.Width - chromeW;
        }
        titleBar?.SetFrameWidth(panel.Width);

        // The whole document now sits at its authored sizes, so this is the one
        // moment every anchor margin can be read off the layout its author wrote.
        // Pin them here rather than on first draw: a group that is hidden when the
        // window is resized is never reached by the draw-time anchor pass, and
        // would otherwise measure its children against the resized parent the first
        // time it is opened.
        panel.CaptureAuthoredAnchorBaselines();

        int revision = hasContentArea
            ? RetailWindowManager.ComputeAuthoredGeometryRevision(PluginWindowChrome.AuthoredInputs(root))
            : RetailWindowManager.ComputeAuthoredGeometryRevision(
                panel.Width, panel.Height, panel.MinWidth, panel.MinHeight, panel.Resizable);
        var window = new MarkupWindow(panel, contentParent, titleBar, revision, warnings);
        if (rootFlex is not null)
            window.TrackContentMinimum(content!, chromeH, authoredMinW, authoredMinH);
        return window;
    }

    /// <summary>
    /// Whether the root asks for the host title bar: <c>titlebar</c> when it
    /// is given, else on exactly when the root uses <c>layout</c>. Any other
    /// value is reported and counts as absent.
    /// </summary>
    private static bool TitleBar(XElement root, bool hasLayout, ICollection<string> warnings)
    {
        switch ((string?)root.Attribute("titlebar"))
        {
            case null: return hasLayout;
            case "false": return false;
            case "true": return true;
            case string other:
                warnings.Add(MarkupFlexAttributes.Ignored(root, "titlebar", other, "must be true or false"));
                return hasLayout;
        }
    }

    private static void AddElement(
        UiElement parent,
        XElement el,
        MarkupBindingSource binding,
        Func<uint, (uint, int, int)> resolve,
        UiDatFont? datFont,
        IMarkupIconResolver? icons, UiPluginMarkupPanel? themedPanel, PluginUiThemeSettings? themes,
        IMarkupControlState? controlState,
        Func<KeyChord, string>? describeKey,
        IMarkupTextureService? textures, MarkupRecentColors recentColors,
        List<string> warnings,
        UiFlexBox? flex = null)
    {
        int firstChild = parent.Children.Count;
        if (flex is null) MarkupFlexAttributes.RejectItem(el, warnings);
        void BindColorSource(string? expression, MarkupBindingSource model, Action<Vector4> setValue,
            Action<Func<Vector4>> setSource)
        {
            if (expression?.StartsWith("theme:", StringComparison.Ordinal) == true)
            {
                string token = expression[6..].Split('|')[0];
                _ = PluginUiPalette.Moss.Token(token);
                Vector4 classic = Color(expression);
                Vector4 Read() => themedPanel is not null && themes?.Palette is { } p ? p.Token(token) : classic;
                setValue(Read()); setSource(Read);
            }
            else MarkupDocument.BindColorSource(expression, model, setValue, setSource);
        }
        MarkupBuildContext Context() => new(
            resolve, datFont, icons,
            (expression, setValue, setSource) => BindColorSource(expression, binding, setValue, setSource),
            controlState,
            (container, child) => AddElement(
                container, child, binding, resolve, datFont, icons, themedPanel, themes, controlState, describeKey, textures, recentColors,
                warnings),
            describeKey, textures, recentColors);
        switch (el.Name.LocalName)
        {
            case "group":
                UiFlexBox? groupFlex = null;
                UiPanel group;
                var (scrollX, scrollY) = MarkupFlexAttributes.Scroll(el, warnings);
                bool scrolls = scrollX || scrollY;
                if (MarkupFlexAttributes.IsContainer(el, warnings))
                {
                    var flexGroup = new UiFlexGroup();
                    MarkupFlexAttributes.ReadContainer(el, flexGroup.Flex.Node);
                    if (scrolls) flexGroup.UseScroll(scrollX, scrollY, resolve);
                    groupFlex = flexGroup.Flex;
                    group = flexGroup;
                }
                else
                {
                    MarkupFlexAttributes.RejectContainer(el, warnings);
                    group = scrolls ? new UiScrollPanel(scrollX, scrollY, resolve) : new UiPanel();
                }
                if (themedPanel is not null && group is IUiScrollHost { ScrollArea: { } groupScroll })
                    groupScroll.RegisterTheme(themedPanel);
                group.Left = F(el, "x");
                group.Top = F(el, "y");
                group.Width = F(el, "w");
                group.Height = F(el, "h");
                group.BackgroundColor = el.Attribute("background") is null
                    ? Vector4.Zero
                    : Color((string?)el.Attribute("background"));
                group.BorderColor = el.Attribute("border") is null
                    ? Vector4.Zero
                    : Color((string?)el.Attribute("border"));
                group.BorderThickness = el.Attribute("border") is null ? 0f : 1f;
                // A scrolling group is hit-testable so the wheel reaches it over empty space.
                group.ClickThrough = !scrolls;
                BindColorSource(
                    (string?)el.Attribute("background"), binding,
                    value => group.BackgroundColor = value,
                    source => group.BackgroundColorSource = source);
                BindColorSource(
                    (string?)el.Attribute("border"), binding,
                    value => group.BorderColor = value,
                    source => group.BorderColorSource = source);
                ApplyCommon(group, el, binding);
                parent.AddChild(group);
                foreach (XElement child in el.Elements())
                    AddElement(group, child, binding, resolve, datFont, icons, themedPanel, themes,
                        controlState, describeKey, textures, recentColors, warnings, groupFlex);
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
                    BindColorSource(
                        (string?)el.Attribute("color"), binding,
                        value => meter.BarColor = value,
                        source => meter.BarColorSource = source);
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
                    BindColorSource(
                        (string?)el.Attribute("color"), binding,
                        value => label.TextColor = value,
                        source => label.TextColorSource = source);
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
                            + $"Action property on {binding.TypeName}");
                    }
                    var button = new UiSimpleButton
                    {
                        AcceptsFocus = true,
                        FocusOnMouseClick = false,
                        TabStop = true,
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
                    BindColorSource(
                        (string?)el.Attribute("color"), binding,
                        value => button.TextColor = value,
                        source => button.TextColorSource = source);
                    BindColorSource(
                        (string?)el.Attribute("background"), binding,
                        value => button.BackgroundColor = value,
                        source => button.BackgroundColorSource = source);
                    BindColorSource(
                        (string?)el.Attribute("border"), binding,
                        value => button.BorderColor = value,
                        source => button.BorderColorSource = source);
                    ApplyButtonIcon(button, el, binding, icons, "button icon");
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
                            "iconkind applies to button, tab and list; icon derives its kind from did/spell/item/file");
                    }

                    string? didAttr = (string?)el.Attribute("did");
                    string? spellAttr = (string?)el.Attribute("spell");
                    string? itemAttr = (string?)el.Attribute("item");
                    string? fileAttr = (string?)el.Attribute("file");
                    int sourceCount = (didAttr is not null ? 1 : 0)
                        + (spellAttr is not null ? 1 : 0)
                        + (itemAttr is not null ? 1 : 0)
                        + (fileAttr is not null ? 1 : 0);
                    if (sourceCount != 1)
                    {
                        throw new FormatException(
                            "<icon> requires exactly one of did/spell/item/file");
                    }

                    Func<(uint tex, int w, int h)> iconSource;
                    if (fileAttr is not null)
                    {
                        iconSource = BuildFileIconSource(
                            BindPathLiteralOrBinding(fileAttr, binding, "icon file"), icons);
                    }
                    else
                    {
                        string iconKind = didAttr is not null ? "did"
                            : spellAttr is not null ? "spell"
                            : "item";
                        string iconExpression = didAttr ?? spellAttr ?? itemAttr!;
                        Func<uint> iconReader = BindUintLiteralOrBinding(
                            iconExpression, binding, $"icon {iconKind}");
                        iconSource = BuildIconSource(iconKind, iconReader, icons);
                    }
                    var icon = new UiMarkupIcon
                    {
                        Left = F(el, "x"),
                        Top = F(el, "y"),
                        Width = FOr(el, "w", 32f),
                        Height = FOr(el, "h", 32f),
                        IconSource = iconSource,
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
                        + $"Action property on {binding.TypeName}");
                }

                var tab = new UiMarkupTabButton
                {
                    AcceptsFocus = true,
                    FocusOnMouseClick = false,
                    TabStop = true,
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
                ApplyButtonIcon(tab, el, binding, icons, "tab icon");
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
                        + $"Action property on {binding.TypeName}");
                }

                string? toggleCaption = (string?)el.Attribute("text");
                var toggle = new UiMarkupToggle
                {
                    AcceptsFocus = true,
                    FocusOnMouseClick = false,
                    TabStop = true,
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
                BindColorSource(
                    (string?)el.Attribute("color"), binding,
                    value => toggle.TextColor = value,
                    source => toggle.TextColorSource = source);
                ApplyCommon(toggle, el, binding);
                parent.AddChild(toggle);
                break;

            case "checkbox":
                parent.AddChild(MarkupCheckbox.Build(el, binding, Context()));
                break;

            case "number":
                parent.AddChild(MarkupNumber.Build(el, binding, Context()));
                break;

            case "color":
                parent.AddChild(MarkupColor.Build(el, binding, Context()));
                break;

            case "hotkey":
                parent.AddChild(MarkupHotkey.Build(el, binding, Context()));
                break;

            case "radio":
                parent.AddChild(MarkupRadio.Build(el, binding, Context()));
                break;

            case "separator":
                parent.AddChild(MarkupSeparator.Build(el, binding, Context()));
                break;

            case "expander":
                parent.AddChild(MarkupExpander.Build(el, binding, Context()));
                break;

            case "split":
                parent.AddChild(MarkupSplit.Build(el, binding, Context()));
                break;

            case "slider":
                string? changeName = (string?)el.Attribute("onchange");
                Action<float>? changed = BindFloatAction(changeName, binding);
                if (changeName is not null && changed is null)
                {
                    throw new FormatException(
                        $"<slider onchange=\"{changeName}\"> did not resolve to an "
                        + $"Action<float> property on {binding.TypeName}");
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
                        + $"Action<string> property on {binding.TypeName}");
                }
                string? submitName = (string?)el.Attribute("onsubmit");
                Action<string>? submitted = BindStringAction(submitName, binding);
                if (submitName is not null && submitted is null)
                {
                    throw new FormatException(
                        $"<field onsubmit=\"{submitName}\"> did not resolve to an "
                        + $"Action<string> property on {binding.TypeName}");
                }

                string? blurName = (string?)el.Attribute("onblur");
                Action<string>? blurred = BindStringAction(blurName, binding);
                if (blurName is not null && blurred is null)
                {
                    throw new FormatException(
                        $"<field onblur=\"{blurName}\"> did not resolve to an "
                        + $"Action<string> property on {binding.TypeName}");
                }

                string? upName = (string?)el.Attribute("onup");
                Action? onUp = BindAction(upName, binding);
                if (upName is not null && onUp is null)
                    throw new FormatException(
                        $"<field onup=\"{upName}\"> did not resolve to an "
                        + $"Action property on {binding.TypeName}");

                string? downName = (string?)el.Attribute("ondown");
                Action? onDown = BindAction(downName, binding);
                if (downName is not null && onDown is null)
                    throw new FormatException(
                        $"<field ondown=\"{downName}\"> did not resolve to an "
                        + $"Action property on {binding.TypeName}");

                var field = new UiField
                {
                    OneLine = B(el, "oneline", true),
                    Editable = B(el, "editable", true),
                    Selectable = true,
                    TabStop = true,
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
                    OnSubmit = submitted,
                    OnUp = onUp,
                    OnDown = onDown,
                };
                // onsubmit has already handled the text an Enter submit leaves with.
                if (blurred is not null)
                {
                    field.OnFocusLost = text =>
                    {
                        if (!field.LosingFocusToSubmit) blurred(text);
                    };
                }
                BindColorSource(
                    (string?)el.Attribute("background"), binding,
                    value => field.BackgroundColor = value,
                    source => field.BackgroundColorSource = source);
                BindColorSource(
                    (string?)el.Attribute("color"), binding,
                    value => field.TextColor = value,
                    source => field.TextColorSource = source);
                Func<string?> fieldText = BindString(
                    (string?)el.Attribute("text"), binding);
                // The owner hears about what is typed, not about its own
                // value being put on show.
                field.SetText(fieldText());
                field.BoundText = () => fieldText() ?? string.Empty;
                field.OnTextChanged = fieldChanged;
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
                        + $"Action<string> property on {binding.TypeName}");
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
                    Searchable = B(el, "searchable", false),
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
                    FitsLongLabels = true,
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

            case "log":
                var log = new UiMarkupLog(resolve)
                {
                    Left = F(el, "x"), Top = F(el, "y"), Width = F(el, "w"), Height = F(el, "h"),
                    DatFont = datFont,
                    ItemsSource = BindStringList((string?)el.Attribute("items"), binding, "log items", preserveSnapshot: true),
                    FirstIndexSource = el.Attribute("firstindex") is null ? static () => 0
                        : BindRequiredIntReader((string?)el.Attribute("firstindex"), binding, "log firstindex"),
                };
                ApplyCommon(log, el, binding);
                parent.AddChild(log);
                break;

            case "list":
                var listChildren = el.Elements()
                    .Where(static child => child.Name.LocalName != "contextmenu")
                    .ToList();
                foreach (var child in listChildren)
                {
                    if (child.Name.LocalName != "column")
                    {
                        throw new FormatException(
                            $"<list> children must all be <column>, got <{child.Name.LocalName}>");
                    }
                    if (child.Element("contextmenu") is not null)
                        throw new FormatException("<contextmenu> goes on the <list>, not on a <column>");
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
                };
                MarkupListSelect.Apply(el, list, binding);

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
                        if (ValidateIconKind(listIconKind) == "file")
                        {
                            Func<IReadOnlyList<string>> listPathsReader =
                                BindStringList(listIcons, binding, "list icons");
                            if (icons is not null)
                            {
                                var fileIds = new FileIconIds(icons);
                                list.IconIdsSource = fileIds.Ids(listPathsReader);
                                list.IconResolve = fileIds.Resolve;
                            }
                        }
                        else
                        {
                            Func<IReadOnlyList<uint>> listIconIdsReader =
                                BindUintList(listIcons, binding, "list icons");
                            if (icons is not null)
                            {
                                list.IconIdsSource = listIconIdsReader;
                                list.IconResolve = BuildRowIconResolve(listIconKind, icons);
                            }
                        }
                    }
                }
                ApplyCommon(list, el, binding);
                if (listUsesColumns)
                    MarkupListHeader.Apply(el, listChildren, list, binding, Context());
                else
                    MarkupListHeader.RejectWithoutColumns(el);
                MarkupListTree.Apply(el, list, binding, Context());
                parent.AddChild(list);
                break;

            case "repeat":
                MarkupRepeat.Build(parent, el, binding, flex,
                    new MarkupRepeat.BuildArgs(resolve, datFont, icons, themedPanel, themes, controlState,
                        describeKey, textures, recentColors),
                    (container, child, source, state, box) => AddElement(container, child, source, resolve, datFont,
                        icons, themedPanel, themes, state, describeKey, textures, recentColors, warnings, box));
                break;

            case "contextmenu":
                // Built onto its owner element after the owner's own case; see the tail below.
                break;

            default:
                throw new FormatException($"unknown element <{el.Name.LocalName}>");
        }
        // A repeat's marker takes no theme, menu or flex item; its copies took their own.
        if (parent.Children.Count > firstChild && parent.Children[firstChild] is not UiMarkupRepeat)
        {
            UiElement built = parent.Children[firstChild];
            if (themedPanel is not null)
                PluginMarkupTheme.Register(themedPanel, built, el);
            if (MarkupContextMenu.Attach(el, built, binding, Context()) is { } contextMenu && themedPanel is not null)
                PluginMarkupTheme.Register(themedPanel, contextMenu, el.Element("contextmenu")!);
            if (flex is not null)
                AddFlexItem(flex, built, el);
        }
    }

    /// <summary>
    /// Enters <paramref name="element"/>, just built from <paramref name="el"/>
    /// under a flex container, into that container's layout: a flex group as an
    /// inner node, an absolute group as a leaf of its authored size, any other
    /// control as a leaf measured by <see cref="MarkupContentSize"/>. The
    /// container places it, so it keeps no anchors.
    /// </summary>
    private static void AddFlexItem(UiFlexBox flex, UiElement element, XElement el)
    {
        UiFlexItem item = element is UiFlexGroup group
            ? flex.AddContainer(group, group.Flex)
            : flex.AddLeaf(element, new FlexNode(),
                el.Name.LocalName == "group" ? AbsoluteGroupSize(element) : null);
        MarkupFlexAttributes.ReadItem(el, item.Node);
        if (element is UiMarkupExpander expander)
        {
            // Its h is the expanded height, given only while it is expanded;
            // collapsed it measures as its header (MarkupContentSize).
            expander.FlexPlaced = true;
            expander.ExpandedHeight = item.Node.Height ?? expander.Height;
            item.Node.Height = null;
            item.ExpandedMaxHeight = item.Node.MaxHeight;
            item.ExpandedMinHeight = item.Node.MinHeight;
            // Without a w, start at the content width, so its content's anchors
            // are captured against a real box rather than a 0-wide one.
            if (expander.Width <= 0f)
            {
                expander.Width = MarkupContentSize.ListWidth;
                expander.LayoutParts();
            }
        }
        if (element is UiMarkupSplit split && (split.Width <= 0f || split.Height <= 0f))
        {
            // Without a w or h, start at the content size, so the panes' children
            // capture their anchors against real panes rather than 0-sized ones.
            if (split.Width <= 0f) split.Width = MarkupContentSize.ListWidth;
            if (split.Height <= 0f) split.Height = MarkupContentSize.ListHeight;
            if (el.Attribute("at") is null)
            {
                float room = (split.Horizontal ? split.Width : split.Height) - UiMarkupSplit.BarSize;
                split.DefaultPosition = MathF.Floor(MathF.Max(0f, room) / 2f);
            }
            split.LayoutAtDefault();
        }
        element.Anchors = AnchorEdges.None;
        if (element is UiLabel label) label.SizedByLayout = true;
        if (element is UiMenu menu)
        {
            // A flex menu has no authored w (the container sizes it), so its
            // popup column would stay at the 20-point minimum; match the popup
            // to the laid-out menu on every open, as an absolute menu's is.
            Action? refresh = menu.BeforeOpen;
            menu.BeforeOpen = () =>
            {
                refresh?.Invoke();
                menu.ColumnWidth = MathF.Max(20f, menu.Width);
            };
        }
    }

    /// <summary>
    /// An absolute group's content size in a flex container: its authored
    /// size, which is also its minimum, except along an axis it scrolls, where
    /// it can shrink to a 40-point viewport (plus the other axis's bar).
    /// </summary>
    private static FlexMeasurement AbsoluteGroupSize(UiElement group)
    {
        var size = new FlexSize(group.Width, group.Height);
        if (group is not UiScrollPanel { ScrollArea: var area }) return new FlexMeasurement(size, size);
        float minW = area.ScrollsX ? FlexLayout.MinimumScrollViewport + (area.ScrollsY ? UiScrollArea.BarSize : 0f) : size.Width;
        float minH = area.ScrollsY ? FlexLayout.MinimumScrollViewport + (area.ScrollsX ? UiScrollArea.BarSize : 0f) : size.Height;
        return new FlexMeasurement(size, new FlexSize(MathF.Min(minW, size.Width), MathF.Min(minH, size.Height)));
    }

    /// <summary>
    /// Reads a button-like control's optional icon/iconkind pair. Both are
    /// validated at build even with no resolver wired, so a typo fails early.
    /// </summary>
    private static void ApplyButtonIcon(
        UiSimpleButton button, XElement el, MarkupBindingSource binding,
        IMarkupIconResolver? icons, string context)
    {
        string? iconExpression = (string?)el.Attribute("icon");
        if (iconExpression is null)
            return;
        string? iconKind = (string?)el.Attribute("iconkind");
        if (ValidateIconKind(iconKind) == "file")
        {
            Func<string?> pathReader = BindPathLiteralOrBinding(iconExpression, binding, context);
            if (icons is not null)
                button.IconSource = BuildFileIconSource(pathReader, icons);
            return;
        }

        Func<uint> iconReader = BindUintLiteralOrBinding(iconExpression, binding, context);
        if (icons is not null)
            button.IconSource = BuildIconSource(iconKind, iconReader, icons);
    }

    private static Func<(uint tex, int w, int h)> BuildFileIconSource(
        Func<string?> pathReader, IMarkupIconResolver? icons)
    {
        if (icons is null)
            return static () => (0u, 0, 0);
        return () => pathReader() is { } path && !string.IsNullOrWhiteSpace(path)
            ? icons.ResolveFile(path)
            : (0u, 0, 0);
    }

    /// <summary>
    /// A file path written literally or bound to a string property. Like every
    /// other binding, a missing property or one of the wrong type throws at
    /// build; a null or blank path at draw time draws nothing.
    /// </summary>
    private static Func<string?> BindPathLiteralOrBinding(
        string expression, MarkupBindingSource binding, string context)
    {
        if (!IsBinding(expression))
            return () => expression;
        PropertyInfo? property = binding.Property(expression[1..^1]);
        if (property is null || property.PropertyType != typeof(string))
        {
            throw new FormatException(
                $"{expression} did not resolve to a string property on "
                + binding.TypeName + $" ({context})");
        }
        return () => (string?)binding.Read(property);
    }

    /// <summary>
    /// Lists and columns carry icons as numbers, 0 meaning none. For file icons
    /// each distinct path gets its own number here, so the list's drawing stays
    /// the same for every kind of icon; a null or blank path is 0.
    /// </summary>
    private sealed class FileIconIds(IMarkupIconResolver? icons)
    {
        private const int MaximumPaths = 4096;
        private readonly Dictionary<string, uint> _ids = new(StringComparer.Ordinal);
        private readonly List<string> _paths = [];

        internal Func<IReadOnlyList<uint>> Ids(Func<IReadOnlyList<string>> paths) => () =>
        {
            IReadOnlyList<string> current = paths();
            // Forget every path at once rather than mid-list, so no number in
            // one answer can point at a path a later row replaced.
            if (_paths.Count + current.Count > MaximumPaths)
            {
                _ids.Clear();
                _paths.Clear();
            }
            var ids = new uint[current.Count];
            for (int i = 0; i < ids.Length; i++)
                ids[i] = IdFor(current[i]);
            return ids;
        };

        internal (uint tex, int w, int h) Resolve(uint id) =>
            icons is not null && id != 0u && id <= _paths.Count
                ? icons.ResolveFile(_paths[(int)id - 1])
                : (0u, 0, 0);

        private uint IdFor(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return 0u;
            if (!_ids.TryGetValue(path, out uint id))
            {
                _paths.Add(path);
                id = (uint)_paths.Count;
                _ids[path] = id;
            }
            return id;
        }
    }

    private static string ValidateIconKind(string? iconKind, string context = "iconkind") =>
        (iconKind ?? "did") switch
        {
            "did" or "spell" or "item" or "file" => iconKind ?? "did",
            var other => throw new FormatException(
                $"{context} must be did, spell, item, or file (got \"{other}\")"),
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
        string expression, MarkupBindingSource binding, string context)
    {
        if (!IsBinding(expression))
        {
            uint literal = ParseUintLiteral(expression, context);
            return () => literal;
        }
        PropertyInfo? property = binding.Property(expression[1..^1]);
        if (property is null)
        {
            throw new FormatException(
                $"{expression} did not resolve to a property on "
                + binding.TypeName + $" ({context})");
        }
        return () => binding.Read(property) switch
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

    internal static Func<string?> BindString(string? attribute, MarkupBindingSource binding)
    {
        if (attribute is null)
            return static () => null;
        if (!IsBinding(attribute))
            return () => attribute;

        string name = attribute[1..^1];
        PropertyInfo? property = binding.Property(name);
        if (property is null)
            return () => attribute;
        return () => binding.Read(property)?.ToString();
    }

    private static Action? BindAction(string? attribute, MarkupBindingSource binding)
    {
        if (attribute is null || !IsBinding(attribute))
            return null;

        string name = attribute[1..^1];
        PropertyInfo? property = binding.Property(name);
        if (property is null || !typeof(Action).IsAssignableFrom(property.PropertyType))
            return null;

        return () => (binding.Read(property) as Action)?.Invoke();
    }

    private static Action<float>? BindFloatAction(
        string? attribute,
        MarkupBindingSource binding)
    {
        if (attribute is null || !IsBinding(attribute))
            return null;

        string name = attribute[1..^1];
        PropertyInfo? property = binding.Property(name);
        if (property is null
            || !typeof(Action<float>).IsAssignableFrom(property.PropertyType))
            return null;
        return value => (binding.Read(property) as Action<float>)?.Invoke(value);
    }

    private static Action<string>? BindStringAction(
        string? attribute,
        MarkupBindingSource binding)
    {
        if (attribute is null || !IsBinding(attribute))
            return null;

        string name = attribute[1..^1];
        PropertyInfo? property = binding.Property(name);
        if (property is null
            || !typeof(Action<string>).IsAssignableFrom(property.PropertyType))
        {
            return null;
        }
        return value => (binding.Read(property) as Action<string>)?.Invoke(value);
    }

    internal static Action<int>? BindIntAction(string? attribute, MarkupBindingSource binding)
    {
        if (attribute is null || !IsBinding(attribute))
            return null;
        PropertyInfo? property = binding.Property(attribute[1..^1]);
        if (property is null
            || !typeof(Action<int>).IsAssignableFrom(property.PropertyType))
        {
            return null;
        }
        return value => (binding.Read(property) as Action<int>)?.Invoke(value);
    }

    internal static Func<IReadOnlyList<string>> BindStringList(
        string? expression,
        MarkupBindingSource binding,
        string context, bool preserveSnapshot = false)
    {
        if (string.IsNullOrWhiteSpace(expression) || !IsBinding(expression))
            throw new FormatException($"{context} must be a string-list binding");
        PropertyInfo? property = binding.Property(expression[1..^1]);
        if (property is null
            || !typeof(IEnumerable<string>).IsAssignableFrom(property.PropertyType))
        {
            throw new FormatException(
                $"{expression} did not resolve to an IEnumerable<string> property on "
                + binding.TypeName);
        }
        return () => binding.Read(property) is IEnumerable<string> values
            ? preserveSnapshot && values is IReadOnlyList<string> snapshot ? snapshot : values.ToArray()
            : Array.Empty<string>();
    }

    private static Func<IReadOnlyList<uint>> BindUintList(
        string? expression,
        MarkupBindingSource binding,
        string context)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return static () => Array.Empty<uint>();
        if (!IsBinding(expression))
            throw new FormatException($"{context} must be a uint-list binding");
        PropertyInfo? property = binding.Property(expression[1..^1]);
        if (property is null)
        {
            throw new FormatException(
                $"{expression} did not resolve to an IEnumerable<uint> or "
                + "IEnumerable<int> property on " + binding.TypeName);
        }
        if (typeof(IEnumerable<uint>).IsAssignableFrom(property.PropertyType))
        {
            return () => binding.Read(property) is IEnumerable<uint> values
                ? values.ToArray()
                : Array.Empty<uint>();
        }
        if (typeof(IEnumerable<int>).IsAssignableFrom(property.PropertyType))
        {
            return () => binding.Read(property) is IEnumerable<int> values
                ? values.Select(static v => v < 0 ? 0u : (uint)v).ToArray()
                : Array.Empty<uint>();
        }
        throw new FormatException(
            $"{expression} did not resolve to an IEnumerable<uint> or "
            + "IEnumerable<int> property on " + binding.TypeName);
    }

    internal static Func<IReadOnlyList<bool>> BindBoolList(
        string? expression, MarkupBindingSource binding, string context)
    {
        if (string.IsNullOrWhiteSpace(expression) || !IsBinding(expression))
            throw new FormatException($"{context} must be a bool-list binding");
        PropertyInfo? property = binding.Property(expression[1..^1]);
        if (property is null
            || !typeof(IEnumerable<bool>).IsAssignableFrom(property.PropertyType))
        {
            throw new FormatException(
                $"{expression} did not resolve to an IEnumerable<bool> property on "
                + binding.TypeName + $" ({context})");
        }
        return () => binding.Read(property) is IEnumerable<bool> values
            ? values.ToArray()
            : Array.Empty<bool>();
    }

    private static Func<IReadOnlyList<uint>> BindRequiredUintList(
        string? expression, MarkupBindingSource binding, string context)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new FormatException($"{context} must be a uint-list binding");
        return BindUintList(expression, binding, context);
    }

    internal static Action<int> BindRequiredIntAction(
        string? attribute, MarkupBindingSource binding, string context)
    {
        if (attribute is null || !IsBinding(attribute))
            throw new FormatException($"{context} must be an Action<int> binding");
        PropertyInfo? property = binding.Property(attribute[1..^1]);
        if (property is null || !typeof(Action<int>).IsAssignableFrom(property.PropertyType))
        {
            throw new FormatException(
                $"{attribute} did not resolve to an Action<int> property on "
                + binding.TypeName + $" ({context})");
        }
        return value => (binding.Read(property) as Action<int>)?.Invoke(value);
    }

    private static UiMarkupListColumn BuildListColumn(
        XElement columnEl, MarkupBindingSource binding, IMarkupIconResolver? icons, int index, bool isLast)
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
                        + $"Action<int> property on {binding.TypeName}");
                }
                return UiMarkupListColumn.Text(width, textSource, colorsSource, textOnClick, isAutoWidth);
            }
            case "check":
            case "checkbox":
            {
                var checkSource = BindBoolList(
                    (string?)columnEl.Attribute("values"), binding, ColumnContext(index, type, "values"));
                var onChange = BindRequiredIntAction(
                    (string?)columnEl.Attribute("onchange"), binding, ColumnContext(index, type, "onchange"));
                return UiMarkupListColumn.Check(width, checkSource, onChange, isAutoWidth, boxed: type == "checkbox");
            }
            case "icon":
            {
                string? iconKind = (string?)columnEl.Attribute("iconkind");
                bool fileKind = ValidateIconKind(iconKind, ColumnContext(index, "icon", "iconkind")) == "file";
                Func<IReadOnlyList<uint>> valuesSource;
                Func<uint, (uint, int, int)>? resolve = null;
                if (fileKind)
                {
                    string? valuesAttr = (string?)columnEl.Attribute("values");
                    if (string.IsNullOrWhiteSpace(valuesAttr))
                        throw new FormatException($"{ColumnContext(index, "icon", "values")} is required");
                    Func<IReadOnlyList<string>> paths =
                        BindStringList(valuesAttr, binding, ColumnContext(index, "icon", "values"));
                    var fileIds = new FileIconIds(icons);
                    valuesSource = fileIds.Ids(paths);
                    if (icons is not null) resolve = fileIds.Resolve;
                }
                else
                {
                    valuesSource = BindRequiredUintList(
                        (string?)columnEl.Attribute("values"), binding, ColumnContext(index, "icon", "values"));
                    if (icons is not null) resolve = BuildRowIconResolve(iconKind, icons);
                }
                var onClick = BindRequiredIntAction(
                    (string?)columnEl.Attribute("onclick"), binding, ColumnContext(index, "icon", "onclick"));
                return UiMarkupListColumn.Icon(width, valuesSource, resolve, onClick, isAutoWidth);
            }
            case "button":
            {
                var captionSource = BindStringList(
                    (string?)columnEl.Attribute("items"), binding, ColumnContext(index, "button", "items"));
                var onClick = BindRequiredIntAction(
                    (string?)columnEl.Attribute("onclick"), binding, ColumnContext(index, "button", "onclick"));
                return UiMarkupListColumn.Button(width, captionSource, onClick, isAutoWidth,
                    ButtonSize(columnEl, "buttonw", index), ButtonSize(columnEl, "buttonh", index));
            }
            default:
                throw new FormatException(
                    $"column[{index}] has unknown type=\"{type}\" (expected text, check, checkbox, icon, or button)");
        }
    }

    private static float ButtonSize(XElement columnEl, string attribute, int index)
    {
        float size = FOr(columnEl, attribute, 0f);
        if (size < 0f)
            throw new FormatException($"{ColumnContext(index, "button", attribute)} must not be negative");
        return size;
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

    internal static bool IsBinding(string value) =>
        value.Length > 2 && value[0] == '{' && value[^1] == '}';

    internal static void ApplyCommon(
        UiElement element,
        XElement source,
        MarkupBindingSource binding)
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
        MarkupBindingSource binding,
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

        PropertyInfo? property = binding.Property(expression[1..^1]);
        if (property is null || property.PropertyType != typeof(bool))
        {
            throw new FormatException(
                $"{expression} did not resolve to a bool property on "
                + binding.TypeName);
        }
        Func<bool> source = () => binding.Read(property) is true;
        setSource(source);

        // Ticks apply the source, but the window's first measurement comes
        // before any tick: seed the value it reports now so that measurement
        // leaves out what is bound hidden (a tabbed window's other pages). A
        // getter that throws this early leaves the default; the first tick
        // reads it again as usual.
        bool seeded;
        try { seeded = source(); }
        catch (TargetInvocationException) { return; }
        setLiteral(seeded);
    }

    internal static Func<bool> BindRequiredBoolReader(
        string? expression,
        MarkupBindingSource binding,
        string context)
    {
        if (string.IsNullOrWhiteSpace(expression) || !IsBinding(expression))
            throw new FormatException($"{context} must be a bool binding");

        PropertyInfo? property = binding.Property(expression[1..^1]);
        if (property is null || property.PropertyType != typeof(bool))
        {
            throw new FormatException(
                $"{expression} did not resolve to a bool property on "
                + binding.TypeName);
        }
        return () => binding.Read(property) is true;
    }

    internal static Func<int> BindRequiredIntReader(
        string? expression,
        MarkupBindingSource binding,
        string context)
    {
        if (string.IsNullOrWhiteSpace(expression) || !IsBinding(expression))
            throw new FormatException($"{context} must be an int binding");
        PropertyInfo? property = binding.Property(expression[1..^1]);
        if (property is null || property.PropertyType != typeof(int))
        {
            throw new FormatException(
                $"{expression} did not resolve to an int property on "
                + binding.TypeName);
        }
        return () => binding.Read(property) is int value ? value : -1;
    }

    internal static float F(XElement e, string attr)
        => float.TryParse((string?)e.Attribute(attr), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var v) ? v : 0f;

    internal static float FOr(XElement e, string attr, float fallback)
        => float.TryParse((string?)e.Attribute(attr), NumberStyles.Float,
            CultureInfo.InvariantCulture, out float value) ? value : fallback;

    private static int I(XElement e, string attr, int fallback)
        => int.TryParse((string?)e.Attribute(attr), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int value) ? value : fallback;

    internal static bool B(XElement e, string attr, bool fallback)
        => bool.TryParse((string?)e.Attribute(attr), out bool value)
            ? value
            : fallback;

    internal static Vector4 Color(string? hex)
    {
        if (hex?.StartsWith("theme:", StringComparison.Ordinal) == true)
            hex = hex.Contains('|') ? hex[(hex.IndexOf('|') + 1)..] : null;
        if (hex is { Length: 9 } && hex[0] == '#'
            && uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber,
                             CultureInfo.InvariantCulture, out uint argb))
            return Argb(argb);
        return Vector4.One;
    }

    /// <summary>Unpacks 0xAARRGGBB, the byte order of the #AARRGGBB literal.</summary>
    private static Vector4 Argb(uint packed) => new(
        ((packed >> 16) & 0xFFu) / 255f,
        ((packed >> 8)  & 0xFFu) / 255f,
        (packed         & 0xFFu) / 255f,
        ((packed >> 24) & 0xFFu) / 255f);

    /// <summary>
    /// A colour attribute may name a binding instead of a literal, so a
    /// plugin whose settings include colours can show them rather than only
    /// store them. The literal has already been applied by the element's own
    /// initializer when this runs; this only takes over when the attribute is
    /// a <c>{Binding}</c> that resolves, and then it both seeds the element's
    /// colour (so the first frame is right) and installs the per-frame source
    /// the element draws with.
    /// <para>
    /// An unresolved binding is silent, exactly as an unresolved <c>text</c>
    /// binding is: nothing is installed, and the colour stays on whatever the
    /// literal parser made of the attribute text -- opaque white for a brace
    /// expression -- which is what an unknown colour binding already did.
    /// </para>
    /// </summary>
    private static void BindColorSource(
        string? expression,
        MarkupBindingSource binding,
        Action<Vector4> setValue,
        Action<Func<Vector4>> setSource)
    {
        if (expression is null || !IsBinding(expression))
            return;
        PropertyInfo? property = binding.Property(expression[1..^1]);
        if (property is null)
            return;

        Vector4 Read() => ColorFromBoundValue(binding.Read(property));
        setValue(Read());
        setSource(Read);
    }

    /// <summary>
    /// The shapes a bound colour may arrive in: a 32-bit 0xAARRGGBB value,
    /// signed or unsigned, which is how a plugin stores a colour setting; or
    /// the same #AARRGGBB text the literal attribute takes. Anything else --
    /// another type, or text the literal parser rejects -- lands on the
    /// literal parser's own fallback rather than throwing at a player.
    /// </summary>
    private static Vector4 ColorFromBoundValue(object? value) => value switch
    {
        uint packed => Argb(packed),
        int packed => Argb(unchecked((uint)packed)),
        string text => Color(text),
        _ => Vector4.One,
    };

    private static Func<float?> BindFloat(string? expr, MarkupBindingSource binding)
    {
        var pi = Prop(expr, binding);
        if (pi is null) return () => 0f;
        return () => binding.Read(pi) switch
        {
            float f  => f,
            null     => (float?)null,
            var v    => Convert.ToSingle(v, CultureInfo.InvariantCulture),
        };
    }

    private static Func<uint?> BindUint(string? expr, MarkupBindingSource binding)
    {
        var pi = Prop(expr, binding);
        if (pi is null) return () => null;
        return () => binding.Read(pi) switch
        {
            uint u => u,
            null   => (uint?)null,
            var v  => Convert.ToUInt32(v, CultureInfo.InvariantCulture),
        };
    }

    private static PropertyInfo? Prop(string? expr, MarkupBindingSource binding)
    {
        if (expr is null || expr.Length < 3 || expr[0] != '{' || expr[^1] != '}') return null;
        return binding.Property(expr[1..^1]);
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
        // No attribute at all is the default corner. An attribute that IS
        // there names edges: anchor="", anchor=" " and anchor="," are all an
        // author who meant something and wrote nothing, and they are told so
        // below rather than silently given the default.
        if (tokens is null)
            return AnchorEdges.Left | AnchorEdges.Top;

        var edges = AnchorEdges.None;
        // An author writes either "right bottom" or "right,bottom"; both read
        // the same aloud, so a comma separates exactly as a space does.
        foreach (string token in tokens.Split(
            AnchorSeparators, System.StringSplitOptions.RemoveEmptyEntries))
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
        if (edges == AnchorEdges.None)
        {
            // Empty or separators only, e.g. anchor="" or anchor=",".
            // Silently leaving the element at the default would look like
            // the attribute had worked.
            throw new FormatException(
                $"{ElementIdentity(source)} anchor=\"{tokens}\" names no edge "
                + "(expected a comma- or space-separated subset of left, top, "
                + "right, bottom)");
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
