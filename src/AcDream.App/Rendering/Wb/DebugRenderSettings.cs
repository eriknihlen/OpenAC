using System.Numerics;

namespace AcDream.App.Rendering.Wb {
    public class DebugRenderSettings {
        public bool ShowBoundingBoxes { get; set; } = false;
        public bool SelectVertices { get; set; } = true;
        public bool SelectBuildings { get; set; } = true;
        public bool SelectStaticObjects { get; set; } = true;
        public bool SelectScenery { get; set; } = false;
        public bool SelectEnvCells { get; set; } = true;
        public bool SelectEnvCellStaticObjects { get; set; } = true;
        public bool SelectPortals { get; set; } = true;
        public bool ShowDisqualifiedScenery { get; set; } = true;
        public bool EnableAnisotropicFiltering { get; set; } = true;

        public Vector4 VertexColor { get; set; } = new Vector4(0.7882353f, 0.34901962f, 0.2901961f, 1.0f);
        public Vector4 BuildingColor { get; set; } = new Vector4(0.76862746f, 0.5803922f, 0.25882354f, 1.0f);
        public Vector4 StaticObjectColor { get; set; } = new Vector4(0.37254903f, 0.88235295f, 0.9019608f, 1.0f);
        public Vector4 SceneryColor { get; set; } = new Vector4(0.45490196f, 0.72156864f, 0.32156864f, 1.0f);
        public Vector4 EnvCellColor { get; set; } = new Vector4(0.5294118f, 0.44705883f, 0.7882353f, 1.0f);
        public Vector4 EnvCellStaticObjectColor { get; set; } = new Vector4(0f, 0.49803922f, 1f, 1.0f);
        public Vector4 PortalColor { get; set; } = new Vector4(1f, 0f, 1f, 1.0f);
    }
}
