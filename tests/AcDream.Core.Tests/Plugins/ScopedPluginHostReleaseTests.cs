using AcDream.Core.Plugins;
using AcDream.Core.Selection;
using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Tests.Plugins;

/// <summary>
/// Everything a plugin reaches through its host comes off with the plugin:
/// the handlers it added to the trade, vendor and equipment surfaces, and the
/// maps, HUDs and textures it was handed. A handler left on a host surface
/// keeps an unloaded plugin's code in memory and calls into a plugin that is
/// switched off.
/// </summary>
public sealed class ScopedPluginHostReleaseTests
{
    /// <summary>
    /// Mutation (2026-09-26): handing the plugin the host's own surfaces, as
    /// before, left every handler attached after the plugin was released.
    /// </summary>
    [Fact]
    public void TradeVendorAndEquipmentHandlersComeOffWithThePlugin()
    {
        var surfaces = new EventSurfaces();
        var scope = new ScopedPluginHost(new StubHost(surfaces), "acdream.events", "Events");
        IAutomationSurface automation = scope.Automation;

        automation.Trade.Opened += _ => { };
        automation.Trade.Closed += () => { };
        automation.Trade.PartnerTradeAccepted += _ => { };
        automation.Trade.ItemAdded += _ => { };
        automation.Vendor.Opened += _ => { };
        automation.Vendor.Closed += () => { };
        automation.Vendor.TransactionCompleted += _ => { };
        automation.Equipment.PlacementObserved += _ => { };
        Assert.Equal(8, surfaces.HandlerCount);

        scope.Dispose();

        Assert.Equal(0, surfaces.HandlerCount);
    }

    [Fact]
    public void AHandlerThePluginRemovesItselfComesOffOnce()
    {
        var surfaces = new EventSurfaces();
        using var scope = new ScopedPluginHost(new StubHost(surfaces), "acdream.events", "Events");
        Action closed = () => { };

        scope.Automation.Vendor.Closed += closed;
        scope.Automation.Vendor.Closed -= closed;

        Assert.Equal(0, surfaces.HandlerCount);
    }

    [Fact]
    public void TheSurfacesStillAnswerFromTheHost()
    {
        var surfaces = new EventSurfaces();
        using var scope = new ScopedPluginHost(new StubHost(surfaces), "acdream.events", "Events");

        Assert.True(scope.Automation.Trade.IsOpen);
        Assert.Equal("Vendor Bob", scope.Automation.Vendor.VendorName);
        Assert.True(scope.Automation.Equipment.IsBusy);
    }

    /// <summary>
    /// Mutation (2026-09-26): forwarding Maps and Rendering unchanged left
    /// the plugin's map, HUD and texture registered after it was released.
    /// </summary>
    [Fact]
    public void MapsHudsAndTexturesAreLetGoWithThePlugin()
    {
        var surfaces = new EventSurfaces();
        var scope = new ScopedPluginHost(new StubHost(surfaces), "acdream.events", "Events");

        _ = scope.Maps.AddMap("world", default);
        _ = scope.Rendering.AddHud(new PluginHudDescriptor("hud", "HUD", default));
        _ = scope.Rendering.LoadTexture("icon.png");
        Assert.Equal(3, surfaces.Registrations.Count(static registration => !registration.IsDisposed));

        scope.Dispose();

        Assert.All(surfaces.Registrations, static registration => Assert.True(registration.IsDisposed));
    }

    private sealed class StubHost(EventSurfaces surfaces) : IPluginHost
    {
        public bool HasUi => false;
        public IPluginLogger Log { get; } = new StubLogger();
        public IGameState State { get; } = new WorldGameState();
        public IEvents Events { get; } = new WorldEvents();
        public ISelectionService Selection { get; } = new SelectionState();
        public IUiRegistry Ui => NoOpUiRegistry.Instance;
        public IAutomationSurface Automation => surfaces;
        public IPluginMapRegistry Maps => surfaces;
        public IPluginRenderRegistry Rendering => surfaces;
    }

    private sealed class StubLogger : IPluginLogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class EventSurfaces :
        IAutomationSurface, ITradeAutomation, IVendorAutomation, IEquipmentAutomation,
        IPluginMapRegistry, IPluginRenderRegistry
    {
        private readonly List<Delegate> _handlers = [];

        internal int HandlerCount => _handlers.Count;
        internal List<Registration> Registrations { get; } = [];

        public bool IsAvailable => true;
        public ICharacterInfo Character => NoOpAutomationSurface.Instance;
        public ISpellCatalog Spells => NoOpAutomationSurface.Instance;
        public IMagicCommands Magic => NoOpAutomationSurface.Instance;
        public IPluginChat Chat => NoOpAutomationSurface.Instance;
        public ITradeAutomation Trade => this;
        public IVendorAutomation Vendor => this;
        public IEquipmentAutomation Equipment => this;

        bool ITradeAutomation.IsOpen => true;
        string IVendorAutomation.VendorName => "Vendor Bob";
        bool IEquipmentAutomation.IsBusy => true;

        event Action<PluginTradeOpened> ITradeAutomation.Opened { add => _handlers.Add(value); remove => _handlers.Remove(value); }
        event Action ITradeAutomation.Closed { add => _handlers.Add(value); remove => _handlers.Remove(value); }
        event Action<uint> ITradeAutomation.PartnerTradeAccepted { add => _handlers.Add(value); remove => _handlers.Remove(value); }
        event Action<PluginTradeItemAdded> ITradeAutomation.ItemAdded { add => _handlers.Add(value); remove => _handlers.Remove(value); }
        event Action<uint> IVendorAutomation.Opened { add => _handlers.Add(value); remove => _handlers.Remove(value); }
        event Action IVendorAutomation.Closed { add => _handlers.Add(value); remove => _handlers.Remove(value); }
        event Action<PluginVendorTransaction> IVendorAutomation.TransactionCompleted { add => _handlers.Add(value); remove => _handlers.Remove(value); }
        event Action<PluginEquipmentObservation> IEquipmentAutomation.PlacementObserved { add => _handlers.Add(value); remove => _handlers.Remove(value); }

        public IPluginMapSurface AddMap(string mapId, PluginMapViewport initialViewport) =>
            Add(new Registration());

        public IPluginHudRegistration AddHud(PluginHudDescriptor descriptor) =>
            Add(new Registration());

        public IPluginTexture? LoadTexture(string resourceId) =>
            Add(new Registration());

        private Registration Add(Registration registration)
        {
            Registrations.Add(registration);
            return registration;
        }
    }

    private sealed class Registration : IPluginMapSurface, IPluginHudRegistration, IPluginTexture
    {
        internal bool IsDisposed { get; private set; }

        public PluginMapViewport Viewport { get; set; }
        public PluginMapImage? Background => null;
        public IReadOnlyList<PluginMapMarker> Markers => [];
        public IReadOnlyList<PluginMapPoint> Route => [];
        public bool IsVisible { get; set; }
        public PluginHudBounds Bounds { get; set; }
        public IPluginRenderSurface Surface => throw new NotSupportedException();
        public string ResourceId => "icon.png";
        public int Width => 1;
        public int Height => 1;

        event Action<PluginMapInput>? IPluginMapSurface.Input { add { } remove { } }
        event Action<PluginHudInput>? IPluginHudRegistration.Input { add { } remove { } }

        public void SetBackground(PluginMapImage image) { }
        public void SetMarkers(IReadOnlyList<PluginMapMarker> markers) { }
        public void SetRoute(IReadOnlyList<PluginMapPoint> points) { }
        public void Dispose() => IsDisposed = true;
    }
}
