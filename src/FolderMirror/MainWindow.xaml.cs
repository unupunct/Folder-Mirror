using System.Windows;
using System.Windows.Threading;
using FolderMirror.Services;
using FolderMirror.ViewModels;

namespace FolderMirror;

public partial class MainWindow : Window
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };

    public MainWindow()
    {
        InitializeComponent();
        ThemeManager.Hook(this);
        VersionText.Text = "Version " + AppInfo.Version;
        // Keep drive availability / next-run information fresh while the dashboard is open.
        _timer.Tick += (_, _) =>
        {
            if (IsVisible && DataContext is MainViewModel vm && vm.CurrentPage == Page.Dashboard && !vm.Coordinator.IsBusy) vm.Dashboard.Refresh();
        };
        _timer.Start();
    }
}
