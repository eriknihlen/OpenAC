using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Input;
using AcDream.UI.Abstractions.Input;

namespace AcDream.App.Tests.UI.Layout;

public sealed class KeyboardConfigControllerTests
{
    private static (uint, int, int) NoTex(uint _) => (0, 0, 0);

    private static ElementInfo? Find(ElementInfo n, uint id)
    {
        if (n.Id == id) return n;
        foreach (ElementInfo c in n.Children)
        {
            ElementInfo? f = Find(c, id);
            if (f is not null) return f;
        }
        return null;
    }

    private static Func<uint, uint, UiElement?> MakeTemplateResolver()
    {
        ElementInfo root = FixtureLoader.LoadKeyboardConfigInfos();
        return (layoutId, elementId) =>
        {
            if (layoutId != KeyboardConfigController.LayoutId) return null;
            ElementInfo? templateInfo = Find(root, elementId);
            return templateInfo is null ? null : LayoutImporter.Build(templateInfo, NoTex, null).Root;
        };
    }

    private static RetailActionMapRow Row(
        uint inputMapId, uint actionId, RetailActionClass cls,
        uint labelHash = 0, uint tooltipHash = 0,
        params RetailKeyChord[] defaults) =>
        new(
            inputMapId,
            actionId,
            cls,
            labelHash == 0 ? 0xDE000000u | (actionId & 0x00FFFFFFu) : labelHash,
            tooltipHash,
            defaults);

    private static string? ResolveSyntheticString(uint _, uint hash) =>
        (hash & 0xFF000000u) == 0xDE000000u ? $"Action {hash & 0x00FFFFFFu:X}" : null;

    private sealed class FakeBindings
    {
        public Dictionary<InputAction, List<Binding>> Mapped { get; } = new();
        public Dictionary<(uint, uint), List<KeyChord>> Unmapped { get; } = new();
        public List<(InputAction Action, IReadOnlyList<Binding> Value)> MappedSets { get; } = new();
        public List<((uint, uint) Row, IReadOnlyList<KeyChord> Value)> UnmappedSets { get; } = new();
        public List<string> Messages { get; } = new();
        public int SaveCalls { get; private set; }
        public int ToggleCalls { get; private set; }
        public Action<KeyChord?>? PendingCapture { get; private set; }
        public (string Message, Action<bool> OnResult)? PendingConfirm { get; private set; }
        public List<string> InstructionOpens { get; } = new();
        public List<uint> InstructionCloses { get; } = new();
        public uint NextInstructionContext { get; set; } = 7u;
        public bool WireInstructions { get; set; }
        public string CurrentKeymapFilename { get; set; } = "acdream.keymap";
        public Action? PendingLoadCompleted { get; private set; }
        public Action? PendingSaveCompleted { get; private set; }

        public void Capture(KeyChord? chord)
        {
            Action<KeyChord?>? cb = PendingCapture;
            PendingCapture = null;
            cb?.Invoke(chord);
        }

        public void RespondToConfirm(bool accept)
        {
            var pending = PendingConfirm ?? throw new InvalidOperationException("no pending confirm");
            PendingConfirm = null;
            pending.OnResult(accept);
        }

        public KeyboardConfigController.Bindings ToBindings() => new(
            CurrentForAction: a => Mapped.TryGetValue(a, out var v) ? v : Array.Empty<Binding>(),
            SetForAction: (a, v) =>
            {
                Mapped[a] = v.ToList();
                MappedSets.Add((a, v));
            },
            CurrentForUnmapped: k => Unmapped.TryGetValue(k, out var v) ? v : Array.Empty<KeyChord>(),
            SetForUnmapped: (k, v) =>
            {
                Unmapped[k] = v.ToList();
                UnmappedSets.Add((k, v));
            },
            BeginCapture: cb => PendingCapture = cb,
            Save: () => SaveCalls++,
            Toggle: () => ToggleCalls++,
            ResolveTemplate: (key, variables) => key switch
            {
                "ID_ActionKeyMap_ButtonLabel" => variables[DatStringResolver.ComputeHash("LABEL")],
                "ID_ActionKeyMap_TT_ExistingBinding" =>
                    $"({variables[DatStringResolver.ComputeHash("VALUE")]}) existing binding",
                "ID_ActionKeyMap_TT_NewBinding" => "new binding",
                "ID_ActionKeyMap_NonUserBindableBinding" =>
                    $"cannot overwrite {variables[DatStringResolver.ComputeHash("KEY")]}",
                "ID_ActionKeyMap_OverwriteExistingBinding" =>
                    $"overwrite {variables[DatStringResolver.ComputeHash("KEY")]} "
                    + variables[DatStringResolver.ComputeHash("ACTION")],
                "ID_ActionKeyMap_Binding" =>
                    $"{variables[DatStringResolver.ComputeHash("ACTION")]} "
                    + $"({variables[DatStringResolver.ComputeHash("KEY")]})",
                "ID_ActionKeyMap_OverwriteExistingBindings" =>
                    $"overwrite {variables[DatStringResolver.ComputeHash("KEY")]}\n"
                    + variables[DatStringResolver.ComputeHash("BINDINGS")],
                _ => null,
            },
            ShowMessage: msg => Messages.Add(msg),
            ConfirmOverwrite: (message, onResult) => PendingConfirm = (message, onResult),
            OpenCaptureInstructions: WireInstructions
                ? label =>
                {
                    InstructionOpens.Add(label);
                    return NextInstructionContext;
                }
                : null,
            CloseCaptureInstructions: context => InstructionCloses.Add(context));

        public KeyboardConfigController.Bindings ToProfileBindings() =>
            ToBindings() with
            {
                CurrentKeymapFilename = () => this.CurrentKeymapFilename,
                OpenLoadKeymap = completed => PendingLoadCompleted = completed,
                OpenSaveKeymap = completed => PendingSaveCompleted = completed,
            };
    }

    private static readonly KeyChord ChordW = new(Silk.NET.Input.Key.W, ModifierMask.None);
    private static readonly KeyChord ChordUp = new(Silk.NET.Input.Key.Up, ModifierMask.None);
    private static readonly KeyChord ChordA = new(Silk.NET.Input.Key.A, ModifierMask.None);
    private static readonly KeyChord LeftMouse = new(
        InputDispatcher.MouseButtonToKey(Silk.NET.Input.MouseButton.Left),
        ModifierMask.None,
        Device: 1);

    [Fact]
    public void Bind_Succeeds_AndBuildsOneRowPerSnapshotRow()
    {
        var snapshot = new RetailActionMapSnapshot(new[]
        {
            Row(0x4, 0x29, RetailActionClass.Movement),       // MovementForward
            Row(0x4, 0x2A, RetailActionClass.Movement),       // MovementBackup
            Row(0x5, 0x33, RetailActionClass.Camera),
        });

        ImportedLayout layout = FixtureLoader.LoadKeyboardConfig();
        var fake = new FakeBindings();
        KeyboardConfigController? controller = KeyboardConfigController.Bind(
            layout, snapshot, MakeTemplateResolver(), ResolveSyntheticString, fake.ToBindings());

        Assert.NotNull(controller);
        Assert.Equal(3, controller!.Rows.Count);
        Assert.Equal(3, controller.Page.Rows.Count);
    }

    [Fact]
    public void Bind_ActivatesTheTabControl_MovementDefaultShown_OtherPagesHidden()
    {
        var snapshot = new RetailActionMapSnapshot(new[]
        {
            Row(0x4, 0x29, RetailActionClass.Movement),
            Row(0x5, 0x33, RetailActionClass.Camera),
        });

        ImportedLayout layout = FixtureLoader.LoadKeyboardConfig();
        var fake = new FakeBindings();
        KeyboardConfigController controller = KeyboardConfigController.Bind(
            layout, snapshot, MakeTemplateResolver(), ResolveSyntheticString, fake.ToBindings())!;
        Assert.NotNull(controller);

        UiTabPanel tabHost = Assert.IsType<UiTabPanel>(layout.FindElement(0x1000049Bu));
        Assert.True(tabHost.BehaviorActive);
        Assert.Equal(0x1000049Du, tabHost.ActivePageElementId);   // Movement, the authored default

        // Exactly one page visible: Movement; the other five hidden.
        foreach ((uint pageId, bool expectVisible) in new[]
        {
            (0x1000049Du, true),    // Movement
            (0x1000049Fu, false),
            (0x100004A1u, false),
            (0x100004A3u, false),   // UI
            (0x10000211u, false),
            (0x100004A5u, false),   // Emote
        })
        {
            UiElement? page = UiElement.FindDescendant(tabHost, pageId);
            Assert.NotNull(page);
            Assert.Equal(expectVisible, page!.Visible);
        }

        tabHost.SwitchTo(0x1000049Fu);
        Assert.False(UiElement.FindDescendant(tabHost, 0x1000049Du)!.Visible);
        Assert.True(UiElement.FindDescendant(tabHost, 0x1000049Fu)!.Visible);
    }

    [Fact]
    public void Bind_MapsEveryRetailActionRow()
    {
        var snapshot = new RetailActionMapSnapshot(new[]
        {
            Row(0x4, 0x29, RetailActionClass.Movement),                 // -> MovementForward
            Row(0x10000006, 0x100000A0, RetailActionClass.Emote),       // -> EmoteBowDeep
        });

        ImportedLayout layout = FixtureLoader.LoadKeyboardConfig();
        var fake = new FakeBindings();
        KeyboardConfigController controller = KeyboardConfigController.Bind(
            layout, snapshot, MakeTemplateResolver(), ResolveSyntheticString, fake.ToBindings())!;

        KeyboardConfigController.RowView forward = controller.Rows.Single(r => r.ActionId == 0x29u);
        Assert.Equal(InputAction.MovementForward, forward.MappedAction);

        KeyboardConfigController.RowView bowDeep = controller.Rows.Single(r => r.ActionId == 0x100000A0u);
        Assert.Equal(InputAction.EmoteBowDeep, bowDeep.MappedAction);
    }

    [Fact]
    public void Bind_SeedsEveryRowFromLiveBindings()
    {
        var snapshot = new RetailActionMapSnapshot(new[]
        {
            Row(0x4, 0x29, RetailActionClass.Movement),
            Row(0x10000006, 0x100000A0, RetailActionClass.Emote),
        });

        var fake = new FakeBindings();
        fake.Mapped[InputAction.MovementForward] = new List<Binding>
        {
            new(ChordW, InputAction.MovementForward),
            new(ChordUp, InputAction.MovementForward),
        };
        fake.Mapped[InputAction.EmoteBowDeep] = new List<Binding>
        {
            new(ChordA, InputAction.EmoteBowDeep),
        };

        ImportedLayout layout = FixtureLoader.LoadKeyboardConfig();
        KeyboardConfigController controller = KeyboardConfigController.Bind(
            layout, snapshot, MakeTemplateResolver(), ResolveSyntheticString, fake.ToBindings())!;

        KeyboardConfigController.RowView forward = controller.Rows.Single(r => r.ActionId == 0x29u);
        Assert.Equal(new[] { ChordW, ChordUp }, forward.Model.Current);

        KeyboardConfigController.RowView bowDeep = controller.Rows.Single(r => r.ActionId == 0x100000A0u);
        Assert.Equal(new[] { ChordA }, bowDeep.Model.Current);
    }

    [Fact]
    public void KeyButtonClick_CapturesAndAppliesNewBinding()
    {
        var snapshot = new RetailActionMapSnapshot(new[] { Row(0x4, 0x29, RetailActionClass.Movement) });
        var fake = new FakeBindings();
        ImportedLayout layout = FixtureLoader.LoadKeyboardConfig();
        KeyboardConfigController controller = KeyboardConfigController.Bind(
            layout, snapshot, MakeTemplateResolver(), ResolveSyntheticString, fake.ToBindings())!;

        KeyboardConfigController.RowView row = controller.Rows.Single();
        Assert.NotEmpty(row.KeyButtons);

        row.KeyButtons[0].OnClick!.Invoke();
        Assert.NotNull(fake.PendingCapture);
        fake.Capture(ChordW);

        Assert.Contains(ChordW, row.Model.Current);
        (InputAction Action, IReadOnlyList<Binding> Value) written = Assert.Single(fake.MappedSets);
        Assert.Equal(InputAction.MovementForward, written.Action);
        Binding onlyBinding = Assert.Single(written.Value);
        Assert.Equal(ChordW, onlyBinding.Chord);
        Assert.Equal(ActivationType.Press, onlyBinding.Activation);
        Assert.Equal(InputScope.Game, onlyBinding.Scope);
        Assert.Equal("W", row.KeyButtons[0].Label);
    }

    [Fact]
    public void KeyButtonCapture_EscapeCancel_LeavesBindingUnchanged()
    {
        var snapshot = new RetailActionMapSnapshot(new[] { Row(0x4, 0x29, RetailActionClass.Movement) });
        var fake = new FakeBindings();
        fake.Mapped[InputAction.MovementForward] = new List<Binding> { new(ChordW, InputAction.MovementForward) };
        ImportedLayout layout = FixtureLoader.LoadKeyboardConfig();
        KeyboardConfigController controller = KeyboardConfigController.Bind(
            layout, snapshot, MakeTemplateResolver(), ResolveSyntheticString, fake.ToBindings())!;

        KeyboardConfigController.RowView row = controller.Rows.Single();
        row.KeyButtons[0].OnClick!.Invoke();
        fake.Capture(null); // Escape sentinel

        Assert.Equal(new[] { ChordW }, row.Model.Current);
        Assert.Empty(fake.MappedSets);
    }

    [Fact]
    public void KeyButtonClick_OpensInstructionDialog_AndClosesOnCapturedKey()
    {
        var snapshot = new RetailActionMapSnapshot(new[] { Row(0x4, 0x29, RetailActionClass.Movement) });
        var fake = new FakeBindings { WireInstructions = true, NextInstructionContext = 42u };
        ImportedLayout layout = FixtureLoader.LoadKeyboardConfig();
        KeyboardConfigController controller = KeyboardConfigController.Bind(
            layout, snapshot, MakeTemplateResolver(), ResolveSyntheticString, fake.ToBindings())!;

        KeyboardConfigController.RowView row = controller.Rows.Single();
        row.KeyButtons[0].OnClick!.Invoke();

        Assert.Single(fake.InstructionOpens);
        Assert.NotNull(fake.PendingCapture);
        Assert.Empty(fake.InstructionCloses);

        fake.Capture(ChordW);
        Assert.Equal(new[] { 42u }, fake.InstructionCloses);
        Assert.Contains(ChordW, row.Model.Current);
    }

    [Fact]
    public void KeyButtonClick_EscapeCapture_StillClosesInstructionDialog()
    {
        var snapshot = new RetailActionMapSnapshot(new[] { Row(0x4, 0x29, RetailActionClass.Movement) });
        var fake = new FakeBindings { WireInstructions = true, NextInstructionContext = 9u };
        ImportedLayout layout = FixtureLoader.LoadKeyboardConfig();
        KeyboardConfigController controller = KeyboardConfigController.Bind(
            layout, snapshot, MakeTemplateResolver(), ResolveSyntheticString, fake.ToBindings())!;

        KeyboardConfigController.RowView row = controller.Rows.Single();
        row.KeyButtons[0].OnClick!.Invoke();
        fake.Capture(null); // Escape sentinel

        Assert.Equal(new[] { 9u }, fake.InstructionCloses);
        Assert.Empty(fake.MappedSets);
    }

    [Fact]
    public void KeyButtonClick_InstructionDialogUnavailable_DoesNotArmCapture()
    {
        var snapshot = new RetailActionMapSnapshot(new[] { Row(0x4, 0x29, RetailActionClass.Movement) });
        var fake = new FakeBindings { WireInstructions = true, NextInstructionContext = 0u };
        ImportedLayout layout = FixtureLoader.LoadKeyboardConfig();
        KeyboardConfigController controller = KeyboardConfigController.Bind(
            layout, snapshot, MakeTemplateResolver(), ResolveSyntheticString, fake.ToBindings())!;

        KeyboardConfigController.RowView row = controller.Rows.Single();
        row.KeyButtons[0].OnClick!.Invoke();

        Assert.Null(fake.PendingCapture);
        Assert.Empty(fake.InstructionCloses);
    }

    [Fact]
    public void Bind_ResolvesTheRowTemplatesAuthoredCaptionFont_OncePerTemplate()
    {
        var snapshot = new RetailActionMapSnapshot(new[]
        {
            Row(0x4, 0x29, RetailActionClass.Movement),
            Row(0x4, 0x2A, RetailActionClass.Movement),
        });
        var fake = new FakeBindings();
        ImportedLayout layout = FixtureLoader.LoadKeyboardConfig();
        var requests = new List<(uint LayoutId, uint ElementId)>();
        KeyboardConfigController? controller = KeyboardConfigController.Bind(
            layout, snapshot, MakeTemplateResolver(), ResolveSyntheticString, fake.ToBindings(),
            resolveTemplateFont: (layoutId, elementId) =>
            {
                requests.Add((layoutId, elementId));
                return null;
            });

        Assert.NotNull(controller);
        (uint LayoutId, uint ElementId) single = Assert.Single(requests);
        Assert.Equal(KeyboardConfigController.LayoutId, single.LayoutId);
        Assert.Equal(0x1000002Fu, single.ElementId);
    }

    [Fact]
    public void KeyButtonRightClick_ErasesThatSlot()
    {
        var snapshot = new RetailActionMapSnapshot(new[] { Row(0x4, 0x29, RetailActionClass.Movement) });
        var fake = new FakeBindings();
        fake.Mapped[InputAction.MovementForward] = new List<Binding>
        {
            new(ChordW, InputAction.MovementForward),
            new(ChordUp, InputAction.MovementForward),
        };
        ImportedLayout layout = FixtureLoader.LoadKeyboardConfig();
        KeyboardConfigController controller = KeyboardConfigController.Bind(
            layout, snapshot, MakeTemplateResolver(), ResolveSyntheticString, fake.ToBindings())!;

        KeyboardConfigController.RowView row = controller.Rows.Single();
        Assert.Equal(2, row.Model.Current.Count);

        row.KeyButtons[0].OnRightClick!.Invoke();

        Assert.Single(row.Model.Current);
        Assert.DoesNotContain(ChordW, row.Model.Current);
    }

    [Fact]
    public void KeyButtonRightClick_OnAlreadyEmptySlot_IsANoOp()
    {
        var snapshot = new RetailActionMapSnapshot(new[] { Row(0x4, 0x29, RetailActionClass.Movement) });
        var fake = new FakeBindings();
        ImportedLayout layout = FixtureLoader.LoadKeyboardConfig();
        KeyboardConfigController controller = KeyboardConfigController.Bind(
            layout, snapshot, MakeTemplateResolver(), ResolveSyntheticString, fake.ToBindings())!;

        KeyboardConfigController.RowView row = controller.Rows.Single();
        Assert.Empty(row.Model.Current);

        row.KeyButtons[0].OnRightClick!.Invoke();

        Assert.Empty(row.Model.Current);
        Assert.Empty(fake.MappedSets);
    }

    [Fact]
    public void KeyButtonClick_PastDenseTail_AppendsAtFirstAvailableButton()
    {
        var snapshot = new RetailActionMapSnapshot(new[] { Row(0x4, 0x29, RetailActionClass.Movement) });
        var fake = new FakeBindings();
        ImportedLayout layout = FixtureLoader.LoadKeyboardConfig();
        KeyboardConfigController controller = KeyboardConfigController.Bind(
            layout, snapshot, MakeTemplateResolver(), ResolveSyntheticString, fake.ToBindings())!;

        KeyboardConfigController.RowView row = controller.Rows.Single();
        Assert.Equal(3, row.KeyButtons.Count);
        Assert.Empty(row.Model.Current);

        row.KeyButtons[2].OnClick!.Invoke(); // "Mapping 3"
        fake.Capture(ChordW);

        Assert.Equal("W", row.KeyButtons[0].Label);
        Assert.Null(row.KeyButtons[1].Label);
        Assert.Null(row.KeyButtons[2].Label);

        (InputAction Action, IReadOnlyList<Binding> Value) written = Assert.Single(fake.MappedSets);
        Binding onlyBinding = Assert.Single(written.Value);
        Assert.Equal(ChordW, onlyBinding.Chord);
    }

    [Fact]
    public void Capture_ConflictWithAnotherRow_OpensConfirmDialog_AcceptReassigns()
    {
        var snapshot = new RetailActionMapSnapshot(new[]
        {
            Row(0x4, 0x29, RetailActionClass.Movement),  // MovementForward
            Row(0x4, 0x2A, RetailActionClass.Movement),
        });
        var fake = new FakeBindings();
        fake.Mapped[InputAction.MovementBackup] = new List<Binding> { new(ChordA, InputAction.MovementBackup) };
        ImportedLayout layout = FixtureLoader.LoadKeyboardConfig();
        KeyboardConfigController controller = KeyboardConfigController.Bind(
            layout, snapshot, MakeTemplateResolver(), ResolveSyntheticString, fake.ToBindings())!;

        KeyboardConfigController.RowView forward = controller.Rows.Single(r => r.ActionId == 0x29u);
        KeyboardConfigController.RowView backup = controller.Rows.Single(r => r.ActionId == 0x2Au);

        forward.KeyButtons[0].OnClick!.Invoke();
        fake.Capture(ChordA);

        Assert.NotNull(fake.PendingConfirm);
        Assert.Equal("overwrite A Action 2A", fake.PendingConfirm?.Message);
        Assert.DoesNotContain(ChordA, forward.Model.Current);
        Assert.Contains(ChordA, backup.Model.Current);
        Assert.Empty(fake.Messages);

        fake.RespondToConfirm(true);

        Assert.Contains(ChordA, forward.Model.Current);
        Assert.DoesNotContain(ChordA, backup.Model.Current);
    }

    [Fact]
    public void Capture_BareShiftConflictWithRetailWalkMode_AlwaysPrompts()
    {
        var shift = new KeyChord(
            Silk.NET.Input.Key.ShiftLeft,
            ModifierMask.None);
        var snapshot = new RetailActionMapSnapshot(new[]
        {
            Row(0x4, 0x29, RetailActionClass.Movement), // Move forward
            Row(0x4, 0x32, RetailActionClass.Movement), // Toggle walk/run
        });
        var fake = new FakeBindings();
        fake.Mapped[InputAction.MovementWalkMode] =
        [new Binding(
            shift,
            InputAction.MovementWalkMode,
            ActivationType.Hold,
            InputScope.Game)];
        ImportedLayout layout = FixtureLoader.LoadKeyboardConfig();
        KeyboardConfigController controller = KeyboardConfigController.Bind(
            layout,
            snapshot,
            MakeTemplateResolver(),
            ResolveSyntheticString,
            fake.ToBindings())!;

        KeyboardConfigController.RowView forward = controller.Rows.Single(
            row => row.ActionId == 0x29u);
        forward.KeyButtons[0].OnClick!.Invoke();
        fake.Capture(shift);

        Assert.NotNull(fake.PendingConfirm);
        Assert.DoesNotContain(shift, forward.Model.Current);
        Assert.Equal(
            [shift],
            controller.Rows.Single(row => row.ActionId == 0x32u).Model.Current);
    }

    [Fact]
    public void Capture_ConflictWithMultipleRows_UsesRetailPluralBindingList()
    {
        var snapshot = new RetailActionMapSnapshot(new[]
        {
            Row(0x4, 0x29, RetailActionClass.Movement),
            Row(0x4, 0x2A, RetailActionClass.Movement),
            Row(0x4, 0x2B, RetailActionClass.Movement),
        });
        var fake = new FakeBindings();
        fake.Mapped[InputAction.MovementBackup] =
            new List<Binding> { new(ChordA, InputAction.MovementBackup) };
        fake.Mapped[InputAction.MovementStop] =
            new List<Binding> { new(ChordA, InputAction.MovementStop) };
        ImportedLayout layout = FixtureLoader.LoadKeyboardConfig();
        KeyboardConfigController controller = KeyboardConfigController.Bind(
            layout, snapshot, MakeTemplateResolver(), ResolveSyntheticString, fake.ToBindings())!;

        controller.Rows.Single(row => row.ActionId == 0x29u).KeyButtons[0].OnClick!.Invoke();
        fake.Capture(ChordA);

        Assert.Equal(
            "overwrite A\nAction 2A (A)\nAction 2B (A)",
            fake.PendingConfirm?.Message);
    }

    [Fact]
    public void Refresh_UsesRetailExistingAndNewBindingTooltipTemplates()
    {
        var snapshot = new RetailActionMapSnapshot(new[]
        {
            Row(0x4, 0x29, RetailActionClass.Movement),
        });
        var fake = new FakeBindings();
        fake.Mapped[InputAction.MovementForward] =
            new List<Binding> { new(ChordW, InputAction.MovementForward) };
        ImportedLayout layout = FixtureLoader.LoadKeyboardConfig();
        KeyboardConfigController controller = KeyboardConfigController.Bind(
            layout, snapshot, MakeTemplateResolver(), ResolveSyntheticString, fake.ToBindings())!;

        KeyboardConfigController.RowView row = Assert.Single(controller.Rows);
        Assert.Equal("(W) existing binding", row.KeyButtons[0].TooltipText);
        Assert.Equal("new binding", row.KeyButtons[1].TooltipText);
        Assert.Equal("new binding", row.KeyButtons[2].TooltipText);

        row.KeyButtons[0].OnRightClick!.Invoke();
        Assert.Equal("new binding", row.KeyButtons[0].TooltipText);
    }

    [Fact]
    public void Capture_ChordAlreadyInAnotherSlotOfSameRow_IsRetailNoOp()
    {
        var snapshot = new RetailActionMapSnapshot(new[]
        {
            Row(0x4, 0x29, RetailActionClass.Movement),
        });
        var fake = new FakeBindings();
        fake.Mapped[InputAction.MovementForward] = new List<Binding>
        {
            new(ChordW, InputAction.MovementForward),
            new(ChordUp, InputAction.MovementForward),
        };
        fake.Mapped[InputAction.AcdreamToggleAudioMute] = new List<Binding>
        {
            new(ChordW, InputAction.AcdreamToggleAudioMute),
        };
        ImportedLayout layout = FixtureLoader.LoadKeyboardConfig();
        KeyboardConfigController controller = KeyboardConfigController.Bind(
            layout, snapshot, MakeTemplateResolver(), ResolveSyntheticString, fake.ToBindings())!;

        KeyboardConfigController.RowView row = Assert.Single(controller.Rows);
        row.KeyButtons[2].OnClick!.Invoke();
        fake.Capture(ChordW);

        Assert.Equal(new[] { ChordW, ChordUp }, row.Model.Current);
        Assert.Null(fake.PendingConfirm);
        Assert.Empty(fake.Messages);
        Assert.Empty(fake.MappedSets);
    }

    [Fact]
    public void Capture_LeftOrRightMouseButton_RemainsArmedUntilSupportedInput()
    {
        var snapshot = new RetailActionMapSnapshot(new[]
        {
            Row(0x4, 0x29, RetailActionClass.Movement),
        });
        var fake = new FakeBindings { WireInstructions = true };
        ImportedLayout layout = FixtureLoader.LoadKeyboardConfig();
        KeyboardConfigController controller = KeyboardConfigController.Bind(
            layout, snapshot, MakeTemplateResolver(), ResolveSyntheticString, fake.ToBindings())!;

        KeyboardConfigController.RowView row = Assert.Single(controller.Rows);
        row.KeyButtons[0].OnClick!.Invoke();
        fake.Capture(LeftMouse);

        Assert.NotNull(fake.PendingCapture);
        Assert.Empty(fake.InstructionCloses);
        Assert.Empty(row.Model.Current);

        fake.Capture(ChordW);

        Assert.Null(fake.PendingCapture);
        Assert.Equal(new[] { ChordW }, row.Model.Current);
        Assert.Equal(new[] { 7u }, fake.InstructionCloses);
    }

    [Fact]
    public void Capture_ConflictWithAnotherRow_DeclineLeavesBothRowsUnchanged()
    {
        var snapshot = new RetailActionMapSnapshot(new[]
        {
            Row(0x4, 0x29, RetailActionClass.Movement),
            Row(0x4, 0x2A, RetailActionClass.Movement),
        });
        var fake = new FakeBindings();
        fake.Mapped[InputAction.MovementForward] = new List<Binding> { new(ChordW, InputAction.MovementForward) };
        fake.Mapped[InputAction.MovementBackup] = new List<Binding> { new(ChordA, InputAction.MovementBackup) };
        ImportedLayout layout = FixtureLoader.LoadKeyboardConfig();
        KeyboardConfigController controller = KeyboardConfigController.Bind(
            layout, snapshot, MakeTemplateResolver(), ResolveSyntheticString, fake.ToBindings())!;

        KeyboardConfigController.RowView forward = controller.Rows.Single(r => r.ActionId == 0x29u);
        KeyboardConfigController.RowView backup = controller.Rows.Single(r => r.ActionId == 0x2Au);

        forward.KeyButtons[0].OnClick!.Invoke();
        fake.Capture(ChordA);
        fake.RespondToConfirm(false);

        Assert.Equal(new[] { ChordW }, forward.Model.Current); // untouched
        Assert.Equal(new[] { ChordA }, backup.Model.Current);  // untouched
    }

    [Fact]
    public void Capture_SharedChordAcrossNonConflictingCombatContexts_KeepsBothBindings()
    {
        const uint meleeMap = 0x10000003u;
        const uint missileMap = 0x10000004u;
        var conflicts = new Dictionary<uint, IReadOnlySet<uint>>
        {
            [meleeMap] = new HashSet<uint> { meleeMap },
            [missileMap] = new HashSet<uint> { missileMap },
        };
        var snapshot = new RetailActionMapSnapshot(new[]
        {
            Row(meleeMap, 0x1000005Du, RetailActionClass.Combat),
            Row(missileMap, 0x100000F1u, RetailActionClass.Combat),
        }, conflicts);
        var fake = new FakeBindings();
        fake.Mapped[InputAction.CombatAimLow] =
            new List<Binding> { new(ChordA, InputAction.CombatAimLow, Scope: InputScope.MissileCombat) };
        ImportedLayout layout = FixtureLoader.LoadKeyboardConfig();
        KeyboardConfigController controller = KeyboardConfigController.Bind(
            layout, snapshot, MakeTemplateResolver(), ResolveSyntheticString, fake.ToBindings())!;

        KeyboardConfigController.RowView melee = controller.Rows.Single(
            row => row.InputMapId == meleeMap);
        KeyboardConfigController.RowView missile = controller.Rows.Single(
            row => row.InputMapId == missileMap);
        melee.KeyButtons[0].OnClick!.Invoke();
        fake.Capture(ChordA);

        Assert.Null(fake.PendingConfirm);
        Assert.Contains(ChordA, melee.Model.Current);
        Assert.Contains(ChordA, missile.Model.Current);
    }

    [Fact]
    public void Capture_SharedChordAcrossDatConflictingContexts_StillPrompts()
    {
        const uint movementMap = 0x4u;
        const uint uiMap = 0x10000009u;
        var conflicts = new Dictionary<uint, IReadOnlySet<uint>>
        {
            [movementMap] = new HashSet<uint> { movementMap, uiMap },
        };
        var snapshot = new RetailActionMapSnapshot(new[]
        {
            Row(movementMap, 0x29u, RetailActionClass.Movement),
            Row(uiMap, 0x10000019u, RetailActionClass.Ui),
        }, conflicts);
        var fake = new FakeBindings();
        fake.Mapped[InputAction.ToggleInventoryPanel] =
            new List<Binding> { new(ChordA, InputAction.ToggleInventoryPanel) };
        ImportedLayout layout = FixtureLoader.LoadKeyboardConfig();
        KeyboardConfigController controller = KeyboardConfigController.Bind(
            layout, snapshot, MakeTemplateResolver(), ResolveSyntheticString, fake.ToBindings())!;

        KeyboardConfigController.RowView movement = controller.Rows.Single(
            row => row.InputMapId == movementMap);
        movement.KeyButtons[0].OnClick!.Invoke();
        fake.Capture(ChordA);

        Assert.NotNull(fake.PendingConfirm);
        Assert.DoesNotContain(ChordA, movement.Model.Current);
    }

    [Fact]
    public void Capture_ConflictWithNonBindableAcdreamAction_ShowsRetailMessageDialog()
    {
        var snapshot = new RetailActionMapSnapshot(new[] { Row(0x4, 0x29, RetailActionClass.Movement) });
        var fake = new FakeBindings();
        var muteChord = new KeyChord(Silk.NET.Input.Key.M, ModifierMask.Ctrl);
        fake.Mapped[InputAction.AcdreamToggleAudioMute] =
            new List<Binding> { new(muteChord, InputAction.AcdreamToggleAudioMute) };
        ImportedLayout layout = FixtureLoader.LoadKeyboardConfig();
        KeyboardConfigController controller = KeyboardConfigController.Bind(
            layout, snapshot, MakeTemplateResolver(), ResolveSyntheticString, fake.ToBindings())!;

        KeyboardConfigController.RowView forward = controller.Rows.Single();
        forward.KeyButtons[0].OnClick!.Invoke();
        fake.Capture(muteChord);

        Assert.Null(fake.PendingConfirm);
        Assert.DoesNotContain(muteChord, forward.Model.Current);
        Assert.Contains(fake.Messages, message => message.Contains("cannot overwrite"));
        Assert.Equal(muteChord, Assert.Single(fake.Mapped[InputAction.AcdreamToggleAudioMute]).Chord);
    }

    [Fact]
    public void Capture_ConflictWithBothARowAndANonBindableAction_NonBindableWins()
    {
        var snapshot = new RetailActionMapSnapshot(new[]
        {
            Row(0x4, 0x29, RetailActionClass.Movement),
            Row(0x4, 0x2A, RetailActionClass.Movement),
        });
        var fake = new FakeBindings();
        var sharedChord = new KeyChord(Silk.NET.Input.Key.M, ModifierMask.Ctrl);
        fake.Mapped[InputAction.MovementBackup] = new List<Binding> { new(sharedChord, InputAction.MovementBackup) };
        fake.Mapped[InputAction.AcdreamToggleAudioMute] =
            new List<Binding> { new(sharedChord, InputAction.AcdreamToggleAudioMute) };
        ImportedLayout layout = FixtureLoader.LoadKeyboardConfig();
        KeyboardConfigController controller = KeyboardConfigController.Bind(
            layout, snapshot, MakeTemplateResolver(), ResolveSyntheticString, fake.ToBindings())!;

        KeyboardConfigController.RowView forward = controller.Rows.Single(r => r.ActionId == 0x29u);
        forward.KeyButtons[0].OnClick!.Invoke();
        fake.Capture(sharedChord);

        Assert.Null(fake.PendingConfirm);
        Assert.Contains(fake.Messages, message => message.Contains("cannot overwrite"));
        Assert.DoesNotContain(sharedChord, forward.Model.Current);
    }

    [Fact]
    public void OkButton_AppliesCommitsSavesAndToggles()
    {
        var snapshot = new RetailActionMapSnapshot(new[] { Row(0x4, 0x29, RetailActionClass.Movement) });
        var fake = new FakeBindings();
        ImportedLayout layout = FixtureLoader.LoadKeyboardConfig();
        KeyboardConfigController controller = KeyboardConfigController.Bind(
            layout, snapshot, MakeTemplateResolver(), ResolveSyntheticString, fake.ToBindings())!;

        KeyboardConfigController.RowView row = controller.Rows.Single();
        row.KeyButtons[0].OnClick!.Invoke();
        fake.Capture(ChordW);
        Assert.True(row.Model.Changed);

        UiButton ok = (UiButton)layout.FindElement(0x1000002Cu)!;
        ok.OnClick!.Invoke();

        Assert.False(row.Model.Changed);
        Assert.Equal(1, fake.SaveCalls);
        Assert.Equal(1, fake.ToggleCalls);
    }

    [Fact]
    public void LoadFile_ReplacesRowsAndRevertBaseline_AndRefreshesFilename()
    {
        var snapshot = new RetailActionMapSnapshot(new[]
        {
            Row(0x4, 0x29, RetailActionClass.Movement),
        });
        var fake = new FakeBindings();
        fake.Mapped[InputAction.MovementForward] =
            [new Binding(ChordW, InputAction.MovementForward)];
        ImportedLayout layout = FixtureLoader.LoadKeyboardConfig();
        KeyboardConfigController controller = KeyboardConfigController.Bind(
            layout, snapshot, MakeTemplateResolver(), ResolveSyntheticString,
            fake.ToProfileBindings())!;

        ((UiButton)layout.FindElement(0x10000027u)!).OnClick!.Invoke();
        Assert.NotNull(fake.PendingLoadCompleted);

        fake.Mapped[InputAction.MovementForward] =
            [new Binding(ChordUp, InputAction.MovementForward)];
        fake.CurrentKeymapFilename = "friends.keymap";
        fake.PendingLoadCompleted!();

        ActionKeyMapOptionRow row = controller.Rows.Single().Model;
        Assert.Equal(new[] { ChordUp }, row.Current);
        Assert.Equal(new[] { ChordUp }, row.Saved);
        Assert.False(row.Changed);
        UiText filename = (UiText)layout.FindElement(0x10000028u)!;
        Assert.Equal("friends.keymap", Assert.Single(filename.LinesProvider()).Text);
    }

    [Fact]
    public void SaveAs_RefreshesActiveFilenameOnlyAfterSuccessfulCallback()
    {
        var snapshot = new RetailActionMapSnapshot(new[]
        {
            Row(0x4, 0x29, RetailActionClass.Movement),
        });
        var fake = new FakeBindings();
        ImportedLayout layout = FixtureLoader.LoadKeyboardConfig();
        _ = KeyboardConfigController.Bind(
            layout, snapshot, MakeTemplateResolver(), ResolveSyntheticString,
            fake.ToProfileBindings());
        UiText filename = (UiText)layout.FindElement(0x10000028u)!;

        ((UiButton)layout.FindElement(0x10000029u)!).OnClick!.Invoke();
        Assert.NotNull(fake.PendingSaveCompleted);
        Assert.Equal("acdream.keymap", Assert.Single(filename.LinesProvider()).Text);

        fake.CurrentKeymapFilename = "alternate.keymap";
        fake.PendingSaveCompleted!();
        Assert.Equal("alternate.keymap", Assert.Single(filename.LinesProvider()).Text);
    }

    [Fact]
    public void RevertButton_IsEnabledExactlyWhileWorkingMapDiffersFromSavedMap()
    {
        var snapshot = new RetailActionMapSnapshot(new[]
        {
            Row(0x4, 0x29, RetailActionClass.Movement),
        });
        var fake = new FakeBindings();
        fake.Mapped[InputAction.MovementForward] =
            new List<Binding> { new(ChordW, InputAction.MovementForward) };
        ImportedLayout layout = FixtureLoader.LoadKeyboardConfig();
        KeyboardConfigController controller = KeyboardConfigController.Bind(
            layout, snapshot, MakeTemplateResolver(), ResolveSyntheticString, fake.ToBindings())!;
        UiButton revert = (UiButton)layout.FindElement(0x1000002Bu)!;

        Assert.False(revert.Enabled);

        KeyboardConfigController.RowView row = controller.Rows.Single();
        row.KeyButtons[0].OnClick!.Invoke();
        fake.Capture(ChordUp);
        Assert.True(controller.Page.Changed);
        Assert.True(revert.Enabled);

        revert.OnClick!.Invoke();
        Assert.Equal(new[] { ChordW }, row.Model.Current);
        Assert.False(controller.Page.Changed);
        Assert.False(revert.Enabled);

        // Defaults is also a live uncommitted edit when the DAT default does
        // not equal the saved user map, and therefore re-enables Revert.
        UiButton defaults = (UiButton)layout.FindElement(0x1000002Au)!;
        defaults.OnClick!.Invoke();
        Assert.True(controller.Page.Changed);
        Assert.True(revert.Enabled);

        UiButton ok = (UiButton)layout.FindElement(0x1000002Cu)!;
        ok.OnClick!.Invoke();
        Assert.False(controller.Page.Changed);
        Assert.False(revert.Enabled);
    }

    [Fact]
    public void CancelButton_RevertsUncommittedEditAndToggles()
    {
        var snapshot = new RetailActionMapSnapshot(new[] { Row(0x4, 0x29, RetailActionClass.Movement) });
        var fake = new FakeBindings();
        fake.Mapped[InputAction.MovementForward] = new List<Binding> { new(ChordW, InputAction.MovementForward) };
        ImportedLayout layout = FixtureLoader.LoadKeyboardConfig();
        KeyboardConfigController controller = KeyboardConfigController.Bind(
            layout, snapshot, MakeTemplateResolver(), ResolveSyntheticString, fake.ToBindings())!;

        KeyboardConfigController.RowView row = controller.Rows.Single();
        row.KeyButtons[0].OnClick!.Invoke();
        fake.Capture(ChordUp); // now [Up] uncommitted (slot 0 replaced)

        UiButton cancel = (UiButton)layout.FindElement(0x1000002Du)!;
        cancel.OnClick!.Invoke();

        Assert.Equal(new[] { ChordW }, row.Model.Current); // reverted to Saved
        Assert.Equal(1, fake.ToggleCalls);
        Assert.Equal(0, fake.SaveCalls);
    }

    [Fact]
    public void DefaultsButton_RestoresDatDefaultLive_WithoutCommitting()
    {
        var snapshot = new RetailActionMapSnapshot(new[]
        {
            Row(0x4, 0x29, RetailActionClass.Movement, defaults:
                new[] { new RetailKeyChord(0x11, 0, 0, 3) }), // DIK_W
        });
        var fake = new FakeBindings();
        fake.Mapped[InputAction.MovementForward] = new List<Binding> { new(ChordUp, InputAction.MovementForward) };
        ImportedLayout layout = FixtureLoader.LoadKeyboardConfig();
        KeyboardConfigController controller = KeyboardConfigController.Bind(
            layout, snapshot, MakeTemplateResolver(), ResolveSyntheticString, fake.ToBindings())!;

        KeyboardConfigController.RowView row = controller.Rows.Single();
        Assert.Equal(new[] { ChordUp }, row.Model.Current);

        UiButton defaultsButton = (UiButton)layout.FindElement(0x1000002Au)!;
        defaultsButton.OnClick!.Invoke();

        Assert.Equal(new[] { ChordW }, row.Model.Current);
        Assert.True(row.Model.Changed);
    }

    [Fact]
    public void DefaultsButton_PreservesActivationAndScope_ForAHoldScopedAction()
    {
        var snapshot = new RetailActionMapSnapshot(new[]
        {
            Row(0x10000003, 0x1000005D, RetailActionClass.Combat, defaults:
                new[] { new RetailKeyChord(0xD3, 0, 0, 3) }),
        });
        var fake = new FakeBindings();
        fake.Mapped[InputAction.CombatLowAttack] = new List<Binding>
        {
            new(ChordUp, InputAction.CombatLowAttack, ActivationType.Hold, InputScope.MeleeCombat),
        };
        ImportedLayout layout = FixtureLoader.LoadKeyboardConfig();
        KeyboardConfigController controller = KeyboardConfigController.Bind(
            layout, snapshot, MakeTemplateResolver(), ResolveSyntheticString, fake.ToBindings())!;

        UiButton defaultsButton = (UiButton)layout.FindElement(0x1000002Au)!;
        defaultsButton.OnClick!.Invoke();

        (InputAction Action, IReadOnlyList<Binding> Value) written = Assert.Single(fake.MappedSets);
        Assert.Equal(InputAction.CombatLowAttack, written.Action);
        Binding result = Assert.Single(written.Value);
        Assert.Equal(Silk.NET.Input.Key.Delete, result.Chord.Key); // the DAT default key
        Assert.Equal(ActivationType.Hold, result.Activation);      // preserved, not reset to Press
        Assert.Equal(InputScope.MeleeCombat, result.Scope);        // preserved, not reset to Game
    }

    [Fact]
    public void CancelButton_PreservesActivationAndScope()
    {
        var snapshot = new RetailActionMapSnapshot(new[] { Row(0x4, 0x32, RetailActionClass.Movement) });
        var fake = new FakeBindings();
        fake.Mapped[InputAction.MovementWalkMode] = new List<Binding>
        {
            new(ChordW, InputAction.MovementWalkMode, ActivationType.Hold, InputScope.Game),
        };
        ImportedLayout layout = FixtureLoader.LoadKeyboardConfig();
        KeyboardConfigController controller = KeyboardConfigController.Bind(
            layout, snapshot, MakeTemplateResolver(), ResolveSyntheticString, fake.ToBindings())!;

        KeyboardConfigController.RowView row = controller.Rows.Single();
        row.KeyButtons[0].OnClick!.Invoke();
        fake.Capture(ChordUp); // uncommitted edit

        UiButton cancel = (UiButton)layout.FindElement(0x1000002Du)!;
        cancel.OnClick!.Invoke();

        (InputAction Action, IReadOnlyList<Binding> Value) written = fake.MappedSets[^1];
        Binding result = Assert.Single(written.Value);
        Assert.Equal(ChordW, result.Chord);            // reverted to saved
        Assert.Equal(ActivationType.Hold, result.Activation);
    }

    [Fact]
    public void CameraContext5And6_AreIndependentRows_NotAliased()
    {
        var snapshot = new RetailActionMapSnapshot(new[]
        {
            Row(0x5, 0x35, RetailActionClass.Camera, defaults: new[] { new RetailKeyChord(0x4B, 0, 0, 3) }),
            Row(0x6, 0x35, RetailActionClass.Camera, defaults: new[] { new RetailKeyChord(0xCB, 0, 0, 3) }),
        });
        var fake = new FakeBindings();
        fake.Mapped[InputAction.CameraRotateLeft] =
        [new Binding(ChordA, InputAction.CameraRotateLeft)];
        fake.Mapped[InputAction.CameraAlternateRotateLeft] =
        [new Binding(
            ChordUp,
            InputAction.CameraAlternateRotateLeft,
            Scope: InputScope.Camera)];
        ImportedLayout layout = FixtureLoader.LoadKeyboardConfig();
        KeyboardConfigController controller = KeyboardConfigController.Bind(
            layout, snapshot, MakeTemplateResolver(), ResolveSyntheticString, fake.ToBindings())!;

        KeyboardConfigController.RowView ctx5 = controller.Rows.Single(r => r.InputMapId == 0x5u);
        KeyboardConfigController.RowView ctx6 = controller.Rows.Single(r => r.InputMapId == 0x6u);

        Assert.Equal(InputAction.CameraRotateLeft, ctx5.MappedAction);
        Assert.Equal(InputAction.CameraAlternateRotateLeft, ctx6.MappedAction);

        // Both rows display their independent live bindings.
        Assert.NotEmpty(ctx6.Model.Current);
        var ctx6InitialDisplay = ctx6.Model.Current.ToArray();

        ctx5.KeyButtons[0].OnClick!.Invoke();
        fake.Capture(ChordW);
        Assert.Contains(ChordW, ctx5.Model.Current);
        Assert.Equal(ctx6InitialDisplay, ctx6.Model.Current);
        Assert.Empty(fake.Unmapped);

        ctx6.KeyButtons[0].OnClick!.Invoke();
        fake.Capture(ChordA);
        Assert.Contains(ChordA, ctx6.Model.Current);
        Assert.Contains(ChordW, ctx5.Model.Current);
        Assert.Contains(fake.MappedSets, write =>
            write.Action == InputAction.CameraAlternateRotateLeft
            && write.Value.Any(binding => binding.Chord == ChordA));
    }

    [Fact]
    public void Bind_MissingWindowRoot_ReturnsNull()
    {
        ElementInfo empty = new() { Id = 0x99999999u, Type = 3 };
        ImportedLayout emptyLayout = LayoutImporter.Build(empty, NoTex, null);
        var snapshot = new RetailActionMapSnapshot(Array.Empty<RetailActionMapRow>());
        var fake = new FakeBindings();

        KeyboardConfigController? controller = KeyboardConfigController.Bind(
            emptyLayout, snapshot, (_, _) => null, (_, _) => null, fake.ToBindings());

        Assert.Null(controller);
    }


    [Fact]
    public void IdentityMap_IsInjective_NoTwoRowsShareOneInputAction()
    {
        var seen = new Dictionary<InputAction, (uint MapId, uint ActionId)>();
        foreach (((uint mapId, uint actionId), InputAction action) in RetailActionIdentityTable.Map)
        {
            Assert.False(
                seen.TryGetValue(action, out (uint MapId, uint ActionId) prior),
                $"InputAction.{action} is mapped by BOTH (0x{prior.MapId:X}, 0x{prior.ActionId:X}) "
                + $"and (0x{mapId:X}, 0x{actionId:X}) — aliasing reintroduces the M2 twin-row clobber.");
            seen[action] = (mapId, actionId);
        }
        Assert.Equal(306, seen.Count);
    }


    [Fact]
    public void EveryRetailRowCaptionIsEnabled()
    {
        var snapshot = new RetailActionMapSnapshot(new[]
        {
            Row(0x4, 0x29, RetailActionClass.Movement),                 // -> MovementForward (mapped)
            Row(0x10000006, 0x100000A0, RetailActionClass.Emote),       // -> EmoteBowDeep
        });

        ImportedLayout layout = FixtureLoader.LoadKeyboardConfig();
        var fake = new FakeBindings();
        KeyboardConfigController controller = KeyboardConfigController.Bind(
            layout, snapshot, MakeTemplateResolver(), ResolveSyntheticString, fake.ToBindings())!;

        KeyboardConfigController.RowView forward = controller.Rows.Single(r => r.ActionId == 0x29u);
        Assert.NotNull(forward.MappedAction);
        UiText forwardCaption = RowCaption(forward);
        Assert.Equal(Vector4.One, forwardCaption.DefaultColor);

        KeyboardConfigController.RowView bowDeep = controller.Rows.Single(r => r.ActionId == 0x100000A0u);
        Assert.Equal(InputAction.EmoteBowDeep, bowDeep.MappedAction);
        UiText bowDeepCaption = RowCaption(bowDeep);
        Assert.Equal(Vector4.One, bowDeepCaption.DefaultColor);
    }

    private static UiText RowCaption(KeyboardConfigController.RowView row)
    {
        UiElement parent = row.KeyButtons.First().Parent
            ?? throw new InvalidOperationException("row's key button has no parent element");
        return parent.Children.OfType<UiText>().Single();
    }
}
