using CaYaScreenBridge.Core.Geometry;

namespace CaYaScreenBridge.Core.Algorithm;

/// <summary>
/// Learns how many desktop pixels one unit of raw HID movement produces.
///
/// This exists because of a specific Windows behaviour: once the cursor is pinned against the outer
/// edge of the desktop, the low level hook keeps firing but the reported position stops changing, so
/// the intended movement is invisible. Raw input still reports the true device delta, but that delta
/// is in device units and pointer acceleration sits between it and the on screen movement. Rather
/// than trying to reimplement the acceleration curve, the calibrator observes the ratio while the
/// cursor is moving freely, and applies the learned ratio only for the axis that is currently
/// blocked.
///
/// The ratio is tracked in three speed buckets because pointer acceleration makes it velocity
/// dependent; a single average would under predict fast flicks and over predict slow movements.
/// </summary>
public sealed class RawDeltaCalibrator
{
    private const double Alpha = 0.08;
    private const double MinRatio = 0.02;
    private const double MaxRatio = 40.0;

    private readonly AxisCalibrator _x = new();
    private readonly AxisCalibrator _y = new();

    public void Reset()
    {
        _x.Reset();
        _y.Reset();
    }

    /// <summary>Feeds an unobstructed movement so the calibrator can learn from it.</summary>
    public void Observe(Vec2 rawDelta, Vec2 pixelDelta)
    {
        _x.Observe(rawDelta.X, pixelDelta.X);
        _y.Observe(rawDelta.Y, pixelDelta.Y);
    }

    /// <summary>Estimates the pixel movement the user intended for a raw device delta.</summary>
    public Vec2 Predict(Vec2 rawDelta) => new(_x.Predict(rawDelta.X), _y.Predict(rawDelta.Y));

    internal double GainX => _x.CurrentGain;

    internal double GainY => _y.CurrentGain;

    private sealed class AxisCalibrator
    {
        // Bucket boundaries in raw device units per event.
        private const double SlowLimit = 4.0;
        private const double FastLimit = 20.0;

        private readonly double[] _gain = { 1.0, 1.0, 1.0 };
        private readonly int[] _samples = new int[3];

        public double CurrentGain => _gain[1];

        public void Reset()
        {
            for (int i = 0; i < 3; i++)
            {
                _gain[i] = 1.0;
                _samples[i] = 0;
            }
        }

        public void Observe(double rawDelta, double pixelDelta)
        {
            double magnitude = Math.Abs(rawDelta);
            if (magnitude < 1.0)
            {
                return;
            }

            double ratio = pixelDelta / rawDelta;
            if (!double.IsFinite(ratio) || ratio < MinRatio || ratio > MaxRatio)
            {
                return;
            }

            int bucket = BucketFor(magnitude);

            // Converge quickly from the neutral seed, then settle into a slow moving average so a
            // single odd event cannot skew the estimate.
            double alpha = _samples[bucket] < 10 ? 0.35 : Alpha;
            _gain[bucket] = (_gain[bucket] * (1 - alpha)) + (ratio * alpha);
            _samples[bucket] = Math.Min(_samples[bucket] + 1, 1000);
        }

        public double Predict(double rawDelta)
        {
            double magnitude = Math.Abs(rawDelta);
            if (magnitude < double.Epsilon)
            {
                return 0;
            }

            return rawDelta * _gain[BucketFor(magnitude)];
        }

        private static int BucketFor(double magnitude) =>
            magnitude < SlowLimit ? 0 : magnitude < FastLimit ? 1 : 2;
    }
}
