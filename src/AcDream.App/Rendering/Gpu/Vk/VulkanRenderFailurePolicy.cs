using Silk.NET.Vulkan;

namespace AcDream.App.Rendering.Gpu.Vk;

internal static class VulkanRenderFailurePolicy
{
    internal static bool IsFatal(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (error is AggregateException aggregate)
        {
            foreach (Exception inner in aggregate.Flatten().InnerExceptions)
            {
                if (IsFatal(inner))
                    return true;
            }
        }

        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is OutOfMemoryException)
                return true;
            if (current is VulkanCallException vulkan && IsFatal(vulkan.Result))
                return true;
        }
        return false;
    }

    private static bool IsFatal(Result result) => result is
        Result.ErrorDeviceLost
        or Result.ErrorOutOfHostMemory
        or Result.ErrorOutOfDeviceMemory
        or Result.ErrorSurfaceLostKhr;
}
