using System.Text.Json.Serialization;

namespace AcDream.Launcher.Core.Launching;

public sealed record LauncherInstallRecord(
    string DatDirectory,
    string PreparedAssetPath,
    string PreparedAssetSha256 = "",
    long PreparedAssetSize = 0,
    uint BakeToolVersion = 0)
{
    public const int CurrentRecordVersion = 1;

    public int Version { get; init; } = CurrentRecordVersion;

    public bool HasIntegrityMetadata =>
        !string.IsNullOrEmpty(PreparedAssetSha256)
        && PreparedAssetSha256.Length == 64
        && PreparedAssetSize > 0
        && BakeToolVersion > 0;

    [JsonIgnore]
    public string? PreparedAssetOverlayPath { get; init; }

    [JsonIgnore]
    public uint EffectiveBakeToolVersion { get; init; }

    [JsonIgnore]
    public bool RequiresClientCompatibilityConfirmation { get; init; }

    [JsonIgnore]
    public uint ResolvedBakeToolVersion =>
        EffectiveBakeToolVersion == 0
            ? BakeToolVersion
            : EffectiveBakeToolVersion;
}
