namespace AcDream.Content.Pak;

public enum PakEntryState
{
    Missing,
    Available,
    Corrupt,
}

/// <summary>
/// Result of reading and deserializing one pak payload. This preserves the
/// distinction between an absent key and a present-but-damaged external file.
/// </summary>
public enum PakObjectReadStatus
{
    Missing,
    Loaded,
    Corrupt,
}
