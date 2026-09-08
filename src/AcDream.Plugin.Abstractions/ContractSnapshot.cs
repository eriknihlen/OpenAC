namespace AcDream.Plugin.Abstractions;

public readonly record struct ContractSnapshot(
    uint ContractId,
    uint Stage,
    uint Progress,
    bool IsDisplayed,
    string Name = "",
    string Description = "",
    string Status = "");
