using System.Diagnostics;
using System.Reflection;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FolderMirror.Core.Data;
using FolderMirror.Core.Engine;
using FolderMirror.Core.Models;
using FolderMirror.Core.Scheduling;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace FolderMirror.Mac.Services;

public static class AppInfo
{
    public const string Name = "Folder Mirror";
    public const string Tagline = "Your files. Always in sync.";
    public const string RepoUrl = "https://github.com/unupunct/Folder-Mirror";
    public static string Version
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 1, 0);
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }
}

/// <summary>Small OS helpers (the app is developed on Windows but ships on macOS).</summary>
public static class Platform
{
    public static void OpenPath(string path)
    {
        try
        {
            if (OperatingSystem.IsMacOS()) Process.Start("open", path);
            else if (OperatingSystem.IsWindows()) Process.Start("explorer.exe", path);
            else Process.Start("xdg-open", path);
        }
        catch { }
    }

    public static void OpenUrl(string url)
    {
        try
        {
            if (OperatingSystem.IsMacOS()) Process.Start("open", url);
            else Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }

    /// <summary>Shows a Notification Center banner (macOS only; silently ignored elsewhere).</summary>
    public static void Notify(string title, string message)
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var psi = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add($"display notification \"{Esc(message)}\" with title \"{Esc(title)}\"");
            Process.Start(psi);
        }
        catch { }
    }
}

public static class Svc
{
    public static Database Db { get; private set; } = null!;
    public static ProfileRepository Profiles { get; private set; } = null!;
    public static HistoryRepository History { get; private set; } = null!;
    public static StateRepository State { get; private set; } = null!;
    public static SettingsRepository SettingsRepo { get; private set; } = null!;
    public static AppSettings Settings { get; set; } = new();
    public static SyncEngine Engine { get; private set; } = null!;
    public static LaunchdService Scheduler { get; private set; } = null!;
    public static RunCoordinator Coordinator { get; private set; } = null!;
    public static LoggingLevelSwitch LevelSwitch { get; } = new(LogEventLevel.Information);
    public static Serilog.ILogger Log { get; private set; } = Serilog.Core.Logger.None;

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
        Scheduler = new LaunchdService();
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
            (Log as IDisposable)?.Dispose();
            foreach (var f in Directory.GetFiles(LogFolder, "*.log"))
            {
                try { File.Delete(f); n++; } catch { }
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

/// <summary>Runs one synchronization at a time, records history and exposes live progress to the UI.</summary>
public sealed partial class RunCoordinator : ObservableObject
{
    CancellationTokenSource? _cts;

    [ObservableProperty] bool _isRunning;
    [ObservableProperty] bool _isPlanning;
    [ObservableProperty] string _statusText = "Idle – ready";
    [ObservableProperty] string _currentFile = "";
    [ObservableProperty] double _fraction;
    [ObservableProperty] string _detailText = "";
    [ObservableProperty] bool _isIndeterminate;

    public bool IsBusy => IsRunning || IsPlanning;
    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(IsBusy));
    partial void OnIsPlanningChanged(bool value) => OnPropertyChanged(nameof(IsBusy));

    public event Action<SyncProfile, SyncResult>? RunFinished;

    public void Cancel() => _cts?.Cancel();

    static void OnUi(Action a) { if (Dispatcher.UIThread.CheckAccess()) a(); else Dispatcher.UIThread.Post(a); }

    public async Task<SyncPlan> PlanAsync(SyncProfile profile, CancellationToken ct = default)
    {
        IsPlanning = true; IsIndeterminate = true;
        StatusText = "Analyzing '" + profile.Name + "'…";
        try
        {
            return await Svc.Engine.PlanAsync(profile, Svc.Settings.MaxParallelOperations, s => OnUi(() => DetailText = s), ct);
        }
        finally
        {
            IsPlanning = false; IsIndeterminate = false;
            if (!IsRunning) { StatusText = "Idle – ready"; DetailText = ""; }
        }
    }

    public async Task<SyncResult> ExecuteAsync(SyncPlan plan, ExecuteOptions options, CancellationToken externalCt = default)
    {
        if (IsRunning) throw new InvalidOperationException("Another synchronization is already running.");
        var profile = plan.Profile;
        FileStream? gate = null;
        try
        {
            gate = new FileStream(Path.Combine(AppPaths.DataFolder, $"run-{profile.Id:N}.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            var skipped = new SyncResult { Status = SyncStatus.Skipped, StartUtc = DateTime.UtcNow, EndUtc = DateTime.UtcNow };
            skipped.Messages.Add("This profile is already being synchronized by another Folder Mirror process.");
            var sid = Svc.History.StartRun(profile, options.Trigger);
            Svc.History.FinishRun(sid, skipped);
            return skipped;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        IsRunning = true; IsIndeterminate = true;
        StatusText = "Synchronizing '" + profile.Name + "'…"; Fraction = 0; CurrentFile = ""; DetailText = "Starting…";
        var runId = Svc.History.StartRun(profile, options.Trigger);
        SyncResult result;
        var progress = new Progress<SyncProgress>(p =>
        {
            IsIndeterminate = p.BytesTotal == 0 && p.FilesTotal == 0;
            Fraction = p.Fraction * 100;
            CurrentFile = p.CurrentFile;
            var speed = p.BytesPerSecond > 0 ? $" · {Format.Bytes((long)p.BytesPerSecond)}/s" : "";
            var eta = p.Remaining is { } r ? $" · about {Format.Duration(r)} left" : "";
            DetailText = $"{p.FilesDone:N0} / {p.FilesTotal:N0} files · {Format.Bytes(p.BytesDone)} of {Format.Bytes(p.BytesTotal)}{speed}{eta}";
            StatusText = p.Phase == "Done" ? "Finishing…" : $"{p.Phase} '{profile.Name}'…";
        });
        try
        {
            result = await Task.Run(() => Svc.Engine.ExecuteAsync(plan, options, progress, _cts.Token));
        }
        catch (Exception ex) when (ex is SyncBlockedException or InvalidOperationException)
        {
            result = new SyncResult { Status = SyncStatus.Failed, StartUtc = DateTime.UtcNow, EndUtc = DateTime.UtcNow };
            result.Messages.Add(ex.Message);
            Svc.Log.Warning("Synchronization blocked: {Message}", ex.Message);
        }
        catch (Exception ex)
        {
            result = new SyncResult { Status = SyncStatus.Failed, StartUtc = DateTime.UtcNow, EndUtc = DateTime.UtcNow };
            result.Messages.Add(ErrorTranslator.Describe(ex));
            Svc.Log.Error(ex, "Unexpected failure");
        }
        try { Svc.History.FinishRun(runId, result); } catch (Exception ex) { Svc.Log.Error(ex, "Could not write history"); }
        _cts.Dispose(); _cts = null;
        gate.Dispose();
        IsRunning = false; IsIndeterminate = false; Fraction = 0; CurrentFile = "";
        StatusText = "Idle – ready"; DetailText = "";
        RunFinished?.Invoke(profile, result);
        return result;
    }

    public static string Describe(SyncResult r)
    {
        var s = r.Status switch
        {
            SyncStatus.Completed => "Synchronization completed",
            SyncStatus.CompletedWithErrors => "Completed with errors",
            SyncStatus.Cancelled => "Synchronization cancelled",
            SyncStatus.Skipped => "Synchronization skipped",
            _ => "Synchronization failed"
        };
        var tail = r.Messages.FirstOrDefault() ?? (r.Errors.Count > 0 ? r.Errors[0].Message : "");
        return $"{s}: {r.FilesCopied} new, {r.FilesModified} updated, {r.FilesDeleted} deleted, {r.Errors.Count} error(s). {tail}".Trim();
    }
}

/// <summary>Headless mode used by launchd: FolderMirror --run-profile {id} --background</summary>
public static class BackgroundRunner
{
    public static async Task<int> RunAsync(Guid profileId)
    {
        var log = Svc.Log;
        try
        {
            if (Svc.Settings.SchedulingPaused) { log.Information("Scheduled run skipped: scheduling is paused."); return 2; }
            var profile = Svc.Profiles.Get(profileId);
            if (profile == null) { log.Warning("Scheduled run: profile {Id} no longer exists.", profileId); Svc.Scheduler.Remove(profileId); return 3; }
            log.Information("Scheduled run starting for '{Name}'", profile.Name);

            var plan = await Svc.Engine.PlanAsync(profile, Svc.Settings.MaxParallelOperations, null, CancellationToken.None);
            var unavailable = plan.Issues.Where(i => i.Code is "SourceDriveUnavailable" or "DestinationDriveUnavailable" or "SourceMissing").ToList();
            if (unavailable.Count > 0 && profile.Schedule.OnlyWhenAvailable)
            {
                var skipped = new SyncResult { Status = SyncStatus.Skipped, StartUtc = DateTime.UtcNow, EndUtc = DateTime.UtcNow };
                skipped.Messages.Add("Skipped: " + string.Join(" ", unavailable.Select(u => u.Message)));
                Svc.History.FinishRun(Svc.History.StartRun(profile, "Scheduled"), skipped);
                return 2;
            }
            if (plan.HasBlockingIssues)
            {
                var failed = new SyncResult { Status = SyncStatus.Failed, StartUtc = DateTime.UtcNow, EndUtc = DateTime.UtcNow };
                failed.Messages.Add(string.Join(" ", plan.Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message)));
                Svc.History.FinishRun(Svc.History.StartRun(profile, "Scheduled"), failed);
                Notify(profile, failed);
                return 1;
            }
            var result = await Svc.Coordinator.ExecuteAsync(plan, new ExecuteOptions
            {
                Unattended = true, MaxParallel = Svc.Settings.MaxParallelOperations, Trigger = "Scheduled"
            });
            Notify(profile, result);
            return result.Status is SyncStatus.Completed ? 0 : result.Status == SyncStatus.Skipped ? 2 : 1;
        }
        catch (Exception ex) { log.Error(ex, "Scheduled run crashed"); return 1; }
    }

    static void Notify(SyncProfile p, SyncResult r)
    {
        if (!Svc.Settings.ShowNotifications) return;
        bool err = r.Status is SyncStatus.Failed or SyncStatus.CompletedWithErrors;
        Platform.Notify(err ? $"Folder Mirror – '{p.Name}' needs attention" : $"Folder Mirror – '{p.Name}' finished", RunCoordinator.Describe(r));
    }
}
