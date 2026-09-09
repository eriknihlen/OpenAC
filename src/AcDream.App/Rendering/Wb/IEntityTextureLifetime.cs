namespace AcDream.App.Rendering.Wb;

public interface IEntityTextureLifetime
{
    void ReleaseOwner(uint localEntityId);
}
