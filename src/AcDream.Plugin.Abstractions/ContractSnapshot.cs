namespace AcDream.Plugin.Abstractions;

/// <summary>
/// One tracked quest contract as the client currently understands it. The
/// text fields are empty when the client has no description for the
/// contract in its loaded content.
/// </summary>
/// <param name="ContractId">The contract's id.</param>
/// <param name="Stage">How far along the contract the character is.</param>
/// <param name="Progress">The contract's progress counter at this stage.</param>
/// <param name="IsDisplayed">
/// Whether this is the contract the character has chosen to track.
/// </param>
/// <param name="Name">The contract's title, or empty when unknown.</param>
/// <param name="Description">
/// The contract's long description, or empty when unknown.
/// </param>
/// <param name="Status">
/// The one-line progress text for the current stage, or empty when the
/// client has no description to build it from.
/// </param>
public readonly record struct ContractSnapshot(
    uint ContractId,
    uint Stage,
    uint Progress,
    bool IsDisplayed,
    string Name = "",
    string Description = "",
    string Status = "");
