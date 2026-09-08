using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank.Expressions;

/// <summary>VTank Meta status HUD backed by one shelf-managed plugin window.</summary>
internal sealed class StatusHudManager(IPluginHost host)
{
    private const uint DefaultColor = 0xE8DEC3u;
    private const string Markup = """
        <panel x="520" y="42" w="340" h="220" title="VTank Meta Status"
               visible="{WindowAvailable}" resize="none">
          <label x="8" y="25" text="VTank Meta" color="#FFE8DEC3" />
          <list x="8" y="45" w="324" h="166" rowheight="18"
                items="{Rows}" colors="{RowColors}" selected="{SelectedRow}" />
        </panel>
        """;

    private readonly Dictionary<string, StatusEntry> _entries =
        new(StringComparer.Ordinal);
    private readonly StatusBinding _binding = new();
    private IDisposable? _registration;

    public int Count => _entries.Count;
    internal IReadOnlyList<string> Rows => _binding.Rows;
    internal IReadOnlyList<uint> RowColors => _binding.RowColors;

    public bool Update(string key, string value, uint? color = null)
    {
        if (string.IsNullOrEmpty(key))
            return false;
        _entries[key] = new StatusEntry(value ?? string.Empty, color ?? DefaultColor);
        _binding.Rows = _entries.Select(static pair =>
            $"{pair.Key}: {pair.Value.Value}").ToArray();
        _binding.RowColors = _entries.Select(static pair => pair.Value.Color).ToArray();
        if (_registration is null && host.HasUi)
        {
            _registration = host.Ui.RegisterPanelContent(
                new PluginPanelDescriptor("vtank-meta-status", "VTank Meta Status")
                {
                    IconText = "S",
                    StartVisible = true,
                    ShowInSidePanel = true,
                },
                Markup,
                _binding);
        }
        return true;
    }

    public void Destroy()
    {
        _registration?.Dispose();
        _registration = null;
        _entries.Clear();
        _binding.Rows = [];
        _binding.RowColors = [];
    }

    private readonly record struct StatusEntry(string Value, uint Color);

    private sealed class StatusBinding
    {
        public bool WindowAvailable => true;
        public IReadOnlyList<string> Rows { get; internal set; } = [];
        public IReadOnlyList<uint> RowColors { get; internal set; } = [];
        public int SelectedRow => -1;
    }
}
