namespace AcDream.App.UI;

internal static class RetailScrollbarChrome
{
    internal const uint Track = 0x06004C5Fu;

    internal const uint UpNormal = 0x06004C6Cu;
    internal const uint UpRollover = 0x06004C6Du;
    internal const uint UpPressed = 0x06004C6Eu;

    internal const uint DownNormal = 0x06004C69u;
    internal const uint DownRollover = 0x06004C6Au;
    internal const uint DownPressed = 0x06004C6Bu;

    internal const uint ThumbTopNormal = 0x06004C60u;
    internal const uint ThumbTopRollover = 0x06004C61u;
    internal const uint ThumbTopPressed = 0x06004C62u;
    internal const uint ThumbMidNormal = 0x06004C63u;
    internal const uint ThumbMidRollover = 0x06004C64u;
    internal const uint ThumbMidPressed = 0x06004C65u;
    internal const uint ThumbBotNormal = 0x06004C66u;
    internal const uint ThumbBotRollover = 0x06004C67u;
    internal const uint ThumbBotPressed = 0x06004C68u;

    internal const uint HTrack = 0x06004C7Fu;

    internal const uint LeftNormal = 0x06004C8Cu;
    internal const uint LeftRollover = 0x06004C8Du;
    internal const uint LeftPressed = 0x06004C8Eu;

    internal const uint RightNormal = 0x06004C89u;
    internal const uint RightRollover = 0x06004C8Au;
    internal const uint RightPressed = 0x06004C8Bu;

    internal const uint HThumbTopNormal = 0x06004C80u;
    internal const uint HThumbTopRollover = 0x06004C81u;
    internal const uint HThumbTopPressed = 0x06004C82u;
    internal const uint HThumbMidNormal = 0x06004C83u;
    internal const uint HThumbMidRollover = 0x06004C84u;
    internal const uint HThumbMidPressed = 0x06004C85u;
    internal const uint HThumbBotNormal = 0x06004C86u;
    internal const uint HThumbBotRollover = 0x06004C87u;
    internal const uint HThumbBotPressed = 0x06004C88u;

    internal static void ApplyVertical(UiScrollbar bar)
    {
        bar.TrackSprite = Track;
        bar.UpSprite = UpNormal;
        bar.UpRolloverSprite = UpRollover;
        bar.UpPressedSprite = UpPressed;
        bar.DownSprite = DownNormal;
        bar.DownRolloverSprite = DownRollover;
        bar.DownPressedSprite = DownPressed;
        bar.ThumbTopSprite = ThumbTopNormal;
        bar.ThumbTopRolloverSprite = ThumbTopRollover;
        bar.ThumbTopPressedSprite = ThumbTopPressed;
        bar.ThumbSprite = ThumbMidNormal;
        bar.ThumbRolloverSprite = ThumbMidRollover;
        bar.ThumbPressedSprite = ThumbMidPressed;
        bar.ThumbBotSprite = ThumbBotNormal;
        bar.ThumbBotRolloverSprite = ThumbBotRollover;
        bar.ThumbBotPressedSprite = ThumbBotPressed;
    }

    internal static void ApplyToMenuPopup(UiMenu menu)
    {
        menu.ScrollTrackSprite = Track;
        menu.ScrollThumbTopSprite = ThumbTopNormal;
        menu.ScrollThumbSprite = ThumbMidNormal;
        menu.ScrollThumbBottomSprite = ThumbBotNormal;
        menu.ScrollUpSprite = UpNormal;
        menu.ScrollDownSprite = DownNormal;
    }

    internal static void ApplyHorizontal(UiScrollbar bar)
    {
        bar.TrackSprite = HTrack;
        bar.UpSprite = LeftNormal;
        bar.UpRolloverSprite = LeftRollover;
        bar.UpPressedSprite = LeftPressed;
        bar.DownSprite = RightNormal;
        bar.DownRolloverSprite = RightRollover;
        bar.DownPressedSprite = RightPressed;
        bar.ThumbTopSprite = HThumbTopNormal;
        bar.ThumbTopRolloverSprite = HThumbTopRollover;
        bar.ThumbTopPressedSprite = HThumbTopPressed;
        bar.ThumbSprite = HThumbMidNormal;
        bar.ThumbRolloverSprite = HThumbMidRollover;
        bar.ThumbPressedSprite = HThumbMidPressed;
        bar.ThumbBotSprite = HThumbBotNormal;
        bar.ThumbBotRolloverSprite = HThumbBotRollover;
        bar.ThumbBotPressedSprite = HThumbBotPressed;
    }
}
