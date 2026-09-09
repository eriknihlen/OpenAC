using System.Numerics;

namespace AcDream.UI.Abstractions.Panels.Chat;

public static class RetailChatColorTable
{
    private static readonly Vector4 ColorWhite        = new(1f,     1f,     1f,     1f);
    private static readonly Vector4 Yellow            = new(1f,     1f,     0.247f, 1f);
    private static readonly Vector4 DarkYellow        = new(0.824f, 0.824f, 0.392f, 1f);
    private static readonly Vector4 ColorBrightPurple = new(1f,     0.498f, 1f,     1f);
    private static readonly Vector4 ColorDarkRed      = new(1f,     0.247f, 0.247f, 1f);
    private static readonly Vector4 ColorLightRed     = new(0.96f,  0.459f, 0.447f, 1f);
    private static readonly Vector4 ColorLightBlue    = new(0.247f, 0.749f, 1f,     1f);
    private static readonly Vector4 ColorPink         = new(1f,     0.588f, 0.588f, 1f);
    private static readonly Vector4 ColorCyan         = new(0.247f, 0.863f, 0.863f, 1f);
    private static readonly Vector4 ColorBlueGrey     = new(0.706f, 0.863f, 0.941f, 1f);
    private static readonly Vector4 ColorGrey         = new(0.824f, 0.824f, 0.784f, 1f);
    private static readonly Vector4 Orange            = new(0.933f, 0.573f, 0.118f, 1f);
    private static readonly Vector4 ColorGreen        = new(0.5f,   1f,     0.498f, 1f);
    private static readonly Vector4 ColorBrightRed    = new(1f,     0f,     0f,     1f);

    public static readonly IReadOnlyList<Vector4> Colors = new[]
    {
        /* 0x00 Default             */ ColorGreen,
        /* 0x01 All                 */ ColorGreen,
        /* 0x02 Speech              */ ColorWhite,
        /* 0x03 Tell                */ Yellow,
        /* 0x04 Speech_Direct_Send  */ DarkYellow,
        /* 0x05 System              */ ColorBrightPurple,
        ColorDarkRed,
        /* 0x07 Magic               */ ColorLightBlue,
        ColorPink,
        ColorPink,
        /* 0x0A Social              */ Yellow,
        /* 0x0B Social_Send         */ DarkYellow,
        /* 0x0C Emote               */ ColorGrey,
        /* 0x0D Advancement         */ ColorCyan,
        /* 0x0E Abuse               */ ColorBlueGrey,
        /* 0x0F Help                */ ColorDarkRed,
        /* 0x10 Appraisal           */ ColorGreen,
        /* 0x11 Spellcasting        */ ColorLightBlue,
        /* 0x12 Allegiance          */ Orange,
        /* 0x13 Fellowship          */ Yellow,
        /* 0x14 World_Broadcast     */ ColorGreen,
        ColorDarkRed,
        ColorLightRed,
        /* 0x17 Recall              */ ColorGreen,
        ColorGreen,
        /* 0x19 Salvaging           */ ColorGreen,
        ColorBrightRed,
        ColorBlueGrey,
        ColorBlueGrey,
        ColorBlueGrey,
        ColorBlueGrey,
        /* 0x1F Admin_Tell          */ Yellow,
        ColorBlueGrey,
        /* 0x21 (reserved)          */ Orange,
    };

    public static bool TryGetColor(uint logTextType, out Vector4 color)
    {
        if (logTextType < (uint)Colors.Count)
        {
            color = Colors[(int)logTextType];
            return true;
        }
        color = default;
        return false;
    }
}
