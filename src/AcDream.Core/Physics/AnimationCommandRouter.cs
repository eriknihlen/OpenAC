namespace AcDream.Core.Physics;

public static class AnimationCommandRouter
{
    private const uint ActionMask = 0x10000000u;
    private const uint ModifierMask = 0x20000000u;
    private const uint SubStateMask = 0x40000000u;
    private const uint ClassMask = 0xFF000000u;

    public static AnimationCommandRouteKind Classify(uint fullCommand)
    {
        if (fullCommand == 0)
            return AnimationCommandRouteKind.None;

        uint cls = fullCommand & ClassMask;
        if (cls == 0x12000000u || cls == 0x13000000u)
            return AnimationCommandRouteKind.ChatEmote;

        if ((fullCommand & ModifierMask) != 0)
            return AnimationCommandRouteKind.Modifier;

        if ((fullCommand & ActionMask) != 0)
            return AnimationCommandRouteKind.Action;

        if ((fullCommand & SubStateMask) != 0)
            return AnimationCommandRouteKind.SubState;

        return AnimationCommandRouteKind.Ignored;
    }

    public static AnimationCommandRouteKind RouteWireCommand(
        AnimationSequencer sequencer,
        uint currentStyle,
        ushort wireCommand,
        float speedMod = 1f)
    {
        uint fullCommand = MotionCommandResolver.ReconstructFullCommand(wireCommand);
        return RouteFullCommand(sequencer, currentStyle, fullCommand, speedMod);
    }

    /// <summary>
    /// Routes a full MotionCommand to the matching sequencer API.
    /// </summary>
    public static AnimationCommandRouteKind RouteFullCommand(
        AnimationSequencer sequencer,
        uint currentStyle,
        uint fullCommand,
        float speedMod = 1f)
    {
        var route = Classify(fullCommand);
        switch (route)
        {
            case AnimationCommandRouteKind.Action:
            case AnimationCommandRouteKind.Modifier:
            case AnimationCommandRouteKind.ChatEmote:
                sequencer.PlayAction(fullCommand, speedMod);
                break;
            case AnimationCommandRouteKind.SubState:
                sequencer.SetCycle(currentStyle, fullCommand, speedMod);
                break;
        }

        return route;
    }
}

public enum AnimationCommandRouteKind
{
    None = 0,
    Action,
    Modifier,
    ChatEmote,
    SubState,
    Ignored,
}
