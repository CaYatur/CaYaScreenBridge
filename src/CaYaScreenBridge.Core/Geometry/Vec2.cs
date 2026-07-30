using System.Globalization;
using System.Runtime.CompilerServices;

namespace CaYaScreenBridge.Core.Geometry;

/// <summary>
/// A double precision 2D vector. Used for both pixel space and physical (millimetre) space.
/// Deliberately a readonly struct with aggressive inlining: instances of this type are created
/// several hundred times per second inside the low level mouse hook callback, where an
/// allocation would mean a GC pause on the input thread.
/// </summary>
public readonly struct Vec2 : IEquatable<Vec2>
{
    public readonly double X;
    public readonly double Y;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vec2(double x, double y)
    {
        X = x;
        Y = y;
    }

    public static Vec2 Zero => new(0, 0);

    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);

    public double Length
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Math.Sqrt((X * X) + (Y * Y));
    }

    public double LengthSquared
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (X * X) + (Y * Y);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec2 operator *(Vec2 a, double s) => new(a.X * s, a.Y * s);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec2 operator *(double s, Vec2 a) => new(a.X * s, a.Y * s);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec2 operator /(Vec2 a, double s) => new(a.X / s, a.Y / s);

    /// <summary>Component wise multiply. Used to apply a per axis pixels-per-millimetre scale.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vec2 Scale(Vec2 factor) => new(X * factor.X, Y * factor.Y);

    /// <summary>Component wise divide. Used to convert pixels into millimetres.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vec2 Unscale(Vec2 factor) => new(X / factor.X, Y / factor.Y);

    public Vec2 Normalized()
    {
        double len = Length;
        return len <= double.Epsilon ? Zero : new Vec2(X / len, Y / len);
    }

    public static double Distance(Vec2 a, Vec2 b) => (a - b).Length;

    public bool Equals(Vec2 other) => X.Equals(other.X) && Y.Equals(other.Y);

    public override bool Equals(object? obj) => obj is Vec2 other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"({X:0.###}, {Y:0.###})");
}
