using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AcDream.App.UI;
using AcDream.Core.Input;
using AcDream.UI.Abstractions.Input;

namespace AcDream.App.UI.Layout;

public sealed class KeyboardConfigController
{
    public const uint LayoutId = 0x21000009u;

    public const uint WindowRootElementId = 0x1000001Fu;

    private const uint LoadButtonId = 0x10000027u;
    private const uint FilenameLabelId = 0x10000028u;
    private const uint SaveAsButtonId = 0x10000029u;
    private const uint DefaultsButtonId = 0x1000002Au;
    private const uint RevertButtonId = 0x1000002Bu;
    private const uint OkButtonId = 0x1000002Cu;
    private const uint CancelButtonId = 0x1000002Du;

    private const uint ListBoxElementId = 0x10000025u;
    private const uint ScrollbarElementId = 0x10000026u;

    private const int HeaderTemplateIndex = 0;
    private const int RowTemplateIndex = 1;

    private const uint TabHostElementId = 0x1000049Bu;

    private static readonly uint[] KeyButtonIds = { 0x10000030u, 0x10000031u, 0x10000032u };

    private static readonly (uint PageContainerId, RetailActionClass Class)[] Pages =
    {
        (0x1000049Du, RetailActionClass.Movement),
        (0x1000049Fu, RetailActionClass.Camera),
        (0x100004A1u, RetailActionClass.Combat),
        (0x100004A3u, RetailActionClass.Ui),
        (0x10000211u, RetailActionClass.CharacterSettings),
        (0x100004A5u, RetailActionClass.Emote),
    };

    public sealed record RowView(
        uint InputMapId,
        uint ActionId,
        InputAction? MappedAction,
        string? Label,
        ActionKeyMapOptionRow Model,
        IReadOnlyList<UiButton> KeyButtons);

    public sealed record Bindings(
        Func<InputAction, IReadOnlyList<Binding>> CurrentForAction,
        Action<InputAction, IReadOnlyList<Binding>> SetForAction,
        Func<(uint InputMapId, uint ActionId), IReadOnlyList<KeyChord>> CurrentForUnmapped,
        Action<(uint InputMapId, uint ActionId), IReadOnlyList<KeyChord>> SetForUnmapped,
        Action<Action<KeyChord?>> BeginCapture,
        Action Save,
        Action Toggle,
        Func<string, IReadOnlyDictionary<uint, string>, string?> ResolveTemplate,
        Action<string> ShowMessage,
        Action<string, Action<bool>> ConfirmOverwrite,
        Func<string, uint>? OpenCaptureInstructions = null,
        Action<uint>? CloseCaptureInstructions = null,
        Func<string>? CurrentKeymapFilename = null,
        Action<Action>? OpenLoadKeymap = null,
        Action<Action>? OpenSaveKeymap = null);

    public OptionPage Page { get; } = new();
    public IReadOnlyList<RowView> Rows => _rows;

    private readonly List<RowView> _rows = new();
    private readonly Dictionary<(uint LayoutId, uint ElementId), UiDatFont?> _templateFontCache = new();
    private RetailActionMapSnapshot? _snapshot;
    private Bindings? _bindings;
    private Func<KeyChord, string> _describe = DescribeChord;
    private Func<uint, uint, UiDatFont?>? _resolveTemplateFont;

    private static readonly uint ActionVariable = DatStringResolver.ComputeHash("ACTION");
    private static readonly uint BindingsVariable = DatStringResolver.ComputeHash("BINDINGS");
    private static readonly uint KeyVariable = DatStringResolver.ComputeHash("KEY");
    private static readonly uint LabelVariable = DatStringResolver.ComputeHash("LABEL");
    private static readonly uint ValueVariable = DatStringResolver.ComputeHash("VALUE");

    private KeyboardConfigController() { }

    public static KeyboardConfigController? Bind(
        ImportedLayout layout,
        RetailActionMapSnapshot snapshot,
        Func<uint, uint, UiElement?> templateResolver,
        Func<uint, uint, string?> resolveString,
        Bindings bindings,
        Func<uint, uint, UiDatFont?>? resolveTemplateFont = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(templateResolver);
        ArgumentNullException.ThrowIfNull(resolveString);
        ArgumentNullException.ThrowIfNull(bindings);

        if (layout.FindElement(WindowRootElementId) is null)
        {
            Console.WriteLine(
                $"[D.2b] KeyboardConfigController: window root 0x{WindowRootElementId:X8} "
                + "not found in the built layout — Configure Keyboard will not open.");
            return null;
        }

        var controller = new KeyboardConfigController
        {
            _snapshot = snapshot,
            _bindings = bindings,
            _resolveTemplateFont = resolveTemplateFont,
            _describe = new RetailKeyNames(resolveString).Describe,
        };

        var byClass = snapshot.Rows
            .Where(r => r.ActionClass != RetailActionClass.None)
            .GroupBy(r => r.ActionClass)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach ((uint pageContainerId, RetailActionClass cls) in Pages)
        {
            UiElement? pageRoot = UiElement.FindDescendant(layout.Root, pageContainerId);
            if (pageRoot is null)
            {
                Console.WriteLine(
                    $"[D.2b] KeyboardConfigController: page container 0x{pageContainerId:X8} "
                    + "not found — that ActionClass tab will have no rows.");
                continue;
            }
            if (UiElement.FindDescendant(pageRoot, ListBoxElementId) is not UiTemplateListBox listBox)
            {
                Console.WriteLine(
                    $"[D.2b] KeyboardConfigController: ListBox 0x{ListBoxElementId:X8} not found "
                    + $"(or not a UiTemplateListBox) under page 0x{pageContainerId:X8}.");
                continue;
            }
            listBox.TemplateResolver = templateResolver;
            if (UiElement.FindDescendant(pageRoot, ScrollbarElementId) is UiScrollbar scrollbar)
                scrollbar.Model = listBox.Scroll;

            if (!byClass.TryGetValue(cls, out List<RetailActionMapRow>? classRows))
                continue;

            var byInputMap = classRows
                .GroupBy(r => r.InputMapId)
                .OrderBy(g => g.Key);

            foreach (var inputMapGroup in byInputMap)
            {
                BuildHeaderRow(listBox, inputMapGroup.Key, resolveString);
                foreach (RetailActionMapRow row in inputMapGroup)
                    controller.BuildActionRow(listBox, row, resolveString, bindings);
            }
        }

        WireScreenButtons(layout, controller, bindings);

        if (layout.FindElement(TabHostElementId) is UiTabPanel tabHost)
        {
            tabHost.ActivateTabBehavior();
        }
        else
        {
            Console.WriteLine(
                $"[D.2b] KeyboardConfigController: tab host 0x{TabHostElementId:X8} not found "
                + "(or not a UiTabPanel) — all six ActionClass pages will render stacked.");
        }

        return controller;
    }

    private static void BuildHeaderRow(
        UiTemplateListBox listBox, uint inputMapId, Func<uint, uint, string?> resolveString)
    {
        if (listBox.AddItemFromTemplateList(HeaderTemplateIndex) is not UiText header)
        {
            Console.WriteLine(
                "[D.2b] KeyboardConfigController: header template did not build as UiText "
                + $"for InputMap 0x{inputMapId:X8}.");
            return;
        }
        if (!RetailInputMapHeaders.NameByInputMapId.TryGetValue(inputMapId, out string? headerKey))
            return;
                     // action of ours falls in one, but stay honest rather than assume.

        string? label = resolveString(
            RetailInputMapHeaders.StringTableId, DatStringResolver.ComputeHash(headerKey));
        if (label is null)
        {
            Console.WriteLine(
                $"[D.2b] KeyboardConfigController: header string '{headerKey}' did not resolve — "
                + "row renders with no text rather than invented English.");
            return;
        }
        header.LinesProvider = () => new[] { new UiText.Line(label, header.DefaultColor) };
    }

    private void BuildActionRow(
        UiTemplateListBox listBox,
        RetailActionMapRow row,
        Func<uint, uint, string?> resolveString,
        Bindings bindings)
    {
        UiElement? built = listBox.AddItemFromTemplateList(RowTemplateIndex);
        if (built is null)
        {
            Console.WriteLine(
                "[D.2b] KeyboardConfigController: row template did not build for InputMap "
                + $"0x{row.InputMapId:X8} action 0x{row.ActionId:X8}.");
            return;
        }

        var keyButtons = new List<UiButton>(KeyButtonIds.Length);
        foreach (uint id in KeyButtonIds)
        {
            if (UiElement.FindDescendant(built, id) is UiButton button)
                keyButtons.Add(button);
        }

        string? label = resolveString(RetailInputMapHeaders.StringTableId, row.LabelHash);
        string? tooltip = resolveString(RetailInputMapHeaders.StringTableId, row.TooltipHash);

        bool mapped = RetailActionIdentityTable.TryResolve(row.InputMapId, row.ActionId, out InputAction action);
        InputAction? mappedAction = mapped ? action : null;

        var captionText = new UiText
        {
            Left = 0f,
            Top = 0f,
            Width = 260f,
            Height = built.Height,
            ClickThrough = true,
            Centered = false,
            RightAligned = false,
            Padding = 2f,
            Anchors = AnchorEdges.Left | AnchorEdges.Top,
            DefaultColor = mapped ? Vector4.One : UiRenderContext.StoreOnlyCaptionColor,
            DatFont = RowCaptionFont(listBox),
        };
        if (label is not null)
            captionText.LinesProvider = () => new[] { new UiText.Line(label, captionText.DefaultColor) };
        captionText.AuthoredTooltipText = tooltip;
        built.AddChild(captionText);

        IReadOnlyList<Binding> liveBindings = mapped
            ? bindings.CurrentForAction(action)
            : Array.Empty<Binding>();
        (ActivationType Activation, InputScope Scope) template = liveBindings.Count > 0
            ? (liveBindings[0].Activation, liveBindings[0].Scope)
            : (
                RetailActionIdentityTable.ActivationFor(row.InputMapId, row.ActionId),
                RetailActionIdentityTable.ScopeForInputMap(row.InputMapId));

        IReadOnlyList<KeyChord> defaults = DatDefaultsToChords(row.DefaultBindings);
        IReadOnlyList<KeyChord> storedUnmapped = mapped
            ? Array.Empty<KeyChord>()
            : bindings.CurrentForUnmapped((row.InputMapId, row.ActionId));
        IReadOnlyList<KeyChord> initial = mapped
            ? liveBindings.Select(b => b.Chord).ToArray()
            : storedUnmapped.Count > 0 ? storedUnmapped : defaults;

        var model = new ActionKeyMapOptionRow(initial, defaults, apply: value =>
        {
            IReadOnlyList<KeyChord> real = value.Where(c => c != default).ToArray();
            if (mapped)
                bindings.SetForAction(
                    action,
                    real.Select(c => new Binding(c, action, template.Activation, template.Scope)).ToArray());
            else
                bindings.SetForUnmapped((row.InputMapId, row.ActionId), real);
        });
        Page.Register(model);

        var view = new RowView(row.InputMapId, row.ActionId, mappedAction, label, model, keyButtons);
        _rows.Add(view);

        RefreshRowButtons(view);

        for (int slot = 0; slot < keyButtons.Count; slot++)
        {
            int capturedSlot = slot;
            keyButtons[slot].OnClick = () => BeginSlotCapture(view, capturedSlot, bindings);
            keyButtons[slot].OnRightClick = () => EraseSlot(view, capturedSlot);
        }
    }

    /// <summary>The authored font of this ListBox's action-row template,
    /// resolved once per (layout, element) pair. Null (template import or
    /// font-load failure, or no resolver wired) keeps the debug-font fallback.</summary>
    private UiDatFont? RowCaptionFont(UiTemplateListBox listBox)
    {
        if (_resolveTemplateFont is null
            || RowTemplateIndex >= listBox.Templates.Count)
            return null;
        (uint layoutId, uint elementId) = (
            listBox.Templates[RowTemplateIndex].TemplateLayoutId,
            listBox.Templates[RowTemplateIndex].TemplateElementId);
        if (_templateFontCache.TryGetValue((layoutId, elementId), out UiDatFont? cached))
            return cached;
        UiDatFont? font = _resolveTemplateFont(layoutId, elementId);
        _templateFontCache[(layoutId, elementId)] = font;
        return font;
    }

    private static IReadOnlyList<KeyChord> DatDefaultsToChords(IReadOnlyList<RetailKeyChord> raw)
    {
        var result = new List<KeyChord>(raw.Count);
        foreach (RetailKeyChord chord in raw)
        {
            Silk.NET.Input.Key? key = RetailScanCodeMap.ToSilkKey(chord.Scan, chord.Device);
            if (key is null) continue;
            result.Add(new KeyChord(key.Value, RetailScanCodeMap.ToModifierMask(chord.Modifier), (byte)chord.Device));
        }
        return result;
    }

    private void RefreshRowButtons(RowView view)
    {
        IReadOnlyList<KeyChord> current = view.Model.Current;
        for (int i = 0; i < view.KeyButtons.Count; i++)
        {
            bool bound = i < current.Count && current[i] != default;
            if (!bound)
            {
                view.KeyButtons[i].Label = null;
                view.KeyButtons[i].TooltipText = ResolveTemplate(
                    "ID_ActionKeyMap_TT_NewBinding",
                    EmptyTemplateVariables);
                continue;
            }

            string keyName = _describe(current[i]);
            string? buttonLabel = ResolveTemplate(
                "ID_ActionKeyMap_ButtonLabel",
                new Dictionary<uint, string> { [LabelVariable] = keyName });
            view.KeyButtons[i].Label = buttonLabel;
            view.KeyButtons[i].TooltipText = buttonLabel is null
                ? null
                : ResolveTemplate(
                    "ID_ActionKeyMap_TT_ExistingBinding",
                    new Dictionary<uint, string> { [ValueVariable] = buttonLabel });
        }
    }

    private static readonly IReadOnlyDictionary<uint, string> EmptyTemplateVariables =
        new Dictionary<uint, string>();

    private string? ResolveTemplate(
        string key,
        IReadOnlyDictionary<uint, string> variables) =>
        _bindings?.ResolveTemplate(key, variables);

    private static string DescribeChord(KeyChord chord)
    {
        string mods = chord.Modifiers == ModifierMask.None ? "" : chord.Modifiers.ToString() + "+";
        return mods + chord.Key;
    }

    private void BeginSlotCapture(RowView view, int slot, Bindings bindings)
    {
        uint instructionsContext = 0u;
        if (bindings.OpenCaptureInstructions is { } openInstructions)
        {
            instructionsContext = openInstructions(view.Label ?? string.Empty);
            if (instructionsContext == 0u)
            {
                Console.WriteLine(
                    "[D.2b] KeyboardConfigController: capture-instruction dialog "
                    + "could not open — capture not armed (retail refuses too).");
                return;
            }
        }

        void ArmCapture() => bindings.BeginCapture(captured =>
        {
            if (captured is { } unsupported && IsUnsupportedRetailCapture(unsupported))
            {
                ArmCapture();
                return;
            }

            if (instructionsContext != 0u)
                bindings.CloseCaptureInstructions?.Invoke(instructionsContext);

            if (captured is not { } chord) return;

            if (view.Model.Current.Contains(chord))
                return;

            (ConflictOutcome outcome, List<RowView> conflictRows) = FindConflicts(chord, exclude: view);
            switch (outcome)
            {
                case ConflictOutcome.NonBindable:
                    string? refusal = bindings.ResolveTemplate(
                        "ID_ActionKeyMap_NonUserBindableBinding",
                        new Dictionary<uint, string> { [KeyVariable] = _describe(chord) });
                    if (refusal is not null)
                        bindings.ShowMessage(refusal);
                    return;

                case ConflictOutcome.Rows:
                    string? message = ComposeOverwriteMessage(chord, conflictRows, bindings);
                    if (message is null)
                        return;
                    bindings.ConfirmOverwrite(message, accepted =>
                    {
                        if (!accepted) return;
                        foreach (RowView conflictRow in conflictRows)
                        {
                            ReplaceSlotValue(conflictRow, RemoveChord(conflictRow.Model.Current, chord));
                            RefreshRowButtons(conflictRow);
                        }
                        ApplySlot(view, slot, chord);
                    });
                    return;

                case ConflictOutcome.None:
                    ApplySlot(view, slot, chord);
                    return;
            }
        });

        ArmCapture();
    }

    private static bool IsUnsupportedRetailCapture(KeyChord chord)
    {
        if (chord.Device > 1)
            return true; // joystick/unknown device
        if (chord.Device == 1
            && (chord.Key == InputDispatcher.MouseButtonToKey(Silk.NET.Input.MouseButton.Left)
                || chord.Key == InputDispatcher.MouseButtonToKey(Silk.NET.Input.MouseButton.Right)))
            return true;
        return !RetailScanCodeMap.TryToFileControl(chord, out _);
    }

    private string? ComposeOverwriteMessage(
        KeyChord chord,
        IReadOnlyList<RowView> conflicts,
        Bindings bindings)
    {
        string keyName = _describe(chord);
        if (conflicts.Count == 1)
        {
            string? action = conflicts[0].Label;
            if (action is null) return null;
            return bindings.ResolveTemplate(
                "ID_ActionKeyMap_OverwriteExistingBinding",
                new Dictionary<uint, string>
                {
                    [KeyVariable] = keyName,
                    [ActionVariable] = action,
                });
        }

        var lines = new List<string>(conflicts.Count);
        foreach (RowView conflict in conflicts)
        {
            if (conflict.Label is null) return null;
            string? line = bindings.ResolveTemplate(
                "ID_ActionKeyMap_Binding",
                new Dictionary<uint, string>
                {
                    [ActionVariable] = conflict.Label,
                    [KeyVariable] = keyName,
                });
            if (line is null) return null;
            lines.Add(line);
        }

        return bindings.ResolveTemplate(
            "ID_ActionKeyMap_OverwriteExistingBindings",
            new Dictionary<uint, string>
            {
                [KeyVariable] = keyName,
                [BindingsVariable] = string.Join("\n", lines),
            });
    }

    private void ApplySlot(RowView view, int slot, KeyChord chord)
    {
        List<KeyChord> updated = new(view.Model.Current);
        int targetSlot = Math.Clamp(slot, 0, updated.Count);
        if (targetSlot == updated.Count)
            updated.Add(chord);
        else
            updated[targetSlot] = chord;
        ReplaceSlotValue(view, updated);
        RefreshRowButtons(view);
    }

    private void EraseSlot(RowView view, int slot)
    {
        if (slot >= view.Model.Current.Count) return;
        if (view.Model.Current[slot] == default) return; // nothing bound in this display slot
        var updated = new List<KeyChord>(view.Model.Current);
        updated.RemoveAt(slot);
        ReplaceSlotValue(view, updated);
        RefreshRowButtons(view);
    }

    private static void ReplaceSlotValue(RowView view, IReadOnlyList<KeyChord> value)
    {
        int lastReal = -1;
        for (int i = 0; i < value.Count; i++)
            if (value[i] != default) lastReal = i;
        view.Model.SetCurrentValue(lastReal < 0 ? Array.Empty<KeyChord>() : value.Take(lastReal + 1).ToArray());
    }

    private static IReadOnlyList<KeyChord> RemoveChord(IReadOnlyList<KeyChord> from, KeyChord chord) =>
        from.Where(c => c != chord).ToArray();

    private enum ConflictOutcome { None, NonBindable, Rows }

    private (ConflictOutcome Outcome, List<RowView> Rows) FindConflicts(KeyChord chord, RowView exclude)
    {
        if (_bindings is not null)
        {
            foreach (InputAction candidate in Enum.GetValues<InputAction>())
            {
                if (RetailActionIdentityTable.Map.Values.Contains(candidate)) continue;
                if (_bindings.CurrentForAction(candidate).Any(b => b.Chord == chord))
                    return (ConflictOutcome.NonBindable, new List<RowView>());
            }
        }

        var rows = new List<RowView>();
        foreach (RowView other in _rows)
        {
            if (ReferenceEquals(other, exclude)) continue;
            if (other.MappedAction is null) continue;
            if (_snapshot?.InputMapsConflict(
                    exclude.InputMapId,
                    other.InputMapId) != true)
            {
                continue;
            }
            if (other.Model.Current.Contains(chord))
                rows.Add(other);
        }
        return rows.Count > 0 ? (ConflictOutcome.Rows, rows) : (ConflictOutcome.None, rows);
    }

    private static void WireScreenButtons(
        ImportedLayout layout, KeyboardConfigController controller, Bindings bindings)
    {
        UiText? filename = layout.FindElement(FilenameLabelId) as UiText;
        void RefreshFilename()
        {
            if (filename is null || bindings.CurrentKeymapFilename is null) return;
            string value = bindings.CurrentKeymapFilename();
            filename.LinesProvider = () =>
                new[] { new UiText.Line(value, filename.DefaultColor) };
        }
        RefreshFilename();

        if (layout.FindElement(LoadButtonId) is UiButton loadButton
            && bindings.OpenLoadKeymap is { } openLoad)
        {
            loadButton.OnClick = () => openLoad(() =>
            {
                controller.ReloadRowsFromBindings(bindings);
                RefreshFilename();
            });
        }

        if (layout.FindElement(SaveAsButtonId) is UiButton saveAsButton
            && bindings.OpenSaveKeymap is { } openSave)
        {
            saveAsButton.OnClick = () => openSave(RefreshFilename);
        }

        if (layout.FindElement(DefaultsButtonId) is UiButton defaultsButton)
            defaultsButton.OnClick = () =>
            {
                controller.Page.Defaults();
                foreach (RowView row in controller._rows)
                    controller.RefreshRowButtons(row);
            };

        if (layout.FindElement(RevertButtonId) is UiButton revertButton)
        {
            revertButton.OnClick = () =>
            {
                controller.Page.Reset();
                foreach (RowView row in controller._rows)
                    controller.RefreshRowButtons(row);
            };

            controller.Page.OnOptionChanged = () =>
                revertButton.Enabled = controller.Page.Changed;
            controller.Page.OnOptionChanged();
        }

        if (layout.FindElement(OkButtonId) is UiButton okButton)
            okButton.OnClick = () =>
            {
                bool changed = controller.Page.Changed;
                if (changed)
                    bindings.Save();
                controller.Page.Apply();
                bindings.Toggle();
            };

        if (layout.FindElement(CancelButtonId) is UiButton cancelButton)
            cancelButton.OnClick = () =>
            {
                controller.Page.Reset();
                foreach (RowView row in controller._rows)
                    controller.RefreshRowButtons(row);
                bindings.Toggle();
            };
    }

    private void ReloadRowsFromBindings(Bindings bindings)
    {
        foreach (RowView row in _rows)
        {
            IReadOnlyList<KeyChord> chords = row.MappedAction is { } action
                ? bindings.CurrentForAction(action).Select(static value => value.Chord).ToArray()
                : bindings.CurrentForUnmapped((row.InputMapId, row.ActionId));
            row.Model.ReloadCurrentAndSaved(chords);
            RefreshRowButtons(row);
        }
        Page.OnOptionChanged?.Invoke();
    }
}
