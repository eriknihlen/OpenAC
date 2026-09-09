using System;
using System.Collections.Generic;
using System.IO;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Content;
using DatReaderWriter;
using DatReaderWriter.Options;

namespace AcDream.App.Tests.UI.Layout;

[Trait("Lane", "InstalledDat")]
public sealed class JournalPanelBoundWidgetTypesTests
{
    private static readonly (uint Id, Type Expected, string Name)[] Bound =
    [
        (0x100005CFu, typeof(UiTemplateListBox), "contracts list"),
        (0x100005DFu, typeof(UiText), "contract status value"),
        (0x100005E0u, typeof(UiText), "contract contact"),
        (0x100005E1u, typeof(UiText), "contract contact location"),
        (0x100005E2u, typeof(UiText), "contract quest location"),
        (0x100005DEu, typeof(UiText), "contract description"),
        (0x100005E3u, typeof(UiText), "contract timed value"),

        // Notes page.
        (0x10000569u, typeof(UiField), "journal label field"),
        (0x1000056Bu, typeof(UiField), "journal title field"),
        (0x1000056Du, typeof(UiField), "journal notes field"),
        (0x10000570u, typeof(UiText), "journal page number"),
        (0x10000573u, typeof(UiField), "journal location readout (AUTHORED EDITABLE)"),
        (0x10000576u, typeof(UiField), "journal timer days"),
        (0x10000578u, typeof(UiField), "journal timer hours"),
        (0x1000057Au, typeof(UiField), "journal timer minutes"),
        (0x1000057Cu, typeof(UiText), "journal running timer readout"),

        // Page list.
        (0x10000583u, typeof(UiTemplateListBox), "page list"),
        (0x10000587u, typeof(UiField), "page list search box"),
    ];

    private static ImportedLayout BuildPanel()
    {
        string? datDir = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR");
        if (string.IsNullOrWhiteSpace(datDir) || !Directory.Exists(datDir))
        {
            datDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents", "Asheron's Call");
        }

        if (!Directory.Exists(datDir))
        {
            Assert.Fail(
                "Lane=InstalledDat requires an installed retail DAT directory; "
                + "see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);

        ElementInfo? root = LayoutImporter.ImportInfos(
            adapter,
            JournalPanelController.HostLayoutId,
            JournalPanelController.SlotElementId);
        Assert.NotNull(root);

        var strings = new DatStringResolver(adapter);
        return LayoutImporter.Build(root!, _ => (0u, 0, 0), null, _ => null, strings.Resolve);
    }

    [Fact]
    public void EveryBoundElementBuildsAsTheWidgetItsControllerCastsItTo()
    {
        ImportedLayout layout = BuildPanel();

        var wrong = new List<string>();
        foreach ((uint id, Type expected, string name) in Bound)
        {
            UiElement? element = layout.FindElement(id);
            if (element is null)
            {
                wrong.Add($"{name} (0x{id:X8}) is missing from the layout");
                continue;
            }

            if (!expected.IsInstanceOfType(element))
            {
                wrong.Add(
                    $"{name} (0x{id:X8}) built as {element.GetType().Name}, "
                    + $"controller expects {expected.Name}");
            }
        }

        Assert.Empty(wrong);
    }
}
