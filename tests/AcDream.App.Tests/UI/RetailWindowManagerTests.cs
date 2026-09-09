using System.Collections.Generic;
using AcDream.App.UI;

namespace AcDream.App.Tests.UI;

public sealed class RetailWindowManagerTests
{
    [Fact]
    public void Hide_ClearsRootOwnership_AndShowRestoresDefaultInput()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var frame = new UiPanel { Width = 300, Height = 150 };
        var input = new UiField { Width = 200, Height = 20 };
        frame.AddChild(input);
        root.AddChild(frame);
        root.DefaultTextInput = input;
        root.Modal = frame;
        root.SetKeyboardFocus(input);
        root.SetCapture(input);

        var controller = new RecordingController();
        RetailWindowHandle handle = root.RegisterWindow("chat", frame, frame, controller);
        var events = new List<string>();
        handle.Hidden += _ => events.Add("hidden");
        handle.Shown += _ => events.Add("shown");

        frame.Visible = false;

        Assert.False(frame.Visible);
        Assert.Null(root.KeyboardFocus);
        Assert.Null(root.Captured);
        Assert.Null(root.Modal);
        Assert.Null(root.DefaultTextInput);
        Assert.Equal(1, controller.ShownCount);
        Assert.Equal(1, controller.HiddenCount);
        Assert.Equal(new[] { "hidden" }, events);

        Assert.True(handle.Show());

        Assert.True(frame.Visible);
        Assert.Same(input, root.DefaultTextInput);
        Assert.Equal(2, controller.ShownCount);
        Assert.Equal(new[] { "hidden", "shown" }, events);
    }

    [Fact]
    public void Handle_ReportsGeometryCloseAndLockLifecycle()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var frame = new UiPanel
        {
            Left = 10,
            Top = 20,
            Width = 100,
            Height = 80,
            MinWidth = 50,
            MinHeight = 40,
        };
        root.AddChild(frame);
        RetailWindowHandle handle = root.RegisterWindow("inventory", frame);
        int moved = 0, resized = 0, hidden = 0, shown = 0, closed = 0;
        var locks = new List<bool>();
        handle.Moved += _ => moved++;
        handle.Resized += _ => resized++;
        handle.Hidden += _ => hidden++;
        handle.Shown += _ => shown++;
        handle.Closed += _ => closed++;
        handle.LockChanged += (_, locked) => locks.Add(locked);

        Assert.True(handle.MoveTo(30, 45));
        Assert.Equal((30f, 45f), (frame.Left, frame.Top));
        Assert.Equal(1, moved);

        Assert.True(handle.ResizeTo(160, 120));
        Assert.Equal((160f, 120f), (frame.Width, frame.Height));
        Assert.Equal(1, resized);

        root.WindowManager.SetLocked(true);
        root.WindowManager.SetLocked(true);
        root.WindowManager.SetLocked(false);
        Assert.Equal(new[] { true, false }, locks);

        Assert.True(handle.Close());
        Assert.True(handle.Close());
        Assert.False(handle.IsVisible);
        Assert.Equal(1, hidden);
        Assert.Equal(1, closed);

        Assert.True(handle.Show());
        Assert.True(handle.Close());
        Assert.Equal(1, shown);
        Assert.Equal(2, hidden);
        Assert.Equal(2, closed);
    }

    [Fact]
    public void PointerResize_ReportsCompletionThroughTypedHandle()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var frame = new UiPanel
        {
            Left = 10,
            Top = 20,
            Width = 100,
            Height = 80,
            Resizable = true,
        };
        root.AddChild(frame);
        RetailWindowHandle handle = root.RegisterWindow("chat", frame);
        int resized = 0;
        handle.Resized += _ => resized++;

        root.OnMouseDown(UiMouseButton.Left, 109, 60);
        root.OnMouseMove(149, 60);
        root.OnMouseUp(UiMouseButton.Left, 149, 60);

        Assert.Equal(140f, frame.Width);
        Assert.Equal(1, resized);
    }

    [Fact]
    public void ProgrammaticResize_ConstrainedToParent_UsesCurrentWindowPosition()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var frame = new UiPanel
        {
            Left = 40,
            Top = 125,
            Width = 300,
            Height = 200,
            ResizeX = false,
            ResizeY = true,
            ConstrainResizeToParent = true,
            MinHeight = 100,
        };
        root.AddChild(frame);
        RetailWindowHandle handle = root.RegisterWindow("main-panel", frame);

        Assert.True(handle.ResizeTo(frame.Width, 2_000f));

        Assert.Equal(475f, frame.Height);
        Assert.Equal(root.Height, frame.Top + frame.Height);
    }

    [Fact]
    public void FocusAndCaptureTransitions_AreScopedToOwningHandle()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var first = new UiPanel { Width = 200, Height = 100 };
        var second = new UiPanel { Left = 250, Width = 200, Height = 100 };
        var firstField = new UiField { Width = 100, Height = 20 };
        var secondField = new UiField { Width = 100, Height = 20 };
        first.AddChild(firstField);
        second.AddChild(secondField);
        root.AddChild(first);
        root.AddChild(second);
        var firstController = new RecordingController();
        var secondController = new RecordingController();
        RetailWindowHandle firstHandle = root.RegisterWindow("first", first, first, firstController);
        RetailWindowHandle secondHandle = root.RegisterWindow("second", second, second, secondController);
        var firstCaptures = new List<UiElement?>();
        var secondCaptures = new List<UiElement?>();
        firstHandle.DescendantCaptureChanged += (_, element) => firstCaptures.Add(element);
        secondHandle.DescendantCaptureChanged += (_, element) => secondCaptures.Add(element);

        root.SetKeyboardFocus(firstField);
        root.SetKeyboardFocus(secondField);
        root.SetCapture(firstField);
        root.SetCapture(secondField);
        root.ReleaseCapture();

        Assert.Equal(new UiElement?[] { firstField, null }, firstController.FocusChanges);
        Assert.Equal(new UiElement?[] { secondField }, secondController.FocusChanges);
        Assert.Equal(new UiElement?[] { firstField, null }, firstCaptures);
        Assert.Equal(new UiElement?[] { secondField, null }, secondCaptures);
    }

    [Fact]
    public void UnregisterAndTreeRemoval_DisposeControllersExactlyOnce()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var first = new UiPanel { Width = 100, Height = 100 };
        var second = new UiPanel { Width = 100, Height = 100 };
        root.AddChild(first);
        root.AddChild(second);
        var firstController = new RecordingController();
        var secondController = new RecordingController();
        RetailWindowHandle firstHandle = root.RegisterWindow("first", first, first, firstController);
        RetailWindowHandle secondHandle = root.RegisterWindow("second", second, second, secondController);

        Assert.True(root.UnregisterWindow("first"));
        Assert.False(firstHandle.IsRegistered);
        Assert.Equal(1, firstController.DisposeCount);
        Assert.False(root.ShowWindow("first"));

        Assert.True(root.RemoveChild(second));
        Assert.False(secondHandle.IsRegistered);
        Assert.Equal(1, secondController.DisposeCount);
        Assert.False(root.ShowWindow("second"));

        root.WindowManager.Dispose();
        Assert.Equal(1, firstController.DisposeCount);
        Assert.Equal(1, secondController.DisposeCount);
    }

    [Fact]
    public void AttachController_AfterRegistration_DeliversVisibilityAndOwnsDisposal()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var frame = new UiPanel { Width = 100, Height = 100 };
        root.AddChild(frame);
        RetailWindowHandle handle = root.RegisterWindow("inventory", frame);
        var controller = new RecordingController();

        root.WindowManager.AttachController("inventory", controller);

        Assert.Same(controller, handle.Controller);
        Assert.Equal(1, controller.ShownCount);
        Assert.Throws<InvalidOperationException>(() =>
            root.WindowManager.AttachController("inventory", new RecordingController()));

        root.WindowManager.Dispose();
        root.WindowManager.Dispose();
        Assert.Equal(1, controller.HiddenCount);
        Assert.Equal(1, controller.DisposeCount);
    }

    [Fact]
    public void VisibilityEvent_tracksEveryRegisteredWindowTransition()
    {
        var root = new UiRoot { Width = 800, Height = 600 };
        var inventory = new UiPanel { Width = 100, Height = 100 };
        root.AddChild(inventory);
        root.RegisterWindow("inventory", inventory);
        var transitions = new List<(string Name, bool Visible)>();
        root.WindowManager.WindowVisibilityChanged +=
            (name, visible) => transitions.Add((name, visible));

        Assert.True(root.HideWindow("inventory"));
        Assert.True(root.ShowWindow("inventory"));
        Assert.False(root.ToggleWindow("inventory"));

        Assert.Equal(
            new[]
            {
                ("inventory", false),
                ("inventory", true),
                ("inventory", false),
            },
            transitions);
    }


    [Fact]
    public void ResizableMarkupPluginWindow_AcceptsResizeWithinMinAndParentConstraints()
    {
        const string xml =
            "<panel x=\"0\" y=\"0\" w=\"300\" h=\"200\" resizable=\"true\" minw=\"250\" minh=\"150\"></panel>";
        var panel = MarkupDocument.Build(xml, new object(), _ => (1u, 32, 32));
        var root = new UiRoot { Width = 800, Height = 600 };
        root.AddChild(panel);
        RetailWindowHandle handle = root.RegisterWindow("plugin-resizable", panel);

        Assert.True(handle.ResizeTo(500f, 400f));
        Assert.Equal((500f, 400f), (panel.Width, panel.Height));

        Assert.True(handle.ResizeTo(50f, 50f));
        Assert.Equal((250f, 150f), (panel.Width, panel.Height));
    }

    [Fact]
    public void NonResizableMarkupPluginWindow_RefusesResize()
    {
        const string xml = "<panel x=\"0\" y=\"0\" w=\"300\" h=\"200\"></panel>";
        var panel = MarkupDocument.Build(xml, new object(), _ => (1u, 32, 32));
        var root = new UiRoot { Width = 800, Height = 600 };
        root.AddChild(panel);
        RetailWindowHandle handle = root.RegisterWindow("plugin-fixed", panel);
        int resized = 0;
        handle.Resized += _ => resized++;

        handle.ResizeTo(500f, 400f);

        Assert.Equal((300f, 200f), (panel.Width, panel.Height));
        Assert.Equal(0, resized);
    }


    [Fact]
    public void ComputeAuthoredGeometryRevision_SameGeometry_IsStable()
    {
        int a = RetailWindowManager.ComputeAuthoredGeometryRevision(856f, 236f, 400f, 150f, true);
        int b = RetailWindowManager.ComputeAuthoredGeometryRevision(856f, 236f, 400f, 150f, true);

        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData(984f, 236f, 400f, 150f, true)]
    [InlineData(856f, 271f, 400f, 150f, true)]
    [InlineData(856f, 236f, 420f, 150f, true)]
    [InlineData(856f, 236f, 400f, 160f, true)]
    [InlineData(856f, 236f, 400f, 150f, false)]
    public void ComputeAuthoredGeometryRevision_AnyFieldDiffers_ChangesTheValue(
        float width, float height, float minWidth, float minHeight, bool resizable)
    {
        int baseline = RetailWindowManager.ComputeAuthoredGeometryRevision(856f, 236f, 400f, 150f, true);
        int changed = RetailWindowManager.ComputeAuthoredGeometryRevision(
            width, height, minWidth, minHeight, resizable);

        Assert.NotEqual(baseline, changed);
    }

    [Fact]
    public void ComputeAuthoredGeometryRevision_IsNeverNegative()
    {
        Assert.True(RetailWindowManager.ComputeAuthoredGeometryRevision(856f, 236f, 400f, 150f, true) >= 0);
        Assert.True(RetailWindowManager.ComputeAuthoredGeometryRevision(0f, 0f, 0f, 0f, false) >= 0);
        Assert.True(RetailWindowManager.ComputeAuthoredGeometryRevision(-1f, -1f, -1f, -1f, true) >= 0);
    }

    private sealed class RecordingController : IRetainedPanelController
    {
        public int ShownCount { get; private set; }
        public int HiddenCount { get; private set; }
        public int DisposeCount { get; private set; }
        public List<UiElement?> FocusChanges { get; } = new();

        public void OnShown() => ShownCount++;
        public void OnHidden() => HiddenCount++;
        public void OnDescendantFocusChanged(UiElement? focusedDescendant)
            => FocusChanges.Add(focusedDescendant);
        public void Dispose() => DisposeCount++;
    }
}
