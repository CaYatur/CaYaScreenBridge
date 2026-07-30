using System.Globalization;
using System.Runtime.CompilerServices;

namespace CaYaScreenBridge.Core.Geometry;

/// <summary>
/// An axis aligned rectangle in double precision. Represents either a pixel rectangle on the
/// Windows virtual desktop or a physical rectangle in millimetres, depending on the caller.
/// </summary>
public readonly struct RectD : IEquatable<RectD>
{
    public readonly double X;
    public readonly double Y;
    public readonly double Width;
    public readonly double Height;

    public RectD(double x, double y, double width, double height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public static RectD FromEdges(double left, double top, double right, double bottom) =>
        new(left, top, right - left, bottom - top);

    public static RectD Empty => new(0, 0, 0, 0);

    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public Vec2 TopLeft => new(X, Y);
    public Vec2 Size => new(Width, Height);
    public Vec2 Center => new(X + (Width / 2.0), Y + (Height / 2.0));
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public double Area => Width * Height;

    /// <summary>
    /// Half open containment (left/top inclusive, right/bottom exclusive) so that two rectangles
    /// sharing an edge never both claim the same point. Without this, a cursor sitting exactly on a
    /// shared border would flip between zones on every event.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(Vec2 p) => p.X >= X && p.X < Right && p.Y >= Y && p.Y < Bottom;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ContainsInclusive(Vec2 p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;

    public bool IntersectsWith(RectD other) =>
        other.Left < Right && other.Right > Left && other.Top < Bottom && other.Bottom > Top;

    public RectD Inflate(double amount) =>
        new(X - amount, Y - amount, Width + (2 * amount), Height + (2 * amount));

    public RectD Offset(Vec2 delta) => new(X + delta.X, Y + delta.Y, Width, Height);

    public RectD WithLocation(Vec2 location) => new(location.X, location.Y, Width, Height);

    public static RectD Union(RectD a, RectD b)
    {
        if (a.IsEmpty)
        {
            return b;
        }

        if (b.IsEmpty)
        {
            return a;
        }

        return FromEdges(
            Math.Min(a.Left, b.Left),
            Math.Min(a.Top, b.Top),
            Math.Max(a.Right, b.Right),
            Math.Max(a.Bottom, b.Bottom));
    }

    /// <summary>The point inside (or on) the rectangle that is closest to <paramref name="p"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vec2 ClosestPoint(Vec2 p) =>
        new(Math.Clamp(p.X, X, Right), Math.Clamp(p.Y, Y, Bottom));

    /// <summary>
    /// Squared distance from <paramref name="p"/> to the rectangle. Zero when the point is inside.
    /// Squared to keep the hot path free of <c>sqrt</c>; only used for ordering candidates.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double DistanceSquaredTo(Vec2 p)
    {
        double dx = Math.Max(Math.Max(X - p.X, 0), p.X - Right);
        double dy = Math.Max(Math.Max(Y - p.Y, 0), p.Y - Bottom);
        return (dx * dx) + (dy * dy);
    }

    /// <summary>
    /// Clamps a point to lie strictly inside the rectangle, staying <paramref name="margin"/> away
    /// from the right and bottom edges. Those edges are exclusive, so a point landing exactly on
    /// them would be reported as outside by <see cref="Contains"/>.
    /// </summary>
    public Vec2 ClampInside(Vec2 p, double margin)
    {
        double maxX = Math.Max(X, Right - margin);
        double maxY = Math.Max(Y, Bottom - margin);
        return new Vec2(Math.Clamp(p.X, X, maxX), Math.Clamp(p.Y, Y, maxY));
    }

    public bool Equals(RectD other) =>
        X.Equals(other.X) && Y.Equals(other.Y) && Width.Equals(other.Width) && Height.Equals(other.Height);

    public override bool Equals(object? obj) => obj is RectD other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"[{X:0.##},{Y:0.##} {Width:0.##}x{Height:0.##}]");
}
