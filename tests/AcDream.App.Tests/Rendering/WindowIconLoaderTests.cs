using System.Reflection;
using AcDream.App.Rendering;
using AcDream.App.Tests.Architecture;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace AcDream.App.Tests.Rendering;

public class WindowIconLoaderTests
{
    private static IReadOnlyList<string> ExpectedResourceNames()
    {
        var field = typeof(WindowIconLoader).GetField(
            "ResourceNames", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var names = (string[]?)field!.GetValue(null);
        Assert.NotNull(names);
        return names!;
    }

    [Fact]
    public void EveryDeclaredIconResource_IsActuallyEmbedded()
    {
        var assembly = typeof(WindowIconLoader).Assembly;
        string[] embedded = assembly.GetManifestResourceNames();

        foreach (string name in ExpectedResourceNames())
        {
            Assert.True(
                embedded.Contains(name),
                $"WindowIconLoader expects embedded resource '{name}', but the "
                + "assembly does not contain it. Check the EmbeddedResource "
                + "LogicalName entries in AcDream.App.csproj.");
        }
    }

    [Fact]
    public void EmbeddedIcons_DecodeToSquareRgbaImagesOfTheDeclaredSize()
    {
        var assembly = typeof(WindowIconLoader).Assembly;

        foreach (string name in ExpectedResourceNames())
        {
            using Stream? stream = assembly.GetManifestResourceStream(name);
            Assert.NotNull(stream);

            using var image = Image.Load<Rgba32>(stream!);
            Assert.Equal(image.Width, image.Height);

            // The trailing "-<size>.png" must match the actual pixel size, or
            // the window manager picks the wrong image for a surface.
            string stem = Path.GetFileNameWithoutExtension(name);
            string declared = stem[(stem.LastIndexOf('-') + 1)..];
            Assert.Equal(int.Parse(declared), image.Width);
        }
    }

    [Fact]
    public void IconSet_CoversBothSmallAndLargeSurfaces()
    {
        var sizes = new List<int>();
        foreach (string name in ExpectedResourceNames())
        {
            string stem = Path.GetFileNameWithoutExtension(name);
            sizes.Add(int.Parse(stem[(stem.LastIndexOf('-') + 1)..]));
        }

        // Title bars want ~16px and Alt-Tab/taskbar want a large one; shipping
        // only one size leaves the window manager resampling badly.
        Assert.Contains(16, sizes);
        Assert.True(sizes.Max() >= 128, "icon set has no large size for Alt-Tab/taskbar");
    }

    [Fact]
    public void IconIsAppliedFromLoad_NotFromWindowConstruction()
    {
        var gameWindow = typeof(GameWindow);
        MethodInfo? onLoad = gameWindow.GetMethod(
            "OnLoad", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(onLoad);

        bool CallsApply(MethodBase method) =>
            CompiledCallGraph.Read(method).Any(
                c => c.Target.DeclaringType == typeof(WindowIconLoader)
                     && c.Target.Name == nameof(WindowIconLoader.Apply));

        Assert.True(
            CallsApply(onLoad!),
            "GameWindow.OnLoad must apply the window icon: it is the first point "
            + "at which the native window exists.");

        foreach (MethodInfo candidate in gameWindow.GetMethods(
                     BindingFlags.NonPublic | BindingFlags.Public
                     | BindingFlags.Instance | BindingFlags.Static))
        {
            if (candidate == onLoad || candidate.IsAbstract || candidate.ContainsGenericParameters)
                continue;

            bool createsWindow = CompiledCallGraph.Read(candidate).Any(
                c => c.Target.Name == "Create"
                     && c.Target.DeclaringType?.FullName == "Silk.NET.Windowing.Window");
            if (!createsWindow)
                continue;

            Assert.False(
                CallsApply(candidate),
                $"{candidate.Name} calls Window.Create and applies the window icon in the "
                + "same method. The window is not initialized there, so the icon is "
                + "silently lost — apply it from OnLoad instead.");
        }
    }
}
