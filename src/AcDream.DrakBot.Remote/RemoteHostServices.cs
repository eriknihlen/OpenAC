namespace AcDream.DrakBot.Remote;

/// <summary>
/// What a host can lend the remote beyond the plugin contract. Everything is
/// optional; the remote reports a capability as absent when the host hands
/// nothing over, and the phone hides the feature.
/// </summary>
public sealed record RemoteHostServices
{
    public static RemoteHostServices None { get; } = new();

    /// <summary>
    /// Renders an icon (a render-surface id from the game's data files) as a
    /// PNG, for the phone's inventory view. Called on the plugin tick, never
    /// from a request thread, so a host may reach into its data files
    /// without locking. Null when the id is unknown.
    /// </summary>
    public Func<uint, byte[]?>? RenderIconPng { get; init; }

    /// <summary>Frames the host presented in the last second, when it renders at all.</summary>
    public Func<double>? FramesPerSecond { get; init; }

    /// <summary>The host's presented frames as JPEG, for the phone's live view; null on a host that does not render.</summary>
    public IRemoteFrameSource? Frames { get; init; }

    /// <summary>
    /// Reads a dungeon's floors and walls from the host's data files, for
    /// the phone's floor plans. Called on the plugin tick, never from a
    /// request thread. Null when the landblock has no cells; a null
    /// service means the host has no data files to read.
    /// </summary>
    public Func<uint, RemoteDungeonGeometry?>? DungeonGeometry { get; init; }

    /// <summary>
    /// Asks the host to leave the world and close, for the phone's
    /// close-client command. Null when the host would rather not be closed
    /// remotely.
    /// </summary>
    public Action? CloseClient { get; init; }
}
