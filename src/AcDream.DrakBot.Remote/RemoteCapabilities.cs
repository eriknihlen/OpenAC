using System.Text.Json;

namespace AcDream.DrakBot.Remote;

/// <summary>
/// What this remote can do, stated in every status document so a phone
/// hides what a client cannot offer instead of showing a button that
/// fails: a headless host has no icons or frames, no host lets itself be
/// closed unless it said so, and the H.264 stream, tap-to-click, the run
/// archive and the dungeon maps of the RynthCore agent are not here yet.
/// <c>video</c> is the MJPEG live view (<c>/frame</c>, <c>/stream</c>).
/// </summary>
public sealed record RemoteCapabilities(
    bool Inventory = true,
    bool Settings = true,
    bool Movement = true,
    bool Chat = true,
    bool Icons = false,
    bool CloseClient = false,
    bool Video = false,
    bool VideoHd = false,
    bool Click = false,
    bool Runs = false,
    bool Maps = false)
{
    public static RemoteCapabilities For(RemoteHostServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return new RemoteCapabilities(
            Icons: services.RenderIconPng is not null,
            CloseClient: services.CloseClient is not null,
            Video: services.Frames?.IsAvailable == true);
    }

    public void Write(Utf8JsonWriter json)
    {
        ArgumentNullException.ThrowIfNull(json);
        json.WriteStartObject();
        json.WriteBoolean("inventory", Inventory);
        json.WriteBoolean("settings", Settings);
        json.WriteBoolean("movement", Movement);
        json.WriteBoolean("chat", Chat);
        json.WriteBoolean("icons", Icons);
        json.WriteBoolean("closeClient", CloseClient);
        json.WriteBoolean("video", Video);
        json.WriteBoolean("videoHd", VideoHd);
        json.WriteBoolean("click", Click);
        json.WriteBoolean("runs", Runs);
        json.WriteBoolean("maps", Maps);
        json.WriteEndObject();
    }
}
