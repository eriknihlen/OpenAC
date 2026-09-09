namespace AcDream.UI.Abstractions.Input;

public enum InputScope
{
    /// <summary>Bottom of the stack — Esc, F1, F11 etc fire here so they
    /// work no matter what else is focused.</summary>
    Always,
    Game,
    Chat,
    EditField,
    Dialog,
    MeleeCombat,
    MissileCombat,
    MagicCombat,
    Camera,
}
