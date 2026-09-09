using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeCharacterTitleStateTests
{
    [Fact]
    public void InitialState_IsEmptyWithDefaultDisplayTitle()
    {
        var titles = new RuntimeCharacterTitleState();

        Assert.Equal(0u, titles.DisplayTitleId);
        Assert.Empty(titles.EarnedTitleIds);
        Assert.False(titles.HasEarnedTitle(13u));
    }

    [Fact]
    public void ReplaceTable_SetsEarnedIdsAndDisplayTitle_FiresTableReplaced()
    {
        var titles = new RuntimeCharacterTitleState();
        int tableReplacedCount = 0;
        titles.TableReplaced += () => tableReplacedCount++;

        titles.ReplaceTable(13u, [1u, 5u, 13u]);

        Assert.Equal(13u, titles.DisplayTitleId);
        Assert.Equal(new HashSet<uint> { 1u, 5u, 13u }, titles.EarnedTitleIds.ToHashSet());
        Assert.True(titles.HasEarnedTitle(5u));
        Assert.False(titles.HasEarnedTitle(99u));
        Assert.Equal(1, tableReplacedCount);
    }

    [Fact]
    public void ReplaceTable_IsAWholesaleReplace_DropsIdsMissingFromTheNewTable()
    {
        var titles = new RuntimeCharacterTitleState();
        titles.ReplaceTable(1u, [1u, 2u, 3u]);

        titles.ReplaceTable(1u, [1u]);

        Assert.Equal(new uint[] { 1u }, titles.EarnedTitleIds.ToArray());
        Assert.False(titles.HasEarnedTitle(2u));
        Assert.False(titles.HasEarnedTitle(3u));
    }

    [Fact]
    public void ReplaceTable_DisplayTitleIdUnchanged_DoesNotFireDisplayTitleChanged()
    {
        var titles = new RuntimeCharacterTitleState();
        titles.ReplaceTable(13u, [13u]);
        var fired = new List<uint>();
        titles.DisplayTitleChanged += id => fired.Add(id);

        titles.ReplaceTable(13u, [13u, 14u]);

        Assert.Empty(fired);
        Assert.Equal(13u, titles.DisplayTitleId);
    }

    [Fact]
    public void ReplaceTable_DisplayTitleIdChanges_FiresDisplayTitleChangedWithNewId()
    {
        var titles = new RuntimeCharacterTitleState();
        titles.ReplaceTable(13u, [13u, 14u]);
        var fired = new List<uint>();
        titles.DisplayTitleChanged += id => fired.Add(id);

        titles.ReplaceTable(14u, [13u, 14u]);

        Assert.Equal([14u], fired);
        Assert.Equal(14u, titles.DisplayTitleId);
    }


    [Fact]
    public void ApplyUpdateTitle_AlwaysAddsAndFiresTitleAdded_EvenWhenNotSetAsDisplay()
    {
        var titles = new RuntimeCharacterTitleState();
        var added = new List<uint>();
        titles.TitleAdded += id => added.Add(id);
        var displayChanged = new List<uint>();
        titles.DisplayTitleChanged += id => displayChanged.Add(id);

        titles.ApplyUpdateTitle(7u, setAsDisplay: false);

        Assert.True(titles.HasEarnedTitle(7u));
        Assert.Equal([7u], added);
        Assert.Empty(displayChanged);
        Assert.Equal(0u, titles.DisplayTitleId);
    }

    [Fact]
    public void ApplyUpdateTitle_SetAsDisplayTrue_AddsAndUpdatesDisplayTitle()
    {
        var titles = new RuntimeCharacterTitleState();
        var added = new List<uint>();
        titles.TitleAdded += id => added.Add(id);
        var displayChanged = new List<uint>();
        titles.DisplayTitleChanged += id => displayChanged.Add(id);

        titles.ApplyUpdateTitle(13u, setAsDisplay: true);

        Assert.True(titles.HasEarnedTitle(13u));
        Assert.Equal([13u], added);
        Assert.Equal([13u], displayChanged);
        Assert.Equal(13u, titles.DisplayTitleId);
    }

    [Fact]
    public void ApplyUpdateTitle_AlreadyEarnedId_DoesNotFireTitleAddedOrBumpRevision()
    {
        var titles = new RuntimeCharacterTitleState();
        titles.ApplyUpdateTitle(7u, setAsDisplay: false);
        long revisionAfterFirstAdd = titles.Revision;
        var added = new List<uint>();
        titles.TitleAdded += id => added.Add(id);

        titles.ApplyUpdateTitle(7u, setAsDisplay: false);

        Assert.Empty(added);
        Assert.Single(titles.EarnedTitleIds);
        Assert.Equal(revisionAfterFirstAdd, titles.Revision);
    }

    [Fact]
    public void ApplyUpdateTitle_SetAsDisplayOnAlreadyCurrentId_DoesNotFireDisplayTitleChangedOrBumpRevision()
    {
        // A re-notice for the id that is ALREADY the
        // display title is a no-op wire message — it must not produce a
        // revision edge or a spurious DisplayTitleChanged.
        var titles = new RuntimeCharacterTitleState();
        titles.ApplyUpdateTitle(13u, setAsDisplay: true);
        long revisionAfterFirst = titles.Revision;
        var displayChanged = new List<uint>();
        titles.DisplayTitleChanged += id => displayChanged.Add(id);
        var added = new List<uint>();
        titles.TitleAdded += id => added.Add(id);

        titles.ApplyUpdateTitle(13u, setAsDisplay: true);

        Assert.Empty(displayChanged);
        Assert.Empty(added);
        Assert.Equal(revisionAfterFirst, titles.Revision);
    }

    [Fact]
    public void ApplyUpdateTitle_AddsNewIdAndSetsDisplay_BumpsRevisionTwice()
    {
        var titles = new RuntimeCharacterTitleState();
        long before = titles.Revision;

        titles.ApplyUpdateTitle(13u, setAsDisplay: true);

        Assert.Equal(before + 2, titles.Revision);
    }

    [Fact]
    public void ApplyUpdateTitle_SetAsDisplayOnAnUnearnedId_AddsItAndSetsDisplay()
    {
        var titles = new RuntimeCharacterTitleState();

        titles.ApplyUpdateTitle(99u, setAsDisplay: true);

        Assert.True(titles.HasEarnedTitle(99u));
        Assert.Equal(99u, titles.DisplayTitleId);
    }

    [Fact]
    public void ReplaceTable_IdenticalResend_DoesNotBumpRevision_ButStillFiresTableReplaced()
    {
        var titles = new RuntimeCharacterTitleState();
        titles.ReplaceTable(13u, [1u, 5u, 13u]);
        long revisionAfterFirst = titles.Revision;
        int tableReplacedCount = 0;
        titles.TableReplaced += () => tableReplacedCount++;

        titles.ReplaceTable(13u, [1u, 5u, 13u]);

        Assert.Equal(revisionAfterFirst, titles.Revision);
        Assert.Equal(1, tableReplacedCount);
    }

    [Fact]
    public void ResetSession_ClearsEarnedIdsAndDisplayTitle()
    {
        var titles = new RuntimeCharacterTitleState();
        titles.ReplaceTable(13u, [1u, 5u, 13u]);

        titles.ResetSession();

        Assert.Equal(0u, titles.DisplayTitleId);
        Assert.Empty(titles.EarnedTitleIds);
    }

    [Fact]
    public void ResetSession_BumpsRevisionEvenWhenAlreadyEmpty()
    {
        var titles = new RuntimeCharacterTitleState();
        long before = titles.Revision;

        titles.ResetSession();

        Assert.True(titles.Revision > before);
    }

    [Fact]
    public void ResetSession_NonEmptyState_FiresTableReplacedAndDisplayTitleChanged()
    {
        var titles = new RuntimeCharacterTitleState();
        titles.ReplaceTable(13u, [1u, 5u, 13u]);
        int tableReplacedCount = 0;
        titles.TableReplaced += () => tableReplacedCount++;
        var displayChanged = new List<uint>();
        titles.DisplayTitleChanged += id => displayChanged.Add(id);

        titles.ResetSession();

        Assert.Equal(1, tableReplacedCount);
        Assert.Equal([0u], displayChanged);
    }

    [Fact]
    public void ResetSession_RepeatedReset_StillFiresTableReplacedButNotDisplayTitleChanged()
    {
        // A2: TableReplaced publishes unconditionally on every reset; a
        // SECOND reset (display id already 0) must not re-fire
        // DisplayTitleChanged — there is no real transition to report.
        var titles = new RuntimeCharacterTitleState();
        titles.ResetSession();
        int tableReplacedCount = 0;
        titles.TableReplaced += () => tableReplacedCount++;
        var displayChanged = new List<uint>();
        titles.DisplayTitleChanged += id => displayChanged.Add(id);

        titles.ResetSession();

        Assert.Equal(1, tableReplacedCount);
        Assert.Empty(displayChanged);
    }

    [Fact]
    public void Count_ReflectsEarnedTitleIdsWithoutAllocatingTheArray()
    {
        var titles = new RuntimeCharacterTitleState();
        Assert.Equal(0, titles.Count);

        titles.ReplaceTable(13u, [1u, 5u, 13u]);

        Assert.Equal(3, titles.Count);
    }

    [Fact]
    public void Snapshot_ReflectsDisplayTitleAndCount()
    {
        var titles = new RuntimeCharacterTitleState();
        titles.ReplaceTable(13u, [1u, 5u, 13u]);

        RuntimeCharacterTitleSnapshot snapshot = titles.Snapshot;

        Assert.Equal(13u, snapshot.DisplayTitleId);
        Assert.Equal(3, snapshot.TitleCount);
        Assert.Equal(titles.Revision, snapshot.Revision);
    }

    [Fact]
    public void ReplaceTable_NullTitleIds_Throws()
    {
        var titles = new RuntimeCharacterTitleState();
        Assert.Throws<ArgumentNullException>(
            () => titles.ReplaceTable(1u, null!));
    }
}
