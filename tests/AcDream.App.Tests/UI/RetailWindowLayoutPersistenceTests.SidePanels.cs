using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.App.Tests.UI;

public sealed partial class RetailWindowLayoutPersistenceTests
{
    [Theory]
    [InlineData(400f)]
    [InlineData(580f)]
    public void SidePanelSize_SurvivesMapSwitchAndFreshLogin(float chosenHeight)
    {
        string[] names = [WindowNames.Inventory, WindowNames.Character, WindowNames.Spellbook,
            WindowNames.MapHouse, WindowNames.Options, WindowNames.SocialPanel, WindowNames.Journal];
        var store = new SettingsStore(PathName);
        for (int login = 0; login < 2; login++)
        {
            var root = new UiRoot { Width = 1280, Height = 900 };
            using var panels = new RetailPanelUiController(root.IsWindowVisible, root.ShowWindow, root.HideWindow);
            panels.ConfigureMainPanelFrame(new ElementInfo
            {
                Width = 310, Height = 372,
                MinWidth = 310, MaxWidth = 310, MinHeight = 372, MaxHeight = 1000,
            });
            var handles = new List<RetailWindowHandle>();
            for (int i = 0; i < names.Length; i++)
            {
                var handle = Mount(root, names[i], width: 310, height: 420 + i * 20);
                handle.Hide();
                handle.OuterFrame.MinHeight = handle.Height;
                handle.OuterFrame.MaxHeight = 1000;
                handle.OuterFrame.ResizeY = names[i] != WindowNames.MapHouse;
                panels.RegisterMainPanel((uint)i + 1, names[i], handle);
                handles.Add(handle);
                Assert.Equal(372, handle.Height);
            }
            using var persistence = new RetailWindowLayoutPersistence(
                root.WindowManager, store, () => "Alice", () => (1280, 900));
            persistence.RestoreAll();
            if (login == 0)
            {
                panels.SetPanelVisibility(1, true);
                handles[0].ResizeTo(310, chosenHeight);
            }
            foreach (int index in new[] { 3, 2, 4, 5, 6, 1, 0, 3 })
            {
                panels.SetPanelVisibility((uint)index + 1, true);
                Assert.All(handles, h => Assert.Equal(chosenHeight, h.Height));
                Assert.Equal(ResizeEdges.Bottom, handles[index].OuterFrame.ResizableEdges);
            }
            if (login == 0)
            {
                handles[3].ResizeTo(310, chosenHeight + 20);
                Assert.All(handles, h => Assert.Equal(chosenHeight + 20, h.Height));
                handles[3].ResizeTo(310, chosenHeight);
            }
            persistence.SaveAll();
        }
    }
}
