using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

public sealed class MossTankPlugin : IAcDreamPlugin
{
    private IPluginHost? _host;
    private MossTankPanel? _panel;
    private Action<double>? _tick;
    private IDisposable? _commandRegistration;

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _panel = new MossTankPanel(host);
        host.Log.Info("MossTank initialized");
    }

    public void Enable()
    {
        if (_host is null || _panel is null)
            return;

        // Markup ships beside the plugin assembly, so it is found relative to
        // this DLL rather than the host's working directory -- plugins are
        // loaded from their own directory and the two are not the same.
        string directory =
            Path.GetDirectoryName(typeof(MossTankPlugin).Assembly.Location) ?? ".";

        _host.Ui.AddPanel(
            new PluginPanelDescriptor("main", "MossTank")
            {
                IconText = "MT",
                // A real installed portal.dat RenderSurface — see
                // MossTankIconInstalledDatTests for the DAT-presence proof.
                // IconText remains the fallback if this id is ever absent
                // from an install.
                IconSurfaceId = 0x06002C41u,
                StartVisible = true,
                ShowInSidePanel = true,
            },
            Path.Combine(directory, "mosstank.xml"),
            _panel);

        _host.Ui.AddPanel(
            new PluginPanelDescriptor("advanced-options", "MossTank Advanced Options")
            {
                StartVisible = true,
                ShowInSidePanel = false,
            },
            Path.Combine(directory, "mosstank-advanced.xml"),
            _panel);
        _host.Ui.AddPanel(
            new PluginPanelDescriptor("loot-editor", "MossTank Loot Editor")
            {
                StartVisible = true,
                ShowInSidePanel = false,
            },
            Path.Combine(directory, "mosstank-loot-editor.xml"),
            _panel);
        _host.Ui.AddPanel(
            new PluginPanelDescriptor("buff-picker", "MossTank Add Buff")
            {
                StartVisible = true,
                ShowInSidePanel = false,
            },
            Path.Combine(directory, "mosstank-buffpicker.xml"),
            _panel);
        _host.Ui.AddPanel(
            new PluginPanelDescriptor("meta-editor", "MossTank Meta Rule Editor")
            {
                StartVisible = true,
                ShowInSidePanel = false,
            },
            Path.Combine(directory, "mosstank-metaeditor.xml"),
            _panel);

        _commandRegistration = _host.Commands.Register(
            "vt",
            _panel.ExecuteVtankCommand);

        _tick = _panel.OnTick;
        _host.Events.Tick += _tick;

        _host.Log.Info(
            _host.Automation.IsAvailable
                ? "MossTank enabled"
                : "MossTank enabled (no live session yet; the Buff button will "
                  + "report 'Not in world' until one is up)");
    }

    public void Disable()
    {
        if (_host is not null && _tick is not null)
            _host.Events.Tick -= _tick;
        _commandRegistration?.Dispose();
        _commandRegistration = null;
        _tick = null;
        _panel?.Disable();
        _host?.Log.Info("MossTank disabled");
    }
}
