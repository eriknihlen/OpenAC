namespace AcDream.UI.Abstractions.Panels.Settings;

public static class ChatOpacityLink
{
    public static (float DefaultOpacity, float ActiveOpacity) SetDefault(
        float currentActive, float newDefault)
    {
        newDefault = System.Math.Clamp(newDefault, 0f, 1f);
        float active = currentActive < newDefault ? newDefault : currentActive;
        return (newDefault, active);
    }

    public static (float DefaultOpacity, float ActiveOpacity) SetActive(
        float currentDefault, float newActive)
    {
        newActive = System.Math.Clamp(newActive, 0f, 1f);
        float def = currentDefault > newActive ? newActive : currentDefault;
        return (def, newActive);
    }
}
