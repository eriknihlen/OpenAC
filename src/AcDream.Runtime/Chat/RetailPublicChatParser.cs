namespace AcDream.Runtime.Chat;

public readonly record struct RetailChatPose(
    uint MotionCommand,
    string SelfText,
    string OthersText);

public static class RetailPublicChatParser
{
    public static string ExtractPoses(
        string text,
        Func<string, RetailChatPose?>? resolve,
        Action<RetailChatPose>? execute)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (resolve is null || execute is null || text.Length == 0)
            return text.Trim();

        string remaining = text;
        int cursor = 0;
        while (cursor < remaining.Length)
        {
            int star = remaining.IndexOf('*', cursor);
            int angle = remaining.IndexOf('<', cursor);
            int open;
            char close;
            if (star < 0)
            {
                open = angle;
                close = '>';
            }
            else if (angle < 0 || star <= angle)
            {
                open = star;
                close = '*';
            }
            else
            {
                open = angle;
                close = '>';
            }

            if (open < 0)
                break;
            int end = remaining.IndexOf(close, open + 1);
            if (end < 0)
            {
                cursor = open + 1;
                continue;
            }

            string command = remaining[(open + 1)..end];
            RetailChatPose? pose = resolve(command);
            if (pose is { MotionCommand: not 0u } resolved)
            {
                execute(resolved);
                remaining = remaining.Remove(open, end - open + 1);
                cursor = open;
            }
            else
            {
                cursor = end + 1;
            }
        }

        return remaining.Trim();
    }
}
