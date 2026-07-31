using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CaYaScreenBridge.Windows.Ui.Controls;

namespace CaYaScreenBridge.Windows.Ui;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        InitializeComponent();

        VersionLabel.Text = "v" + (Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "1.0.0");

        Editor.SelectionChanged += item => _viewModel.SelectedDisplay = item;
        Editor.ItemMoved += item => _viewModel.CommitDisplay(item);
        _viewModel.LayoutRefreshed += SyncEditor;

        SyncEditor();
    }

    /// <summary>
    /// Closing the settings window leaves the engine running in the tray. Exiting for real happens
    /// from the tray menu, which is what a background utility should do.
    /// </summary>
    public bool AllowRealClose { get; set; }

    private void SyncEditor()
    {
        Editor.SetItems(_viewModel.Displays.ToList());
        Editor.Refresh();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _viewModel.StartLiveUpdates();
        _viewModel.RefreshWallpapers();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        _viewModel.StopLiveUpdates();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _viewModel.Persist();

        if (!AllowRealClose)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    private void OnMinimise(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximise(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnSnapChanged(object sender, RoutedEventArgs e) =>
        Editor.SnapEnabled = SnapToggle.IsChecked == true;

    /// <summary>
    /// Rules are plain objects rather than observable ones, so the edit is picked up when focus
    /// leaves the row. That is also the natural moment to push the change to the engine.
    /// </summary>
    private void OnRuleLostFocus(object sender, KeyboardFocusChangedEventArgs e) => _viewModel.NotifyRuleEdited();

    /// <summary>Keeps a display edited through the numeric fields in sync with the editor drawing.</summary>
    private void OnDisplayFieldLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_viewModel.SelectedDisplay is { } item)
        {
            _viewModel.CommitDisplay(item);
            Editor.Refresh();
        }
    }

    private void OnDisplayFieldSourceUpdated(object sender, System.Windows.Data.DataTransferEventArgs e)
    {
        if (_viewModel.SelectedDisplay is { } item)
        {
            _viewModel.CommitDisplay(item);
            Editor.Refresh();
        }
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source is TabControl)
        {
            Editor.Refresh();
        }
    }
}
