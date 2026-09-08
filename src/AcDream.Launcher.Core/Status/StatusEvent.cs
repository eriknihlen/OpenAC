namespace AcDream.Launcher.Core.Status;

public abstract record StatusEvent
{
    public required int V { get; init; }

    public required string E { get; init; }

    public required DateTimeOffset T { get; init; }

    public required string SessionId { get; init; }
}

public sealed record StartedStatusEvent : StatusEvent;

public sealed record ConnectedStatusEvent : StatusEvent;

public readonly record struct StatusCharacterEntry(
    uint Id,
    string Name,
    uint SecondsGreyedOut);

public sealed record CharacterListStatusEvent : StatusEvent
{
    public required string AccountName { get; init; }

    public required int SlotCount { get; init; }

    public required IReadOnlyList<StatusCharacterEntry> Characters { get; init; }
}

public sealed record EnteredWorldStatusEvent : StatusEvent
{
    public required uint CharacterId { get; init; }

    public required string CharacterName { get; init; }
}

public sealed record CharacterCreatedStatusEvent : StatusEvent
{
    public required uint Guid { get; init; }

    public required string Name { get; init; }
}

public sealed record CreationFailedStatusEvent : StatusEvent
{
    public required uint Code { get; init; }

    public required string Reason { get; init; }

    public required string Name { get; init; }
}

public sealed record PluginLoadedStatusEvent : StatusEvent
{
    public required string Plugin { get; init; }
}

public sealed record PluginFailedStatusEvent : StatusEvent
{
    public required string Plugin { get; init; }

    public required string Error { get; init; }
}

public sealed record LoginCommandFailedStatusEvent : StatusEvent
{
    public required int CommandIndex { get; init; }

    public required string Command { get; init; }

    public required string Error { get; init; }
}

public sealed record DisconnectedStatusEvent : StatusEvent
{
    public required string Reason { get; init; }
}

public sealed record ExitedStatusEvent : StatusEvent
{
    public required int Code { get; init; }

    public required string Reason { get; init; }
}

public sealed record UnknownStatusEvent : StatusEvent
{
    public required string RawJson { get; init; }
}

public sealed record MalformedStatusEvent : StatusEvent
{
    public required string Error { get; init; }
}
