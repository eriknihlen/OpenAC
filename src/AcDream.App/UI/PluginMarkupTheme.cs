using System.Xml.Linq;
namespace AcDream.App.UI;

internal static class PluginMarkupTheme
{
    public static void Register(UiPluginMarkupPanel panel, UiElement element, XElement xml)
    {
        if (panel.ModernFont is { } modern)
        {
            void Bind(Func<UiDatFont?> get, Action<UiDatFont?> set)
            {
                UiDatFont? classic = get();
                panel.AddThemeAction(p => set(p is null ? classic : modern));
            }
            switch (element)
            {
                case UiLabel label: Bind(() => label.DatFont, f => label.DatFont = f); break;
                case UiSimpleButton button: Bind(() => button.DatFont, f => button.DatFont = f); break;
                case UiMarkupToggle toggle: Bind(() => toggle.DatFont, f => toggle.DatFont = f); break;
                case UiField field: Bind(() => field.DatFont, f => field.DatFont = f); break;
                case UiMenu menu:
                    Bind(() => menu.DatFont, f => menu.DatFont = f);
                    Bind(() => menu.ButtonDatFont, f => menu.ButtonDatFont = f);
                    break;
                case UiMarkupList list: Bind(() => list.DatFont, f => list.DatFont = f); break;
            }
        }
        bool Default(string name) => xml.Attribute(name) is null;
        switch (element)
        {
            case UiLabel label:
                var labelColor = label.TextColor;
                var labelOutline = label.Outline;
                panel.AddThemeAction(p => { label.Outline = p is null && labelOutline;
                    if (Default("color")) label.TextColor = p?.Text ?? labelColor; });
                break;
            case UiMarkupTabButton tab:
                panel.AddThemeAction(p => { tab.ThemePalette = p; tab.Outline = p is null; });
                break;
            case UiSimpleButton button:
                var bg = button.BackgroundColor; var border = button.BorderColor;
                var text = button.TextColor; var outline = button.Outline;
                panel.AddThemeAction(p => {
                    button.Outline = p is null && outline;
                    if (Default("background")) button.BackgroundColor = p?.Field ?? bg;
                    if (Default("border")) button.BorderColor = p?.Border ?? border;
                    if (Default("color")) button.TextColor = p?.Text ?? text;
                });
                break;
            case UiMarkupToggle toggle:
                var toggleText = toggle.TextColor;
                panel.AddThemeAction(p => { toggle.ThemePalette = p;
                    if (Default("color")) toggle.TextColor = p?.Text ?? toggleText; });
                break;
            case UiField field:
                var fieldBg = field.BackgroundColor; var fieldText = field.TextColor;
                var fieldOutline = field.Outline; var sprite = field.BackgroundSprite;
                var focus = field.FocusFieldSprite; var left = field.FocusRailLeftSprite;
                var right = field.FocusRailRightSprite; var selection = field.SelectionColor;
                panel.AddThemeAction(p => {
                    field.Outline = p is null && fieldOutline;
                    field.BackgroundSprite = p is null ? sprite : 0;
                    field.FocusFieldSprite = p is null ? focus : 0;
                    field.FocusRailLeftSprite = p is null ? left : 0;
                    field.FocusRailRightSprite = p is null ? right : 0;
                    field.SelectionColor = p?.Selected ?? selection;
                    if (Default("background")) field.BackgroundColor = p?.Field ?? fieldBg;
                    if (Default("color")) field.TextColor = p?.Text ?? fieldText;
                });
                break;
            case UiMenu menu:
                bool art = menu.RetailButtonArt, menuOutline = menu.Outline;
                bool flatScroll = menu.PlainPopupScrollbar;
                var mb = menu.PlainBackgroundColor; var me = menu.PlainBorderColor;
                var mo = menu.PlainOpenBorderColor; var mt = menu.PlainTextColor;
                var ma = menu.PlainTriangleColor; var ms = menu.PlainSelectedColor;
                var mh = menu.PlainHoverColor;
                panel.AddThemeAction(p => {
                    menu.PlainPopupScrollbar = p is not null || flatScroll;
                    menu.RetailButtonArt = p is null && art; menu.Outline = p is null && menuOutline;
                    menu.PlainBackgroundColor = p?.Field ?? mb; menu.PlainBorderColor = p?.Border ?? me;
                    menu.PlainOpenBorderColor = p?.Accent ?? mo; menu.PlainTextColor = p?.Text ?? mt;
                    menu.PlainTriangleColor = p?.Muted ?? ma; menu.PlainSelectedColor = p?.Selected ?? ms;
                    menu.PlainHoverColor = p?.Selected ?? mh;
                });
                break;
            case UiMarkupList list:
                var lb = list.BackgroundColor; var le = list.BorderColor; var lt = list.TextColor;
                var ls = list.SelectedColor; var band = list.SelectionBandEnabled;
                panel.AddThemeAction(p => {
                    list.ThemePalette = p; list.BackgroundColor = p?.Field ?? lb;
                    list.BorderColor = p?.Border ?? le; list.TextColor = p?.Text ?? lt;
                    list.SelectedColor = p?.Selected ?? ls; list.SelectionBandEnabled = p is not null || band;
                });
                break;
            case UiScrollbar scroll:
                var retail = scroll.RetailArt; var track = scroll.PlainTrackColor;
                var rail = scroll.PlainBorderColor; var nub = scroll.PlainNubColor;
                panel.AddThemeAction(p => { scroll.RetailArt = p is null && retail;
                    scroll.PlainTrackColor = p?.Field ?? track; scroll.PlainBorderColor = p?.Border ?? rail;
                    scroll.PlainNubColor = p?.Muted ?? nub; });
                break;
        }
    }
}
