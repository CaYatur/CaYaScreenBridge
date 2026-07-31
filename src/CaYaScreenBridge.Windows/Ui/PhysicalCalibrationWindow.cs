using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CaYaScreenBridge.Windows.Native;
using CaYaScreenBridge.Windows.Ui.Controls;

namespace CaYaScreenBridge.Windows.Ui;

/// <summary>
/// Physical multi-display calibration using shared guide lines and draggable edge rulers. The
/// primary display remains fixed; rulers on the selected display update its saved X/Y offset.
/// </summary>
public sealed class PhysicalCalibrationWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IReadOnlyList<DisplayItem> _targets;
    private readonly ComboBox _displayPicker;
    private readonly ComboBox _modePicker;
    private readonly TextBlock _valueText;
    private readonly StackPanel _xAdjustments;
    private readonly StackPanel _yAdjustments;
    private readonly List<CalibrationOverlayWindow> _overlays = new();
    private readonly List<CalibrationRulerWindow> _rulers = new();

    public PhysicalCalibrationWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        _targets = viewModel.Displays.Where(display => !display.IsPrimary).ToArray();

        Title = Loc.Get("calibration.title");
        Width = 760;
        Height = 590;
        MinWidth = 650;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x11, 0x1B));
        Foreground = Brushes.White;

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var root = new StackPanel { Margin = new Thickness(24) };
        scroll.Content = root;
        Content = scroll;

        root.Children.Add(new TextBlock
        {
            Text = Loc.Get("calibration.title"),
            FontSize = 23,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8),
        });
        root.Children.Add(new TextBlock
        {
            Text = Loc.Get("calibration.hint"),
            Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xBE, 0xCC)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 18),
        });

        var form = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(form);

        AddLabel(form, Loc.Get("calibration.target"), 0);
        _displayPicker = new ComboBox
        {
            ItemsSource = _targets,
            DisplayMemberPath = nameof(DisplayItem.Label),
            SelectedItem = PickInitialTarget(),
            Margin = new Thickness(0, 0, 0, 10),
        };
        Grid.SetColumn(_displayPicker, 1);
        Grid.SetRow(_displayPicker, 0);
        form.Children.Add(_displayPicker);

        AddLabel(form, Loc.Get("calibration.orientation"), 1);
        _modePicker = new ComboBox { Margin = new Thickness(0, 0, 0, 10), SelectedIndex = 0 };
        _modePicker.Items.Add(Loc.Get("calibration.both"));
        _modePicker.Items.Add(Loc.Get("calibration.horizontal"));
        _modePicker.Items.Add(Loc.Get("calibration.vertical"));
        Grid.SetColumn(_modePicker, 1);
        Grid.SetRow(_modePicker, 1);
        form.Children.Add(_modePicker);

        root.Children.Add(new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0xA8, 0x20)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0xE5, 0xA8, 0x20)),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 14),
            Child = new TextBlock
            {
                Text = Loc.Get("calibration.dragHint"),
                TextWrapping = TextWrapping.Wrap,
            },
        });

        _valueText = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 15,
            Margin = new Thickness(0, 0, 0, 14),
        };
        root.Children.Add(_valueText);

        _xAdjustments = CreateAdjustmentGroup(Loc.Get("calibration.adjustX"), CalibrationAxis.X);
        _yAdjustments = CreateAdjustmentGroup(Loc.Get("calibration.adjustY"), CalibrationAxis.Y);
        root.Children.Add(_xAdjustments);
        root.Children.Add(_yAdjustments);

        root.Children.Add(new TextBlock
        {
            Text = Loc.Get("calibration.precisionHint"),
            Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xBE, 0xCC)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 18),
        });

        var close = new Button
        {
            Content = Loc.Get("common.close"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(18, 8, 18, 8),
            IsDefault = true,
        };
        close.Click += (_, _) => Close();
        root.Children.Add(close);

        Loaded += (_, _) =>
        {
            CreateOverlays();
            RecreateRulers();
            UpdateCalibration();
        };
        Closed += (_, _) => CloseCalibrationWindows();
        _displayPicker.SelectionChanged += (_, _) =>
        {
            RecreateRulers();
            UpdateCalibration();
        };
        _modePicker.SelectionChanged += (_, _) =>
        {
            RecreateRulers();
            UpdateCalibration();
        };
    }

    private CalibrationMode Mode => _modePicker.SelectedIndex switch
    {
        1 => CalibrationMode.Horizontal,
        2 => CalibrationMode.Vertical,
        _ => CalibrationMode.Both,
    };

    private DisplayItem? PickInitialTarget() =>
        _viewModel.SelectedDisplay is { IsPrimary: false } selected
            ? _targets.FirstOrDefault(item => ReferenceEquals(item, selected) || item.StableId == selected.StableId)
            : _targets.FirstOrDefault();

    private static void AddLabel(Grid grid, string text, int row)
    {
        var label = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 10),
        };
        Grid.SetRow(label, row);
        grid.Children.Add(label);
    }

    private StackPanel CreateAdjustmentGroup(string title, CalibrationAxis axis)
    {
        var group = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        group.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
        });

        var buttons = new WrapPanel();
        foreach (double amount in new[] { -10.0, -1.0, -0.1, 0.1, 1.0, 10.0 })
        {
            var button = new Button
            {
                Content = amount > 0 ? $"+{amount:0.#} mm" : $"{amount:0.#} mm",
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(12, 7, 12, 7),
                Tag = amount,
            };
            button.Click += (_, _) => Adjust(axis, (double)button.Tag, commit: true);
            buttons.Children.Add(button);
        }
        group.Children.Add(buttons);
        return group;
    }

    private void Adjust(CalibrationAxis axis, double millimetres, bool commit)
    {
        if (_displayPicker.SelectedItem is not DisplayItem target || target.IsPrimary)
        {
            return;
        }

        if (axis == CalibrationAxis.X)
        {
            target.LeftMm = Math.Round(target.LeftMm + millimetres, 3);
        }
        else
        {
            target.TopMm = Math.Round(target.TopMm + millimetres, 3);
        }

        _viewModel.SelectedDisplay = target;
        if (commit)
        {
            _viewModel.CommitDisplay(target);
        }
        UpdateCalibration();
    }

    private void CommitDraggedDisplay()
    {
        if (_displayPicker.SelectedItem is DisplayItem target && !target.IsPrimary)
        {
            _viewModel.SelectedDisplay = target;
            _viewModel.CommitDisplay(target);
            UpdateCalibration();
        }
    }

    private void CreateOverlays()
    {
        foreach (CalibrationOverlayWindow overlay in _overlays)
        {
            overlay.Close();
        }
        _overlays.Clear();

        foreach (DisplayItem display in _viewModel.Displays)
        {
            var overlay = new CalibrationOverlayWindow(display);
            overlay.Show();
            overlay.PlaceOnMonitor();
            _overlays.Add(overlay);
        }
    }

    private void RecreateRulers()
    {
        foreach (CalibrationRulerWindow ruler in _rulers)
        {
            ruler.Close();
        }
        _rulers.Clear();

        if (!IsLoaded || _displayPicker.SelectedItem is not DisplayItem target || target.IsPrimary)
        {
            return;
        }

        bool showHorizontal = Mode is CalibrationMode.Both or CalibrationMode.Horizontal;
        bool showVertical = Mode is CalibrationMode.Both or CalibrationMode.Vertical;

        if (showHorizontal)
        {
            CreateRuler(target, RulerEdge.Top);
            CreateRuler(target, RulerEdge.Bottom);
        }
        if (showVertical)
        {
            CreateRuler(target, RulerEdge.Left);
            CreateRuler(target, RulerEdge.Right);
        }
    }

    private void CreateRuler(DisplayItem target, RulerEdge edge)
    {
        var ruler = new CalibrationRulerWindow(
            target,
            edge,
            deltaMm => Adjust(edge is RulerEdge.Left or RulerEdge.Right ? CalibrationAxis.X : CalibrationAxis.Y, deltaMm, commit: false),
            CommitDraggedDisplay);
        ruler.Show();
        ruler.PlaceOnMonitor();
        _rulers.Add(ruler);
    }

    private void UpdateCalibration()
    {
        if (!IsLoaded || _viewModel.Displays.Count == 0)
        {
            return;
        }

        DisplayItem reference = _viewModel.Displays.FirstOrDefault(display => display.IsPrimary) ?? _viewModel.Displays[0];
        double[] horizontalMm =
        [
            reference.TopMm + (reference.HeightMm * 0.25),
            reference.TopMm + (reference.HeightMm * 0.50),
            reference.TopMm + (reference.HeightMm * 0.75),
        ];
        double[] verticalMm =
        [
            reference.LeftMm + (reference.WidthMm * 0.25),
            reference.LeftMm + (reference.WidthMm * 0.50),
            reference.LeftMm + (reference.WidthMm * 0.75),
        ];

        bool showHorizontal = Mode is CalibrationMode.Both or CalibrationMode.Horizontal;
        bool showVertical = Mode is CalibrationMode.Both or CalibrationMode.Vertical;
        DisplayItem? selected = _displayPicker.SelectedItem as DisplayItem;

        foreach (CalibrationOverlayWindow overlay in _overlays)
        {
            double[] horizontalFractions = horizontalMm
                .Select(value => (value - overlay.Display.TopMm) / overlay.Display.HeightMm)
                .ToArray();
            double[] verticalFractions = verticalMm
                .Select(value => (value - overlay.Display.LeftMm) / overlay.Display.WidthMm)
                .ToArray();
            overlay.SetGuides(
                showHorizontal,
                horizontalFractions,
                showVertical,
                verticalFractions,
                selected?.StableId == overlay.Display.StableId);
        }

        foreach (CalibrationRulerWindow ruler in _rulers)
        {
            ruler.UpdateValue();
        }

        _xAdjustments.IsEnabled = selected is not null && !selected.IsPrimary && showVertical;
        _yAdjustments.IsEnabled = selected is not null && !selected.IsPrimary && showHorizontal;

        _valueText.Text = selected is null
            ? Loc.Get("calibration.noTarget")
            : $"{Loc.Get("displays.position")}:  X = {selected.LeftMm:0.000} mm   |   Y = {selected.TopMm:0.000} mm";
    }

    private void CloseCalibrationWindows()
    {
        foreach (CalibrationRulerWindow ruler in _rulers)
        {
            ruler.Close();
        }
        _rulers.Clear();

        foreach (CalibrationOverlayWindow overlay in _overlays)
        {
            overlay.Close();
        }
        _overlays.Clear();
    }

    private enum CalibrationMode
    {
        Both,
        Horizontal,
        Vertical,
    }

    private enum CalibrationAxis
    {
        X,
        Y,
    }

    private enum RulerEdge
    {
        Top,
        Bottom,
        Left,
        Right,
    }

    private sealed class CalibrationOverlayWindow : Window
    {
        private readonly GuideSurface _surface;

        public CalibrationOverlayWindow(DisplayItem display)
        {
            Display = display;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Left = -32000;
            Top = -32000;
            Width = 1;
            Height = 1;

            _surface = new GuideSurface(display.Label);
            Content = _surface;
            SourceInitialized += (_, _) => MakeClickThrough();
        }

        public DisplayItem Display { get; }

        public void PlaceOnMonitor()
        {
            nint hwnd = new WindowInteropHelper(this).Handle;
            _ = Win32.SetWindowPos(
                hwnd,
                new nint(-1),
                (int)Math.Round(Display.PixelLeft),
                (int)Math.Round(Display.PixelTop),
                Math.Max(1, (int)Math.Round(Display.PixelWidth)),
                Math.Max(1, (int)Math.Round(Display.PixelHeight)),
                Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        }

        public void SetGuides(
            bool showHorizontal,
            IReadOnlyList<double> horizontalFractions,
            bool showVertical,
            IReadOnlyList<double> verticalFractions,
            bool selected) =>
            _surface.SetGuides(showHorizontal, horizontalFractions, showVertical, verticalFractions, selected);

        private void MakeClickThrough()
        {
            nint hwnd = new WindowInteropHelper(this).Handle;
            long style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            style |= WsExTransparent | WsExNoActivate | WsExToolWindow;
            _ = SetWindowLongPtr(hwnd, GwlExStyle, new nint(style));
        }
    }

    private sealed class GuideSurface : FrameworkElement
    {
        private static readonly Pen ShadowPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0xC8, 0, 0, 0)), 11));
        private static readonly Pen MainPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x24, 0x35)), 6));
        private static readonly Pen HelperPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x20)), 2));
        private static readonly Pen SelectedOutline = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x20)), 4));
        private readonly string _label;
        private bool _showHorizontal;
        private bool _showVertical;
        private bool _selected;
        private IReadOnlyList<double> _horizontalFractions = Array.Empty<double>();
        private IReadOnlyList<double> _verticalFractions = Array.Empty<double>();

        public GuideSurface(string label)
        {
            _label = label;
            IsHitTestVisible = false;
        }

        public void SetGuides(
            bool showHorizontal,
            IReadOnlyList<double> horizontalFractions,
            bool showVertical,
            IReadOnlyList<double> verticalFractions,
            bool selected)
        {
            _showHorizontal = showHorizontal;
            _horizontalFractions = horizontalFractions;
            _showVertical = showVertical;
            _verticalFractions = verticalFractions;
            _selected = selected;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            if (_selected)
            {
                drawingContext.DrawRectangle(null, SelectedOutline, new Rect(2, 2, Math.Max(0, width - 4), Math.Max(0, height - 4)));
            }

            if (_showHorizontal)
            {
                foreach (double fraction in _horizontalFractions)
                {
                    DrawHorizontalGroup(drawingContext, width, height, fraction);
                }
            }

            if (_showVertical)
            {
                foreach (double fraction in _verticalFractions)
                {
                    DrawVerticalGroup(drawingContext, width, height, fraction);
                }
            }

            DrawLabel(drawingContext);
        }

        private static void DrawHorizontalGroup(DrawingContext dc, double width, double height, double fraction)
        {
            if (fraction < 0 || fraction > 1)
            {
                return;
            }

            double y = Math.Clamp(fraction * height, 0, height);
            dc.DrawLine(ShadowPen, new Point(0, y), new Point(width, y));
            dc.DrawLine(MainPen, new Point(0, y), new Point(width, y));
            dc.DrawLine(HelperPen, new Point(0, y - 14), new Point(width, y - 14));
            dc.DrawLine(HelperPen, new Point(0, y + 14), new Point(width, y + 14));
        }

        private static void DrawVerticalGroup(DrawingContext dc, double width, double height, double fraction)
        {
            if (fraction < 0 || fraction > 1)
            {
                return;
            }

            double x = Math.Clamp(fraction * width, 0, width);
            dc.DrawLine(ShadowPen, new Point(x, 0), new Point(x, height));
            dc.DrawLine(MainPen, new Point(x, 0), new Point(x, height));
            dc.DrawLine(HelperPen, new Point(x - 14, 0), new Point(x - 14, height));
            dc.DrawLine(HelperPen, new Point(x + 14, 0), new Point(x + 14, height));
        }

        private void DrawLabel(DrawingContext dc)
        {
            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var text = new FormattedText(
                _label,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI Semibold"),
                15,
                Brushes.White,
                pixelsPerDip);
            var background = new Rect(14, 14, text.Width + 20, text.Height + 12);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xD0, 0, 0, 0)), null, background, 6, 6);
            dc.DrawText(text, new Point(24, 20));
        }
    }

    private sealed class CalibrationRulerWindow : Window
    {
        private readonly DisplayItem _display;
        private readonly RulerEdge _edge;
        private readonly Action<double> _onDelta;
        private readonly Action _onCompleted;
        private readonly RulerSurface _surface;
        private bool _dragging;
        private POINT _lastCursor;

        public CalibrationRulerWindow(
            DisplayItem display,
            RulerEdge edge,
            Action<double> onDelta,
            Action onCompleted)
        {
            _display = display;
            _edge = edge;
            _onDelta = onDelta;
            _onCompleted = onCompleted;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Left = -32000;
            Top = -32000;
            Width = IsHorizontal ? 300 : 48;
            Height = IsHorizontal ? 48 : 300;
            Cursor = IsHorizontal ? Cursors.SizeNS : Cursors.SizeWE;

            _surface = new RulerSurface(edge);
            Content = _surface;
            SourceInitialized += (_, _) => MakeToolWindow();
            MouseLeftButtonDown += OnMouseLeftButtonDown;
            MouseMove += OnMouseMove;
            MouseLeftButtonUp += OnMouseLeftButtonUp;
            LostMouseCapture += (_, _) => FinishDrag();
            UpdateValue();
        }

        private bool IsHorizontal => _edge is RulerEdge.Top or RulerEdge.Bottom;

        public void PlaceOnMonitor()
        {
            int width = IsHorizontal ? 300 : 48;
            int height = IsHorizontal ? 48 : 300;
            int left;
            int top;

            switch (_edge)
            {
                case RulerEdge.Top:
                    left = (int)Math.Round(_display.PixelLeft + 24);
                    top = (int)Math.Round(_display.PixelTop + 20);
                    break;
                case RulerEdge.Bottom:
                    left = (int)Math.Round(_display.PixelLeft + _display.PixelWidth - width - 24);
                    top = (int)Math.Round(_display.PixelTop + _display.PixelHeight - height - 20);
                    break;
                case RulerEdge.Left:
                    left = (int)Math.Round(_display.PixelLeft + 20);
                    top = (int)Math.Round(_display.PixelTop + 78);
                    break;
                default:
                    left = (int)Math.Round(_display.PixelLeft + _display.PixelWidth - width - 20);
                    top = (int)Math.Round(_display.PixelTop + _display.PixelHeight - height - 78);
                    break;
            }

            nint hwnd = new WindowInteropHelper(this).Handle;
            _ = Win32.SetWindowPos(
                hwnd,
                new nint(-1),
                left,
                top,
                width,
                height,
                Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        }

        public void UpdateValue()
        {
            _surface.ValueMm = IsHorizontal ? _display.TopMm : _display.LeftMm;
            _surface.InvalidateVisual();
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!Win32.GetCursorPos(out _lastCursor))
            {
                return;
            }

            _dragging = true;
            CaptureMouse();
            e.Handled = true;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging || e.LeftButton != MouseButtonState.Pressed || !Win32.GetCursorPos(out POINT cursor))
            {
                return;
            }

            int pixelDelta = IsHorizontal ? cursor.Y - _lastCursor.Y : cursor.X - _lastCursor.X;
            _lastCursor = cursor;
            if (pixelDelta == 0)
            {
                return;
            }

            double pixelsPerMm = IsHorizontal
                ? _display.PixelHeight / Math.Max(1, _display.HeightMm)
                : _display.PixelWidth / Math.Max(1, _display.WidthMm);
            double precision = Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
                ? 0.1
                : Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
                    ? 0.25
                    : 1.0;
            _onDelta((pixelDelta / Math.Max(0.001, pixelsPerMm)) * precision);
            UpdateValue();
            e.Handled = true;
        }

        private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            FinishDrag();
            e.Handled = true;
        }

        private void FinishDrag()
        {
            if (!_dragging)
            {
                return;
            }

            _dragging = false;
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }
            _onCompleted();
        }

        private void MakeToolWindow()
        {
            nint hwnd = new WindowInteropHelper(this).Handle;
            long style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            style |= WsExToolWindow;
            _ = SetWindowLongPtr(hwnd, GwlExStyle, new nint(style));
        }
    }

    private sealed class RulerSurface : FrameworkElement
    {
        private static readonly Brush BackgroundBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xEC, 0x0B, 0x0E, 0x16)));
        private static readonly Pen BorderPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x20)), 2));
        private static readonly Pen TickPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x62)), 1.5));
        private readonly RulerEdge _edge;

        public RulerSurface(RulerEdge edge) => _edge = edge;

        public double ValueMm { get; set; }

        private bool IsHorizontal => _edge is RulerEdge.Top or RulerEdge.Bottom;

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            dc.DrawRoundedRectangle(BackgroundBrush, BorderPen, new Rect(1, 1, Math.Max(0, width - 2), Math.Max(0, height - 2)), 7, 7);
            if (IsHorizontal)
            {
                DrawHorizontalTicks(dc, width, height);
            }
            else
            {
                DrawVerticalTicks(dc, width, height);
            }
            DrawCaption(dc, width, height);
        }

        private void DrawHorizontalTicks(DrawingContext dc, double width, double height)
        {
            bool fromBottom = _edge == RulerEdge.Top;
            for (double x = 10; x < width - 10; x += 10)
            {
                bool major = ((int)x % 50) == 0;
                double length = major ? 18 : 9;
                double start = fromBottom ? height - 5 : 5;
                double end = fromBottom ? start - length : start + length;
                dc.DrawLine(TickPen, new Point(x, start), new Point(x, end));
            }
        }

        private void DrawVerticalTicks(DrawingContext dc, double width, double height)
        {
            bool fromRight = _edge == RulerEdge.Left;
            for (double y = 10; y < height - 10; y += 10)
            {
                bool major = ((int)y % 50) == 0;
                double length = major ? 18 : 9;
                double start = fromRight ? width - 5 : 5;
                double end = fromRight ? start - length : start + length;
                dc.DrawLine(TickPen, new Point(start, y), new Point(end, y));
            }
        }

        private void DrawCaption(DrawingContext dc, double width, double height)
        {
            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            string axis = IsHorizontal ? "Y ↕" : "X ↔";
            var text = new FormattedText(
                $"{axis}   {ValueMm:0.000} mm",
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI Semibold"),
                13,
                Brushes.White,
                pixelsPerDip);

            if (IsHorizontal)
            {
                dc.DrawText(text, new Point(Math.Max(8, (width - text.Width) / 2), Math.Max(4, (height - text.Height) / 2)));
            }
            else
            {
                dc.PushTransform(new RotateTransform(-90, width / 2, height / 2));
                dc.DrawText(text, new Point((width - text.Width) / 2, (height - text.Height) / 2));
                dc.Pop();
            }
        }
    }

    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;

    private static T Freeze<T>(T value) where T : Freezable
    {
        value.Freeze();
        return value;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr64(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern nint GetWindowLongPtr32(nint hwnd, int index);

    private static nint GetWindowLongPtr(nint hwnd, int index) =>
        nint.Size == 8 ? GetWindowLongPtr64(hwnd, index) : GetWindowLongPtr32(hwnd, index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr64(nint hwnd, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern nint SetWindowLongPtr32(nint hwnd, int index, nint value);

    private static nint SetWindowLongPtr(nint hwnd, int index, nint value) =>
        nint.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : SetWindowLongPtr32(hwnd, index, value);
}
