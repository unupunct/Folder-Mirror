using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FolderMirror.Mac.Services;
using FolderMirror.Mac.ViewModels;

namespace FolderMirror.Mac;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            SettingsViewModel.ApplyTheme(Svc.Settings.DarkTheme);
            try { Svc.History.MarkInterrupted(); } catch { }
            var main = new MainWindow { DataContext = new MainViewModel() };
            desktop.MainWindow = main;
            var args = desktop.Args ?? Array.Empty<string>();
            var i = Array.IndexOf(args, "--screenshots");
            if (i >= 0 && i + 1 < args.Length)
                {
                var d = Array.IndexOf(args, "--demo-root");
                var demo = d >= 0 && d + 1 < args.Length ? args[d + 1] : null;
                main.Opened += async (_, _) => await ScreenshotTool.RunAsync(args[i + 1], main, desktop, demo);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
