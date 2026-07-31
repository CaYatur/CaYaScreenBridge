using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CaYaScreenBridge.Core.Config;
using CaYaScreenBridge.Core.Diagnostics;
using CaYaScreenBridge.Core.Geometry;
using CaYaScreenBridge.Core.Model;
using CaYaScreenBridge.Windows.Engine;
using CaYaScreenBridge.Windows.Services;
using CaYaScreenBridge.Windows.Ui.Controls;
using Microsoft.Win32;

namespace CaYaScreenBridge.Windows.Ui;

/// <summary>
/// Backs the settings window. Every change is written straight into the live configuration and
/// pushed to the engine, then persisted on a short debounce, so the effect of a setting is felt
/// immediately while the disk is not touched on every keystroke.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(600);

    private readonly IBridgeEngine _engine;
    private readonly ConfigStore _store;
    private readonly StartupManager _startup;
    private readonly RingLog _log;
    private readonly FileLogSink _fileLog;
    private readonly DesktopWallpaperService _wallpapers;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _liveTimer;

    private bool _suppressPersist;
    private EngineStatus _status = EngineStatus.Stopped;
    private DisplayItem? _selectedDisplay;
    private string _logText = string.Empty;
    private string _liveCursor = string.Empty;
    private string _startupSummary = string.Empty;

    public MainViewModel(
        IBridgeEngine engine,
        ConfigStore store,
        StartupManager startup,
        RingLog log,
        FileLogSink fileLog)
    {
        _engine = engine;
        _store = store;
        _startup = startup;
        _log = log;
        _fileLog = fileLog;
        _wallpapers = new DesktopWallpaperService(log);

        _saveTimer = new DispatcherTimer { Interval = SaveDebounce };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            Persist();
        };

        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _liveTimer.Tick += (_, _) => RefreshLive();

        RebuildLayoutCommand = new RelayCommand(() => _engine.RebuildLayout("manual"));
        AutoLayoutCommand = new RelayCommand(ResetLayoutToWindows);
        RevertSizeCommand = new RelayCommand(RevertSelectedSize, () => SelectedDisplay is not null);
        AddRuleCommand = new RelayCommand(AddRule);
        RemoveRuleCommand = new RelayCommand(RemoveSelectedRule, () => SelectedRule is not null);
        CopyLogCommand = new RelayCommand(CopyLog);
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);
        RearmHookCommand = new RelayCommand(() => _engine.RebuildLayout("re-arm"));
        RepairStartupCommand = new RelayCommand(RepairStartup);
        ExportSettingsCommand = new RelayCommand(ExportSettings);
        ImportSettingsCommand = new RelayCommand(ImportSettings);
        OpenCalibrationCommand = new RelayCommand(OpenCalibration, () => Displays.Count > 1);

        _engine.StatusChanged += OnEngineStatus;
        _engine.LayoutChanged += OnEngineLayout;
        _log.EntryWritten += OnLogEntry;

        LoadFromConfig();
        OnEngineLayout(_engine.Layout);
        _status = _engine.Status;
        RefreshStartupSummary();
        RefreshLogText();
    }

    public AppConfig Config => _engine.Config;

    public ObservableCollection<DisplayItem> Displays { get; } = new();

    public ObservableCollection<AppRule> Rules { get; } = new();

    public RelayCommand RebuildLayoutCommand { get; }

    public RelayCommand AutoLayoutCommand { get; }

    public RelayCommand RevertSizeCommand { get; }

    public RelayCommand AddRuleCommand { get; }

    public RelayCommand RemoveRuleCommand { get; }

    public RelayCommand CopyLogCommand { get; }

    public RelayCommand OpenLogFolderCommand { get; }

    public RelayCommand RearmHookCommand { get; }

    public RelayCommand RepairStartupCommand { get; }

    public RelayCommand ExportSettingsCommand { get; }

    public RelayCommand ImportSettingsCommand { get; }

    public RelayCommand OpenCalibrationCommand { get; }

    // -------------------------------------------------------------------------------------------
    // Status
    // -------------------------------------------------------------------------------------------

    public EngineStatus Status
    {
        get => _status;
        private set
        {
            _status = value;
            Raise();
            Raise(nameof(StatusText));
            Raise(nameof(IsHealthy));
            Raise(nameof(PolicyText));
            Raise(nameof(ProfileText));
        }
    }

    public bool IsHealthy => _status.Running && _status.HookInstalled;

    public string StatusText => _status switch
    {
        { Running: false } => Loc.Get("overview.paused"),
        { HookInstalled: false } => Loc.Get("overview.paused"),
        { CorrectingCursor: false } => Loc.Get("overview.paused"),
        _ => Loc.Get("overview.running"),
    };

    public string PolicyText => _status.PolicyReason;

    public string ProfileText => _status.ActiveProfileId;

    public string LiveCursor
    {
        get => _liveCursor;
        private set => Set(ref _liveCursor, value);
    }

    public string LogText
    {
        get => _logText;
        private set => Set(ref _logText, value);
    }

    public string StartupSummary
    {
        get => _startupSummary;
        private set => Set(ref _startupSummary, value);
    }

    public bool IsUniformLayout => _engine.Layout.Count > 1 && _engine.Layout.IsUniform;

    // -------------------------------------------------------------------------------------------
    // General
    // -------------------------------------------------------------------------------------------

    public bool MasterEnabled
    {
        get => Config.Enabled;
        set => Update(() => Config.Enabled = value);
    }

    public bool AlignCursor
    {
        get => Config.Transition.AlignCursor;
        set => Update(() => Config.Transition.AlignCursor = value);
    }

    public bool UseRawInputAssist
    {
        get => Config.Transition.UseRawInputAssist;
        set => Update(() => Config.Transition.UseRawInputAssist = value);
    }

    public bool PreventCursorLoss
    {
        get => Config.Transition.PreventCursorLoss;
        set => Update(() => Config.Transition.PreventCursorLoss = value);
    }

    public bool NormalisePointerSpeed
    {
        get => Config.Transition.NormalisePointerSpeed;
        set => Update(() => Config.Transition.NormalisePointerSpeed = value);
    }

    public double BorderResistanceMm
    {
        get => Config.Transition.BorderResistanceMm;
        set => Update(() => Config.Transition.BorderResistanceMm = Math.Round(value, 1));
    }

    public bool SpeedAdaptiveResistance
    {
        get => Config.Transition.SpeedAdaptiveResistance;
        set => Update(() => Config.Transition.SpeedAdaptiveResistance = value);
    }

    public double ResistanceSpeedReferenceMmPerSecond
    {
        get => Config.Transition.ResistanceSpeedReferenceMmPerSecond;
        set => Update(() => Config.Transition.ResistanceSpeedReferenceMmPerSecond = Math.Clamp(Math.Round(value), 10, 5000));
    }

    public int WrapIndex
    {
        get => (int)Config.Transition.Wrap;
        set => Update(() => Config.Transition.Wrap = (WrapMode)Math.Clamp(value, 0, 3));
    }

    // -------------------------------------------------------------------------------------------
    // Dragging
    // -------------------------------------------------------------------------------------------

    public int DragModeIndex
    {
        get => (int)Config.Drag.Mode;
        set => Update(() => Config.Drag.Mode = (DragScalingMode)Math.Clamp(value, 0, 2));
    }

    public bool PreserveGrabPoint
    {
        get => Config.Drag.PreserveGrabPoint;
        set => Update(() => Config.Drag.PreserveGrabPoint = value);
    }

    public bool SeamlessCrossDisplay
    {
        get => Config.Drag.SeamlessCrossDisplay;
        set => Update(() => Config.Drag.SeamlessCrossDisplay = value);
    }

    public bool SkipDpiUnawareWindows
    {
        get => Config.Drag.SkipDpiUnawareWindows;
        set => Update(() => Config.Drag.SkipDpiUnawareWindows = value);
    }

    public bool SkipMaximisedWindows
    {
        get => Config.Drag.SkipMaximisedWindows;
        set => Update(() => Config.Drag.SkipMaximisedWindows = value);
    }

    public double LiveThrottleMs
    {
        get => Config.Drag.LiveThrottleMs;
        set => Update(() => Config.Drag.LiveThrottleMs = (int)Math.Round(value));
    }

    // -------------------------------------------------------------------------------------------
    // Games
    // -------------------------------------------------------------------------------------------

    public bool PauseInExclusiveFullScreen
    {
        get => Config.Games.PauseInExclusiveFullScreen;
        set => Update(() => Config.Games.PauseInExclusiveFullScreen = value);
    }

    public bool CorrectInBorderlessFullScreen
    {
        get => Config.Games.CorrectInBorderlessFullScreen;
        set => Update(() => Config.Games.CorrectInBorderlessFullScreen = value);
    }

    public bool DeepWindowsIntegration
    {
        get => Config.Games.DeepWindowsIntegration;
        set => Update(
            () => Config.Games.DeepWindowsIntegration = value,
            afterApply: () =>
            {
                if (value && !Config.General.StartElevated)
                {
                    Config.General.StartElevated = true;
                    Raise(nameof(StartElevated));
                    ApplyStartup();
                }
            });
    }

    public bool PauseForAntiCheat
    {
        get => Config.Games.PauseForAntiCheat;
        set => Update(() => Config.Games.PauseForAntiCheat = value);
    }

    // -------------------------------------------------------------------------------------------
    // Startup
    // -------------------------------------------------------------------------------------------

    public bool StartWithWindows
    {
        get => Config.General.StartWithWindows;
        set => Update(
            () => Config.General.StartWithWindows = value,
            afterApply: ApplyStartup);
    }

    public bool StartElevated
    {
        get => Config.General.StartElevated;
        set => Update(
            () => Config.General.StartElevated = value,
            afterApply: ApplyStartup);
    }

    public bool StartMinimised
    {
        get => Config.General.StartMinimised;
        set => Update(() => Config.General.StartMinimised = value);
    }

    public bool ShowTrayIcon
    {
        get => Config.General.ShowTrayIcon;
        set => Update(() => Config.General.ShowTrayIcon = value);
    }

    public bool RaiseHookTimeout
    {
        get => Config.General.RaiseHookTimeout;
        set => Update(
            () => Config.General.RaiseHookTimeout = value,
            afterApply: () => _startup.ApplyHookTimeout(value));
    }

    public bool VerboseLogging
    {
        get => Config.General.VerboseLogging;
        set => Update(
            () => Config.General.VerboseLogging = value,
            afterApply: () =>
            {
                _log.MinimumLevel = value ? LogLevel.Debug : LogLevel.Info;
                _fileLog.MinimumLevel = _log.MinimumLevel;
            });
    }

    public int LanguageIndex
    {
        get => Config.General.Language switch { "tr" => 1, "en" => 2, _ => 0 };
        set => Update(() => Config.General.Language = value switch { 1 => "tr", 2 => "en", _ => "auto" });
    }

    // -------------------------------------------------------------------------------------------
    // Displays
    // -------------------------------------------------------------------------------------------

    public DisplayItem? SelectedDisplay
    {
        get => _selectedDisplay;
        set
        {
            if (Set(ref _selectedDisplay, value))
            {
                Raise(nameof(HasSelectedDisplay));
                RaiseSelectedResistanceProperties();
            }
        }
    }

    public bool LeftUseCustomResistance
    {
        get => GetEdgeResistance(DisplayEdge.Left)?.UseCustomResistance ?? false;
        set => SetEdgeCustom(DisplayEdge.Left, value, nameof(LeftUseCustomResistance));
    }

    public double LeftResistanceMm
    {
        get => GetEdgeResistance(DisplayEdge.Left)?.ResistanceMm ?? BorderResistanceMm;
        set => SetEdgeResistance(DisplayEdge.Left, value, nameof(LeftResistanceMm));
    }

    public int LeftSpeedAdaptiveIndex
    {
        get => ToSpeedMode(GetEdgeResistance(DisplayEdge.Left)?.SpeedAdaptive);
        set => SetEdgeSpeedMode(DisplayEdge.Left, value, nameof(LeftSpeedAdaptiveIndex));
    }

    public bool TopUseCustomResistance
    {
        get => GetEdgeResistance(DisplayEdge.Top)?.UseCustomResistance ?? false;
        set => SetEdgeCustom(DisplayEdge.Top, value, nameof(TopUseCustomResistance));
    }

    public double TopResistanceMm
    {
        get => GetEdgeResistance(DisplayEdge.Top)?.ResistanceMm ?? BorderResistanceMm;
        set => SetEdgeResistance(DisplayEdge.Top, value, nameof(TopResistanceMm));
    }

    public int TopSpeedAdaptiveIndex
    {
        get => ToSpeedMode(GetEdgeResistance(DisplayEdge.Top)?.SpeedAdaptive);
        set => SetEdgeSpeedMode(DisplayEdge.Top, value, nameof(TopSpeedAdaptiveIndex));
    }

    public bool RightUseCustomResistance
    {
        get => GetEdgeResistance(DisplayEdge.Right)?.UseCustomResistance ?? false;
        set => SetEdgeCustom(DisplayEdge.Right, value, nameof(RightUseCustomResistance));
    }

    public double RightResistanceMm
    {
        get => GetEdgeResistance(DisplayEdge.Right)?.ResistanceMm ?? BorderResistanceMm;
        set => SetEdgeResistance(DisplayEdge.Right, value, nameof(RightResistanceMm));
    }

    public int RightSpeedAdaptiveIndex
    {
        get => ToSpeedMode(GetEdgeResistance(DisplayEdge.Right)?.SpeedAdaptive);
        set => SetEdgeSpeedMode(DisplayEdge.Right, value, nameof(RightSpeedAdaptiveIndex));
    }

    public bool BottomUseCustomResistance
    {
        get => GetEdgeResistance(DisplayEdge.Bottom)?.UseCustomResistance ?? false;
        set => SetEdgeCustom(DisplayEdge.Bottom, value, nameof(BottomUseCustomResistance));
    }

    public double BottomResistanceMm
    {
        get => GetEdgeResistance(DisplayEdge.Bottom)?.ResistanceMm ?? BorderResistanceMm;
        set => SetEdgeResistance(DisplayEdge.Bottom, value, nameof(BottomResistanceMm));
    }

    public int BottomSpeedAdaptiveIndex
    {
        get => ToSpeedMode(GetEdgeResistance(DisplayEdge.Bottom)?.SpeedAdaptive);
        set => SetEdgeSpeedMode(DisplayEdge.Bottom, value, nameof(BottomSpeedAdaptiveIndex));
    }

    public bool HasSelectedDisplay => _selectedDisplay is not null;

    public AppRule? SelectedRule { get; set; }

    private EdgeResistanceSettings? GetEdgeResistance(DisplayEdge edge, bool create = false)
    {
        if (SelectedDisplay is not { } display)
        {
            return null;
        }

        DisplayResistanceSettings? settings = Config.Transition.DisplayResistance.FirstOrDefault(d =>
            string.Equals(d.StableId, display.StableId, StringComparison.OrdinalIgnoreCase));

        if (settings is null && create)
        {
            settings = new DisplayResistanceSettings { StableId = display.StableId };
            foreach (DisplayEdge value in Enum.GetValues<DisplayEdge>())
            {
                settings.For(value).ResistanceMm = BorderResistanceMm;
            }
            Config.Transition.DisplayResistance.Add(settings);
        }

        return settings?.For(edge);
    }

    private void SetEdgeCustom(DisplayEdge edge, bool value, string propertyName)
    {
        EdgeResistanceSettings? settings = GetEdgeResistance(edge, create: true);
        if (settings is null) return;
        Update(() => settings.UseCustomResistance = value, propertyName: propertyName);
    }

    private void SetEdgeResistance(DisplayEdge edge, double value, string propertyName)
    {
        EdgeResistanceSettings? settings = GetEdgeResistance(edge, create: true);
        if (settings is null) return;
        Update(() => settings.ResistanceMm = Math.Clamp(Math.Round(value, 1), 0, 200), propertyName: propertyName);
    }

    private void SetEdgeSpeedMode(DisplayEdge edge, int value, string propertyName)
    {
        EdgeResistanceSettings? settings = GetEdgeResistance(edge, create: true);
        if (settings is null) return;
        Update(() => settings.SpeedAdaptive = value switch { 1 => true, 2 => false, _ => null }, propertyName: propertyName);
    }

    private static int ToSpeedMode(bool? value) => value switch { true => 1, false => 2, _ => 0 };

    private void RaiseSelectedResistanceProperties()
    {
        foreach (string property in new[]
        {
            nameof(LeftUseCustomResistance), nameof(LeftResistanceMm), nameof(LeftSpeedAdaptiveIndex),
            nameof(TopUseCustomResistance), nameof(TopResistanceMm), nameof(TopSpeedAdaptiveIndex),
            nameof(RightUseCustomResistance), nameof(RightResistanceMm), nameof(RightSpeedAdaptiveIndex),
            nameof(BottomUseCustomResistance), nameof(BottomResistanceMm), nameof(BottomSpeedAdaptiveIndex),
        })
        {
            Raise(property);
        }
    }

    /// <summary>
    /// Writes an edited panel back into the saved profile and rebuilds the router's layout, so the
    /// change is felt on the very next mouse movement.
    /// </summary>
    public void CommitDisplay(DisplayItem item)
    {
        LayoutProfile? profile = FindActiveProfile();
        if (profile is null)
        {
            return;
        }

        // Pin the complete current layout before rebuilding it. Saving only the moved panel leaves
        // untouched panels without position overrides, so LayoutBuilder reconstructs them from the
        // Windows arrangement and makes the edited panel appear to jump on mouse release.
        foreach (DisplayItem display in Displays)
        {
            DisplayOverride position = profile.GetOrCreate(display.StableId);
            position.PhysicalLeftMm = Math.Round(display.LeftMm, 2);
            position.PhysicalTopMm = Math.Round(display.TopMm, 2);
        }

        DisplayOverride ovr = profile.GetOrCreate(item.StableId);
        ovr.Label = item.Label;
        ovr.PhysicalLeftMm = Math.Round(item.LeftMm, 2);
        ovr.PhysicalTopMm = Math.Round(item.TopMm, 2);

        bool matchesEdid =
            item.EdidWidthMm > 1 &&
            Math.Abs(item.WidthMm - item.EdidWidthMm) < 0.5 &&
            Math.Abs(item.HeightMm - item.EdidHeightMm) < 0.5;

        if (matchesEdid)
        {
            ovr.ClearSize();
            item.SizeFromEdid = true;
        }
        else
        {
            ovr.PhysicalWidthMm = Math.Round(item.WidthMm, 2);
            ovr.PhysicalHeightMm = Math.Round(item.HeightMm, 2);
            item.SizeFromEdid = false;
        }

        _engine.ApplyConfig(Config);
        _engine.RebuildLayout("layout edited");
        SchedulePersist();
    }

    private void OpenCalibration()
    {
        var window = new PhysicalCalibrationWindow(this)
        {
            Owner = Application.Current?.MainWindow,
        };
        window.ShowDialog();
        LayoutRefreshed?.Invoke();
    }

    private void RevertSelectedSize()
    {
        if (SelectedDisplay is not { } item || item.EdidWidthMm <= 1)
        {
            return;
        }

        item.WidthMm = item.EdidWidthMm;
        item.HeightMm = item.EdidHeightMm;
        CommitDisplay(item);
    }

    private void ResetLayoutToWindows()
    {
        LayoutProfile? profile = FindActiveProfile();
        if (profile is null)
        {
            return;
        }

        foreach (DisplayOverride ovr in profile.Displays)
        {
            ovr.ClearLocation();
        }

        _engine.ApplyConfig(Config);
        _engine.RebuildLayout("layout reset");
        SchedulePersist();
    }

    private LayoutProfile? FindActiveProfile()
    {
        string id = _engine.Status.ActiveProfileId;
        return string.IsNullOrEmpty(id)
            ? null
            : Config.Profiles.FirstOrDefault(p => p.Id == id);
    }

    private void OnEngineLayout(ZoneLayout layout)
    {
        void Rebuild()
        {
            foreach (DisplayItem existing in Displays)
            {
                existing.PropertyChanged -= OnDisplayItemChanged;
            }

            Displays.Clear();

            IReadOnlyList<DisplaySnapshot> snapshots = _engine.Displays;

            foreach (DisplayZone zone in layout.Zones)
            {
                DisplaySnapshot? snapshot = snapshots.FirstOrDefault(s => s.StableId == zone.StableId);

                var item = new DisplayItem
                {
                    StableId = zone.StableId,
                    Label = zone.DisplayName,
                    PixelLeft = zone.PixelBounds.Left,
                    PixelTop = zone.PixelBounds.Top,
                    PixelWidth = zone.PixelBounds.Width,
                    PixelHeight = zone.PixelBounds.Height,
                    IsPrimary = zone.IsPrimary,
                    ScaleFactor = zone.EffectiveDpi / 96.0,
                    EdidWidthMm = snapshot?.EdidWidthMm ?? 0,
                    EdidHeightMm = snapshot?.EdidHeightMm ?? 0,
                    LeftMm = zone.PhysicalBounds.Left,
                    TopMm = zone.PhysicalBounds.Top,
                    WidthMm = zone.PhysicalBounds.Width,
                    HeightMm = zone.PhysicalBounds.Height,
                };

                item.PropertyChanged += OnDisplayItemChanged;
                Displays.Add(item);
            }

            ApplyWallpapers();
            SelectedDisplay = Displays.FirstOrDefault(d => d.IsPrimary) ?? Displays.FirstOrDefault();
            Raise(nameof(IsUniformLayout));
            OpenCalibrationCommand.RaiseCanExecuteChanged();
            LayoutRefreshed?.Invoke();
        }

        Dispatch(Rebuild);
    }

    public void RefreshWallpapers()
    {
        ApplyWallpapers();
        LayoutRefreshed?.Invoke();
    }

    private void ApplyWallpapers()
    {
        IReadOnlyList<WallpaperSnapshot> snapshots = _wallpapers.Enumerate();
        foreach (DisplayItem display in Displays)
        {
            WallpaperSnapshot? match = snapshots
                .Select(w => new { Wallpaper = w, Area = IntersectionArea(display.PixelRect, w.PixelBounds) })
                .OrderByDescending(x => x.Area)
                .FirstOrDefault(x => x.Area > 0)?.Wallpaper;
            display.WallpaperBrush = match?.Brush;
        }
    }

    private static double IntersectionArea(RectD a, RectD b)
    {
        double width = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
        double height = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        return width * height;
    }

    public event Action? LayoutRefreshed;

    private void OnDisplayItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Position changes stream in while dragging and are committed on release by the editor;
        // a size change comes from a numeric field and is committed straight away.
        if (sender is DisplayItem item && e.PropertyName is nameof(DisplayItem.WidthMm) or nameof(DisplayItem.HeightMm))
        {
            CommitDisplay(item);
        }
    }

    // -------------------------------------------------------------------------------------------
    // Rules
    // -------------------------------------------------------------------------------------------

    private void AddRule()
    {
        var rule = new AppRule { Process = "example", Action = RuleAction.PassThrough };
        Config.Rules.Add(rule);
        Rules.Add(rule);
        SelectedRule = rule;
        _engine.ApplyConfig(Config);
        SchedulePersist();
    }

    private void RemoveSelectedRule()
    {
        if (SelectedRule is not { } rule)
        {
            return;
        }

        Config.Rules.Remove(rule);
        Rules.Remove(rule);
        SelectedRule = null;
        _engine.ApplyConfig(Config);
        SchedulePersist();
    }

    public void NotifyRuleEdited()
    {
        _engine.ApplyConfig(Config);
        SchedulePersist();
    }

    private void ExportSettings()
    {
        var dialog = new SaveFileDialog
        {
            Title = Loc.Get("settings.export"),
            Filter = Loc.Get("settings.fileFilter"),
            DefaultExt = ".json",
            AddExtension = true,
            FileName = $"CaYaScreenBridge-settings-{DateTime.Now:yyyyMMdd-HHmm}.json",
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            Persist();
            _store.Export(Config, dialog.FileName);
            MessageBox.Show(Loc.Get("settings.exportSuccess"), Loc.Get("app.name"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _log.Error("Config", $"Export failed: {ex.Message}");
            MessageBox.Show(
                Loc.Get("settings.exportFailed") + Environment.NewLine + ex.Message,
                Loc.Get("app.name"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ImportSettings()
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.Get("settings.import"),
            Filter = Loc.Get("settings.fileFilter"),
            DefaultExt = ".json",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            AppConfig imported = _store.Import(dialog.FileName);
            _engine.ApplyConfig(imported);
            _store.Save(imported);
            LoadFromConfig();
            RaiseAllSettings();
            _engine.RebuildLayout("settings imported");
            ApplyStartup();
            MessageBox.Show(Loc.Get("settings.importSuccess"), Loc.Get("app.name"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _log.Error("Config", $"Import failed: {ex.Message}");
            MessageBox.Show(
                Loc.Get("settings.importFailed") + Environment.NewLine + ex.Message,
                Loc.Get("app.name"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void RaiseAllSettings()
    {
        foreach (string property in new[]
        {
            nameof(MasterEnabled), nameof(AlignCursor), nameof(UseRawInputAssist), nameof(PreventCursorLoss),
            nameof(NormalisePointerSpeed), nameof(BorderResistanceMm), nameof(SpeedAdaptiveResistance),
            nameof(ResistanceSpeedReferenceMmPerSecond), nameof(WrapIndex), nameof(DragModeIndex),
            nameof(PreserveGrabPoint), nameof(SeamlessCrossDisplay), nameof(SkipDpiUnawareWindows),
            nameof(SkipMaximisedWindows), nameof(LiveThrottleMs), nameof(PauseInExclusiveFullScreen),
            nameof(CorrectInBorderlessFullScreen), nameof(DeepWindowsIntegration), nameof(PauseForAntiCheat),
            nameof(StartWithWindows), nameof(StartElevated), nameof(StartMinimised), nameof(ShowTrayIcon),
            nameof(RaiseHookTimeout), nameof(VerboseLogging), nameof(LanguageIndex),
        })
        {
            Raise(property);
        }
        RaiseSelectedResistanceProperties();
    }

    // -------------------------------------------------------------------------------------------
    // Diagnostics
    // -------------------------------------------------------------------------------------------

    public void StartLiveUpdates() => _liveTimer.Start();

    public void StopLiveUpdates() => _liveTimer.Stop();

    private void RefreshLive()
    {
        Vec2 physical = _engine.PhysicalPosition;
        string zone = _engine.CurrentZoneName ?? "-";
        LiveCursor = $"{physical.X:0.0} × {physical.Y:0.0} mm · {zone}";
        Status = _engine.Status;
    }

    private void OnLogEntry(LogEntry entry) => Dispatch(RefreshLogText);

    private void RefreshLogText()
    {
        IReadOnlyList<LogEntry> entries = _log.Snapshot();
        int skip = Math.Max(0, entries.Count - 400);
        LogText = string.Join(Environment.NewLine, entries.Skip(skip).Select(e => e.ToString()));
    }

    private void CopyLog()
    {
        try
        {
            Clipboard.SetText(_log.Render());
        }
        catch (Exception ex)
        {
            _log.Warn("Ui", $"Could not copy the log to the clipboard: {ex.Message}");
        }
    }

    private void OpenLogFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_store.Directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warn("Ui", $"Could not open the configuration folder: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------------------------------
    // Plumbing
    // -------------------------------------------------------------------------------------------

    private void LoadFromConfig()
    {
        _suppressPersist = true;

        Rules.Clear();
        foreach (AppRule rule in Config.Rules)
        {
            Rules.Add(rule);
        }

        _suppressPersist = false;
    }

    private void Update(Action mutate, Action? afterApply = null, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        mutate();
        Raise(propertyName);

        _engine.ApplyConfig(Config);
        afterApply?.Invoke();
        SchedulePersist();
    }

    private void ApplyStartup()
    {
        StartupStatus status = _startup.Apply(Config.General.StartWithWindows, Config.General.StartElevated);
        RefreshStartupSummary(status);
    }

    private void RepairStartup()
    {
        _startup.RepairIfNeeded(Config.General.StartWithWindows, Config.General.StartElevated);
        RefreshStartupSummary();
    }

    private void RefreshStartupSummary(StartupStatus? known = null)
    {
        StartupStatus status = known ?? _startup.GetStatus();

        string method = status.Method switch
        {
            StartupMethod.ScheduledTask => Loc.Get("startup.method.task"),
            StartupMethod.RunKey => Loc.Get("startup.method.run"),
            _ => Loc.Get("startup.method.none"),
        };

        StartupSummary = status.Detail is { Length: > 0 } detail ? $"{method} — {detail}" : method;
    }

    private void OnEngineStatus(EngineStatus status) => Dispatch(() =>
    {
        Status = status;
        Raise(nameof(IsUniformLayout));
    });

    public void SchedulePersist()
    {
        if (_suppressPersist)
        {
            return;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void Persist()
    {
        _saveTimer.Stop();
        _store.Save(Config);
    }

    private static void Dispatch(Action action)
    {
        Dispatcher? dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    public void Detach()
    {
        _engine.StatusChanged -= OnEngineStatus;
        _engine.LayoutChanged -= OnEngineLayout;
        _log.EntryWritten -= OnLogEntry;
        _liveTimer.Stop();
        Persist();
    }
}
