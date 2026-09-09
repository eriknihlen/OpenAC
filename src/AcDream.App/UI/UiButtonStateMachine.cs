namespace AcDream.App.UI;

public readonly record struct UiButtonVisualInput(
    bool Disabled,
    bool Selected,
    bool RolloverEnabled,
    bool Pressed,
    bool PointerOver);

public static class UiButtonStateMachine
{
    public const uint Normal = 1u;
    public const uint NormalRollover = 2u;
    public const uint NormalPressed = 3u;
    public const uint Highlight = 6u;
    public const uint HighlightRollover = 7u;
    public const uint HighlightPressed = 8u;
    public const uint Ghosted = 13u;

    public static uint RequestedState(UiButtonVisualInput input)
    {
        if (input.Disabled)
            return Ghosted;

        uint requested = input.Selected ? Highlight : Normal;
        if (input.Pressed || (input.RolloverEnabled && input.PointerOver))
            requested = input.Selected ? HighlightRollover : NormalRollover;
        if (input.Pressed && input.PointerOver)
            requested = input.Selected ? HighlightPressed : NormalPressed;
        return requested;
    }

    public static uint ResolveState(
        uint currentState,
        UiButtonVisualInput input,
        IReadOnlySet<uint> availableStates)
    {
        uint requested = RequestedState(input);
        return availableStates.Contains(requested) ? requested : currentState;
    }

    public static string StateName(uint stateId)
        => stateId switch
        {
            Normal => "Normal",
            NormalRollover => "Normal_rollover",
            NormalPressed => "Normal_pressed",
            Highlight => "Highlight",
            HighlightRollover => "Highlight_rollover",
            HighlightPressed => "Highlight_pressed",
            Ghosted => "Ghosted",
            _ => "",
        };

    public static bool TryStateId(string stateName, out uint stateId)
    {
        stateId = stateName switch
        {
            "Normal" => Normal,
            "Normal_rollover" => NormalRollover,
            "Normal_pressed" => NormalPressed,
            "Highlight" => Highlight,
            "Highlight_rollover" => HighlightRollover,
            "Highlight_pressed" => HighlightPressed,
            "Ghosted" => Ghosted,
            _ => 0u,
        };
        return stateId != 0;
    }
}
