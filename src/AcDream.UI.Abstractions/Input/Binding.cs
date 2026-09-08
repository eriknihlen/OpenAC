namespace AcDream.UI.Abstractions.Input;

public readonly record struct Binding(
    KeyChord Chord,
    InputAction Action,
    ActivationType Activation = ActivationType.Press,
    InputScope Scope = InputScope.Game);
