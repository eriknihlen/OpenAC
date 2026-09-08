using System.Collections.Generic;
using System.IO;
using AcDream.App.UI;
using AcDream.App.UI.Testing;
using AcDream.Core.Items;
using AcDream.UI.Abstractions.Input;

namespace AcDream.App.Tests.UI;

public sealed class RetailUiAutomationProbeTests
{
    private sealed class SpyHandler : IItemListDragHandler
    {
        public (UiItemList list, UiItemSlot cell, ItemDragPayload payload)? LastDrop;
        public (UiItemList list, UiItemSlot cell, ItemDragPayload payload)? LastLift;

        public void OnDragLift(UiItemList sourceList, UiItemSlot sourceCell, ItemDragPayload payload)
            => LastLift = (sourceList, sourceCell, payload);

        public ItemDragAcceptance OnDragOver(UiItemList targetList, UiItemSlot targetCell, ItemDragPayload payload)
            => ItemDragAcceptance.Accept;

        public void HandleDropRelease(UiItemList targetList, UiItemSlot targetCell, ItemDragPayload payload)
            => LastDrop = (targetList, targetCell, payload);
    }

    private sealed class FakeRuntime : IRetailUiAutomationRuntime
    {
        private sealed class FakeCheckpoint(int sequence, string name) :
            IRetailUiAutomationCheckpoint
        {
            public int Sequence { get; } = sequence;
            public string Name { get; } = name;
            public RetailUiAutomationCheckpointStatus Status { get; set; } =
                RetailUiAutomationCheckpointStatus.Pending;
            public string? Error { get; set; }
        }

        private readonly List<FakeCheckpoint> _checkpointTokens = [];

        public bool IsWorldReady { get; set; }
        public bool IsWorldViewportVisible { get; set; }
        public int PortalMaterializationCount { get; set; }
        public int RenderPackPerformanceSampleCount { get; set; }
        public bool RenderPackFailedToRetail { get; set; }
        public RetailUiAutomationRenderPackStatus RenderPackStatus { get; set; } =
            RetailUiAutomationRenderPackStatus.Retail;
        public int FramebufferWidth { get; set; } = 1280;
        public int FramebufferHeight { get; set; } = 720;
        public int RenderPackPerformanceResetCount { get; private set; }
        public List<string> RenderPackSelections { get; } = [];
        public int RenderPackDisableCount { get; private set; }
        public int RenderPackReenableCount { get; private set; }
        public List<(int Width, int Height)> FramebufferResizes { get; } = [];
        public bool PublishFramebufferSizeOnResize { get; set; }
        public bool ResizeSucceeds { get; set; } = true;
        public int ClientCloseRequestCount { get; private set; }
        public List<string> Checkpoints { get; } = new();
        public HashSet<string> ScreenshotRequests { get; } = new();
        public HashSet<string> CompletedScreenshots { get; } = new();
        public HashSet<string> PublishedSignals { get; } = new();

        public bool TryResetRenderPackPerformance(out string error)
        {
            RenderPackPerformanceResetCount++;
            RenderPackPerformanceSampleCount = 0;
            error = string.Empty;
            return true;
        }

        public bool TrySelectRenderPack(string presetId, out string error)
        {
            RenderPackSelections.Add(presetId);
            error = string.Empty;
            return true;
        }

        public bool TryDisableRenderPack(out string error)
        {
            RenderPackDisableCount++;
            error = string.Empty;
            return true;
        }

        public bool TryReenableRenderPack(out string error)
        {
            RenderPackReenableCount++;
            error = string.Empty;
            return true;
        }

        public bool TryResizeFramebuffer(int width, int height, out string error)
        {
            FramebufferResizes.Add((width, height));
            if (!ResizeSucceeds)
            {
                error = "resize rejected";
                return false;
            }
            if (PublishFramebufferSizeOnResize)
            {
                FramebufferWidth = width;
                FramebufferHeight = height;
            }
            error = string.Empty;
            return true;
        }

        public bool TryRequestClientClose(out string error)
        {
            ClientCloseRequestCount++;
            error = string.Empty;
            return true;
        }

        public bool TryRequestCheckpoint(
            string name,
            out IRetailUiAutomationCheckpoint? checkpoint,
            out string error)
        {
            Checkpoints.Add(name);
            var token = new FakeCheckpoint(_checkpointTokens.Count + 1, name);
            _checkpointTokens.Add(token);
            checkpoint = token;
            error = string.Empty;
            return true;
        }

        public void CancelCheckpoint(IRetailUiAutomationCheckpoint checkpoint)
        {
            var token = Assert.IsType<FakeCheckpoint>(checkpoint);
            token.Status = RetailUiAutomationCheckpointStatus.Cancelled;
            token.Error = $"checkpoint '{token.Name}' cancelled";
        }

        public void CompleteCheckpoint(
            string name,
            RetailUiAutomationCheckpointStatus status =
                RetailUiAutomationCheckpointStatus.Succeeded,
            string? error = null)
        {
            FakeCheckpoint token = Assert.Single(
                _checkpointTokens,
                value => value.Name == name);
            if (token.Status != RetailUiAutomationCheckpointStatus.Pending)
                throw new InvalidOperationException("checkpoint is already terminal");
            token.Status = status;
            token.Error = error;
        }

        public bool TryRequestScreenshot(string name, out string error)
        {
            ScreenshotRequests.Add(name);
            error = string.Empty;
            return true;
        }

        public bool IsScreenshotComplete(string name) => CompletedScreenshots.Contains(name);

        public bool TryIsAutomationSignalPublished(
            string name,
            out bool published,
            out string error)
        {
            published = PublishedSignals.Contains(name);
            error = string.Empty;
            return true;
        }
    }

    private static (UiRoot root, UiItemList source, UiItemList target, SpyHandler handler, ClientObjectTable objects)
        RootWithTwoItemLists()
    {
        var root = new UiRoot { Width = 240, Height = 120 };
        var objects = new ClientObjectTable();

        var source = new UiItemList(_ => (1u, 1, 1))
        {
            DatElementId = 0x10000010u,
            Left = 10,
            Top = 10,
            Width = 32,
            Height = 32,
        };
        source.Cell.SlotIndex = 0;
        source.Cell.SourceKind = ItemDragSource.Inventory;
        source.Cell.SetItem(0x5001u, 0x99u);

        var target = new UiItemList(_ => (1u, 1, 1))
        {
            DatElementId = 0x10000020u,
            Left = 70,
            Top = 10,
            Width = 32,
            Height = 32,
        };
        target.Cell.SlotIndex = 1;

        var handler = new SpyHandler();
        target.RegisterDragHandler(handler);

        root.AddChild(source);
        root.AddChild(target);
        return (root, source, target, handler, objects);
    }

    [Fact]
    public void Snapshot_listsDatElementsAndItemSlots()
    {
        var (root, _, _, _, objects) = RootWithTwoItemLists();
        var probe = new RetailUiAutomationProbe(root, objects);

        var rows = probe.Snapshot();

        Assert.Contains(rows, r => r.DatElementId == 0x10000010u && r.TypeName == nameof(UiItemList));
        var itemRow = Assert.Single(rows, r => r.ItemId == 0x5001u);
        Assert.Equal(ItemDragSource.Inventory, itemRow.SourceKind);
        Assert.Equal(0, itemRow.SlotIndex);
        Assert.True(itemRow.Width > 0f);
        Assert.True(itemRow.Height > 0f);
    }

    [Fact]
    public void DoubleClickItem_routesThroughUiRootDoubleClick()
    {
        var (root, source, _, _, objects) = RootWithTwoItemLists();
        bool doubleClicked = false;
        source.Cell.DoubleClicked = () => doubleClicked = true;
        var probe = new RetailUiAutomationProbe(root, objects);

        Assert.True(probe.DoubleClickItem(0x5001u, ItemDragSource.Inventory));

        Assert.True(doubleClicked);
    }

    [Fact]
    public void DragItemToElement_deliversDropReleaseToTargetCell()
    {
        var (root, _, target, handler, objects) = RootWithTwoItemLists();
        var probe = new RetailUiAutomationProbe(root, objects);

        Assert.True(probe.DragItemToElement(0x5001u, 0x10000020u, ItemDragSource.Inventory));

        Assert.NotNull(handler.LastDrop);
        Assert.Same(target, handler.LastDrop!.Value.list);
        Assert.Same(target.Cell, handler.LastDrop.Value.cell);
        Assert.Equal(0x5001u, handler.LastDrop.Value.payload.ObjId);
    }

    [Fact]
    public void DragItemOutside_raisesRootOutsideUiEvent()
    {
        var (root, _, _, _, objects) = RootWithTwoItemLists();
        object? payload = null;
        (int x, int y) release = default;
        root.DragReleasedOutsideUi += (p, x, y) =>
        {
            payload = p;
            release = (x, y);
        };
        var probe = new RetailUiAutomationProbe(root, objects);

        Assert.True(probe.DragItemOutside(0x5001u, 220, 100, ItemDragSource.Inventory));

        var itemPayload = Assert.IsType<ItemDragPayload>(payload);
        Assert.Equal(0x5001u, itemPayload.ObjId);
        Assert.Equal((220, 100), release);
    }

    [Fact]
    public void AssertItem_checksObjectTableState()
    {
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 0x5001u,
            ContainerId = 0x7001u,
            ContainerSlot = 3,
            CurrentlyEquippedLocation = EquipMask.ChestArmor | EquipMask.UpperArmArmor,
        });
        var probe = new RetailUiAutomationProbe(new UiRoot(), objects);

        var ok = probe.AssertItem(
            0x5001u,
            equippedLocation: EquipMask.ChestArmor | EquipMask.UpperArmArmor,
            containerId: 0x7001u,
            slot: 3);
        var bad = probe.AssertItem(0x5001u, slot: 4);

        Assert.True(ok.Success);
        Assert.False(bad.Success);
    }

    [Fact]
    public void ScriptRunner_waitItemThenDoubleClick_executesThroughProbe()
    {
        var (root, source, _, _, objects) = RootWithTwoItemLists();
        bool doubleClicked = false;
        source.Cell.DoubleClicked = () => doubleClicked = true;
        var probe = new RetailUiAutomationProbe(root, objects);
        var logs = new List<string>();
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ui-probe.txt");
        File.WriteAllLines(path, new[]
        {
            "wait item 0x5001 inventory 100",
            "doubleclick item 0x5001 inventory",
        });

        try
        {
            var runner = new RetailUiAutomationScriptRunner(probe, path, dumpOnStart: false, logs.Add);

            runner.Tick(0.016);

            Assert.True(doubleClicked);
            Assert.True(runner.Completed);
            Assert.Contains(logs, line => line.Contains("UI probe script complete"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScriptRunner_command_routesThroughInjectedClientSubmitPath()
    {
        var (root, _, _, _, objects) = RootWithTwoItemLists();
        var probe = new RetailUiAutomationProbe(root, objects);
        var submitted = new List<string>();
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ui-probe.txt");
        File.WriteAllText(path, "command /ls");

        try
        {
            var runner = new RetailUiAutomationScriptRunner(
                probe,
                path,
                dumpOnStart: false,
                submitCommand: submitted.Add);

            runner.Tick(0.016);

            Assert.Equal(["/ls"], submitted);
            Assert.True(runner.Completed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScriptRunner_sleep_preserves_fractional_uncapped_frame_time()
    {
        var (root, _, _, _, objects) = RootWithTwoItemLists();
        var probe = new RetailUiAutomationProbe(root, objects);
        var submitted = new List<string>();
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ui-probe.txt");
        File.WriteAllLines(path, ["sleep 10", "command /ls"]);

        try
        {
            var runner = new RetailUiAutomationScriptRunner(
                probe,
                path,
                dumpOnStart: false,
                submitCommand: submitted.Add);

            runner.Tick(0d); // Arm the sleep at script time zero.
            for (int i = 0; i < 1_000; i++)
                runner.Tick(0.000001); // 1 ms total, not the old artificial 1,000 ms.

            Assert.Empty(submitted);
            Assert.False(runner.Completed);

            runner.Tick(0.009); // Exact 10 ms deadline, including the 1 ms above.

            Assert.Equal(["/ls"], submitted);
            Assert.True(runner.Completed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScriptRunner_sleep_releases_on_exact_fractional_frame_deadline()
    {
        var (root, _, _, _, objects) = RootWithTwoItemLists();
        var probe = new RetailUiAutomationProbe(root, objects);
        var submitted = new List<string>();
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ui-probe.txt");
        File.WriteAllLines(path, ["sleep 1000", "command /ls"]);

        try
        {
            var runner = new RetailUiAutomationScriptRunner(
                probe,
                path,
                dumpOnStart: false,
                submitCommand: submitted.Add);

            runner.Tick(0d); // Arm the sleep at script time zero.
            for (int i = 0; i < 60; i++)
                runner.Tick(1d / 60d);

            Assert.Equal(["/ls"], submitted);
            Assert.True(runner.Completed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScriptRunner_sleep_rebases_fractional_clock_after_long_prior_command()
    {
        var (root, _, _, _, objects) = RootWithTwoItemLists();
        var probe = new RetailUiAutomationProbe(root, objects);
        var submitted = new List<string>();
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ui-probe.txt");
        File.WriteAllLines(path, ["sleep 1000000", "sleep 1000", "command /ls"]);

        try
        {
            var runner = new RetailUiAutomationScriptRunner(
                probe,
                path,
                dumpOnStart: false,
                submitCommand: submitted.Add);

            runner.Tick(0d);
            runner.Tick(1000d);
            for (int i = 0; i < 60; i++)
                runner.Tick(1d / 60d);

            Assert.Equal(["/ls"], submitted);
            Assert.True(runner.Completed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScriptRunner_sleep_ignores_invalid_or_overflowing_frame_deltas()
    {
        var (root, _, _, _, objects) = RootWithTwoItemLists();
        var probe = new RetailUiAutomationProbe(root, objects);
        var submitted = new List<string>();
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ui-probe.txt");
        File.WriteAllLines(path, ["sleep 1", "command /ls"]);

        try
        {
            var runner = new RetailUiAutomationScriptRunner(
                probe,
                path,
                dumpOnStart: false,
                submitCommand: submitted.Add);

            runner.Tick(0d);
            runner.Tick(double.NaN);
            runner.Tick(double.PositiveInfinity);
            runner.Tick(double.NegativeInfinity);
            runner.Tick(double.MaxValue);
            runner.Tick(-1d);

            Assert.Empty(submitted);
            Assert.False(runner.Completed);

            runner.Tick(0.001d);

            Assert.Equal(["/ls"], submitted);
            Assert.True(runner.Completed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(30)]
    [InlineData(120)]
    public void ScriptRunner_wait_timeout_does_not_fire_on_exact_fractional_deadline(int framesPerSecond)
    {
        var (root, _, _, _, objects) = RootWithTwoItemLists();
        var probe = new RetailUiAutomationProbe(root, objects);
        var logs = new List<string>();
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ui-probe.txt");
        File.WriteAllLines(path, ["sleep 1000000", "wait item 0xDEAD inventory 1000"]);

        try
        {
            var runner = new RetailUiAutomationScriptRunner(
                probe,
                path,
                dumpOnStart: false,
                log: logs.Add);

            runner.Tick(0d);
            runner.Tick(1000d);
            for (int i = 0; i < framesPerSecond; i++)
                runner.Tick(1d / framesPerSecond);

            Assert.False(runner.Completed);
            Assert.DoesNotContain(logs, line => line.Contains("timed out", StringComparison.Ordinal));

            runner.Tick(1d / framesPerSecond);

            Assert.True(runner.Completed);
            Assert.Contains(logs, line => line.Contains("timed out", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScriptRunner_one_hour_sleep_releases_on_exact_60Hz_deadline()
    {
        var (root, _, _, _, objects) = RootWithTwoItemLists();
        var probe = new RetailUiAutomationProbe(root, objects);
        var submitted = new List<string>();
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ui-probe.txt");
        File.WriteAllLines(path, ["sleep 3600000", "command /ls"]);

        try
        {
            var runner = new RetailUiAutomationScriptRunner(
                probe,
                path,
                dumpOnStart: false,
                submitCommand: submitted.Add);

            runner.Tick(0d);
            for (int i = 0; i < 60 * 60 * 60; i++)
                runner.Tick(1d / 60d);

            Assert.Equal(["/ls"], submitted);
            Assert.True(runner.Completed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScriptRunner_one_hour_wait_does_not_timeout_on_exact_30Hz_deadline()
    {
        var (root, _, _, _, objects) = RootWithTwoItemLists();
        var probe = new RetailUiAutomationProbe(root, objects);
        var logs = new List<string>();
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ui-probe.txt");
        File.WriteAllText(path, "wait item 0xDEAD inventory 3600000");

        try
        {
            var runner = new RetailUiAutomationScriptRunner(
                probe,
                path,
                dumpOnStart: false,
                log: logs.Add);

            runner.Tick(0d);
            for (int i = 0; i < 30 * 60 * 60; i++)
                runner.Tick(1d / 30d);

            Assert.False(runner.Completed);
            Assert.DoesNotContain(logs, line => line.Contains("timed out", StringComparison.Ordinal));

            runner.Tick(1d / 30d);

            Assert.True(runner.Completed);
            Assert.Contains(logs, line => line.Contains("timed out", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScriptRunner_input_drivesSemanticPressAndHeldEdges()
    {
        var (root, _, _, _, objects) = RootWithTwoItemLists();
        var probe = new RetailUiAutomationProbe(root, objects);
        var presses = new List<InputAction>();
        var held = new List<(InputAction Action, bool Held)>();
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ui-probe.txt");
        File.WriteAllLines(path,
        [
            "input down MovementForward",
            "sleep 10",
            "input up MovementForward",
            "input press CombatToggleCombat",
        ]);

        try
        {
            using var runner = new RetailUiAutomationScriptRunner(
                probe,
                path,
                dumpOnStart: false,
                pressInput: action =>
                {
                    presses.Add(action);
                    return true;
                },
                setInputHeld: (action, isHeld) =>
                {
                    held.Add((action, isHeld));
                    return true;
                });

            runner.Tick(0d);
            Assert.Equal([(InputAction.MovementForward, true)], held);
            Assert.False(runner.Completed);

            runner.Tick(0.010d);

            Assert.True(runner.Completed);
            Assert.Equal(
                [(InputAction.MovementForward, true), (InputAction.MovementForward, false)],
                held);
            Assert.Equal([InputAction.CombatToggleCombat], presses);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScriptRunner_mouselook_injectsRawDeltaThroughDelegate()
    {
        var (root, _, _, _, objects) = RootWithTwoItemLists();
        var probe = new RetailUiAutomationProbe(root, objects);
        var logs = new List<string>();
        var deltas = new List<(float Dx, float Dy)>();
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ui-probe.txt");
        File.WriteAllLines(path, ["mouselook 3 -4.5"]);

        try
        {
            using var runner = new RetailUiAutomationScriptRunner(
                probe,
                path,
                dumpOnStart: false,
                log: logs.Add,
                queueMouseLookDelta: (dx, dy) => deltas.Add((dx, dy)));

            runner.Tick(0d);

            Assert.True(runner.Completed);
            Assert.Contains(logs, line => line.Contains("UI probe script complete"));
            Assert.Equal([(3f, -4.5f)], deltas);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScriptRunner_mouselook_wrongArity_stopsWithUsageAndNeverInvokesDelegate()
    {
        var (root, _, _, _, objects) = RootWithTwoItemLists();
        var probe = new RetailUiAutomationProbe(root, objects);
        var logs = new List<string>();
        var deltas = new List<(float Dx, float Dy)>();
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ui-probe.txt");
        File.WriteAllLines(path, ["mouselook 1"]);

        try
        {
            using var runner = new RetailUiAutomationScriptRunner(
                probe,
                path,
                dumpOnStart: false,
                log: logs.Add,
                queueMouseLookDelta: (dx, dy) => deltas.Add((dx, dy)));

            runner.Tick(0d);

            Assert.True(runner.Completed);
            Assert.Contains(logs, line =>
                line.Contains("usage: mouselook <dx> <dy>", StringComparison.Ordinal));
            Assert.Empty(deltas);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScriptRunner_mouselook_noDelegate_stopsAsUnavailable()
    {
        var (root, _, _, _, objects) = RootWithTwoItemLists();
        var probe = new RetailUiAutomationProbe(root, objects);
        var logs = new List<string>();
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ui-probe.txt");
        File.WriteAllLines(path, ["mouselook 3 -4.5"]);

        try
        {
            using var runner = new RetailUiAutomationScriptRunner(
                probe,
                path,
                dumpOnStart: false,
                log: logs.Add);

            runner.Tick(0d);

            Assert.True(runner.Completed);
            Assert.Contains(logs, line =>
                line.Contains("mouselook injection is unavailable", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScriptRunner_dispose_releasesInjectedHeldActions()
    {
        var (root, _, _, _, objects) = RootWithTwoItemLists();
        var probe = new RetailUiAutomationProbe(root, objects);
        var held = new List<(InputAction Action, bool Held)>();
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ui-probe.txt");
        File.WriteAllLines(path, ["input down MovementForward", "sleep 10000"]);

        try
        {
            var runner = new RetailUiAutomationScriptRunner(
                probe,
                path,
                dumpOnStart: false,
                setInputHeld: (action, isHeld) =>
                {
                    held.Add((action, isHeld));
                    return true;
                });

            runner.Tick(0d);
            runner.Dispose();

            Assert.Equal(
                [(InputAction.MovementForward, true), (InputAction.MovementForward, false)],
                held);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScriptRunner_worldLifecycleCommands_waitForCanonicalRuntimeState()
    {
        var (root, _, _, _, objects) = RootWithTwoItemLists();
        var runtime = new FakeRuntime();
        var probe = new RetailUiAutomationProbe(root, objects);
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ui-probe.txt");
        File.WriteAllLines(path,
        [
            "wait world-ready 1000",
            "wait world-visible 1000",
            "wait materialized 2 1000",
            "checkpoint stable",
            "screenshot stable 1000",
        ]);

        try
        {
            using var runner = new RetailUiAutomationScriptRunner(
                probe,
                path,
                dumpOnStart: false,
                runtime: runtime);

            runner.Tick(0d);
            Assert.False(runner.Completed);

            runtime.IsWorldReady = true;
            runner.Tick(0.001d);
            Assert.False(runner.Completed);

            runtime.IsWorldViewportVisible = true;
            runtime.PortalMaterializationCount = 2;
            runner.Tick(0.001d);

            Assert.Equal(["stable"], runtime.Checkpoints);
            Assert.DoesNotContain("stable", runtime.ScreenshotRequests);
            Assert.False(runner.Completed);

            runner.Tick(0.001d);
            Assert.Equal(["stable"], runtime.Checkpoints);
            Assert.DoesNotContain("stable", runtime.ScreenshotRequests);

            runtime.CompleteCheckpoint("stable");
            runner.Tick(0.001d);

            Assert.Contains("stable", runtime.ScreenshotRequests);
            Assert.False(runner.Completed);

            runtime.CompletedScreenshots.Add("stable");
            runner.Tick(0.001d);

            Assert.True(runner.Completed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScriptRunner_signalWaitBlocksUntilHarnessPublishesExactName()
    {
        var (root, _, _, _, objects) = RootWithTwoItemLists();
        var runtime = new FakeRuntime();
        var probe = new RetailUiAutomationProbe(root, objects);
        string path = Path.Combine(
            Path.GetTempPath(),
            Path.GetRandomFileName() + ".ui-probe.txt");
        File.WriteAllLines(path,
        [
            "wait signal observer-moved 1000",
            "input press CombatToggleCombat",
        ]);
        var presses = new List<InputAction>();

        try
        {
            using var runner = new RetailUiAutomationScriptRunner(
                probe,
                path,
                dumpOnStart: false,
                pressInput: action =>
                {
                    presses.Add(action);
                    return true;
                },
                runtime: runtime);

            runner.Tick(0d);
            Assert.False(runner.Completed);
            Assert.Empty(presses);

            runtime.PublishedSignals.Add("another-signal");
            runner.Tick(0.5d);
            Assert.False(runner.Completed);
            Assert.Empty(presses);

            runtime.PublishedSignals.Add("observer-moved");
            runner.Tick(0.001d);
            Assert.True(runner.Completed);
            Assert.Equal([InputAction.CombatToggleCombat], presses);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScriptRunner_resetsThenWaitsForCompleteRenderPackEvidenceWindow()
    {
        var (root, _, _, _, objects) = RootWithTwoItemLists();
        var runtime = new FakeRuntime
        {
            RenderPackPerformanceSampleCount = 1200,
        };
        var probe = new RetailUiAutomationProbe(root, objects);
        string path = Path.Combine(
            Path.GetTempPath(),
            Path.GetRandomFileName() + ".ui-probe.txt");
        File.WriteAllLines(path,
        [
            "renderpack reset-performance",
            "wait render-pack-samples 2048 1000",
            "screenshot complete-window 1000",
        ]);

        try
        {
            using var runner = new RetailUiAutomationScriptRunner(
                probe,
                path,
                dumpOnStart: false,
                runtime: runtime);

            runner.Tick(0d);
            Assert.Equal(1, runtime.RenderPackPerformanceResetCount);
            Assert.Equal(0, runtime.RenderPackPerformanceSampleCount);
            Assert.Empty(runtime.ScreenshotRequests);

            runtime.RenderPackPerformanceSampleCount = 2047;
            runner.Tick(0.5d);
            Assert.Empty(runtime.ScreenshotRequests);

            runtime.RenderPackPerformanceSampleCount = 2048;
            runner.Tick(0.001d);
            Assert.Contains("complete-window", runtime.ScreenshotRequests);
            Assert.False(runner.Completed);

            runtime.CompletedScreenshots.Add("complete-window");
            runner.Tick(0.001d);
            Assert.True(runner.Completed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScriptRunner_renderPackWaitTerminatesEarlyForFailedToRetailFallback()
    {
        var (root, _, _, _, objects) = RootWithTwoItemLists();
        var runtime = new FakeRuntime
        {
            RenderPackFailedToRetail = true,
        };
        var probe = new RetailUiAutomationProbe(root, objects);
        string path = Path.Combine(
            Path.GetTempPath(),
            Path.GetRandomFileName() + ".ui-probe.txt");
        File.WriteAllLines(path,
        [
            "renderpack reset-performance",
            "wait render-pack-samples 2048 300000",
            "screenshot safe-fallback 1000",
        ]);

        try
        {
            using var runner = new RetailUiAutomationScriptRunner(
                probe,
                path,
                dumpOnStart: false,
                runtime: runtime);

            runner.Tick(0d);

            Assert.Equal(1, runtime.RenderPackPerformanceResetCount);
            Assert.Equal(0, runtime.RenderPackPerformanceSampleCount);
            Assert.Contains("safe-fallback", runtime.ScreenshotRequests);
            Assert.False(runner.Completed);

            runtime.CompletedScreenshots.Add("safe-fallback");
            runner.Tick(0.001d);
            Assert.True(runner.Completed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScriptRunner_renderPackTransitionsAndResizeWaitForPublishedRuntimeState()
    {
        var (root, _, _, _, objects) = RootWithTwoItemLists();
        var runtime = new FakeRuntime();
        var probe = new RetailUiAutomationProbe(root, objects);
        string path = Path.Combine(
            Path.GetTempPath(),
            Path.GetRandomFileName() + ".ui-probe.txt");
        File.WriteAllLines(path,
        [
            "renderpack select high",
            "wait render-pack high 1000",
            "renderpack disable",
            "wait render-pack retail 1000",
            "renderpack reenable",
            "wait render-pack high 1000",
            "resize 1024 768",
            "wait framebuffer 1024 768 1000",
        ]);

        try
        {
            using var runner = new RetailUiAutomationScriptRunner(
                probe,
                path,
                dumpOnStart: false,
                runtime: runtime);

            runner.Tick(0d);
            Assert.Equal(["high"], runtime.RenderPackSelections);
            Assert.False(runner.Completed);

            runtime.RenderPackStatus = new RetailUiAutomationRenderPackStatus(
                RetailUiAutomationRenderPackState.Active,
                "acdream.atmospheric",
                "high",
                ActivationGeneration: 1,
                FailureReason: null);
            runner.Tick(0.001d);
            Assert.Equal(1, runtime.RenderPackDisableCount);

            runtime.RenderPackStatus = RetailUiAutomationRenderPackStatus.Retail;
            runner.Tick(0.001d);
            Assert.Equal(1, runtime.RenderPackReenableCount);

            runtime.RenderPackStatus = new RetailUiAutomationRenderPackStatus(
                RetailUiAutomationRenderPackState.Active,
                "acdream.atmospheric",
                "high",
                ActivationGeneration: 3,
                FailureReason: null);
            runner.Tick(0.001d);
            Assert.Equal([(1024, 768)], runtime.FramebufferResizes);
            Assert.False(runner.Completed);

            runtime.FramebufferWidth = 1024;
            runtime.FramebufferHeight = 768;
            runner.Tick(0.001d);
            Assert.True(runner.Completed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScriptRunner_synchronousResizeYieldsBeforeWaitAndFirstScreenshot()
    {
        var (root, _, _, _, objects) = RootWithTwoItemLists();
        var runtime = new FakeRuntime { PublishFramebufferSizeOnResize = true };
        var probe = new RetailUiAutomationProbe(root, objects);
        string path = Path.Combine(
            Path.GetTempPath(),
            Path.GetRandomFileName() + ".ui-probe.txt");
        File.WriteAllLines(path,
        [
            "resize 1024 768",
            "wait framebuffer 1024 768 1000",
            "screenshot first-resized-frame 1000",
        ]);

        try
        {
            using var runner = new RetailUiAutomationScriptRunner(
                probe,
                path,
                dumpOnStart: false,
                runtime: runtime);

            runner.Tick(0d);

            Assert.Equal([(1024, 768)], runtime.FramebufferResizes);
            Assert.Empty(runtime.ScreenshotRequests);
            Assert.False(runner.Completed);

            runner.Tick(0.001d);

            Assert.Equal([(1024, 768)], runtime.FramebufferResizes);
            Assert.Equal(["first-resized-frame"], runtime.ScreenshotRequests);
            Assert.False(runner.Completed);

            runtime.CompletedScreenshots.Add("first-resized-frame");
            runner.Tick(0.001d);
            Assert.True(runner.Completed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScriptRunner_sequentialSynchronousResizesYieldOnceEachWithoutRepeating()
    {
        var (root, _, _, _, objects) = RootWithTwoItemLists();
        var runtime = new FakeRuntime { PublishFramebufferSizeOnResize = true };
        var probe = new RetailUiAutomationProbe(root, objects);
        string path = Path.Combine(
            Path.GetTempPath(),
            Path.GetRandomFileName() + ".ui-probe.txt");
        File.WriteAllLines(path,
        [
            "resize 1024 768",
            "wait framebuffer 1024 768 1000",
            "resize 1600 900",
            "wait framebuffer 1600 900 1000",
            "screenshot second-resized-frame 1000",
        ]);

        try
        {
            using var runner = new RetailUiAutomationScriptRunner(
                probe,
                path,
                dumpOnStart: false,
                runtime: runtime);

            runner.Tick(0d);
            Assert.Equal([(1024, 768)], runtime.FramebufferResizes);
            Assert.Empty(runtime.ScreenshotRequests);

            runner.Tick(0.001d);
            Assert.Equal([(1024, 768), (1600, 900)], runtime.FramebufferResizes);
            Assert.Empty(runtime.ScreenshotRequests);

            runner.Tick(0.001d);
            runner.Tick(0.001d);
            Assert.Equal([(1024, 768), (1600, 900)], runtime.FramebufferResizes);
            Assert.Equal(["second-resized-frame"], runtime.ScreenshotRequests);
            Assert.False(runner.Completed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScriptRunner_failedResizeStopsWithoutYieldingIntoFollowingCommands()
    {
        var (root, _, _, _, objects) = RootWithTwoItemLists();
        var runtime = new FakeRuntime { ResizeSucceeds = false };
        var probe = new RetailUiAutomationProbe(root, objects);
        var logs = new List<string>();
        string path = Path.Combine(
            Path.GetTempPath(),
            Path.GetRandomFileName() + ".ui-probe.txt");
        File.WriteAllLines(path,
        [
            "resize 1024 768",
            "screenshot must-not-run 1000",
        ]);

        try
        {
            using var runner = new RetailUiAutomationScriptRunner(
                probe,
                path,
                dumpOnStart: false,
                log: logs.Add,
                runtime: runtime);

            runner.Tick(0d);
            runner.Tick(0.001d);

            Assert.True(runner.Completed);
            Assert.Equal([(1024, 768)], runtime.FramebufferResizes);
            Assert.Empty(runtime.ScreenshotRequests);
            Assert.Contains(logs, line => line.Contains("resize rejected", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScriptRunner_closeClientRequestsNormalRuntimeShutdownExactlyOnce()
    {
        var (root, _, _, _, objects) = RootWithTwoItemLists();
        var runtime = new FakeRuntime();
        var probe = new RetailUiAutomationProbe(root, objects);
        string path = Path.Combine(
            Path.GetTempPath(),
            Path.GetRandomFileName() + ".ui-probe.txt");
        File.WriteAllText(path, "close-client");

        try
        {
            using var runner = new RetailUiAutomationScriptRunner(
                probe,
                path,
                dumpOnStart: false,
                runtime: runtime);

            runner.Tick(0d);
            runner.Tick(0d);

            Assert.True(runner.Completed);
            Assert.Equal(1, runtime.ClientCloseRequestCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(RetailUiAutomationCheckpointStatus.Failed, "deferred write failed")]
    [InlineData(RetailUiAutomationCheckpointStatus.Cancelled, "shutdown cancelled")]
    public void ScriptRunner_checkpointTerminalFailure_StopsWithExactError(
        RetailUiAutomationCheckpointStatus status,
        string expectedError)
    {
        var (root, _, _, _, objects) = RootWithTwoItemLists();
        var runtime = new FakeRuntime();
        var probe = new RetailUiAutomationProbe(root, objects);
        var logs = new List<string>();
        string path = Path.Combine(
            Path.GetTempPath(),
            Path.GetRandomFileName() + ".ui-probe.txt");
        File.WriteAllLines(path, ["checkpoint stable", "input press CombatToggleCombat"]);

        try
        {
            using var runner = new RetailUiAutomationScriptRunner(
                probe,
                path,
                dumpOnStart: false,
                log: logs.Add,
                runtime: runtime);

            runner.Tick(0d);
            runtime.CompleteCheckpoint("stable", status, expectedError);
            runner.Tick(0d);

            Assert.True(runner.Completed);
            Assert.Equal(["stable"], runtime.Checkpoints);
            Assert.Contains(logs, line => line.Contains(
                expectedError,
                StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScriptRunner_disposeCancelsPendingCheckpoint()
    {
        var (root, _, _, _, objects) = RootWithTwoItemLists();
        var runtime = new FakeRuntime();
        var probe = new RetailUiAutomationProbe(root, objects);
        string path = Path.Combine(
            Path.GetTempPath(),
            Path.GetRandomFileName() + ".ui-probe.txt");
        File.WriteAllText(path, "checkpoint stable");

        try
        {
            var runner = new RetailUiAutomationScriptRunner(
                probe,
                path,
                dumpOnStart: false,
                runtime: runtime);
            runner.Tick(0d);

            runner.Dispose();

            Assert.Equal(["stable"], runtime.Checkpoints);
            Assert.Throws<InvalidOperationException>(() =>
                runtime.CompleteCheckpoint("stable"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScriptRunner_worldReadyTimeout_stopsWithActionableLine()
    {
        var (root, _, _, _, objects) = RootWithTwoItemLists();
        var runtime = new FakeRuntime();
        var probe = new RetailUiAutomationProbe(root, objects);
        var logs = new List<string>();
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ui-probe.txt");
        File.WriteAllText(path, "wait world-ready 10");

        try
        {
            using var runner = new RetailUiAutomationScriptRunner(
                probe,
                path,
                dumpOnStart: false,
                log: logs.Add,
                runtime: runtime);

            runner.Tick(0d);
            runner.Tick(0.011d);

            Assert.True(runner.Completed);
            Assert.Contains(logs, line =>
                line.Contains("timed out waiting for world readiness", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
