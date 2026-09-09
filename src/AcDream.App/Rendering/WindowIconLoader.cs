using Silk.NET.Core;
using Silk.NET.Windowing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AcDream.App.Rendering;

internal static class WindowIconLoader
{
    // Ordered small -> large purely for readability; the window manager selects
    // by size, not by position.
    private static readonly string[] ResourceNames =
    {
        "AcDream.App.Rendering.Icons.acdream-client-16.png",
        "AcDream.App.Rendering.Icons.acdream-client-32.png",
        "AcDream.App.Rendering.Icons.acdream-client-48.png",
        "AcDream.App.Rendering.Icons.acdream-client-256.png",
    };

    public static void Apply(IWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!window.IsInitialized)
        {
            Console.Error.WriteLine(
                "window icon: refusing to set an icon on an uninitialized window — "
                + "call WindowIconLoader.Apply from the Load callback, not next to "
                + "Window.Create.");
            return;
        }

        RawImage[] images;
        try
        {
            images = Decode();
        }
        catch (Exception failure)
        {
            Console.Error.WriteLine($"window icon: could not decode embedded icons — {failure}");
            return;
        }

        if (images.Length == 0)
        {
            Console.Error.WriteLine("window icon: no embedded icon resources found");
            return;
        }

        try
        {
            window.SetWindowIcon(images.AsSpan());
        }
        catch (Exception failure)
        {
            // Wayland has no window-icon protocol and GLFW reports the request
            // as unsupported there. That is a platform fact, not a bug, and it
            // must not be fatal — but it is still worth printing so an
            // unexpectedly icon-less window on a supported platform is
            // traceable rather than mysterious.
            Console.Error.WriteLine($"window icon: platform rejected the icon — {failure.Message}");
        }
    }

    private static RawImage[] Decode()
    {
        var assembly = typeof(WindowIconLoader).Assembly;
        var decoded = new List<RawImage>(ResourceNames.Length);

        foreach (string name in ResourceNames)
        {
            using Stream? stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                Console.Error.WriteLine($"window icon: embedded resource missing — {name}");
                continue;
            }

            using var image = Image.Load<Rgba32>(stream);
            var pixels = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(pixels);
            decoded.Add(new RawImage(image.Width, image.Height, pixels));
        }

        return decoded.ToArray();
    }
}
