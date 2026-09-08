namespace AcDream.App.UI;

public interface IUiViewportRenderer
{
    uint Render(int width, int height);

    bool TextureIsBottomUp { get; }
}
