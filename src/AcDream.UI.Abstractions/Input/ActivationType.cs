namespace AcDream.UI.Abstractions.Input;

public enum ActivationType
{
    /// <summary>Fire on key-down. Default for most actions.</summary>
    Press,
    /// <summary>Fire on key-up. Used for paired walk-mode toggles.</summary>
    Release,
    Hold,
    DoubleClick,
    Click,
    /// <summary>Mouse axis or other analog input. Reserved for future
    /// rebindable mouse-look — K.1a does not emit this.</summary>
    Analog,
}
