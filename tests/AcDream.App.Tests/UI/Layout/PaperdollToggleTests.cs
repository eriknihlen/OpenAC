using AcDream.App.UI.Layout;
using Xunit;

namespace AcDream.App.Tests.UI.Layout;

public class PaperdollToggleTests
{
    private static readonly uint[] ArmorIds =
    {
        0x100005abu, 0x100005acu, 0x100005adu, 0x100005aeu, 0x100005afu,
        0x100005b0u, 0x100005b1u, 0x100005b2u, 0x100005b3u,
    };

    [Fact]
    public void ArmorSlotIds_match_the_reference_nine()
    {
        Assert.Equal(ArmorIds, PaperdollController.ArmorSlotElementIds);
    }

    [Fact]
    public void DollView_default_shows_doll_hides_armor()
    {
        var v = new PaperdollController.PaperdollViewState();
        Assert.False(v.SlotView);
        Assert.True(v.DollVisible);
        Assert.False(v.ArmorSlotsVisible);
    }

    [Fact]
    public void Toggle_to_slotview_hides_doll_shows_armor_and_back()
    {
        var v = new PaperdollController.PaperdollViewState();
        v.Toggle();
        Assert.True(v.SlotView);
        Assert.False(v.DollVisible);
        Assert.True(v.ArmorSlotsVisible);
        v.Toggle();
        Assert.False(v.SlotView);
        Assert.True(v.DollVisible);
        Assert.False(v.ArmorSlotsVisible);
    }
}
