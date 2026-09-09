namespace AcDream.Launcher.Core.Profiles;

public readonly record struct CharacterRosterEntry(
    uint Id,
    string Name,
    uint SecondsGreyedOut);
