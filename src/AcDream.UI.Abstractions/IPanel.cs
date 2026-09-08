namespace AcDream.UI.Abstractions;

public interface IPanel
{
    string Id { get; }

    string Title { get; }

    bool IsVisible { get; set; }

    void Render(PanelContext ctx, IPanelRenderer renderer);
}
