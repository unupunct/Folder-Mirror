using System.Diagnostics;
using System.IO;
using System.Reflection;
using FolderMirror.Core.Data;
using FolderMirror.Core.Engine;
using FolderMirror.Core.Models;
using FolderMirror.Core.Scheduling;
using Microsoft.Win32;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace FolderMirror.Services;

public static class AppInfo
{
    public const string Name = "Folder Mirror";
    public const string Tagline = "Your files. Always in sync.";
    public const string Publisher = "Folder Mirror";
    public const string RepoUrl = "https://github.com/unupunct/Folder-Mirror";
    public static string Version
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }
}

/// <summary>Application-wide singletons (simple manual composition root).</summary>
public static class Svc
{
    public static Database Db { get; private set; } = null!;
    public static ProfileRepository Profiles { get; private set; } = null!;
    public static HistoryRepository History { get; private set; } = null!;
    public static StateRepository State { get; private set; } = null!;
    public static SettingsRepository SettingsRepo { get; private set; } = null!;
    public static AppSettings Settings { get; set; } = new();
    public static SyncEngine Engine { get; private set; } = null!;
    public static TaskSchedulerService Scheduler { get; private set; } = null!;
    public static RunCoordinator Coordinator { get; private set; } = null!;
    public static LoggingLevelSwitch LevelSwitch { get; } = new(LogEventLevel.Information);
    public static ILogger Log { get; private set; } = Serilog.Core.Logger.None;

    public static void Init()
    {
        Db = new Database(AppPaths.DatabaseFile);
        Profiles = new ProfileRepository(Db);
        History = new HistoryRepository(Db);
        State = new StateRepository(Db);
        SettingsRepo = new SettingsRepository(Db);
        Settings = SettingsRepo.Load();
        ConfigureLogging();
        Engine = new SyncEngine(State, Log);
        Scheduler = new TaskSchedulerService();
        Coordinator = new RunCoordinator();
    }

    public static string LogFolder => string.IsNullOrWhiteSpace(Settings.LogFolder) ? AppPaths.DefaultLogFolder : Settings.LogFolder;

    public static void ConfigureLogging()
    {
        LevelSwitch.MinimumLevel = Settings.LogLevel switch
        {
            "Debug" => LogEventLevel.Debug, "Warning" => LogEventLevel.Warning, "Error" => LogEventLevel.Error, _ => LogEventLevel.Information
        };
        try
        {
            Directory.CreateDirectory(LogFolder);
            (Log as IDisposable)?.Dispose();
            Log = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(LevelSwitch)
                .WriteTo.File(Path.Combine(LogFolder, "foldermirror-.log"), rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: Math.Max(1, Settings.MaxLogSizeMb) * 1024L * 1024L, rollOnFileSizeLimit: true,
                    retainedFileCountLimit: 14, shared: true,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
        }
        catch { Log = Serilog.Core.Logger.None; }
        if (Engine != null) Engine = new SyncEngine(State, Log);
    }

    public static void SaveSettings() => SettingsRepo.Save(Settings);

    public static int ClearLogs()
    {
        int n = 0;
        try
        {
            Log.Information("Logs cleared by user");
            (Log as IDisposable)?.Dispose();
            foreach (var f in Directory.GetFiles(LogFolder, "*.log"))
            {
                try { File.Delete(f); n++; }
                catch { try { using var fs = new FileStream(f, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); fs.SetLength(0); n++; } catch { } }
            }
        }
        catch { }
        ConfigureLogging();
        return n;
    }

    public static DateTime? NextScheduledRun()
    {
        if (Settings.SchedulingPaused) return null;
        DateTime? best = null;
        foreach (var p in Profiles.GetAll())
        {
            var n = ScheduleCalculator.NextRun(p.Schedule, DateTime.Now);
            if (n != null && (best == null || n < best)) best = n;
        }
        return best;
    }
}

public static class StartupRegistration
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "FolderMirror";

    public static void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key, true) ?? Registry.CurrentUser.CreateSubKey(Key);
        if (enabled) k.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --minimized");
        else k.DeleteValue(ValueName, false);
    }

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key);
        return k?.GetValue(ValueName) != null;
    }
}
