using AcDream.Runtime.Chat;
using AcDream.Content;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatMotionCommand = DatReaderWriter.Enums.MotionCommand;

namespace AcDream.App.Net;

internal sealed class DatChatPoseCatalog
{
    private const uint ChatPoseTableId = 0x0E000007u;
    private readonly IReadOnlyDictionary<string, RetailChatPose> _poses;

    private DatChatPoseCatalog(
        IReadOnlyDictionary<string, RetailChatPose> poses) =>
        _poses = poses;

    public static DatChatPoseCatalog Load(IDatReaderWriter dats, object datLock)
    {
        ArgumentNullException.ThrowIfNull(dats);
        ArgumentNullException.ThrowIfNull(datLock);
        lock (datLock)
        {
            ChatPoseTable? table = dats.Get<ChatPoseTable>(ChatPoseTableId);
            if (table is null)
                return new DatChatPoseCatalog(
                    new Dictionary<string, RetailChatPose>(
                        StringComparer.OrdinalIgnoreCase));

            var emotes = new Dictionary<string, (string Self, string Others)>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var pair in table.ChatEmotes)
            {
                emotes[pair.Key.Value] = (
                    pair.Value.MyEmote.Value,
                    pair.Value.OtherEmote.Value);
            }

            var poses = new Dictionary<string, RetailChatPose>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var pair in table.ChatPoses)
            {
                string command = pair.Key.Value;
                string motionName = pair.Value.Value;
                if (string.IsNullOrEmpty(command)
                    || !Enum.TryParse(
                        motionName,
                        ignoreCase: true,
                        out DatMotionCommand motion))
                {
                    continue;
                }
                emotes.TryGetValue(motionName, out var text);
                poses[command] = new RetailChatPose(
                    (uint)motion,
                    text.Self ?? string.Empty,
                    text.Others ?? string.Empty);
            }
            return new DatChatPoseCatalog(poses);
        }
    }

    public RetailChatPose? Resolve(string command, bool male)
    {
        if (!_poses.TryGetValue(command, out RetailChatPose pose))
            return null;
        string possessive = male ? "his" : "her";
        return pose with
        {
            OthersText = pose.OthersText.Replace(
                "%p",
                possessive,
                StringComparison.Ordinal),
        };
    }
}
