using System.IO;
using System.Windows;
using FolderMirror.Core.Models;
using FolderMirror.Services;
using FolderMirror.ViewModels;
using FolderMirror.Views;

namespace FolderMirror;

public partial class App : Application
{
    Mutex? _single;
    EventWaitHandle? _showEvent;
    TrayService? _tray;
    MainWindow? _main;
    bool _exiting;
    bool _trayHintShown;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        string? Arg(string name) { var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        bool Has(string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

        DispatcherUnhandledException += (_, ex) =>
        {
            try { Svc.Log.Error(ex.Exception, "Unhandled UI exception"); } catch { }
            Dialogs.Info("Something went wrong", "An unexpected error occurred, but your files were not affected.\n\n" + ex.Exception.Message + "\n\nDetails were written to the log.", true);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => { try { Svc.Log.Fatal(ex.ExceptionObject as Exception, "Fatal error"); } catch { } };

        try { Svc.Init(); }
        catch (Exception ex)
        {
            MessageBox.Show("Folder Mirror could not open its data folder:\n" + ex.Message, "Folder Mirror", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1); return;
        }
        Loc.Instance.Language = Svc.Settings.Language;
        ThemeManager.Apply(Svc.Settings.DarkTheme, Svc.Settings.AccentColor);

        // ---- Background (Task Scheduler) mode ----
        var runId = Arg("--run-profile");
        if (runId != null && Has("--background") && Guid.TryParse(runId, out var pid))
        {
            var code = await BackgroundRunner.RunAsync(pid);
            Shutdown(code);
            return;
        }

        // ---- Developer tool: capture screenshots with demo data ----
        var shotDir = Arg("--screenshots");
        if (shotDir != null)
        {
            StartMainWindow(show: true);
            await ScreenshotTool.RunAsync(shotDir, Arg("--demo-root") ?? Path.Combine(Path.GetTempPath(), "FolderMirrorDemo"), _main!);
            _exiting = true; Shutdown(0); return;
        }

        // ---- Normal interactive start (single instance) ----
        _single = new Mutex(true, @"Local\FolderMirror.SingleInstance", out var created);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\FolderMirror.Show");
        if (!created)
        {
            _showEvent.Set();
            Shutdown(0); return;
        }
        var wake = new Thread(() => { while (!_exiting) { if (_showEvent.WaitOne(1000)) Dispatcher.BeginInvoke(ShowMain); } }) { IsBackground = true };
        wake.Start();

        try { var n = Svc.History.MarkInterrupted(); if (n > 0) Svc.Log.Warning("{N} run(s) from a previous session were interrupted.", n); } catch { }

        bool minimized = Has("--minimized") || Svc.Settings.StartMinimized;
        StartMainWindow(show: !minimized);
    }

    void StartMainWindow(bool show)
    {
        _tray = new TrayService { Visible = true };
        _tray.OpenRequested += ShowMain;
        _tray.ExitRequested += () => Dispatcher.Invoke(ExitApp);
        _tray.StatusRequested += () => { ShowMain(); _tray.Notify("Folder Mirror", _tray.StatusSummary(), false); MainViewModel.Instance.Go(Page.Dashboard); };
        _tray.RunProfileRequested += p => Dispatcher.Invoke(async () => { ShowMain(); await MainViewModel.Instance.RunProfileAsync(p); });
        _tray.PauseChanged += () => Dispatcher.Invoke(() => MainViewModel.Instance.Schedules.Refresh());

        var vm = new MainViewModel();
        _main = new MainWindow { DataContext = vm };
        _main.Closing += (s, ev) =>
        {
            if (_exiting) return;
            if (Svc.Settings.MinimizeToTray)
            {
                ev.Cancel = true; _main!.Hide();
                if (!_trayHintShown) { _trayHintShown = true; _tray?.Notify("Folder Mirror is still running", "Use the tray icon to open it again or to exit. Scheduled tasks keep working.", false); }
            }
            else { ev.Cancel = true; ExitApp(); }
        };
        Svc.Coordinator.RunFinished += (p, r) =>
            Dispatcher.Invoke(() =>
            {
                if (!Svc.Settings.ShowNotifications) return;
                bool err = r.Status is SyncStatus.Failed or SyncStatus.CompletedWithErrors;
                _tray?.Notify(err ? $"'{p.Name}' needs attention" : $"'{p.Name}' finished", RunCoordinator.Describe(r), err);
            });
        Svc.Coordinator.PropertyChanged += (_, ev) =>
        {
            if (ev.PropertyName is nameof(RunCoordinator.StatusText) or nameof(RunCoordinator.IsRunning))
                Dispatcher.Invoke(() => _tray?.SetTooltip(Svc.Coordinator.IsRunning ? "Folder Mirror – " + Svc.Coordinator.StatusText : "Folder Mirror"));
        };
        if (show) _main.Show();
    }

    public void ShowMain()
    {
        if (_main == null) return;
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate(); _main.Topmost = true; _main.Topmost = false; _main.Focus();
    }

    void ExitApp()
    {
        if (Svc.Coordinator.IsRunning)
        {
            ShowMain();
            if (!Dialogs.Confirm("Synchronization in progress", "A synchronization is still running. Exiting now cancels it; files already copied stay in place and unfinished files are discarded.", "Cancel and exit", true)) return;
            Svc.Coordinator.Cancel();
        }
        _exiting = true;
        _tray?.Dispose();
        _main?.Close();
        try { _single?.ReleaseMutex(); } catch { }
        Shutdown(0);
    }
}
