namespace AcDream.Core.Physics;

public static class MotionCommandResolver
{
    private static readonly AceModernCommandCatalog s_aceModern = new();

    public static uint ReconstructFullCommand(ushort wireCommand)
    {
        return s_aceModern.ReconstructFullCommand(wireCommand);
    }
}
