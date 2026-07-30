using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CaYaScreenBridge.Core.Geometry;

namespace CaYaScreenBridge.Windows.Ui.Controls;

/// <summary>
/// One display as the editor sees it. Positions and sizes are millimetres, matching the physical
/// layout the router works in, so what is dragged here is literally the model the cursor uses.
/// </summary>
public sealed class DisplayItem : ObservableObject
{
    private double _leftMm;
    private double _topMm;
    private double _widthMm;
    private double _heightMm;

    public required string StableId { get; init; }

    public required string Label { get; set; }

    public required double PixelWidth { get; init; }

    public required double PixelHeight { get; init; }

    public bool IsPrimary { get; init; }

    public double ScaleFactor { get; init; } = 1.0;

    /// <summary>True when the panel size came from EDID rather than from the user.</summary>
    public bool SizeFromEdid { get; set; } = true;

    public double EdidWidthMm { get; init; }

    public double EdidHeightMm { get; init; }

    public double LeftMm
    {
        get => _leftMm;
        set { if (Set(ref _leftMm, value)) { Raise(nameof(Summary)); } }
    }

    public double TopMm
    {
        get => _topMm;
        set { if (Set(ref _topMm, value)) { Raise(nameof(Summary)); } }
    }

    public double WidthMm
    {
        get => _widthMm;
        set { if (Set(ref _widthMm, Math.Max(20, value))) { RaiseDerived(); } }
    }

    public double HeightMm
    {
        get => _heightMm;
        set { if (Set(ref _heightMm, Math.Max(20, value))) { RaiseDerived(); } }
    }

    public RectD PhysicalRect => new(LeftMm, TopMm, WidthMm, HeightMm);

    public double DiagonalInches =>
        Math.Sqrt((WidthMm * WidthMm) + (HeightMm * HeightMm)) / 25.4;

    /// <summary>True pixel density of the panel, which is what the alignment maths turns on.</summary>
    public double PhysicalDpi => WidthMm > 0 ? PixelWidth / WidthMm * 25.4 : 0;

    public string Summary => string.Create(
        CultureInfo.CurrentCulture,
        $"{PixelWidth:0}x{PixelHeight:0} · {DiagonalInches:0.0}\" · {PhysicalDpi:0} DPI · {ScaleFactor * 100:0}%");

    private void RaiseDerived()
    {
        Raise(nameof(PhysicalRect));
        Raise(nameof(DiagonalInches));
        Raise(nameof(PhysicalDpi));
        Raise(nameof(Summary));
    }
}

/// <summary>
/// A direct manipulation editor for the physical display layout.
///
/// The Windows display settings page arranges rectangles of pixels; this one arranges rectangles of
/// millimetres. That difference is the whole point of the application, so the editor draws each
/// panel at its true relative size: a 27 inch 4K display next to a 24 inch 1080p display looks like
/// what is actually on the desk, and dragging one against the other is what teaches the router where
/// the physical edges meet.
/// </summary>
public sealed class LayoutEditor : FrameworkElement
{
    private const double PaddingPx = 28;
    private const double SnapThresholdPx = 9;
    private const double MinimumScale = 0.05;

    private readonly Typeface _typeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private readonly Typeface _typefaceSmall = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private readonly Brush _surface = Freeze(new SolidColorBrush(Color.FromRgb(0x0E, 0x11, 0x1B)));
    private readonly Brush _panel = Freeze(new SolidColorBrush(Color.FromRgb(0x1B, 0x20, 0x32)));
    private readonly Brush _panelSelected = Freeze(new SolidColorBrush(Color.FromRgb(0x2A, 0x14, 0x1D)));
    private readonly Pen _panelBorder = FreezePen(Color.FromRgb(0x33, 0x3C, 0x55), 1);
    private readonly Pen _panelBorderSelected = FreezePen(Color.FromRgb(0xE5, 0x20, 0x2B), 2);
    private readonly Pen _snapGuide = FreezePen(Color.FromRgb(0xE5, 0x20, 0x2B), 1, dashed: true);
    private readonly Brush _text = Freeze(new SolidColorBrush(Colors.White));
    private readonly Brush _textDim = Freeze(new SolidColorBrush(Color.FromRgb(0xA8, 0xAE, 0xBF)));
    private readonly Brush _accent = Freeze(new SolidColorBrush(Color.FromRgb(0xE5, 0x20, 0x2B)));

    private IReadOnlyList<DisplayItem> _items = Array.Empty<DisplayItem>();
    private DisplayItem? _selected;
    private DisplayItem? _dragging;
    private Point _dragOriginPx;
    private Vec2 _dragOriginMm;
    private double _scale = 1;
    private Vec2 _origin;
    private double? _snapGuideX;
    private double? _snapGuideY;

    public LayoutEditor()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    public bool SnapEnabled { get; set; } = true;

    public event Action<DisplayItem?>? SelectionChanged;

    public event Action<DisplayItem>? ItemMoved;

    public DisplayItem? Selected
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value))
            {
                return;
            }

            _selected = value;
            SelectionChanged?.Invoke(value);
            InvalidateVisual();
        }
    }

    public void SetItems(IReadOnlyList<DisplayItem> items)
    {
        _items = items;

        if (_selected is not null && !items.Any(i => i.StableId == _selected.StableId))
        {
            _selected = null;
            SelectionChanged?.Invoke(null);
        }

        _selected ??= items.FirstOrDefault(i => i.IsPrimary) ?? items.FirstOrDefault();
        SelectionChanged?.Invoke(_selected);
        InvalidateVisual();
    }

    /// <summary>Redraws after the model changed from outside (a numeric field was edited).</summary>
    public void Refresh() => InvalidateVisual();

    // -------------------------------------------------------------------------------------------
    // Rendering
    // -------------------------------------------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRoundedRectangle(_surface, null, bounds, 10, 10);

        if (_items.Count == 0 || ActualWidth < 10 || ActualHeight < 10)
        {
            return;
        }

        RecomputeTransform();
        DrawGrid(dc, bounds);

        foreach (DisplayItem item in _items)
        {
            DrawDisplay(dc, item, ReferenceEquals(item, _selected));
        }

        DrawSnapGuides(dc, bounds);
    }

    private void RecomputeTransform()
    {
        RectD extent = RectD.Empty;
        foreach (DisplayItem item in _items)
        {
            extent = RectD.Union(extent, item.PhysicalRect);
        }

        if (extent.Width <= 0 || extent.Height <= 0)
        {
            _scale = 1;
            _origin = Vec2.Zero;
            return;
        }

        double available = Math.Max(1, ActualWidth - (2 * PaddingPx));
        double availableHeight = Math.Max(1, ActualHeight - (2 * PaddingPx));

        _scale = Math.Max(MinimumScale, Math.Min(available / extent.Width, availableHeight / extent.Height));

        double drawnWidth = extent.Width * _scale;
        double drawnHeight = extent.Height * _scale;

        _origin = new Vec2(
            ((ActualWidth - drawnWidth) / 2.0) - (extent.Left * _scale),
            ((ActualHeight - drawnHeight) / 2.0) - (extent.Top * _scale));
    }

    private Point ToScreen(Vec2 mm) => new((mm.X * _scale) + _origin.X, (mm.Y * _scale) + _origin.Y);

    private Vec2 ToMm(Point p) => new((p.X - _origin.X) / _scale, (p.Y - _origin.Y) / _scale);

    private void DrawGrid(DrawingContext dc, Rect bounds)
    {
        // A 100 mm grid gives a sense of real scale without competing with the panels.
        double step = 100 * _scale;
        if (step < 14)
        {
            return;
        }

        var pen = FreezePen(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF), 1);

        for (double x = _origin.X % step; x < bounds.Width; x += step)
        {
            dc.DrawLine(pen, new Point(x, 0), new Point(x, bounds.Height));
        }

        for (double y = _origin.Y % step; y < bounds.Height; y += step)
        {
            dc.DrawLine(pen, new Point(0, y), new Point(bounds.Width, y));
        }
    }

    private void DrawDisplay(DrawingContext dc, DisplayItem item, bool isSelected)
    {
        Point topLeft = ToScreen(new Vec2(item.LeftMm, item.TopMm));
        double width = item.WidthMm * _scale;
        double height = item.HeightMm * _scale;
        var rect = new Rect(topLeft.X, topLeft.Y, Math.Max(6, width), Math.Max(6, height));

        dc.DrawRoundedRectangle(
            isSelected ? _panelSelected : _panel,
            isSelected ? _panelBorderSelected : _panelBorder,
            rect,
            6,
            6);

        if (item.IsPrimary)
        {
            // A short accent bar marks the primary display without needing a legend.
            dc.DrawRoundedRectangle(
                _accent,
                null,
                new Rect(rect.Left + 10, rect.Top + 8, Math.Min(26, Math.Max(8, rect.Width - 20)), 3),
                1.5,
                1.5);
        }

        if (rect.Width < 54 || rect.Height < 36)
        {
            return;
        }

        FormattedText label = Format(item.Label, 12.5, _text, _typeface, rect.Width - 20);
        dc.DrawText(label, new Point(rect.Left + 10, rect.Top + (item.IsPrimary ? 18 : 12)));

        if (rect.Height < 62)
        {
            return;
        }

        FormattedText detail = Format(
            string.Create(CultureInfo.CurrentCulture, $"{item.PixelWidth:0}x{item.PixelHeight:0}"),
            11,
            _textDim,
            _typefaceSmall,
            rect.Width - 20);

        dc.DrawText(detail, new Point(rect.Left + 10, rect.Top + (item.IsPrimary ? 38 : 32)));

        if (rect.Height < 86)
        {
            return;
        }

        FormattedText physical = Format(
            string.Create(CultureInfo.CurrentCulture, $"{item.WidthMm:0} × {item.HeightMm:0} mm · {item.PhysicalDpi:0} DPI"),
            11,
            _textDim,
            _typefaceSmall,
            rect.Width - 20);

        dc.DrawText(physical, new Point(rect.Left + 10, rect.Bottom - 24));
    }

    private void DrawSnapGuides(DrawingContext dc, Rect bounds)
    {
        if (_snapGuideX is { } x)
        {
            dc.DrawLine(_snapGuide, new Point(x, 0), new Point(x, bounds.Height));
        }

        if (_snapGuideY is { } y)
        {
            dc.DrawLine(_snapGuide, new Point(0, y), new Point(bounds.Width, y));
        }
    }

    private FormattedText Format(string text, double size, Brush brush, Typeface typeface, double maxWidth)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            MaxTextWidth = Math.Max(10, maxWidth),
        };

        return formatted;
    }

    // -------------------------------------------------------------------------------------------
    // Interaction
    // -------------------------------------------------------------------------------------------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();

        Point position = e.GetPosition(this);
        DisplayItem? hit = HitTest(position);

        Selected = hit;

        if (hit is null)
        {
            return;
        }

        _dragging = hit;
        _dragOriginPx = position;
        _dragOriginMm = new Vec2(hit.LeftMm, hit.TopMm);
        CaptureMouse();
        Cursor = Cursors.SizeAll;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_dragging is null || e.LeftButton != MouseButtonState.Pressed)
        {
            Cursor = HitTest(e.GetPosition(this)) is null ? Cursors.Arrow : Cursors.Hand;
            return;
        }

        Point position = e.GetPosition(this);
        double dxMm = (position.X - _dragOriginPx.X) / _scale;
        double dyMm = (position.Y - _dragOriginPx.Y) / _scale;

        double left = _dragOriginMm.X + dxMm;
        double top = _dragOriginMm.Y + dyMm;

        _snapGuideX = null;
        _snapGuideY = null;

        if (SnapEnabled && !Keyboard.IsKeyDown(Key.LeftAlt) && !Keyboard.IsKeyDown(Key.RightAlt))
        {
            (left, top) = ApplySnap(_dragging, left, top);
        }

        _dragging.LeftMm = left;
        _dragging.TopMm = top;

        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (_dragging is null)
        {
            return;
        }

        DisplayItem moved = _dragging;
        _dragging = null;
        _snapGuideX = null;
        _snapGuideY = null;

        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;

        ItemMoved?.Invoke(moved);
        InvalidateVisual();
    }

    /// <summary>
    /// Pulls the dragged panel against its neighbours' edges. Edge to edge is by far the most common
    /// real arrangement, and it is also the one where an accidental one millimetre gap would leave a
    /// dead band the cursor has to be nudged through, so snapping is on by default. Holding Alt
    /// bypasses it for a deliberately offset setup.
    /// </summary>
    private (double Left, double Top) ApplySnap(DisplayItem dragged, double left, double top)
    {
        double threshold = SnapThresholdPx / _scale;
        double right = left + dragged.WidthMm;
        double bottom = top + dragged.HeightMm;

        double bestX = double.MaxValue;
        double bestY = double.MaxValue;
        double snappedLeft = left;
        double snappedTop = top;

        foreach (DisplayItem other in _items)
        {
            if (ReferenceEquals(other, dragged))
            {
                continue;
            }

            double otherLeft = other.LeftMm;
            double otherRight = other.LeftMm + other.WidthMm;
            double otherTop = other.TopMm;
            double otherBottom = other.TopMm + other.HeightMm;

            Consider(left - otherRight, otherRight, ref bestX, ref snappedLeft);
            Consider(right - otherLeft, otherLeft - dragged.WidthMm, ref bestX, ref snappedLeft);
            Consider(left - otherLeft, otherLeft, ref bestX, ref snappedLeft);
            Consider(right - otherRight, otherRight - dragged.WidthMm, ref bestX, ref snappedLeft);

            Consider(top - otherBottom, otherBottom, ref bestY, ref snappedTop);
            Consider(bottom - otherTop, otherTop - dragged.HeightMm, ref bestY, ref snappedTop);
            Consider(top - otherTop, otherTop, ref bestY, ref snappedTop);
            Consider(bottom - otherBottom, otherBottom - dragged.HeightMm, ref bestY, ref snappedTop);
        }

        if (bestX <= threshold)
        {
            left = snappedLeft;
            _snapGuideX = ToScreen(new Vec2(left, 0)).X;
        }

        if (bestY <= threshold)
        {
            top = snappedTop;
            _snapGuideY = ToScreen(new Vec2(0, top)).Y;
        }

        return (left, top);

        static void Consider(double delta, double candidate, ref double best, ref double result)
        {
            double distance = Math.Abs(delta);
            if (distance < best)
            {
                best = distance;
                result = candidate;
            }
        }
    }

    private DisplayItem? HitTest(Point position)
    {
        Vec2 mm = ToMm(position);

        // Reverse order so the panel drawn last (visually on top) wins.
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            if (_items[i].PhysicalRect.ContainsInclusive(mm))
            {
                return _items[i];
            }
        }

        return null;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        InvalidateVisual();
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static Pen FreezePen(Color color, double thickness, bool dashed = false)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();

        var pen = new Pen(brush, thickness);
        if (dashed)
        {
            pen.DashStyle = new DashStyle(new double[] { 4, 4 }, 0);
        }

        pen.Freeze();
        return pen;
    }
}
