namespace AcDream.UI.Abstractions;

public interface IPanelHost
{
    void Register(IPanel panel);

    /// <summary>Remove the panel with the matching id. No-op if not present.</summary>
    void Unregister(string panelId);

    void RenderAll(PanelContext ctx);
}
