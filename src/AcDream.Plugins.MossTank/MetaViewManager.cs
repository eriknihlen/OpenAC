using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal sealed class MetaViewManager
{
    private const int OfficialViewLimit = 5;
    private readonly IPluginHost _host;
    private readonly Dictionary<string, IDisposable> _views =
        new(StringComparer.Ordinal);

    public MetaViewManager(IPluginHost host) =>
        _host = host ?? throw new ArgumentNullException(nameof(host));

    public int Count => _views.Count;

    public bool Create(string name, string markup)
    {
        if (!_host.HasUi || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(markup))
            return false;

        if (_views.Count > OfficialViewLimit)
            return false;

        try
        {
            XElement root = XDocument.Parse(markup).Root
                ?? throw new InvalidDataException("View markup has no root element.");
            if (!root.Name.LocalName.Equals("panel", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        catch (Exception error) when (error is InvalidDataException or System.Xml.XmlException)
        {
            _host.Log.Warn($"MossTank Meta view '{name}' is invalid: {error.Message}");
            return false;
        }

        Destroy(name);
        IDisposable token = _host.Ui.RegisterPanelContent(
            new PluginPanelDescriptor(WindowId(name), name)
            {
                IconText = Initials(name),
                StartVisible = true,
                ShowInSidePanel = true,
            },
            markup,
            MetaViewBinding.Instance);
        _views.Add(name, token);
        return true;
    }

    public bool Destroy(string name)
    {
        if (!_views.Remove(name, out IDisposable? registration))
            return false;
        registration.Dispose();
        return true;
    }

    public void DestroyAll()
    {
        IDisposable[] registrations = _views.Values.ToArray();
        _views.Clear();
        foreach (IDisposable registration in registrations)
            registration.Dispose();
    }

    private static string WindowId(string name)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        return "meta-" + Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    private static string Initials(string name)
    {
        string[] words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return "M";
        return string.Concat(words.Take(2).Select(static word => word[0])).ToUpperInvariant();
    }

    private sealed class MetaViewBinding
    {
        internal static MetaViewBinding Instance { get; } = new();
        public bool WindowAvailable => true;
    }
}
