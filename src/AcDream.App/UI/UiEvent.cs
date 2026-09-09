using System.Numerics;

namespace AcDream.App.UI;

public readonly record struct UiEvent(
    uint SourceId,
    UiElement? Target,
    int Type,
    int Data0 = 0,
    int Data1 = 0,
    int Data2 = 0,
    int Data3 = 0,
    object? Payload = null);

public static class UiEventType
{
    public const int Click        = 0x01;
    public const int HoverEnter   = 0x05;
    public const int HoverLeave   = 0x06;
    public const int Tooltip      = 0x07;
    public const int DoubleClick  = 0x08;
    public const int Scroll       = 0x0A;
    public const int RightClick   = 0x0E;
    public const int DragBegin    = 0x15;
    public const int DragOver     = 0x1C;
    public const int DragEnter    = 0x21;
    public const int FocusLost    = 0x28;
    public const int FocusGained  = 0x29;
    public const int DropReleased = 0x3E;

    public const int MouseMove    = 0x200;
    public const int MouseDown    = 0x201;   // left button down
    public const int MouseUp      = 0x202;   // left button up
    public const int CaptureChanged = 0x215;
    public const int DoubleClickLeft = 0x203;
    public const int RightDown    = 0x204;
    public const int RightUp      = 0x205;
    public const int MiddleDown   = 0x207;
    public const int MiddleUp     = 0x208;

    public const int KeyDown      = 0x100;
    public const int KeyUp        = 0x101;
    public const int Char         = 0x102;
}

public enum UiMouseButton
{
    Left   = 1,
    Right  = 2,
    Middle = 3,
}
