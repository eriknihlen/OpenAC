using System.Numerics;
using AcDream.App.UI;
using AcDream.UI.Abstractions.Panels.Settings;
namespace AcDream.App.Tests.UI;

public sealed class PluginUiThemeTests
{
    private sealed class Binding
    {
        public uint StateColor => 0xFF123456;
        public IReadOnlyList<string> Items => ["A", "B"];
        public string Selected => "A";
    }
    private static UiNineSlicePanel Build(string theme, PluginUiThemeSettings settings) => MarkupDocument.Build($$"""
        <panel x="0" y="0" w="250" h="200" {{theme}}>
          <group x="0" y="0" w="200" h="150" visible="false">
            <label x="0" y="0" text="ExactVariableName" />
            <label x="0" y="20" text="Status" color="{StateColor}" />
            <label x="0" y="40" text="Muted" color="theme:muted|#FF123456" />
            <menu x="0" y="60" w="150" h="20" items="{Items}" selected="{Selected}" searchable="true" />
          </group>
        </panel>
        """, new Binding(), _ => (0u, 0, 0), themes: settings);

    [Fact]
    public void OptedInWindowSwitchesAndRestoresClassicIncludingHiddenGroups()
    {
        var settings = new PluginUiThemeSettings();
        var panel = Build("theme=\"plugin\"", settings);
        var root = new UiRoot(); root.AddChild(panel);
        var group = panel.Children[0];
        var label = (UiLabel)group.Children[0];
        var status = (UiLabel)group.Children[1];
        var muted = (UiLabel)group.Children[2];
        var original = label.TextColor;
        var originalMuted = muted.TextColorSource!();
        foreach (var theme in new[] { PluginUiTheme.Moss, PluginUiTheme.Brass })
        {
            settings.Theme = theme; root.Tick(0.016, 10);
            Assert.Equal(settings.Palette!.Text, label.TextColor);
            Assert.False(label.Outline);
            Assert.Equal(originalMuted, status.TextColorSource!());
            Assert.Equal(settings.Palette.Muted, muted.TextColorSource!());
            Assert.Equal("ExactVariableName", label.TextSource!());
        }
        settings.Theme = PluginUiTheme.Classic; root.Tick(0.016, 20);
        Assert.Equal(original, label.TextColor);
        Assert.Equal(originalMuted, muted.TextColorSource!());
        Assert.True(label.Outline);
        Assert.True(((UiMenu)group.Children[3]).Searchable);
    }

    [Fact]
    public void NonOptedInWindowKeepsClassicWhenGlobalThemeChanges()
    {
        var settings = new PluginUiThemeSettings { Theme = PluginUiTheme.Moss };
        var panel = Build("", settings);
        Assert.IsType<UiNineSlicePanel>(panel);
        Assert.True(((UiLabel)panel.Children[0].Children[0]).Outline);
        Assert.Equal(Vector4.One, ((UiLabel)panel.Children[0].Children[0]).TextColor);
    }

    [Fact]
    public void ReadOnlyMultilineDescriptionKeepsBindingAndDoesNotCaptureTyping()
    {
        var panel = MarkupDocument.Build("""
            <panel x="0" y="0" w="200" h="100">
              <field x="1" y="1" w="180" h="70" text="ExactVariableName description"
                     oneline="false" editable="false" />
            </panel>
            """, new object(), _ => (0u, 0, 0));
        var field = Assert.IsType<UiField>(panel.Children[0]);
        Assert.False(field.OneLine);
        Assert.False(field.Editable);
        Assert.False(field.AcceptsFocus);
        Assert.False(field.IsEditControl);
        Assert.Equal("ExactVariableName description", field.BoundText!());
    }

    [Fact]
    public void ThemePersistsWithoutReplacingOtherSettings()
    {
        string path = Path.Combine(Path.GetTempPath(), $"plugin-theme-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(path, "{\"other\":{\"keep\":true}}");
            var store = new SettingsStore(path);
            Assert.Equal(PluginUiTheme.Classic, new PluginUiThemeSettings(store).Theme);
            new PluginUiThemeSettings(store).Theme = PluginUiTheme.Brass;
            Assert.Equal(PluginUiTheme.Brass, new PluginUiThemeSettings(store).Theme);
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            Assert.True(json.RootElement.GetProperty("other").GetProperty("keep").GetBoolean());
        }
        finally { File.Delete(path); }
    }
}
