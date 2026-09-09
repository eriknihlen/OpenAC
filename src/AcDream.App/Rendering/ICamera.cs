using System.Numerics;

namespace AcDream.App.Rendering;

public interface ICamera
{
    Matrix4x4 View { get; }
    Matrix4x4 Projection { get; }
    float Aspect { get; set; }
}
