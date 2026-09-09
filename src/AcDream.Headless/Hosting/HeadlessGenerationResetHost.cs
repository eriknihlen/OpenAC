using AcDream.Runtime;
using AcDream.Runtime.Entities;

namespace AcDream.Headless.Hosting;

internal sealed class HeadlessGenerationResetHost
    : IRuntimeGenerationResetHost
{
    public void RetireEntityProjection(RuntimeEntityRecord entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
    }

    public void DrainEntityProjectionBoundary()
    {
    }

    public void CompleteEntityProjectionRetirement()
    {
    }
}
