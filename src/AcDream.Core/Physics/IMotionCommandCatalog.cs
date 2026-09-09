namespace AcDream.Core.Physics;

public interface IMotionCommandCatalog
{
    /// <summary>
    /// Reconstruct the full 32-bit MotionCommand from a 16-bit wire value.
    /// Returns 0 if no entry matches.
    /// </summary>
    uint ReconstructFullCommand(ushort wireCommand);
}
