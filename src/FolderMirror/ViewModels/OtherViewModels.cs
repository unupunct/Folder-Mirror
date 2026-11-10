using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderMirror.Core.Data;
using FolderMirror.Core.Models;
using FolderMirror.Core.Scheduling;
using FolderMirror.Services;
using FolderMirror.Views;
using Microsoft.Win32;

namespace FolderMirror.ViewModels;

// ---------- Schedule dialog ----------
public partial class ScheduleViewModel : ObservableObject
{
    public SyncProfile Profile { get; }
    public Action<bool>? CloseRequested { get; set; }
    public static ScheduleKind[] Kinds { get; } = { ScheduleKind.Every15Minutes, ScheduleKind.Every30Minutes, ScheduleKind.Hourly, ScheduleKind.Daily, ScheduleKind.Weekly, ScheduleKind.Custom };
    public static string KindText(ScheduleKind k) => k switch
    {
        ScheduleKind.Every15Minutes => "Every 15 minutes", ScheduleKind.Every30Minutes => "Every 30 minutes", ScheduleKind.Hourly => "Every hour",
        ScheduleKind.Daily => "Daily", ScheduleKind.Weekly => "Weekly", ScheduleKind.Custom => "Custom", _ => "None"
    };

    [ObservableProperty] bool _enabled;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowTime), nameof(ShowDays), nameof(ShowInterval))] ScheduleKind _kind;
    [ObservableProperty] string _time;
    [ObservableProperty] int _interval;
    [ObservableProperty] bool _mon, _tue, _wed, _thu, _fri, _sat, _sun;
    [ObservableProperty] bool _atStartup, _atLogon, _onlyWhenAvailable, _allowDeletions;
    [ObservableProperty] string _error = "";
    public bool ShowTime => Kind is ScheduleKind.Daily or ScheduleKind.Weekly or ScheduleKind.Custom;
    public bool ShowDays => Kind is ScheduleKind.Weekly or ScheduleKind.Custom;
    public bool ShowInterval => Kind == ScheduleKind.Custom;
    public bool ShowDeletions => Profile.Mode is SyncMode.Mirror or SyncMode.TwoWay;
    public string ProfileName => Profile.Name;

    public ScheduleViewModel(SyncProfile p)
    {
        Profile = p; var s = p.Schedule;
        _enabled = s.Enabled; _kind = s.Kind == ScheduleKind.None ? ScheduleKind.Daily : s.Kind; _time = s.TimeOfDay; _interval = s.CustomIntervalMinutes;
        _mon = s.DaysOfWeek.Contains(DayOfWeek.Monday); _tue = s.DaysOfWeek.Contains(DayOfWeek.Tuesday); _wed = s.DaysOfWeek.Contains(DayOfWeek.Wednesday);
        _thu = s.DaysOfWeek.Contains(DayOfWeek.Thursday); _fri = s.DaysOfWeek.Contains(DayOfWeek.Friday); _sat = s.DaysOfWeek.Contains(DayOfWeek.Saturday); _sun = s.DaysOfWeek.Contains(DayOfWeek.Sunday);
        _atStartup = s.RunAtWindowsStartup; _atLogon = s.RunAtUserLogon; _onlyWhenAvailable = s.OnlyWhenAvailable; _allowDeletions = p.AllowUnattendedDeletions;
    }

    [RelayCommand] void Cancel() => CloseRequested?.Invoke(false);

    [RelayCommand]
    void Save()
    {
        Error = "";
        if (!TimeSpan.TryParse(Time, out var t) || t < TimeSpan.Zero || t >= TimeSpan.FromDays(1)) { Error = "Enter the time as HH:mm, for example 20:30."; return; }
        var days = new List<DayOfWeek>();
        if (Mon) days.Add(DayOfWeek.Monday); if (Tue) days.Add(DayOfWeek.Tuesday); if (Wed) days.Add(DayOfWeek.Wednesday);
        if (Thu) days.Add(DayOfWeek.Thursday); if (Fri) days.Add(DayOfWeek.Friday); if (Sat) days.Add(DayOfWeek.Saturday); if (Sun) days.Add(DayOfWeek.Sunday);
        if (Enabled && ShowDays && days.Count == 0) { Error = "Select at least one day of the week."; return; }
        if (Enabled && Kind == ScheduleKind.Custom && (Interval < 1 || Interval > 1440)) { Error = "The interval must be between 1 and 1440 minutes."; return; }
        if (Enabled && !PathsOk()) return;

        var s = Profile.Schedule;
        s.Enabled = Enabled; s.Kind = Kind; s.TimeOfDay = t.ToString(@"hh\:mm"); s.CustomIntervalMinutes = Math.Max(1, Interval);
        s.DaysOfWeek = days.Count > 0 ? days : new() { DayOfWeek.Monday };
        s.RunAtWindowsStartup = AtStartup; s.RunAtUserLogon = AtLogon; s.OnlyWhenAvailable = OnlyWhenAvailable;
        s.AnchorLocal = DateTime.Now;
        Profile.AllowUnattendedDeletions = AllowDeletions;
        var live = Svc.Profiles.Get(Profile.Id);
        if (live != null) { live.Schedule = s; live.AllowUnattendedDeletions = AllowDeletions; Svc.Profiles.Save(live); }
        else Svc.Profiles.Save(Profile);
        var (ok, msg) = Svc.Scheduler.Apply(live ?? Profile);
        if (!ok) { Error = msg; return; }
        Svc.Log.Information("Schedule updated for {Name}: {Desc}", Profile.Name, ScheduleCalculator.Describe(s));
        CloseRequested?.Invoke(true);
    }

    bool PathsOk()
    {
        if (!(Kind != ScheduleKind.None || AtStartup || AtLogon)) { Error = "Choose when the profile should run."; return false; }
        return true;
    }
}

// ---------- Schedules page ----------
public partial class ScheduleRowViewModel : ObservableObject
{
    public SyncProfile Profile { get; }
    public string Name => Profile.Name;
    public string Description => Profile.Schedule.Enabled ? ScheduleCalculator.Describe(Profile.Schedule) : "Not scheduled";
    public string NextText { get; }
    public string Folders => $"{Profile.SourcePath}  →  {Profile.DestinationPath}";
    bool _enabled;
    bool _loading = true;
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value) && !_loading) Toggle(value);
        }
    }
    public ScheduleRowViewModel(SyncProfile p)
    {
        Profile = p; _enabled = p.Schedule.Enabled;
        var next = ScheduleCalculator.NextRun(p.Schedule, DateTime.Now);
        NextText = !p.Schedule.Enabled ? "—" : Svc.Settings.SchedulingPaused ? "Paused" : next?.ToString("g") ?? (p.Schedule.RunAtUserLogon || p.Schedule.RunAtWindowsStartup ? "At logon / startup" : "—");
        _loading = false;
    }

    void Toggle(bool on)
    {
        var live = Svc.Profiles.Get(Profile.Id);
        if (live == null) return;
        if (on && live.Schedule.Kind == ScheduleKind.None && !live.Schedule.RunAtUserLogon && !live.Schedule.RunAtWindowsStartup)
        {
            ProfileActions.EditSchedule(live);
            MainViewModel.Instance.Schedules.Refresh();
            return;
        }
        live.Schedule.Enabled = on;
        if (on) live.Schedule.AnchorLocal = DateTime.Now;
        Svc.Profiles.Save(live);
        var (ok, msg) = Svc.Scheduler.Apply(live);
        if (!ok) Dialogs.Info("Windows Task Scheduler", msg, true);
        MainViewModel.Instance.Schedules.Refresh();
    }

    [RelayCommand] void Edit() => ProfileActions.EditSchedule(Profile);
    [RelayCommand] Task RunNow() => MainViewModel.Instance.RunProfileAsync(Profile);
}

public partial class SchedulesViewModel : ObservableObject
{
    public ObservableCollection<ScheduleRowViewModel> Rows { get; } = new();
    [ObservableProperty] bool _paused;
    [ObservableProperty] bool _hasRows;
    bool _loading;

    public void Refresh()
    {
        _loading = true;
        Paused = Svc.Settings.SchedulingPaused;
        Rows.Clear();
        foreach (var p in Svc.Profiles.GetAll()) Rows.Add(new ScheduleRowViewModel(p));
        HasRows = Rows.Count > 0;
        _loading = false;
    }

    partial void OnPausedChanged(bool value)
    {
        if (_loading) return;
        Svc.Settings.SchedulingPaused = value; Svc.SaveSettings();
        Refresh();
    }
}

// ---------- History ----------
public partial class HistoryViewModel : ObservableObject
{
    public ObservableCollection<RunRecord> Runs { get; } = new();
    public ObservableCollection<OpRow> Ops { get; } = new();
    public string[] Statuses { get; } = { "All", "Completed", "CompletedWithErrors", "Failed", "Cancelled", "Skipped", "Interrupted" };
    public string[] Periods { get; } = { "All time", "Today", "Last 7 days", "Last 30 days" };
    [ObservableProperty] string _search = "";
    [ObservableProperty] string _status = "All";
    [ObservableProperty] string _period = "All time";
    [ObservableProperty] RunRecord? _selected;
    [ObservableProperty] string _opFilter = "";
    [ObservableProperty] string _countText = "";
    [ObservableProperty] bool _hasSelection;

    partial void OnSearchChanged(string value) => Refresh();
    partial void OnStatusChanged(string value) => Refresh();
    partial void OnPeriodChanged(string value) => Refresh();
    partial void OnOpFilterChanged(string value) => LoadOps();
    partial void OnSelectedChanged(RunRecord? value) { HasSelection = value != null; LoadOps(); }

    public void Refresh()
    {
        DateTime? from = Period switch { "Today" => DateTime.Today, "Last 7 days" => DateTime.Today.AddDays(-6), "Last 30 days" => DateTime.Today.AddDays(-29), _ => null };
        try
        {
            var id = Selected?.Id;
            var list = Svc.History.Query(Search, Status, from, null);
            Runs.Clear(); foreach (var r in list) Runs.Add(r);
            CountText = $"{list.Count:N0} run(s)";
            Selected = id != null ? Runs.FirstOrDefault(r => r.Id == id) : null;
        }
        catch (Exception ex) { Svc.Log.Error(ex, "History query failed"); }
    }

    void LoadOps()
    {
        Ops.Clear();
        if (Selected == null) return;
        foreach (var o in Svc.History.GetOps(Selected.Id).Where(o => OpFilter.Length == 0 || o.RelPath.Contains(OpFilter, StringComparison.OrdinalIgnoreCase) || o.Status.Contains(OpFilter, StringComparison.OrdinalIgnoreCase) || o.Action.Contains(OpFilter, StringComparison.OrdinalIgnoreCase)))
            Ops.Add(o);
    }

    [RelayCommand] void Reload() => Refresh();

    [RelayCommand]
    void ExportCsv()
    {
        if (Runs.Count == 0) { Dialogs.Info("Nothing to export", "There are no history entries matching the current filter."); return; }
        var dlg = new SaveFileDialog { Title = "Export history", Filter = "CSV file (*.csv)|*.csv", FileName = $"FolderMirror-history-{DateTime.Now:yyyyMMdd}.csv" };
        if (dlg.ShowDialog() != true) return;
        try { File.WriteAllText(dlg.FileName, HistoryRepository.ToCsv(Runs), new UTF8Encoding(true)); Dialogs.Info("Export complete", $"{Runs.Count} run(s) exported to:\n{dlg.FileName}"); }
        catch (Exception ex) { Dialogs.Info("Export failed", ex.Message, true); }
    }

    [RelayCommand]
    void ExportReport()
    {
        if (Selected == null) return;
        var dlg = new SaveFileDialog { Title = "Export run report", Filter = "CSV file (*.csv)|*.csv", FileName = $"FolderMirror-report-{Selected.Id}.csv" };
        if (dlg.ShowDialog() != true) return;
        var sb = new StringBuilder("Action,File,Size,Status,Message\n");
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        foreach (var o in Svc.History.GetOps(Selected.Id)) sb.AppendLine($"{Q(o.Action)},{Q(o.RelPath)},{o.Size},{Q(o.Status)},{Q(o.Message)}");
        try { File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true)); }
        catch (Exception ex) { Dialogs.Info("Export failed", ex.Message, true); }
    }

    [RelayCommand]
    void Clear()
    {
        if (!Dialogs.Confirm("Clear history?", "All recorded synchronization runs and their reports will be removed. This cannot be undone. Your files and profiles are not affected.", "Clear history", true)) return;
        Svc.History.Clear(); Selected = null; Refresh();
    }
}

// ---------- Settings ----------
public partial class SettingsViewModel : ObservableObject
{
    bool _loading = true;
    public string[] Accents => ThemeManager.Accents;
    public string[] Languages => Loc.Languages;
    public string[] LogLevels { get; } = { "Debug", "Information", "Warning", "Error" };
    public SyncMode[] Modes { get; } = Enum.GetValues<SyncMode>();
    public ComparisonMethod[] Comparisons { get; } = Enum.GetValues<ComparisonMethod>();
    public ConflictPolicy[] ConflictPolicies { get; } = Enum.GetValues<ConflictPolicy>();
    public DeletionBehavior[] DeletionBehaviors { get; } = Enum.GetValues<DeletionBehavior>();
    public int[] ParallelOptions { get; } = { 1, 2, 4, 8, 16 };
    public int[] LogSizes { get; } = { 1, 5, 10, 25, 50, 100 };

    [ObservableProperty] bool _startWithWindows, _minimizeToTray, _startMinimized, _confirmBeforeSync, _checkAvailability, _showNotifications;
    [ObservableProperty] ComparisonMethod _defaultComparison;
    [ObservableProperty] SyncMode _defaultMode;
    [ObservableProperty] ConflictPolicy _defaultConflict;
    [ObservableProperty] DeletionBehavior _defaultDeletion;
    [ObservableProperty] int _maxParallel;
    [ObservableProperty] bool _verifyCopies;
    [ObservableProperty] bool _darkTheme;
    [ObservableProperty] string _accent = "Green";
    [ObservableProperty] string _language = "English";
    [ObservableProperty] string _logLevel = "Information";
    [ObservableProperty] string _logFolder = "";
    [ObservableProperty] int _maxLogSize;
    [ObservableProperty] string _message = "";

    public SettingsViewModel()
    {
        var s = Svc.Settings;
        _startWithWindows = StartupRegistration.IsEnabled(); _minimizeToTray = s.MinimizeToTray; _startMinimized = s.StartMinimized;
        _confirmBeforeSync = s.ConfirmBeforeSync; _checkAvailability = s.CheckDestinationAvailability; _showNotifications = s.ShowNotifications;
        _defaultComparison = s.DefaultComparison; _defaultMode = s.DefaultMode; _defaultConflict = s.DefaultConflictPolicy; _defaultDeletion = s.DefaultDeletionBehavior;
        _maxParallel = s.MaxParallelOperations; _verifyCopies = s.VerifyCopies; _darkTheme = s.DarkTheme; _accent = s.AccentColor; _language = s.Language;
        _logLevel = s.LogLevel; _logFolder = Svc.LogFolder; _maxLogSize = s.MaxLogSizeMb;
        _loading = false;
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName == nameof(Message)) return;
        Persist(e.PropertyName!);
    }

    void Persist(string prop)
    {
        var s = Svc.Settings;
        s.MinimizeToTray = MinimizeToTray; s.StartMinimized = StartMinimized; s.ConfirmBeforeSync = ConfirmBeforeSync;
        s.CheckDestinationAvailability = CheckAvailability; s.ShowNotifications = ShowNotifications;
        s.DefaultComparison = DefaultComparison; s.DefaultMode = DefaultMode; s.DefaultConflictPolicy = DefaultConflict; s.DefaultDeletionBehavior = DefaultDeletion;
        s.MaxParallelOperations = MaxParallel; s.VerifyCopies = VerifyCopies; s.DarkTheme = DarkTheme; s.AccentColor = Accent; s.Language = Language;
        s.LogLevel = LogLevel; s.MaxLogSizeMb = MaxLogSize;
        s.LogFolder = string.Equals(LogFolder, AppPaths.DefaultLogFolder, StringComparison.OrdinalIgnoreCase) ? "" : LogFolder;
        try
        {
            switch (prop)
            {
                case nameof(StartWithWindows): StartupRegistration.Set(StartWithWindows); break;
                case nameof(DarkTheme): case nameof(Accent): ThemeManager.Apply(DarkTheme, Accent); break;
                case nameof(Language): Loc.Instance.Language = Language; break;
                case nameof(LogLevel): case nameof(MaxLogSize): case nameof(LogFolder): Svc.ConfigureLogging(); break;
            }
            Svc.SaveSettings();
            Message = "";
        }
        catch (Exception ex) { Message = "Could not apply the setting: " + ex.Message; }
    }

    [RelayCommand]
    void BrowseLogFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Select the log folder" };
        if (dlg.ShowDialog() == true) LogFolder = dlg.FolderName;
    }

    [RelayCommand] void OpenLogFolder() { try { Directory.CreateDirectory(Svc.LogFolder); Process.Start("explorer.exe", Svc.LogFolder); } catch { } }

    [RelayCommand]
    void ClearLogs()
    {
        if (!Dialogs.Confirm("Clear log files?", "All diagnostic log files will be deleted.", "Clear logs", true)) return;
        var n = Svc.ClearLogs();
        Message = $"{n} log file(s) cleared.";
    }
}

// ---------- About ----------
public partial class AboutViewModel : ObservableObject
{
    public string Name => AppInfo.Name;
    public string Tagline => AppInfo.Tagline;
    public string Version => "Version " + AppInfo.Version;
    public string Runtime => RuntimeInformation.FrameworkDescription + " · " + RuntimeInformation.OSDescription;
    public string DataFolder => AppPaths.DataFolder;
    public string LogFolder => Svc.LogFolder;
    public string Copyright => "© 2026 Folder Mirror · MIT License";
    public string Description =>
        "Folder Mirror keeps folders in sync across local drives, USB drives and network shares. Preview every change before it happens, " +
        "choose between mirror, one-way, two-way and incremental backup modes, and let Windows Task Scheduler run your profiles in the background. " +
        "Deleted and overwritten files go to a dated archive by default, and conflicts are never resolved silently.";

    [RelayCommand] void OpenData() { try { Process.Start("explorer.exe", DataFolder); } catch { } }
    [RelayCommand] void OpenLogs() { try { Directory.CreateDirectory(LogFolder); Process.Start("explorer.exe", LogFolder); } catch { } }
    [RelayCommand] void OpenRepo() { try { Process.Start(new ProcessStartInfo(AppInfo.RepoUrl) { UseShellExecute = true }); } catch { } }
}
