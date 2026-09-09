namespace AcDream.App.Rendering;

internal static class QuadClipper
{
    public static bool TryClip(
        float clipLeft,
        float clipTop,
        float clipRight,
        float clipBottom,
        ref float x,
        ref float y,
        ref float w,
        ref float h,
        ref float u0,
        ref float v0,
        ref float u1,
        ref float v1)
    {
        if (clipRight <= clipLeft || clipBottom <= clipTop || w <= 0f || h <= 0f)
            return false;

        float left = MathF.Max(x, clipLeft);
        float top = MathF.Max(y, clipTop);
        float right = MathF.Min(x + w, clipRight);
        float bottom = MathF.Min(y + h, clipBottom);
        if (right <= left || bottom <= top)
            return false;

        float oldX = x, oldY = y, oldW = w, oldH = h;
        float oldU0 = u0, oldV0 = v0, oldU1 = u1, oldV1 = v1;
        float tx0 = (left - oldX) / oldW;
        float tx1 = (right - oldX) / oldW;
        float ty0 = (top - oldY) / oldH;
        float ty1 = (bottom - oldY) / oldH;

        x = left;
        y = top;
        w = right - left;
        h = bottom - top;
        u0 = oldU0 + (oldU1 - oldU0) * tx0;
        u1 = oldU0 + (oldU1 - oldU0) * tx1;
        v0 = oldV0 + (oldV1 - oldV0) * ty0;
        v1 = oldV0 + (oldV1 - oldV0) * ty1;
        return true;
    }
}
