namespace AcDream.Runtime.Session;

/// <summary>
/// Shared <c>ICharacterInfo</c>-style identity getters over a live
/// <see cref="GameRuntime"/>, used verbatim by both the graphical and
/// headless plugin automation surfaces so a client that reads Name,
/// WorldName, AccountName, ServerPopulation, or CharacterIndex gets the
/// same value from either host.
/// </summary>
public static class RuntimeCharacterIdentity
{
    /// <summary>
    /// The local player's display name. The player object hasn't streamed
    /// in yet at the moment login completes, but the character roster
    /// already carried the name from the selection edge -- fall back to
    /// that entry until the object arrives and takes over.
    /// </summary>
    public static string Name(GameRuntime runtime)
    {
        uint playerId = runtime.PlayerIdentity.ServerGuid;
        string? hydratedName = runtime.InventoryOwner.Objects.Get(playerId)?.Name;
        if (!string.IsNullOrEmpty(hydratedName))
            return hydratedName;
        return runtime.CharacterSelection.TryGet(
            playerId, out RuntimeCharacterSelectionEntry entry)
            ? entry.Name
            : string.Empty;
    }

    public static string WorldName(GameRuntime runtime) =>
        runtime.CharacterSelection.Snapshot.WorldName;

    public static string AccountName(GameRuntime runtime) =>
        runtime.CharacterSelection.Snapshot.AccountName;

    /// <summary>
    /// The population the server reported in its login-time world-name
    /// message, or -1 before that message has arrived. The server never
    /// sends an update after login, so this value is fixed for the rest
    /// of the session even as players come and go.
    /// </summary>
    public static int ServerPopulation(GameRuntime runtime) =>
        runtime.CharacterSelection.Snapshot.ServerPopulation ?? -1;

    public static int CharacterIndex(GameRuntime runtime) =>
        runtime.CharacterSelection.TryGet(
            runtime.PlayerIdentity.ServerGuid,
            out RuntimeCharacterSelectionEntry entry)
            ? entry.ActiveIndex
            : -1;
}
