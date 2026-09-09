using System.IO;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using DatReaderWriter;
using DatReaderWriter.Options;

namespace AcDream.App.Tests.UI.Layout;

[Trait("Lane", "Manual")]
[Trait("ManualTask", "LiveMountProbe")]
public sealed class ChatIndicatorButtonLiveMountProbeTests
{
    [Fact]
    public void IndicatorButtons_ResolveNormalStateAtRest_ThroughTheLiveImportPath()
    {
        if (Environment.GetEnvironmentVariable("ACDREAM_PROBE_LIVE_MOUNT") != "1")
            Assert.Fail("Lane=Manual live-mount probe requires ACDREAM_PROBE_LIVE_MOUNT=1.");

        var datDir = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents",
                "Asheron's Call");
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var strings = new DatStringResolver(dats);

        ElementInfo? rootInfo = LayoutImporter.ImportInfos(dats, ChatWindowController.LayoutId);
        Assert.NotNull(rootInfo);

        ElementInfo? parentInfo = FindInfo(rootInfo!, 0x10000600u);
        Assert.NotNull(parentInfo);
        Assert.True(
            parentInfo!.States.TryGetValue(UiStateInfo.DirectStateId, out UiStateInfo? parentDirect)
            && parentDirect.PassToChildren,
            "the indicator column's backing panel (0x10000600) no longer authors " +
            "PassToChildren on its own DirectState — the root-cause premise has " +
            "changed; re-verify the fix is still needed.");

        ImportedLayout layout = LayoutImporter.Build(
            rootInfo!,
            resolve: _ => (1u, 16, 16),
            datFont: null,
            fontResolve: null,
            stringResolve: strings.Resolve);

        uint[] indicatorIds = { 0x10000522u, 0x10000523u, 0x10000524u, 0x10000525u };
        foreach (uint id in indicatorIds)
        {
            UiElement? el = layout.FindElement(id);
            Assert.True(el is UiButton, $"0x{id:X8} did not build as a UiButton (was {el?.GetType().Name ?? "null"}).");
            var button = (UiButton)el!;

            Assert.Equal("Normal", button.ActiveState);

            Assert.True(button.TrySetRetailState(UiButtonStateMachine.Normal));
            Assert.Equal("Normal", button.ActiveState);

            Assert.False(button.TrySetRetailState(UiStateInfo.DirectStateId));
            Assert.Equal("Normal", button.ActiveState);

            button.OnEvent(new UiEvent(0, button, UiEventType.HoverEnter));
            Assert.Equal("Normal", button.ActiveState);
            button.OnEvent(new UiEvent(0, button, UiEventType.HoverLeave));
            Assert.Equal("Normal", button.ActiveState);
        }
    }

    private static ElementInfo? FindInfo(ElementInfo node, uint id)
    {
        if (node.Id == id) return node;
        foreach (ElementInfo child in node.Children)
        {
            ElementInfo? found = FindInfo(child, id);
            if (found is not null) return found;
        }
        return null;
    }
}
