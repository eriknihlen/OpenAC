using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Content;
using AcDream.Core.Input;
using AcDream.Content;
using AcDream.UI.Abstractions.Input;

namespace AcDream.App.Tests.UI.Layout;

[Trait("Lane", "InstalledDat")]
public sealed class KeyboardConfigInstalledDatConformanceTests
{
    [Fact]
    public void InstalledEorLayout_MountsEveryBindableActionAsALiveRow()
    {
        string? datDir = ResolveDatDir();
        if (datDir is null)
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new AcDream.App.Tests.BoundedTestDatCollection(datDir);
        var strings = new DatStringResolver(dats);
        ElementInfo? info = LayoutImporter.ImportInfos(
            dats,
            KeyboardConfigController.LayoutId);
        Assert.NotNull(info);

        ImportedLayout layout = LayoutImporter.Build(
            info!,
            _ => (0u, 0, 0),
            datFont: null,
            fontResolve: null,
            strings.Resolve);
        RetailActionMapSnapshot? snapshot = RetailActionMapReader.Read(
            (IDatObjectSource)dats);
        Assert.NotNull(snapshot);

        KeyBindings live = KeyBindings.RetailDefaults();
        KeyboardConfigController? controller = KeyboardConfigController.Bind(
            layout,
            snapshot!,
            templateResolver: (templateLayoutId, templateElementId) =>
            {
                ElementInfo? template = LayoutImporter.ImportInfos(
                    dats,
                    templateLayoutId,
                    templateElementId);
                return template is null
                    ? null
                    : LayoutImporter.Build(
                        template,
                        _ => (0u, 0, 0),
                        datFont: null,
                        fontResolve: null,
                        strings.Resolve,
                        templateLayoutId).Root;
            },
            resolveString: (tableId, stringId) => strings.Resolve(tableId, stringId),
            new KeyboardConfigController.Bindings(
                CurrentForAction: action => live.ForAction(action).ToArray(),
                SetForAction: (_, _) => { },
                CurrentForUnmapped: _ => Array.Empty<KeyChord>(),
                SetForUnmapped: (_, _) => { },
                BeginCapture: _ => { },
                Save: () => { },
                Toggle: () => { },
                ResolveTemplate: (key, variables) =>
                    strings.ResolveTemplate(0x23000004u, key, variables),
                ShowMessage: _ => { },
                ConfirmOverwrite: (_, _) => { },
                CurrentKeymapFilename: () => "acdream.keymap",
                OpenLoadKeymap: completed => completed(),
                OpenSaveKeymap: completed => completed()));

        Assert.NotNull(controller);
        Assert.Equal(306, snapshot!.Rows.Count);
        Assert.Equal(snapshot.Rows.Count, controller!.Rows.Count);
        Assert.Equal(306, controller.Page.Rows.Count);
        Assert.All(controller.Rows, row =>
        {
            Assert.NotNull(row.MappedAction);
            Assert.False(string.IsNullOrWhiteSpace(row.Label));
            Assert.Equal(3, row.KeyButtons.Count);
            Assert.All(row.KeyButtons, button =>
            {
                Assert.NotNull(button.OnClick);
                Assert.NotNull(button.OnRightClick);
                Assert.False(string.IsNullOrWhiteSpace(button.TooltipText));
            });
        });
        Assert.Equal(
            snapshot.Rows.Select(static row => (row.InputMapId, row.ActionId)).ToHashSet(),
            controller.Rows.Select(static row => (row.InputMapId, row.ActionId)).ToHashSet());

        uint key = DatStringResolver.ComputeHash("KEY");
        uint action = DatStringResolver.ComputeHash("ACTION");
        uint bindings = DatStringResolver.ComputeHash("BINDINGS");
        Assert.Equal(
            "'Ctrl+M' is currently bound to a non user-bindable action. Please select a different binding.",
            strings.ResolveTemplate(
                0x23000004u,
                "ID_ActionKeyMap_NonUserBindableBinding",
                new Dictionary<uint, string> { [key] = "Ctrl+M" }));
        Assert.Equal(
            "'A' is currently bound to 'Move Backward'. Do you wish to erase that binding?",
            strings.ResolveTemplate(
                0x23000004u,
                "ID_ActionKeyMap_OverwriteExistingBinding",
                new Dictionary<uint, string>
                {
                    [key] = "A",
                    [action] = "Move Backward",
                }));
        Assert.Equal(
            "'A' conflicts with the following bindings:\n'Move Backward' ('A')\n'Turn Right' ('A')\nDo you wish to erase those bindings?",
            strings.ResolveTemplate(
                0x23000004u,
                "ID_ActionKeyMap_OverwriteExistingBindings",
                new Dictionary<uint, string>
                {
                    [key] = "A",
                    [bindings] = "'Move Backward' ('A')\n'Turn Right' ('A')",
                }));

        foreach (uint buttonId in new[]
        {
            0x10000027u, // Load File
            0x10000029u, // Save As
            0x1000002Au, // Defaults
            0x1000002Bu, // Revert
            0x1000002Cu, // OK
            0x1000002Du,
        })
        {
            UiButton button = Assert.IsType<UiButton>(layout.FindElement(buttonId));
            Assert.NotNull(button.OnClick);
        }

        UiText filename = Assert.IsType<UiText>(layout.FindElement(0x10000028u));
        Assert.Equal("acdream.keymap", Assert.Single(filename.LinesProvider()).Text);

        uint dialogLayoutId = RetailDataIdResolver.Resolve(dats, 2u, 5u);
        Assert.NotEqual(0u, dialogLayoutId);
        ElementInfo? menuInfo = LayoutImporter.ImportInfos(
            dats,
            dialogLayoutId,
            RetailConfirmationMenuDialogView.RootElementId);
        Assert.NotNull(menuInfo);
        ImportedLayout menuLayout = LayoutImporter.Build(
            menuInfo!, _ => (0u, 0, 0), null, null, strings.Resolve);
        Assert.IsType<UiDialogRoot>(menuLayout.Root);
        UiMenu catalogMenu = Assert.IsType<UiMenu>(menuLayout.FindElement(
            RetailConfirmationMenuDialogView.MenuElementId));
        Assert.NotEqual(0u, catalogMenu.NormalSprite);
        Assert.NotEqual(0u, catalogMenu.PressedSprite);
        Assert.NotEqual(0u, catalogMenu.PopupBgSprite);
        Assert.NotEqual(0u, catalogMenu.ItemNormalSprite);
        Assert.IsType<UiButton>(menuLayout.FindElement(
            RetailConfirmationMenuDialogView.AcceptButtonId));
        Assert.IsType<UiButton>(menuLayout.FindElement(
            RetailConfirmationMenuDialogView.RejectButtonId));
    }

    private static string? ResolveDatDir()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && Directory.Exists(fromEnvironment))
            return fromEnvironment;

        string installed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents",
            "Asheron's Call");
        return Directory.Exists(installed) ? installed : null;
    }

}
