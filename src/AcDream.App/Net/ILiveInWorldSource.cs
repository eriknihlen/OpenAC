namespace AcDream.App.Net;

internal interface ILiveInWorldSource
{
    bool IsInWorld { get; }
}

internal interface ILiveWorldSessionSource
{
    AcDream.Core.Net.WorldSession? CurrentSession { get; }
}

internal interface ILiveUiSessionTarget : ILiveInWorldSource, ILiveWorldSessionSource
{
    AcDream.Runtime.Chat.ICommandBus Commands { get; }
}
