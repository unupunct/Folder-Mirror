using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderMirror.Core.Data;
using FolderMirror.Core.Engine;
using FolderMirror.Core.Models;
using FolderMirror.Core.Scheduling;
using FolderMirror.Mac.Services;

namespace FolderMirror.Mac.ViewModels;

public interface IRefreshable { void Refresh(); }

public sealed record NavItem(string Key, string Title);

public sealed class Option<T>
{
    public T Value { get; }
    public string Text { get; }
    public Option(T value, string text) { Value = value; Text = text; }
    public override string ToString() => Text;
}

public partial class MainViewModel : ObservableObject
{
    public ObservableCollection<NavItem> Items { get; } = new()
    {
        new("dashboard", "Dashboard"), new("profiles", "Profiles"), new("sync", "Synchronize"),
        new("schedules", "Scheduled Tasks"), new("history", "History"), new("settings", "Settings")
    };

    public DashboardViewModel Dashboard { get; }
    public ProfilesViewModel Profiles { get; }
    public SyncViewModel Sync { get; }
    public SchedulesViewModel Schedules { get; }
    public HistoryViewModel History { get; }
    public SettingsViewModel Settings { get; }
    public RunCoordinator Coordinator => Svc.Coordinator;

    [ObservableProperty] NavItem? _selected;
    [ObservableProperty] object? _current;

    public MainViewModel()
    {
        Dashboard = new DashboardViewModel(this);
        Profiles = new ProfilesViewModel(this);
        Sync = new SyncViewModel(this);
        Schedules = new SchedulesViewModel(this);
        History = new HistoryViewModel();
        Settings = new SettingsViewModel();
        Selected = Items[0];
        Svc.Coordinator.RunFinished += (_, _) => (Current as IRefreshable)?.Refresh();
    }

    partial void OnSelectedChanged(NavItem? value)
    {
        Current = value?.Key switch
        {
            "profiles" => Profiles, "sync" => Sync, "schedules" => Schedules,
            "history" => History, "settings" => Settings, _ => Dashboard
        };
        (Current as IRefreshable)?.Refresh();
    }

    public void GoTo(string key) => Selected = Items.First(i => i.Key == key);
}

/// <summary>One profile as shown on cards.</summary>
public sealed class ProfileCard
{
    public SyncProfile Profile { get; }
    public ICommand SyncCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand DuplicateCommand { get; }
    public ICommand DeleteCommand { get; }

    public ProfileCard(SyncProfile p, Func<ProfileCard, Task>? sync = null, Func<ProfileCard, Task>? edit = null,
                       Func<ProfileCard, Task>? duplicate = null, Func<ProfileCard, Task>? delete = null)
    {
        Profile = p;
        SyncCommand = new AsyncRelayCommand(() => sync?.Invoke(this) ?? Task.CompletedTask);
        EditCommand = new AsyncRelayCommand(() => edit?.Invoke(this) ?? Task.CompletedTask);
        DuplicateCommand = new AsyncRelayCommand(() => duplicate?.Invoke(this) ?? Task.CompletedTask);
        DeleteCommand = new AsyncRelayCommand(() => delete?.Invoke(this) ?? Task.CompletedTask);
        var last = Svc.History.LastSyncFor(p.Id);
        LastText = last == null ? "Never synchronized" : "Last: " + last.Value.ToString("g");
        var next = ScheduleCalculator.NextRun(p.Schedule, DateTime.Now);
        NextText = next == null ? "" : "Next: " + next.Value.ToString("g");
    }
    public string Name => Profile.Name;
    public string Route => $"{Profile.SourcePath}  →  {Profile.DestinationPath}";
    public string ModeText => ModeInfo.Title(Profile.Mode);
    public string ScheduleText => ScheduleCalculator.Describe(Profile.Schedule).Replace("at Windows start", "at login");
    public string LastText { get; }
    public string NextText { get; }
}

public partial class DashboardViewModel : ObservableObject, IRefreshable
{
    readonly MainViewModel _main;
    public DashboardViewModel(MainViewModel main) { _main = main; }

    [ObservableProperty] string _profileCount = "0";
    [ObservableProperty] string _filesSynced = "0";
    [ObservableProperty] string _lastSync = "—";
    [ObservableProperty] string _nextRun = "—";
    public ObservableCollection<ProfileCard> Cards { get; } = new();
    public ObservableCollection<RunRecord> Recent { get; } = new();
    public bool HasProfiles => Cards.Count > 0;
    public bool HasRecent => Recent.Count > 0;

    public void Refresh()
    {
        var s = Svc.History.Stats();
        ProfileCount = Svc.Profiles.GetAll().Count.ToString("N0");
        FilesSynced = s.FilesSynchronized.ToString("N0");
        LastSync = s.LastSyncLocal?.ToString("g") ?? "—";
        NextRun = Svc.NextScheduledRun()?.ToString("g") ?? (Svc.Settings.SchedulingPaused ? "Paused" : "—");
        Cards.Clear(); foreach (var p in Svc.Profiles.GetAll().Take(6)) Cards.Add(new ProfileCard(p, c => { _main.Sync.Open(c.Profile); return Task.CompletedTask; }));
        Recent.Clear(); foreach (var r in Svc.History.Query(limit: 6)) Recent.Add(r);
        OnPropertyChanged(nameof(HasProfiles)); OnPropertyChanged(nameof(HasRecent));
    }

    [RelayCommand] void NewProfile() { _main.GoTo("profiles"); _main.Profiles.NewProfileCommand.Execute(null); }
}

public partial class ProfilesViewModel : ObservableObject, IRefreshable
{
    readonly MainViewModel _main;
    public ProfilesViewModel(MainViewModel main) { _main = main; }
    public ObservableCollection<ProfileCard> Cards { get; } = new();
    public bool IsEmpty => Cards.Count == 0;

    public void Refresh()
    {
        Cards.Clear(); foreach (var p in Svc.Profiles.GetAll()) Cards.Add(Card(p));
        OnPropertyChanged(nameof(IsEmpty));
    }

    public ProfileCard Card(SyncProfile p) => new(p,
        c => { _main.Sync.Open(c.Profile); return Task.CompletedTask; }, Edit, Duplicate, Delete);

    [RelayCommand]
    async Task NewProfile()
    {
        var p = new SyncProfile();
        Svc.Settings.ApplyDefaultsTo(p);
        await OpenEditor(p, true);
    }

    public async Task Edit(ProfileCard c) => await OpenEditor(c.Profile.Clone(), false);

    async Task Duplicate(ProfileCard c)
    {
        var p = c.Profile.Clone();
        p.Id = Guid.NewGuid(); p.Name += " (copy)"; p.Schedule = new ScheduleSettings();
        Svc.Profiles.Save(p); Refresh();
        await Task.CompletedTask;
    }

    async Task Delete(ProfileCard c)
    {
        if (!await Dialogs.Confirm("Delete profile?", $"'{c.Name}' will be removed together with its schedule and stored two-way state. Your files in the source and destination folders are not touched. History entries are kept.", "Delete profile", true)) return;
        Svc.Scheduler.Remove(c.Profile.Id);
        Svc.Profiles.Delete(c.Profile.Id);
        Svc.State.Clear(c.Profile.Id);
        Refresh();
    }

    [RelayCommand]
    async Task Export()
    {
        var list = Svc.Profiles.GetAll();
        if (list.Count == 0) { await Dialogs.Info("Nothing to export", "There are no profiles to export yet."); return; }
        var path = await Dialogs.PickSaveFile("Export profiles", "FolderMirror-profiles.json", "Folder Mirror profiles", "*.json");
        if (path == null) return;
        try { File.WriteAllText(path, SyncProfile.ProfilesToJson(list)); await Dialogs.Info("Export complete", $"{list.Count} profile(s) exported to:\n{path}"); }
        catch (Exception ex) { await Dialogs.Info("Export failed", ex.Message, true); }
    }

    [RelayCommand]
    async Task Import()
    {
        var path = await Dialogs.PickOpenFile("Import profiles", "Folder Mirror profiles", "*.json");
        if (path == null) return;
        try
        {
            var list = SyncProfile.ProfilesFromJson(File.ReadAllText(path));
            if (list.Count == 0) { await Dialogs.Info("Nothing imported", "The file does not contain any profiles.", true); return; }
            foreach (var p in list)
            {
                p.Id = Guid.NewGuid(); p.Schedule = new ScheduleSettings();
                Svc.Profiles.Save(p);
            }
            Refresh();
            await Dialogs.Info("Import complete", $"{list.Count} profile(s) imported. Schedules were left disabled; review them under Scheduled Tasks.");
        }
        catch (Exception ex) { await Dialogs.Info("Import failed", "The file could not be read as a Folder Mirror profile file.\n" + ex.Message, true); }
    }

    async Task OpenEditor(SyncProfile p, bool isNew)
    {
        var vm = new ProfileEditorViewModel(p, isNew);
        var win = new Views.ProfileEditorWindow { DataContext = vm };
        var owner = Dialogs.Owner;
        if (owner == null) return;
        var saved = await win.ShowDialog<bool>(owner);
        if (saved) { Refresh(); if (!string.IsNullOrEmpty(vm.ScheduleWarning)) await Dialogs.Info("Schedule", vm.ScheduleWarning, true); }
    }
}

public sealed class DayToggle : ObservableObject
{
    public DayOfWeek Day { get; }
    public string Label { get; }
    bool _checked;
    public bool IsChecked { get => _checked; set => SetProperty(ref _checked, value); }
    public DayToggle(DayOfWeek d, bool on) { Day = d; Label = d.ToString()[..3]; _checked = on; }
}

public partial class ProfileEditorViewModel : ObservableObject
{
    readonly SyncProfile _p;
    public bool IsNew { get; }
    public string Heading => IsNew ? "New profile" : "Edit profile";

    public ProfileEditorViewModel(SyncProfile p, bool isNew)
    {
        _p = p; IsNew = isNew;
        _name = p.Name; _source = p.SourcePath; _destination = p.DestinationPath;
        _mode = Modes.First(m => m.Value == p.Mode);
        _conflict = Conflicts.First(c => c.Value == p.ConflictPolicy);
        _deletion = Deletions.First(d => d.Value == p.DeletionBehavior);
        var cmp = !p.CompareSize && p.CompareModifiedDate ? ComparisonMethod.DateOnly
            : p.CompareHash ? ComparisonMethod.Sha256
            : p.CompareSize && !p.CompareModifiedDate ? ComparisonMethod.SizeOnly : ComparisonMethod.SizeAndDate;
        _comparison = Comparisons.First(c => c.Value == cmp);
        _subdirs = p.IncludeSubdirectories; _ignoreHidden = p.IgnoreHidden; _ignoreTemp = p.IgnoreTemporary;
        _ignoreEmpty = p.IgnoreEmptyFolders; _verify = p.VerifyAfterCopy; _keepOverwritten = p.BackupOverwrittenFiles;
        _unattendedDeletes = p.AllowUnattendedDeletions;
        _excludedExt = string.Join(", ", p.ExcludedExtensions);
        _excludedFolders = string.Join(", ", p.ExcludedFolders);
        var s = p.Schedule;
        _scheduleChoice = ScheduleKinds.First(k => k.Value == s.Kind);
        _time = s.TimeOfDay; _interval = s.CustomIntervalMinutes.ToString();
        _atLogin = s.RunAtUserLogon || s.RunAtWindowsStartup; _onlyWhenAvailable = s.OnlyWhenAvailable;
        foreach (var d in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday })
            Days.Add(new DayToggle(d, s.DaysOfWeek.Contains(d)));
    }

    public static IReadOnlyList<Option<SyncMode>> Modes { get; } = Enum.GetValues<SyncMode>().Select(m => new Option<SyncMode>(m, ModeInfo.Title(m))).ToList();
    public static IReadOnlyList<Option<ConflictPolicy>> Conflicts { get; } = new List<Option<ConflictPolicy>>
    {
        new(ConflictPolicy.Skip, "Skip and report (safest)"), new(ConflictPolicy.KeepBoth, "Keep both versions"),
        new(ConflictPolicy.PreferSource, "Prefer source"), new(ConflictPolicy.PreferDestination, "Prefer destination")
    };
    public static IReadOnlyList<Option<DeletionBehavior>> Deletions { get; } = new List<Option<DeletionBehavior>>
    {
        new(DeletionBehavior.MoveToArchive, "Move to archive (recoverable)"), new(DeletionBehavior.DeletePermanently, "Delete permanently")
    };
    public static IReadOnlyList<Option<ComparisonMethod>> Comparisons { get; } = new List<Option<ComparisonMethod>>
    {
        new(ComparisonMethod.SizeAndDate, "Size and modified date"), new(ComparisonMethod.SizeOnly, "Size only"),
        new(ComparisonMethod.DateOnly, "Modified date only"), new(ComparisonMethod.Sha256, "Content (SHA-256, slower)")
    };
    public static IReadOnlyList<Option<ScheduleKind>> ScheduleKinds { get; } = new List<Option<ScheduleKind>>
    {
        new(ScheduleKind.None, "No schedule"), new(ScheduleKind.Every15Minutes, "Every 15 minutes"), new(ScheduleKind.Every30Minutes, "Every 30 minutes"),
        new(ScheduleKind.Hourly, "Every hour"), new(ScheduleKind.Daily, "Daily"), new(ScheduleKind.Weekly, "Weekly"), new(ScheduleKind.Custom, "Custom interval")
    };

    [ObservableProperty] string _name;
    [ObservableProperty] string _source;
    [ObservableProperty] string _destination;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ModeDescription)), NotifyPropertyChangedFor(nameof(ShowConflict))] Option<SyncMode> _mode;
    [ObservableProperty] Option<ConflictPolicy> _conflict;
    [ObservableProperty] Option<DeletionBehavior> _deletion;
    [ObservableProperty] Option<ComparisonMethod> _comparison;
    [ObservableProperty] bool _subdirs;
    [ObservableProperty] bool _ignoreHidden;
    [ObservableProperty] bool _ignoreTemp;
    [ObservableProperty] bool _ignoreEmpty;
    [ObservableProperty] bool _verify;
    [ObservableProperty] bool _keepOverwritten;
    [ObservableProperty] bool _unattendedDeletes;
    [ObservableProperty] string _excludedExt;
    [ObservableProperty] string _excludedFolders;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowTime)), NotifyPropertyChangedFor(nameof(ShowDays)), NotifyPropertyChangedFor(nameof(ShowInterval))] Option<ScheduleKind> _scheduleChoice;
    [ObservableProperty] string _time;
    [ObservableProperty] string _interval;
    [ObservableProperty] bool _atLogin;
    [ObservableProperty] bool _onlyWhenAvailable;
    [ObservableProperty] string _error = "";
    public string ScheduleWarning { get; private set; } = "";
    public ObservableCollection<DayToggle> Days { get; } = new();

    public string ModeDescription => ModeInfo.Description(Mode.Value);
    public bool ShowConflict => Mode.Value == SyncMode.TwoWay;
    public bool ShowTime => ScheduleChoice.Value is ScheduleKind.Daily or ScheduleKind.Weekly or ScheduleKind.Custom;
    public bool ShowDays => ScheduleChoice.Value is ScheduleKind.Weekly or ScheduleKind.Custom;
    public bool ShowInterval => ScheduleChoice.Value == ScheduleKind.Custom;

    [RelayCommand] async Task BrowseSource() { var f = await Dialogs.PickFolder("Choose the source folder"); if (f != null) Source = f; }
    [RelayCommand] async Task BrowseDestination() { var f = await Dialogs.PickFolder("Choose the destination folder"); if (f != null) Destination = f; }

    /// <summary>Validates and stores the profile. Returns false (with <see cref="Error"/> set) when it cannot be saved.</summary>
    public bool TrySave()
    {
        Error = "";
        if (string.IsNullOrWhiteSpace(Name)) { Error = "Give the profile a name."; return false; }
        if (string.IsNullOrWhiteSpace(Source)) { Error = "Choose a source folder."; return false; }
        if (string.IsNullOrWhiteSpace(Destination)) { Error = "Choose a destination folder."; return false; }
        if (!TimeSpan.TryParse(Time, out _)) { Error = "Enter the time as HH:MM, for example 20:00."; return false; }
        if (!int.TryParse(Interval, out var minutes) || minutes < 1) { Error = "The interval must be a whole number of minutes."; return false; }

        var p = _p;
        p.Name = Name.Trim(); p.SourcePath = PathTools.Normalize(Source); p.DestinationPath = PathTools.Normalize(Destination);
        p.Mode = Mode.Value; p.ConflictPolicy = Conflict.Value; p.DeletionBehavior = Deletion.Value;
        p.IncludeSubdirectories = Subdirs; p.IgnoreHidden = IgnoreHidden; p.IgnoreTemporary = IgnoreTemp; p.IgnoreEmptyFolders = IgnoreEmpty;
        p.VerifyAfterCopy = Verify; p.BackupOverwrittenFiles = KeepOverwritten; p.AllowUnattendedDeletions = UnattendedDeletes;
        p.ExcludedExtensions = ExcludedExt.Split(',', ';').ToList();
        p.ExcludedFolders = ExcludedFolders.Split(',', ';').ToList();
        p.NormalizeLists();
        switch (Comparison.Value)
        {
            case ComparisonMethod.SizeAndDate: p.CompareSize = true; p.CompareModifiedDate = true; p.CompareHash = false; break;
            case ComparisonMethod.SizeOnly: p.CompareSize = true; p.CompareModifiedDate = false; p.CompareHash = false; break;
            case ComparisonMethod.DateOnly: p.CompareSize = false; p.CompareModifiedDate = true; p.CompareHash = false; break;
            default: p.CompareSize = true; p.CompareModifiedDate = false; p.CompareHash = true; break;
        }

        var errors = SafetyChecker.CheckPaths(p).Where(i => i.Severity == IssueSeverity.Error && i.Code is not ("SourceMissing" or "SourceDriveUnavailable" or "DestinationDriveUnavailable")).ToList();
        if (errors.Count > 0) { Error = string.Join("\n", errors.Select(e => e.Message)); return false; }

        var s = p.Schedule;
        var changed = s.Kind != ScheduleChoice.Value || s.TimeOfDay != Time || s.CustomIntervalMinutes != minutes;
        s.Kind = ScheduleChoice.Value; s.TimeOfDay = TimeSpan.Parse(Time).ToString(@"hh\:mm"); s.CustomIntervalMinutes = minutes;
        s.DaysOfWeek = Days.Where(d => d.IsChecked).Select(d => d.Day).ToList();
        if (s.DaysOfWeek.Count == 0) s.DaysOfWeek = new List<DayOfWeek> { DayOfWeek.Monday };
        s.RunAtUserLogon = AtLogin; s.RunAtWindowsStartup = false; s.OnlyWhenAvailable = OnlyWhenAvailable;
        s.Enabled = s.Kind != ScheduleKind.None || AtLogin;
        if (changed) s.AnchorLocal = DateTime.Now;

        Svc.Profiles.Save(p);
        var (ok, msg) = Svc.Scheduler.Apply(p);
        if (!ok) ScheduleWarning = "The profile was saved, but its schedule could not be registered.\n" + msg;
        return true;
    }
}

public partial class SchedulesViewModel : ObservableObject, IRefreshable
{
    readonly MainViewModel _main;
    public SchedulesViewModel(MainViewModel main) { _main = main; }
    public ObservableCollection<ProfileCard> Cards { get; } = new();
    public bool IsEmpty => Cards.Count == 0;
    public bool Paused
    {
        get => Svc.Settings.SchedulingPaused;
        set { Svc.Settings.SchedulingPaused = value; Svc.SaveSettings(); OnPropertyChanged(); OnPropertyChanged(nameof(PausedText)); }
    }
    public string PausedText => Paused ? "Scheduling is paused. Scheduled runs are skipped until you resume." : "Scheduled runs happen in the background through macOS launchd, even when Folder Mirror is closed.";

    public void Refresh()
    {
        Cards.Clear(); foreach (var p in Svc.Profiles.GetAll()) Cards.Add(_main.Profiles.Card(p));
        OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(Paused)); OnPropertyChanged(nameof(PausedText));
    }
}

public sealed class IssueRow
{
    public string Text { get; init; } = "";
    public IBrush Brush { get; init; } = Brushes.Gray;
}

public sealed class PlanRow
{
    readonly PlanItem _i;
    public PlanRow(PlanItem i) { _i = i; }
    public string ActionText => _i.ActionText;
    public string Direction => _i.DirectionText;
    public string RelPath => _i.RelPath;
    public string SizeText => _i.SizeText;
    public string Reason => _i.Reason;
    public bool IsConflict => _i.Action == PlanAction.Conflict;
    public ConflictResolution Resolution { get => _i.Resolution; set => _i.Resolution = value; }
    public IBrush Brush => _i.Action switch
    {
        PlanAction.New => new SolidColorBrush(Color.Parse("#2ECC71")),
        PlanAction.Modified => new SolidColorBrush(Color.Parse("#4DA3FF")),
        PlanAction.Delete => new SolidColorBrush(Color.Parse("#FF6B6B")),
        _ => new SolidColorBrush(Color.Parse("#F5B84B"))
    };
}

public partial class SyncViewModel : ObservableObject, IRefreshable
{
    readonly MainViewModel _main;
    SyncPlan? _plan;
    public SyncViewModel(MainViewModel main) { _main = main; }

    public RunCoordinator Coordinator => Svc.Coordinator;
    public ObservableCollection<SyncProfile> Profiles { get; } = new();
    public ObservableCollection<PlanRow> Rows { get; } = new();
    public ObservableCollection<IssueRow> Issues { get; } = new();
    public static IReadOnlyList<ConflictResolution> ResolutionList { get; } = Enum.GetValues<ConflictResolution>();

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))] SyncProfile? _selectedProfile;
    [ObservableProperty] string _summary = "";
    [ObservableProperty] string _result = "";
    [ObservableProperty] bool _resultIsError;
    [ObservableProperty] bool _hasPlan;
    [ObservableProperty] bool _truncated;
    [ObservableProperty] string _modeText = "";

    public bool HasIssues => Issues.Count > 0;
    public bool HasResult => !string.IsNullOrEmpty(Result);
    partial void OnResultChanged(string value) => OnPropertyChanged(nameof(HasResult));

    public void Refresh()
    {
        var keep = SelectedProfile?.Id;
        Profiles.Clear(); foreach (var p in Svc.Profiles.GetAll()) Profiles.Add(p);
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == keep) ?? Profiles.FirstOrDefault();
    }

    public void Open(SyncProfile p)
    {
        _main.GoTo("sync");
        SelectedProfile = Profiles.FirstOrDefault(x => x.Id == p.Id) ?? p;
        ClearPlan(); Result = "";
        _ = AnalyzeAsync();
    }

    partial void OnSelectedProfileChanged(SyncProfile? value)
    {
        ModeText = value == null ? "" : ModeInfo.Title(value.Mode) + " · " + value.SourcePath + "  →  " + value.DestinationPath;
        ClearPlan();
    }

    void ClearPlan()
    {
        _plan = null; HasPlan = false; Rows.Clear(); Issues.Clear(); Summary = ""; Truncated = false;
        OnPropertyChanged(nameof(HasIssues)); StartCommand.NotifyCanExecuteChanged();
    }

    bool CanAnalyze() => SelectedProfile != null && !Svc.Coordinator.IsBusy;

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    async Task Analyze() => await AnalyzeAsync();

    async Task AnalyzeAsync()
    {
        var p = SelectedProfile; if (p == null || Svc.Coordinator.IsBusy) return;
        Result = ""; ClearPlan();
        try
        {
            var plan = await Svc.Coordinator.PlanAsync(p);
            _plan = plan;
            foreach (var i in plan.Issues)
                Issues.Add(new IssueRow
                {
                    Text = (i.Severity == IssueSeverity.Error ? "✕  " : i.Severity == IssueSeverity.Warning ? "!  " : "i  ") + i.Message,
                    Brush = i.Severity == IssueSeverity.Error ? new SolidColorBrush(Color.Parse("#FF6B6B")) : i.Severity == IssueSeverity.Warning ? new SolidColorBrush(Color.Parse("#F5B84B")) : Brushes.Gray
                });
            foreach (var w in plan.ScanWarnings) Issues.Add(new IssueRow { Text = "!  " + w, Brush = new SolidColorBrush(Color.Parse("#F5B84B")) });
            var changes = plan.Items.Where(i => i.Action != PlanAction.Unchanged && !i.IsDirectory).ToList();
            foreach (var i in changes.Take(5000)) Rows.Add(new PlanRow(i));
            Truncated = changes.Count > 5000;
            var s = plan.Summary;
            Summary = $"{s.FilesToCopy:N0} new · {s.FilesToUpdate:N0} updated · {s.FilesToDelete:N0} to delete · {s.Conflicts:N0} conflicts · {s.FilesUnchanged:N0} unchanged · {Format.Bytes(s.BytesToTransfer)} to transfer";
            HasPlan = true;
            OnPropertyChanged(nameof(HasIssues));
        }
        catch (Exception ex) { await Dialogs.Info("The folders could not be analyzed", ErrorTranslator.Describe(ex), true); }
        StartCommand.NotifyCanExecuteChanged();
    }

    bool CanStart() => _plan != null && !_plan.HasBlockingIssues && !Svc.Coordinator.IsBusy &&
        (_plan.Items.Any(i => i.Action != PlanAction.Unchanged && !i.IsDirectory));

    [RelayCommand(CanExecute = nameof(CanStart))]
    async Task Start()
    {
        var plan = _plan; if (plan == null) return;
        var s = plan.Summary;
        if (plan.HasDeletions)
        {
            var how = plan.Profile.DeletionBehavior == DeletionBehavior.MoveToArchive
                ? "They will be moved to the _FolderMirror/Archive folder inside the destination, so you can restore them."
                : "They will be deleted permanently and cannot be restored.";
            if (!await Dialogs.Confirm("Confirm file deletions", $"{s.FilesToDelete:N0} file(s) ({Format.Bytes(s.BytesToDelete)}) will be removed from the destination. {how}", "Delete and synchronize", true)) return;
        }
        else if (Svc.Settings.ConfirmBeforeSync)
        {
            if (!await Dialogs.Confirm("Start synchronization?", $"'{plan.Profile.Name}' will copy {s.FilesToCopy + s.FilesToUpdate:N0} file(s), {Format.Bytes(s.BytesToTransfer)} in total.", "Start")) return;
        }
        if (plan.ConflictCount > 0 &&
            !await Dialogs.Confirm("Conflicts will be skipped", $"{plan.ConflictCount} conflicting file(s) have no resolution chosen and will be left untouched.", "Continue")) return;

        Result = "";
        var result = await Svc.Coordinator.ExecuteAsync(plan, new ExecuteOptions
        {
            ConfirmedDeletions = true, MaxParallel = Svc.Settings.MaxParallelOperations, Trigger = "Manual"
        });
        Result = RunCoordinator.Describe(result);
        ResultIsError = result.Status is SyncStatus.Failed or SyncStatus.CompletedWithErrors;
        if (Svc.Settings.ShowNotifications) Platform.Notify("Folder Mirror", Result);
        ClearPlan();
    }

    [RelayCommand] void Cancel() => Svc.Coordinator.Cancel();
}

public partial class HistoryViewModel : ObservableObject, IRefreshable
{
    public ObservableCollection<RunRecord> Runs { get; } = new();
    public ObservableCollection<OpRow> Ops { get; } = new();
    public static IReadOnlyList<string> Statuses { get; } = new[] { "All statuses" }.Concat(Enum.GetNames<SyncStatus>().Where(n => n != "Running")).ToList();

    [ObservableProperty] string _search = "";
    [ObservableProperty] string _status = "All statuses";
    [ObservableProperty] RunRecord? _selected;
    [ObservableProperty] string _countText = "";
    public bool HasSelection => Selected != null;
    public bool IsEmpty => Runs.Count == 0;

    partial void OnSearchChanged(string value) => Refresh();
    partial void OnStatusChanged(string value) => Refresh();
    partial void OnSelectedChanged(RunRecord? value)
    {
        Ops.Clear();
        if (value != null) foreach (var o in Svc.History.GetOps(value.Id)) Ops.Add(o);
        OnPropertyChanged(nameof(HasSelection));
    }

    public void Refresh()
    {
        var keep = Selected?.Id;
        Runs.Clear();
        foreach (var r in Svc.History.Query(string.IsNullOrWhiteSpace(Search) ? null : Search, Status == "All statuses" ? null : Status)) Runs.Add(r);
        CountText = $"{Runs.Count:N0} run(s)";
        Selected = Runs.FirstOrDefault(r => r.Id == keep);
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    async Task ExportCsv()
    {
        if (Runs.Count == 0) { await Dialogs.Info("Nothing to export", "There are no history entries matching the current filter."); return; }
        var path = await Dialogs.PickSaveFile("Export history", "FolderMirror-history.csv", "CSV file", "*.csv");
        if (path == null) return;
        try { File.WriteAllText(path, HistoryRepository.ToCsv(Runs), new System.Text.UTF8Encoding(true)); await Dialogs.Info("Export complete", $"{Runs.Count} run(s) exported to:\n{path}"); }
        catch (Exception ex) { await Dialogs.Info("Export failed", ex.Message, true); }
    }

    [RelayCommand]
    async Task Clear()
    {
        if (!await Dialogs.Confirm("Clear history?", "All recorded synchronization runs and their reports will be removed. This cannot be undone. Your files and profiles are not affected.", "Clear history", true)) return;
        Svc.History.Clear(); Refresh();
    }
}

public partial class SettingsViewModel : ObservableObject, IRefreshable
{
    bool _loading;
    public static IReadOnlyList<Option<SyncMode>> Modes => ProfileEditorViewModel.Modes;
    public static IReadOnlyList<int> ParallelOptions { get; } = new[] { 1, 2, 4, 8, 16 };
    public static IReadOnlyList<string> LogLevels { get; } = new[] { "Debug", "Information", "Warning", "Error" };

    [ObservableProperty] bool _darkTheme = true;
    [ObservableProperty] bool _confirmBeforeSync = true;
    [ObservableProperty] bool _showNotifications = true;
    [ObservableProperty] bool _verifyCopies;
    [ObservableProperty] int _maxParallel = 4;
    [ObservableProperty] Option<SyncMode>? _defaultMode;
    [ObservableProperty] string _logLevel = "Information";
    public string Version => AppInfo.Version;
    public string DataFolder => AppPaths.DataFolder;
    public string LogFolder => Svc.LogFolder;
    public string Tagline => AppInfo.Tagline;

    public SettingsViewModel() => Refresh();

    public void Refresh()
    {
        _loading = true;
        var s = Svc.Settings;
        DarkTheme = s.DarkTheme; ConfirmBeforeSync = s.ConfirmBeforeSync; ShowNotifications = s.ShowNotifications;
        VerifyCopies = s.VerifyCopies; MaxParallel = s.MaxParallelOperations; LogLevel = s.LogLevel;
        DefaultMode = Modes.First(m => m.Value == s.DefaultMode);
        _loading = false;
    }

    void Save()
    {
        if (_loading) return;
        var s = Svc.Settings;
        s.DarkTheme = DarkTheme; s.ConfirmBeforeSync = ConfirmBeforeSync; s.ShowNotifications = ShowNotifications;
        s.VerifyCopies = VerifyCopies; s.MaxParallelOperations = MaxParallel; s.LogLevel = LogLevel;
        if (DefaultMode != null) s.DefaultMode = DefaultMode.Value;
        Svc.SaveSettings(); Svc.ConfigureLogging();
    }

    public static void ApplyTheme(bool dark)
    {
        if (Application.Current != null) Application.Current.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    partial void OnDarkThemeChanged(bool value) { ApplyTheme(value); Save(); }
    partial void OnConfirmBeforeSyncChanged(bool value) => Save();
    partial void OnShowNotificationsChanged(bool value) => Save();
    partial void OnVerifyCopiesChanged(bool value) => Save();
    partial void OnMaxParallelChanged(int value) => Save();
    partial void OnDefaultModeChanged(Option<SyncMode>? value) => Save();
    partial void OnLogLevelChanged(string value) => Save();

    [RelayCommand] void OpenData() => Platform.OpenPath(DataFolder);
    [RelayCommand] void OpenLogs() { Directory.CreateDirectory(LogFolder); Platform.OpenPath(LogFolder); }
    [RelayCommand] void OpenRepo() => Platform.OpenUrl(AppInfo.RepoUrl);
    [RelayCommand]
    async Task ClearLogs()
    {
        if (!await Dialogs.Confirm("Clear log files?", "All diagnostic log files will be deleted.", "Clear logs", true)) return;
        Svc.ClearLogs();
    }
}
