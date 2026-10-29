namespace AcDream.Plugin.Abstractions;

/// <summary>
/// An image the host holds on a plugin's behalf. The handle is a number
/// that means nothing outside the host that issued it; a plugin keeps it,
/// draws with it and gives it back through <see cref="IPluginImages.Release"/>.
/// The size is carried so the plugin can lay the image out without asking
/// again.
/// </summary>
/// <param name="Handle">The host's number for this image; 0 means no image.</param>
/// <param name="Width">The image's width in pixels.</param>
/// <param name="Height">The image's height in pixels.</param>
public readonly record struct PluginImage(int Handle, int Width, int Height)
{
    /// <summary>No image: what every refused request returns.</summary>
    public static PluginImage None => default;

    /// <summary>True when the host issued this image and it has not yet been released.</summary>
    public bool IsValid => Handle != 0;
}

/// <summary>
/// Images a plugin can draw on a canvas: the client's own art by surface
/// id, the icons the client composes for spells and objects, and art the
/// plugin ships itself, decoded by the host. There are no raw pixel uploads.
///
/// <para>Every request is counted once per distinct thing asked for and
/// held as many times as it was asked for: asking twice for the same
/// surface returns the same image, and it takes two releases to let it
/// go. A plugin may hold at most <see cref="MaximumCount"/> images, and its
/// own art at most <see cref="MaximumBytes"/> of texture memory; a request
/// past either limit, or a plugin image wider or taller than
/// <see cref="MaximumDimension"/>, is refused with
/// <see cref="PluginImage.None"/> and reported once in the client's log.
/// Client art is shared with every other reader of the same surface and is
/// not counted against the byte budget. The file icons the plugin's markup
/// draws (<c>&lt;icon file="..."/&gt;</c>) are held here too, under the same
/// limits, while a window draws them.</para>
///
/// <para>Call this only from the thread the plugin's own callbacks run on
/// (the one that raises the tick); the host refuses any other. On a host
/// without a window, or before the client's interface is up, every request
/// answers <see cref="PluginImage.None"/> and <see cref="IsAvailable"/> is
/// false. Images are dropped when the interface is torn down, for example
/// on a reconnect; drawing with a dropped image draws nothing, and the
/// plugin asks again once it is drawing again.</para>
/// </summary>
public interface IPluginImages
{
    /// <summary>
    /// Whether requests can currently be answered: false on a host without
    /// a window and until the client's interface is up.
    /// </summary>
    bool IsAvailable => false;

    /// <summary>
    /// The client's own art for a render surface, either a full id or a bare
    /// index, normalised the way <see cref="PluginIcons.Normalize"/> does.
    /// </summary>
    /// <param name="surfaceIdOrIndex">The surface's full id, or its bare index in the image block.</param>
    /// <returns>The image, or <see cref="PluginImage.None"/> when the client has no such surface or the request was refused.</returns>
    PluginImage FromClientArt(uint surfaceIdOrIndex) => PluginImage.None;

    /// <summary>The icon the client composes for a spell, as the spell bar draws it.</summary>
    /// <param name="spellId">The spell's id.</param>
    /// <returns>The image, or <see cref="PluginImage.None"/> when there is no such spell or the request was refused.</returns>
    PluginImage FromSpellIcon(uint spellId) => PluginImage.None;

    /// <summary>
    /// The icon the client composes for a world object it currently knows,
    /// with the object's underlay, overlay and effect layers, as the
    /// inventory draws it.
    /// </summary>
    /// <param name="objectId">The object's id.</param>
    /// <returns>The image, or <see cref="PluginImage.None"/> when the client does not know the object, it has no icon, or the request was refused.</returns>
    PluginImage FromObjectIcon(uint objectId) => PluginImage.None;

    /// <summary>
    /// Art the plugin ships, opened through <paramref name="open"/> only when
    /// the host does not already hold an image under <paramref name="name"/>.
    /// The host decodes the stream (PNG, JPEG, BMP, TGA or GIF) and disposes
    /// it; the plugin never sees pixels. A second request under the same
    /// name is the same image, held once more.
    /// </summary>
    /// <param name="name">The plugin's own name for the image, unique within the plugin, such as a relative file path.</param>
    /// <param name="open">Opens a fresh readable stream of the encoded image.</param>
    /// <returns>The image, or <see cref="PluginImage.None"/> when the stream could not be opened or decoded, the image is too large, or the request was refused.</returns>
    PluginImage FromStream(string name, Func<Stream> open) => PluginImage.None;

    /// <summary>
    /// Lets go of one hold on an image. The image stays while another hold
    /// remains and is freed on the last.
    /// </summary>
    /// <param name="image">An image this surface issued.</param>
    /// <returns>False for an image this surface did not issue or has already let go of completely.</returns>
    bool Release(PluginImage image) => false;

    /// <summary>
    /// How many distinct images the plugin currently holds: the ones it asked for
    /// here, and the file icons its markup windows are drawing, which count against
    /// the same <see cref="MaximumCount"/> and <see cref="MaximumBytes"/>.
    /// </summary>
    int Count => 0;

    /// <summary>The most distinct images the plugin may hold at once; 0 on a host that draws nothing.</summary>
    int MaximumCount => 0;

    /// <summary>The most texture memory, in bytes, the plugin's own art may take; 0 on a host that draws nothing.</summary>
    long MaximumBytes => 0;

    /// <summary>The widest or tallest a plugin's own image may be, in pixels; 0 on a host that draws nothing.</summary>
    int MaximumDimension => 0;
}

/// <summary>
/// The image surface a host that draws nothing hands out: every request
/// answers <see cref="PluginImage.None"/> and nothing is held.
/// </summary>
public sealed class NoOpPluginImages : IPluginImages
{
    /// <summary>The shared instance; this type holds no state.</summary>
    public static NoOpPluginImages Instance { get; } = new();

    private NoOpPluginImages()
    {
    }
}
