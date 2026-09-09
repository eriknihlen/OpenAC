using System.Collections.Generic;
using AcDream.App.UI;

namespace AcDream.App.Tests.UI;

public sealed class RetainedPanelControllerGroupTests
{
    [Fact]
    public void Lifecycle_ForwardsInOwnershipOrder_AndDisposesInReverseExactlyOnce()
    {
        var calls = new List<string>();
        var first = new RecordingController("first", calls);
        var second = new RecordingController("second", calls);
        var group = new RetainedPanelControllerGroup(first, second);
        var focus = new UiPanel();

        group.OnShown();
        group.OnDescendantFocusChanged(focus);
        group.OnHidden();
        group.Dispose();
        group.Dispose();

        Assert.Equal(
        [
            "first:shown", "second:shown",
            "first:focus", "second:focus",
            "second:hidden", "first:hidden",
            "second:dispose", "first:dispose",
        ], calls);
    }

    private sealed class RecordingController(
        string name,
        List<string> calls) : IRetainedPanelController
    {
        public void OnShown() => calls.Add($"{name}:shown");
        public void OnHidden() => calls.Add($"{name}:hidden");
        public void OnDescendantFocusChanged(UiElement? focusedDescendant)
            => calls.Add($"{name}:focus");
        public void Dispose() => calls.Add($"{name}:dispose");
    }
}
