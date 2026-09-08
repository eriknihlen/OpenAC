namespace AcDream.Core.Physics.Motion;

public sealed class TargettedVoyeurInfo
{
    internal IPhysicsObjHost WatcherHost { get; }

    public uint ObjectId { get; }

    public double Quantum { get; set; }

    public float Radius { get; set; }

    public Position LastSentPosition { get; set; }

    public TargettedVoyeurInfo(
        uint objectId,
        float radius,
        double quantum,
        IPhysicsObjHost watcherHost)
    {
        ArgumentNullException.ThrowIfNull(watcherHost);
        if (watcherHost.Id != objectId)
        {
            throw new ArgumentException(
                "Watcher identity must match the voyeur object ID.",
                nameof(watcherHost));
        }

        ObjectId = objectId;
        Radius = radius;
        Quantum = quantum;
        WatcherHost = watcherHost;
    }
}
