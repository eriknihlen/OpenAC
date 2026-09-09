using AcDream.App.UI.Layout;

namespace AcDream.App.Tests.UI.Layout;

public sealed class OptionPageModelTests
{
    // ── Empty page (a generic zero-row PlayerOptionPage-shaped page — NOT
    //    modeling Gameplay, which is not an OptionPage at all) ─────────────

    [Fact]
    public void EmptyPage_ChangedIsAlwaysFalse()
    {
        var page = new OptionPage();
        Assert.False(page.Changed);
    }

    [Fact]
    public void EmptyPage_ApplyResetDefaults_AreNoOps()
    {
        var page = new OptionPage();

        page.Apply();
        page.Reset();
        page.Defaults();

        Assert.Empty(page.Rows);
        Assert.False(page.Changed);
    }

    [Fact]
    public void EmptyPage_OnShownAndOnHidden_DoNotThrow()
    {
        var page = new OptionPage();

        page.OnShown();
        page.OnHidden();
    }

    [Fact]
    public void EmptyPlayerOptionPageShapedPage_WithAfterApplyWired_Apply_StillFlushes()
    {
        var page = new OptionPage();
        int flushCount = 0;
        page.AfterApply = () => flushCount++;

        page.Apply();

        Assert.Equal(1, flushCount);
    }

    [Fact]
    public void EmptyPlayerOptionPageShapedPage_WithAfterApplyWired_OnShown_StillFlushes()
    {
        var page = new OptionPage();
        int flushCount = 0;
        page.AfterApply = () => flushCount++;

        page.OnShown();

        Assert.Equal(1, flushCount);
    }

    [Fact]
    public void EmptyPage_WithNoAfterApplyWired_Apply_NeverFlushes()
    {
        var page = new OptionPage { AfterApply = null };

        Exception? thrown = Record.Exception(page.Apply);

        Assert.Null(thrown);
    }

    [Fact]
    public void EmptyPage_ResetAndDefaults_DoNotInvokeAfterApply()
    {
        var page = new OptionPage();
        int flushCount = 0;
        page.AfterApply = () => flushCount++;

        page.Reset();
        page.Defaults();

        Assert.Equal(0, flushCount);
    }

    // ── BoolOptionRow leaf semantics (UIOption_Checkbox port) ───────────────

    [Fact]
    public void BoolOptionRow_SetCurrentValue_AppliesLiveImmediately_WithoutCommitting()
    {
        var applied = new List<bool>();
        var row = new BoolOptionRow(initial: false, defaultValue: false, apply: v => applied.Add(v));

        row.SetCurrentValue(true);

        Assert.True(row.Current);
        Assert.False(row.Saved);
        Assert.True(row.Changed);
        Assert.Equal([true], applied);
    }

    [Fact]
    public void BoolOptionRow_SaveCurrentValue_CommitsBaseline()
    {
        var row = new BoolOptionRow(initial: false, defaultValue: false);
        row.SetCurrentValue(true);

        row.SaveCurrentValue();

        Assert.True(row.Saved);
        Assert.False(row.Changed);
    }

    [Fact]
    public void BoolOptionRow_RestoreSavedValue_RevertsAndReapplies()
    {
        var applied = new List<bool>();
        var row = new BoolOptionRow(initial: false, defaultValue: false, apply: v => applied.Add(v));
        row.SetCurrentValue(true);
        applied.Clear();

        row.RestoreSavedValue();

        Assert.False(row.Current);
        Assert.False(row.Changed);
        Assert.Equal([false], applied);
    }

    [Fact]
    public void BoolOptionRow_RestoreDefaultValue_AppliesLiveWithoutCommitting()
    {
        var applied = new List<bool>();
        var row = new BoolOptionRow(initial: false, defaultValue: true, apply: v => applied.Add(v));

        row.RestoreDefaultValue();

        Assert.True(row.Current);
        Assert.False(row.Saved);
        Assert.True(row.Changed);
        Assert.Equal([true], applied);
    }

    // ── Synthetic 2-option page ──────────────────────────────────────────────

    private static (OptionPage Page, BoolOptionRow A, BoolOptionRow B) MakeTwoOptionPage()
    {
        var page = new OptionPage();
        var a = new BoolOptionRow(initial: true, defaultValue: true);
        var b = new BoolOptionRow(initial: false, defaultValue: true);
        page.Register(a);
        page.Register(b);
        return (page, a, b);
    }

    [Fact]
    public void TwoOptionPage_Changed_TrueWhenAnyRowChanged()
    {
        var (page, a, _) = MakeTwoOptionPage();
        Assert.False(page.Changed);

        a.SetCurrentValue(false);

        Assert.True(page.Changed);
    }

    [Fact]
    public void TwoOptionPage_Apply_CommitsEveryRowUnconditionally()
    {
        var (page, a, b) = MakeTwoOptionPage();
        a.SetCurrentValue(false);
        // b left at its initial (unchanged) value.

        page.Apply();

        Assert.False(page.Changed);
        Assert.True(a.Saved == a.Current);
        Assert.True(b.Saved == b.Current);
    }

    [Fact]
    public void TwoOptionPage_Reset_RevertsOnlyChangedRows()
    {
        var (page, a, b) = MakeTwoOptionPage();
        a.SetCurrentValue(false);

        page.Reset();

        Assert.True(a.Current); // reverted to its original saved baseline
        Assert.False(b.Current);
        Assert.False(page.Changed);
    }

    [Fact]
    public void TwoOptionPage_Defaults_RestoresEveryRow_WithoutCommitting()
    {
        var (page, a, b) = MakeTwoOptionPage();
        a.SetCurrentValue(false);
        page.Apply();

        page.Defaults();

        Assert.True(a.Current); // default is true
        Assert.True(b.Current); // default is true (was false)
        Assert.True(page.Changed);
    }

    [Fact]
    public void TwoOptionPage_OnHidden_RevertsUncommittedEdits()
    {
        var (page, a, _) = MakeTwoOptionPage();
        a.SetCurrentValue(false);

        page.OnHidden();

        Assert.True(a.Current);
        Assert.False(page.Changed);
    }

    [Fact]
    public void TwoOptionPage_OnShown_AppliesAndCommits()
    {
        var (page, a, _) = MakeTwoOptionPage();
        a.SetCurrentValue(false);

        page.OnShown();

        Assert.False(page.Changed);
        Assert.False(a.Saved != a.Current);
    }


    [Fact]
    public void OnOptionChanged_FiresAsLastStepOf_Apply()
    {
        var (page, a, _) = MakeTwoOptionPage();
        a.SetCurrentValue(false);
        var order = new List<string>();
        page.AfterApply = () => order.Add("afterApply");
        page.OnOptionChanged = () => order.Add("onOptionChanged");

        page.Apply();

        Assert.Equal(["afterApply", "onOptionChanged"], order);
    }

    [Fact]
    public void OnOptionChanged_FiresOnReset_EvenWithNoAfterApply()
    {
        var (page, a, _) = MakeTwoOptionPage();
        a.SetCurrentValue(false);
        int notifyCount = 0;
        page.OnOptionChanged = () => notifyCount++;

        page.Reset();

        Assert.Equal(1, notifyCount);
    }

    [Fact]
    public void OnOptionChanged_FiresOnDefaults()
    {
        var (page, _, _) = MakeTwoOptionPage();
        int notifyCount = 0;
        page.OnOptionChanged = () => notifyCount++;

        page.Defaults();

        Assert.Equal(1, notifyCount);
    }

    [Fact]
    public void OnOptionChanged_FiresOnEmptyPage_ForEveryVerb()
    {
        var page = new OptionPage();
        int notifyCount = 0;
        page.OnOptionChanged = () => notifyCount++;

        page.Apply();
        page.Reset();
        page.Defaults();

        Assert.Equal(3, notifyCount);
    }

    [Fact]
    public void BoolOptionRow_SetCurrentValue_NotifiesAttachedPage()
    {
        var row = new BoolOptionRow(initial: false, defaultValue: false);
        int notifyCount = 0;
        row.AttachPageNotify(() => notifyCount++);

        row.SetCurrentValue(true);

        Assert.Equal(1, notifyCount);
    }

    [Fact]
    public void BoolOptionRow_RestoreSavedValueAndRestoreDefaultValue_DoNotNotifyAttachedPage()
    {
        var row = new BoolOptionRow(initial: false, defaultValue: true);
        int notifyCount = 0;
        row.AttachPageNotify(() => notifyCount++);
        row.SetCurrentValue(true);
        notifyCount = 0; // drop the SetCurrentValue notify above

        row.RestoreSavedValue();
        row.RestoreDefaultValue();

        Assert.Equal(0, notifyCount);
    }

    [Fact]
    public void Register_AttachesPageNotify_SoSubsequentLiveEditsNotifyThePage()
    {
        var page = new OptionPage();
        var row = new BoolOptionRow(initial: false, defaultValue: false);
        int notifyCount = 0;
        page.OnOptionChanged = () => notifyCount++;

        page.Register(row);
        row.SetCurrentValue(true);

        Assert.Equal(1, notifyCount);
    }


    [Fact]
    public void SaveCurrentValue_ReReadsLiveSource_AndPushesTheWidgetRefresh()
    {
        bool live = false;
        bool widgetChecked = true; // deliberately out of sync with live
        var row = new BoolOptionRow(
            initial: true,
            defaultValue: false,
            read: () => live,
            refresh: value => widgetChecked = value);

        live = false;
        row.SaveCurrentValue();

        Assert.False(row.Current);
        Assert.False(row.Changed);
        Assert.False(widgetChecked);

        live = true;
        row.SaveCurrentValue();
        Assert.True(row.Current);
        Assert.True(widgetChecked);
    }


    [Fact]
    public void ReloadFromLive_ReReadsRows_WithoutFiringAfterApply()
    {
        bool live = false;
        bool widgetChecked = false;
        int flushCount = 0;
        int gatingCount = 0;
        var page = new OptionPage { AfterApply = () => flushCount++ };
        var row = new BoolOptionRow(
            initial: false,
            defaultValue: false,
            read: () => live,
            refresh: value => widgetChecked = value);
        page.Register(row);
        page.OnOptionChanged = () => gatingCount++;

        live = true;
        page.ReloadFromLive();

        Assert.True(row.Current);
        Assert.True(widgetChecked);
        Assert.False(row.Changed);
        Assert.Equal(0, flushCount); // NO AfterApply publication on a seed
        Assert.Equal(1, gatingCount); // Apply/Reset ghosting re-evaluated
    }

    [Fact]
    public void RemoveTail_DetachesRowsAndPageNotifyOwnership()
    {
        var page = new OptionPage();
        var retained = new BoolOptionRow(false, false);
        var removed = new BoolOptionRow(false, false);
        int notifications = 0;
        page.OnOptionChanged = () => notifications++;
        page.Register(retained);
        page.Register(removed);

        page.RemoveTail(1);
        notifications = 0;
        removed.SetCurrentValue(true);

        Assert.Single(page.Rows);
        Assert.Same(retained, page.Rows[0]);
        Assert.Equal(0, notifications);
        Assert.False(page.Changed);
    }

    [Fact]
    public void Defaults_IsSafeWhenAnEarlierRowReplacesTheDynamicTail()
    {
        var page = new OptionPage();
        int removedApplyCount = 0;
        var pack = new BoolOptionRow(
            initial: true,
            defaultValue: false,
            apply: _ => page.RemoveTail(1));
        var dynamic = new BoolOptionRow(
            initial: false,
            defaultValue: true,
            apply: _ => removedApplyCount++);
        page.Register(pack);
        page.Register(dynamic);

        page.Defaults();

        Assert.Single(page.Rows);
        Assert.Equal(0, removedApplyCount);
    }
}
