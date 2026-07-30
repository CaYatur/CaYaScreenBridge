namespace CaYaScreenBridge.Core.Geometry;

/// <summary>
/// Segment/rectangle intersection used by the transition solver to answer "which screen does this
/// movement actually pass through", rather than only looking at where the movement ended up.
/// </summary>
public static class RayCast
{
    /// <summary>
    /// Slab based segment/rect intersection.
    /// </summary>
    /// <param name="origin">Segment start.</param>
    /// <param name="delta">Segment vector (origin + delta is the end point).</param>
    /// <param name="rect">Rectangle to test against.</param>
    /// <param name="tEnter">Parametric entry point in [0,1] when the segment reaches the rect.</param>
    /// <param name="tExit">Parametric exit point in [0,1].</param>
    /// <returns>True when the segment overlaps the rectangle at all.</returns>
    public static bool SegmentIntersectsRect(Vec2 origin, Vec2 delta, RectD rect, out double tEnter, out double tExit)
    {
        double t0 = 0.0;
        double t1 = 1.0;

        if (!ClipAxis(origin.X, delta.X, rect.Left, rect.Right, ref t0, ref t1) ||
            !ClipAxis(origin.Y, delta.Y, rect.Top, rect.Bottom, ref t0, ref t1))
        {
            tEnter = 0;
            tExit = 0;
            return false;
        }

        tEnter = t0;
        tExit = t1;
        return true;
    }

    private static bool ClipAxis(double origin, double delta, double min, double max, ref double t0, ref double t1)
    {
        const double Epsilon = 1e-9;

        if (Math.Abs(delta) < Epsilon)
        {
            // Movement is parallel to this pair of edges: either we are already within the slab for
            // the whole segment, or we never touch the rectangle at all.
            return origin >= min && origin <= max;
        }

        double inv = 1.0 / delta;
        double near = (min - origin) * inv;
        double far = (max - origin) * inv;

        if (near > far)
        {
            (near, far) = (far, near);
        }

        if (near > t0)
        {
            t0 = near;
        }

        if (far < t1)
        {
            t1 = far;
        }

        return t0 <= t1;
    }
}
