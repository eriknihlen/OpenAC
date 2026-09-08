using System.Reflection;
using AcDream.App.Rendering;
using AcDream.App.Streaming;
using AcDream.App.Tests.Architecture;
using AcDream.App.UI;
using AcDream.Content;
using AcDream.Content.Vfx;
using AcDream.Core.World;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Options;

namespace AcDream.App.Tests.Rendering;

public sealed class PortalTunnelAssetTests
{
    [Fact]
    public void PortalWorldHandoff_HidesTunnelBeforePublishingWorldFadeInProjection()
    {
        MethodInfo tick = typeof(LocalPlayerTeleportPresentation).GetMethod(
            nameof(LocalPlayerTeleportPresentation.Tick),
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException("Presentation Tick method missing.");
        IReadOnlyList<CompiledCall> calls = CompiledCallGraph.Read(tick);
        int hide = CompiledCallGraph.IndexOf(
            calls,
            typeof(PortalTunnelPresentation),
            nameof(PortalTunnelPresentation.Exit));
        int publish = CompiledCallGraph.IndexOf(
            calls,
            typeof(TeleportViewPlaneController),
            nameof(TeleportViewPlaneController.Update));

        Assert.True(hide >= 0, "Presentation Tick no longer retires portal space.");
        Assert.True(publish >= 0, "Presentation Tick no longer publishes its view plane.");
        Assert.True(
            hide < publish,
            "Portal space must retire before the WorldFadeIn projection is visible to rendering.");
    }

    [Fact]
    public void PortalSpace_RhiPassReclearsTheSameOpaqueBlackTheFrameEstablished()
    {
        Assert.Equal(
            new System.Numerics.Vector4(0f, 0f, 0f, 1f),
            PortalTunnelPresentation.RetailPortalSpaceClearColor);
    }

    [Fact]
    public void PortalSpace_RequiresAPassScope()
    {
        var error = Assert.Throws<ArgumentNullException>(() =>
            PortalTunnelPresentation.CreateRequired(
                scope: null!,
                frames: null!,
                dats: null!,
                animationLoader: null!,
                hookSink: null!,
                dispatcher: null!,
                lightUbo: null!,
                meshAdapter: null!));

        Assert.Equal("scope", error.ParamName);
    }

    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void InstalledDat_ResolvesRetailPortalSetupAndAnimation()
    {
        string? datDir = ResolveDatDir();
        if (datDir is null)
            throw new InvalidOperationException(
                "Lane=InstalledDat requires client_portal.dat for the portal-space asset gate; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        uint setupDid = RetailDataIdResolver.Resolve(
            dats,
            PortalTunnelPresentation.SetupClientEnum,
            PortalTunnelPresentation.ClientEnumCategory);
        uint animationDid = RetailDataIdResolver.Resolve(
            dats,
            PortalTunnelPresentation.AnimationClientEnum,
            PortalTunnelPresentation.ClientEnumCategory);

        Assert.NotEqual(0u, setupDid);
        Assert.NotEqual(0u, animationDid);
        Assert.Equal(0x02000306u, setupDid);
        Assert.Equal(0x030005ACu, animationDid);
        Setup setup = Assert.IsType<Setup>(dats.Get<Setup>(setupDid));
        Animation animation = Assert.IsType<Animation>(
            new RetailAnimationLoader(dats).LoadAnimation(animationDid));

        Assert.NotEmpty(setup.Parts);
        Assert.True(
            animation.PartFrames.Count >= TeleportAnimSequencer.TunnelEndFrame,
            $"Retail tunnel timing samples frame {TeleportAnimSequencer.TunnelEndFrame}, "
            + $"but animation 0x{animationDid:X8} has only {animation.PartFrames.Count} frames.");
        Assert.All(
            setup.Parts,
            part => Assert.Equal(0x01u, ((uint)part >> 24) & 0xFFu));
        Assert.Equal(0u, (uint)setup.DefaultAnimation);
        Assert.Equal(0u, (uint)setup.DefaultScript);
        Assert.Equal(0u, (uint)setup.DefaultScriptTable);
        var hook = Assert.Single(animation.PartFrames.SelectMany(frame => frame.Hooks));
        Assert.Equal(DatReaderWriter.Enums.AnimationHookType.SoundTweaked, hook.HookType);
    }

    [Fact]
    public void RequiredAssets_MissingSetupOrAnimationFailsWithActionableIds()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            PortalTunnelPresentation.EnsureRequiredAssets(
                setupDid: 0u,
                setupLoaded: false,
                animationDid: 0x030005ACu,
                animationLoaded: true));

        Assert.Contains("required retail DAT assets unavailable", error.Message);
        Assert.Contains("setup=0x00000000 (missing)", error.Message);
        Assert.Contains("animation=0x030005AC (ok)", error.Message);
    }

    [Fact]
    public void PortalTunnelCamera_RollsAroundForwardAxisWithoutLeavingPassage()
    {
        var camera = new PortalTunnelCamera
        {
            DirectionDegrees = 0f,
            Aspect = 1f,
        };
        Assert.True(System.Numerics.Matrix4x4.Invert(camera.View, out var world));
        Assert.Equal(PortalTunnelCamera.RetailEye.X, world.Translation.X, precision: 5);
        Assert.Equal(PortalTunnelCamera.RetailEye.Y, world.Translation.Y, precision: 5);
        Assert.Equal(PortalTunnelCamera.RetailEye.Z, world.Translation.Z, precision: 5);

        camera.DirectionDegrees = 90f;
        Assert.True(System.Numerics.Matrix4x4.Invert(camera.View, out var rotatedWorld));
        System.Numerics.Vector3 forward = System.Numerics.Vector3.Normalize(
            System.Numerics.Vector3.TransformNormal(-System.Numerics.Vector3.UnitZ, rotatedWorld));
        System.Numerics.Vector3 up = System.Numerics.Vector3.Normalize(
            System.Numerics.Vector3.TransformNormal(System.Numerics.Vector3.UnitY, rotatedWorld));

        Assert.Equal(0f, forward.X, precision: 5);
        Assert.Equal(1f, forward.Y, precision: 5);
        Assert.Equal(0f, forward.Z, precision: 5);
        Assert.Equal(1f, up.X, precision: 5);
        Assert.Equal(0f, up.Y, precision: 5);
        Assert.Equal(0f, up.Z, precision: 5);
    }

    [Fact]
    public void TeleportViewPlane_FadeOutPortsRetailDistanceFovAndNearPlane()
    {
        var baseProjection = System.Numerics.Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 3f,
            16f / 9f,
            0.1f,
            5000f);
        var controller = new TeleportViewPlaneController();
        controller.Begin(baseProjection);

        controller.Update(new TeleportAnimSnapshot(
            TeleportAnimState.TunnelFadeOut,
            ViewPlaneBlend: 0.5f,
            ShowTunnel: true,
            ShowPleaseWait: false));
        System.Numerics.Matrix4x4 projection = controller.Apply(baseProjection);

        float gameDistance = baseProjection.M22;
        float expectedDistance = gameDistance
            + (TeleportViewPlaneController.TransitionViewPlaneDistance - gameDistance) * 0.5f;
        float expectedNear = MathF.Max(0.1f, expectedDistance * 0.25f);
        float actualNear = projection.M43 / projection.M33;

        Assert.True(controller.Enabled);
        Assert.Equal(expectedDistance, controller.CurrentViewPlaneDistance, precision: 5);
        Assert.Equal(expectedDistance, projection.M22, precision: 5);
        Assert.Equal(expectedNear, actualNear, precision: 5);
    }

    [Fact]
    public void TeleportViewPlane_WorldFadeInStartsAtRetailWideProjectionAndRestoresGameProjection()
    {
        var baseProjection = System.Numerics.Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 3f,
            16f / 9f,
            0.1f,
            5000f);
        var controller = new TeleportViewPlaneController();
        controller.Begin(baseProjection);

        controller.Update(new TeleportAnimSnapshot(
            TeleportAnimState.WorldFadeIn,
            ViewPlaneBlend: 1f,
            ShowTunnel: false,
            ShowPleaseWait: false));
        System.Numerics.Matrix4x4 wideProjection = controller.Apply(baseProjection);

        Assert.Equal(
            TeleportViewPlaneController.TransitionViewPlaneDistance,
            wideProjection.M22,
            precision: 5);
        Assert.Equal(0.00025f, wideProjection.M43 / wideProjection.M33, precision: 7);

        controller.Update(new TeleportAnimSnapshot(
            TeleportAnimState.Off,
            ViewPlaneBlend: 0f,
            ShowTunnel: false,
            ShowPleaseWait: false));

        Assert.False(controller.Enabled);
        Assert.Equal(baseProjection, controller.Apply(baseProjection));
    }

    [Fact]
    public void TeleportViewPlane_TunnelKeepsRetailNearPlaneWhileWorldUsesCoverageNearPlane()
    {
        var baseProjection = System.Numerics.Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 3f,
            16f / 9f,
            0.1f,
            5000f);
        var controller = new TeleportViewPlaneController();
        controller.Begin(baseProjection);

        controller.Update(new TeleportAnimSnapshot(
            TeleportAnimState.TunnelFadeOut,
            ViewPlaneBlend: 1f,
            ShowTunnel: true,
            ShowPleaseWait: false));
        System.Numerics.Matrix4x4 tunnelProjection = controller.Apply(baseProjection);

        controller.Update(new TeleportAnimSnapshot(
            TeleportAnimState.WorldFadeIn,
            ViewPlaneBlend: 1f,
            ShowTunnel: false,
            ShowPleaseWait: false));
        System.Numerics.Matrix4x4 worldProjection = controller.Apply(baseProjection);

        Assert.Equal(0.1f, tunnelProjection.M43 / tunnelProjection.M33, precision: 5);
        Assert.Equal(0.00025f, worldProjection.M43 / worldProjection.M33, precision: 7);
        Assert.Equal(tunnelProjection.M11, worldProjection.M11);
        Assert.Equal(tunnelProjection.M22, worldProjection.M22);
    }

    [Fact]
    public void TeleportViewPlane_ApplyToSuppliesOverrideToEveryCameraConsumer()
    {
        var source = new PortalTunnelCamera
        {
            Aspect = 16f / 9f,
            FovRadians = MathF.PI / 3f,
            Near = 0.1f,
            Far = 5000f,
        };
        var controller = new TeleportViewPlaneController();
        controller.Begin(source.Projection);
        controller.Update(new TeleportAnimSnapshot(
            TeleportAnimState.WorldFadeIn,
            ViewPlaneBlend: 1f,
            ShowTunnel: false,
            ShowPleaseWait: false));

        ICamera renderCamera = controller.ApplyTo(source);

        Assert.Equal(source.View, renderCamera.View);
        Assert.Equal(
            TeleportViewPlaneController.TransitionViewPlaneDistance,
            renderCamera.Projection.M22,
            precision: 5);
        renderCamera.Aspect = 4f / 3f;
        Assert.Equal(4f / 3f, source.Aspect, precision: 5);
    }

    [Fact]
    public void TeleportViewPlane_TunnelPreservesLogoutOverrideUntilTunnelContinue()
    {
        var capturedProjection = System.Numerics.Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 3f,
            16f / 9f,
            0.1f,
            5000f);
        var changedProjection = System.Numerics.Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 2f,
            16f / 9f,
            0.1f,
            5000f);
        var controller = new TeleportViewPlaneController();
        controller.Begin(capturedProjection);

        controller.Update(new TeleportAnimSnapshot(
            TeleportAnimState.WorldFadeOut,
            ViewPlaneBlend: 1f,
            ShowTunnel: false,
            ShowPleaseWait: false));
        controller.Update(new TeleportAnimSnapshot(
            TeleportAnimState.TunnelFadeIn,
            ViewPlaneBlend: 0f,
            ShowTunnel: true,
            ShowPleaseWait: false));
        controller.Update(new TeleportAnimSnapshot(
            TeleportAnimState.Tunnel,
            ViewPlaneBlend: 0f,
            ShowTunnel: true,
            ShowPleaseWait: false));

        Assert.True(controller.Enabled);
        Assert.Equal(capturedProjection.M22, controller.Apply(changedProjection).M22, precision: 5);

        controller.Update(new TeleportAnimSnapshot(
            TeleportAnimState.TunnelContinue,
            ViewPlaneBlend: 0f,
            ShowTunnel: true,
            ShowPleaseWait: false));

        Assert.False(controller.Enabled);
        Assert.Equal(changedProjection, controller.Apply(changedProjection));
    }

    [Fact]
    public void TeleportViewPlane_NormalPortalTunnelDoesNotEnableOverride()
    {
        var baseProjection = System.Numerics.Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 3f,
            16f / 9f,
            0.1f,
            5000f);
        var controller = new TeleportViewPlaneController();
        controller.Begin(baseProjection);

        controller.Update(new TeleportAnimSnapshot(
            TeleportAnimState.Tunnel,
            ViewPlaneBlend: 0f,
            ShowTunnel: true,
            ShowPleaseWait: false));

        Assert.False(controller.Enabled);
        Assert.Equal(baseProjection, controller.Apply(baseProjection));
    }

    [Theory]
    [InlineData(45f)]
    [InlineData(60f)]
    [InlineData(90f)]
    public void PortalTunnelCamera_UsesActiveSmartBoxProjectionClipRange(float degrees)
    {
        float expected = degrees * (MathF.PI / 180f);
        var smartBoxProjection = System.Numerics.Matrix4x4.CreatePerspectiveFieldOfView(
            expected,
            16f / 9f,
            0.35f,
            5000f);
        var camera = new PortalTunnelCamera();

        camera.UseSmartBoxFov(smartBoxProjection);

        Assert.Equal(expected, camera.FovRadians, precision: 5);
        Assert.Equal(0.35f, camera.Near, precision: 5);
        Assert.InRange(camera.Far, 4995f, 5005f);
    }

    private static string? ResolveDatDir()
    {
        string? configured = System.Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR");
        if (!string.IsNullOrWhiteSpace(configured)
            && File.Exists(Path.Combine(configured, "client_portal.dat")))
            return configured;

        const string installed = @"C:\Turbine\Asheron's Call";
        if (File.Exists(Path.Combine(installed, "client_portal.dat")))
            return installed;

        string conventional = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            "Documents",
            "Asheron's Call");
        return File.Exists(Path.Combine(conventional, "client_portal.dat"))
            ? conventional
            : null;
    }
}
