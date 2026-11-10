using Avalonia;
using FolderMirror.Mac.Services;

namespace FolderMirror.Mac;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try { Svc.Init(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Folder Mirror could not open its data folder: " + ex.Message);
            return 1;
        }

        // Headless mode used by launchd schedules.
        var i = Array.FindIndex(args, a => a.Equals("--run-profile", StringComparison.OrdinalIgnoreCase));
        if (i >= 0 && i + 1 < args.Length && args.Contains("--background") && Guid.TryParse(args[i + 1], out var id))
            return BackgroundRunner.RunAsync(id).GetAwaiter().GetResult();

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        // FOLDERMIRROR_TRACE=1 prints UI binding/XAML warnings to stderr (diagnostics only).
        if (Environment.GetEnvironmentVariable("FOLDERMIRROR_TRACE") == "1")
            System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(Console.Error));
        return AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace(Avalonia.Logging.LogEventLevel.Warning);
    }
}
