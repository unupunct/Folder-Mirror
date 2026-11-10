using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderMirror.Core.Engine;
using FolderMirror.Core.Models;
using FolderMirror.Services;
using Microsoft.Win32;

namespace FolderMirror.ViewModels;

public partial class WizardViewModel : ObservableObject
{
    public SyncProfile Profile { get; }
    public bool IsNew { get; }
    public Action<bool>? CloseRequested { get; set; }
    readonly IDiskSpaceProvider _space = new WindowsDiskSpace();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsStep1), nameof(IsStep2), nameof(IsStep3), nameof(IsStep4), nameof(CanNext), nameof(IsLast), nameof(StepTitle))] int _step = 1;
    [ObservableProperty] string _name;
    [ObservableProperty] string _source;
    [ObservableProperty] string _destination;
    [ObservableProperty] SyncMode _mode;
    [ObservableProperty] bool _includeSubdirectories, _preserveTimestamps, _preserveAttributes, _compareSize, _compareDate, _compareHash;
    [ObservableProperty] bool _ignoreHidden, _ignoreSystem, _ignoreTemp, _ignoreEmpty, _verify, _backupOverwritten;
    [ObservableProperty] string _excludedExtensions, _excludedFolders;
    [ObservableProperty] ConflictPolicy _conflictPolicy;
    [ObservableProperty] DeletionBehavior _deletionBehavior;
    [ObservableProperty] bool _reviewed;
    [ObservableProperty] string _sourceSpace = "", _destinationSpace = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanNext))] bool _hasBlocking;
    public ObservableCollection<SafetyIssue> Issues { get; } = new();

    public static ConflictPolicy[] ConflictPolicies { get; } = Enum.GetValues<ConflictPolicy>();
    public static DeletionBehavior[] DeletionBehaviors { get; } = Enum.GetValues<DeletionBehavior>();
    public static string ConflictText(ConflictPolicy c) => c switch
    {
        ConflictPolicy.Skip => "Ask me – leave conflicts untouched (safest)",
        ConflictPolicy.KeepBoth => "Keep both versions (rename the older copy)",
        ConflictPolicy.PreferSource => "Prefer the source version (backs up the other)",
        _ => "Prefer the destination version (backs up the other)"
    };

    public bool IsStep1 => Step == 1; public bool IsStep2 => Step == 2; public bool IsStep3 => Step == 3; public bool IsStep4 => Step == 4;
    public bool IsLast => Step == 4;
    public bool CanNext => !(Step == 1 && HasBlocking);
    public string StepTitle => Step switch { 1 => "Select folders", 2 => "Choose synchronization mode", 3 => "Configure options", _ => "Review and confirm" };
    public string WindowTitle => IsNew ? "New synchronization" : "Edit synchronization";
    public bool ShowConflictOption => Mode == SyncMode.TwoWay;
    public bool ShowDeletionOption => Mode is SyncMode.Mirror or SyncMode.TwoWay;
    public bool ShowBackupOption => Mode is SyncMode.Mirror or SyncMode.OneWay;
    public bool IsMirror => Mode == SyncMode.Mirror;
    public string ReviewText { get; private set; } = "";

    public WizardViewModel(SyncProfile p, bool isNew)
    {
        Profile = p; IsNew = isNew;
        _name = p.Name; _source = p.SourcePath; _destination = p.DestinationPath; _mode = p.Mode;
        _includeSubdirectories = p.IncludeSubdirectories; _preserveTimestamps = p.PreserveTimestamps; _preserveAttributes = p.PreserveAttributes;
        _compareSize = p.CompareSize; _compareDate = p.CompareModifiedDate; _compareHash = p.CompareHash;
        _ignoreHidden = p.IgnoreHidden; _ignoreSystem = p.IgnoreSystem; _ignoreTemp = p.IgnoreTemporary; _ignoreEmpty = p.IgnoreEmptyFolders;
        _verify = p.VerifyAfterCopy; _backupOverwritten = p.BackupOverwrittenFiles;
        _excludedExtensions = string.Join(", ", p.ExcludedExtensions);
        _excludedFolders = string.Join(Environment.NewLine, p.ExcludedFolders);
        _conflictPolicy = p.ConflictPolicy; _deletionBehavior = p.DeletionBehavior;
        Validate();
    }

    partial void OnSourceChanged(string value) => Validate();
    partial void OnDestinationChanged(string value) => Validate();
    partial void OnModeChanged(SyncMode value)
    {
        OnPropertyChanged(nameof(ShowConflictOption)); OnPropertyChanged(nameof(ShowDeletionOption));
        OnPropertyChanged(nameof(ShowBackupOption)); OnPropertyChanged(nameof(IsMirror)); Validate();
    }

    void Validate()
    {
        Issues.Clear();
        var tmp = Profile.Clone(); tmp.SourcePath = Source; tmp.DestinationPath = Destination; tmp.Mode = Mode;
        if (string.IsNullOrWhiteSpace(Source) && string.IsNullOrWhiteSpace(Destination)) { HasBlocking = true; SourceSpace = DestinationSpace = ""; return; }
        foreach (var i in SafetyChecker.CheckPaths(tmp, _space)) Issues.Add(i);
        HasBlocking = Issues.Any(i => i.Severity == IssueSeverity.Error);
        SourceSpace = SpaceText(Source); DestinationSpace = SpaceText(Destination);
    }

    string SpaceText(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            if (_space.TryGetSpace(path, out var free, out var total) && total > 0)
                return $"{Format.Bytes(free)} free of {Format.Bytes(total)}";
        }
        catch { }
        return "Drive information unavailable";
    }

    string? Browse(string title, string current)
    {
        var dlg = new OpenFolderDialog { Title = title, Multiselect = false };
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current)) dlg.InitialDirectory = current;
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    [RelayCommand] void BrowseSource() { var f = Browse("Select the source folder", Source); if (f != null) Source = f; }
    [RelayCommand] void BrowseDestination() { var f = Browse("Select the destination folder", Destination); if (f != null) Destination = f; }
    [RelayCommand]
    void AddExcludedFolder()
    {
        var f = Browse("Select a folder to exclude", Source);
        if (f == null) return;
        string entry = f;
        if (!string.IsNullOrWhiteSpace(Source) && PathTools.IsInside(f, Source)) entry = Path.GetRelativePath(PathTools.Normalize(Source), PathTools.Normalize(f));
        else entry = Path.GetFileName(f.TrimEnd('\\'));
        ExcludedFolders = (ExcludedFolders.TrimEnd() + Environment.NewLine + entry).Trim();
    }

    [RelayCommand] void SelectMode(string mode) => Mode = Enum.Parse<SyncMode>(mode);

    [RelayCommand]
    void Next()
    {
        if (Step == 1)
        {
            Validate();
            if (string.IsNullOrWhiteSpace(Name)) Name = Path.GetFileName(PathTools.Normalize(Source).TrimEnd('\\')) is { Length: > 0 } n ? n + " backup" : "New synchronization";
            if (HasBlocking) return;
        }
        if (Step < 4) { Step++; if (Step == 4) BuildReview(); }
    }

    [RelayCommand] void Back() { if (Step > 1) Step--; }
    [RelayCommand] void Cancel() => CloseRequested?.Invoke(false);

    SyncProfile Apply()
    {
        var p = Profile;
        p.Name = string.IsNullOrWhiteSpace(Name) ? "Untitled" : Name.Trim();
        p.SourcePath = PathTools.Normalize(Source); p.DestinationPath = PathTools.Normalize(Destination);
        p.Mode = Mode;
        p.IncludeSubdirectories = IncludeSubdirectories; p.PreserveTimestamps = PreserveTimestamps || Mode == SyncMode.TwoWay; p.PreserveAttributes = PreserveAttributes;
        p.CompareSize = CompareSize; p.CompareModifiedDate = CompareDate; p.CompareHash = CompareHash;
        p.IgnoreHidden = IgnoreHidden; p.IgnoreSystem = IgnoreSystem; p.IgnoreTemporary = IgnoreTemp; p.IgnoreEmptyFolders = IgnoreEmpty;
        p.VerifyAfterCopy = Verify; p.BackupOverwrittenFiles = BackupOverwritten;
        p.ExcludedExtensions = ExcludedExtensions.Split(new[] { ',', ';', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        p.ExcludedFolders = ExcludedFolders.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        p.ConflictPolicy = ConflictPolicy; p.DeletionBehavior = DeletionBehavior;
        p.NormalizeLists();
        return p;
    }

    void BuildReview()
    {
        var p = Apply();
        var lines = new List<string>
        {
            $"Name:\t{p.Name}", $"Source:\t{p.SourcePath}", $"Destination:\t{p.DestinationPath}", $"Mode:\t{ModeInfo.Title(p.Mode)}",
            $"Subfolders:\t{(p.IncludeSubdirectories ? "included" : "not included")}",
            "Compare by:\t" + string.Join(", ", new[] { p.CompareSize ? "size" : null, p.CompareModifiedDate ? "modified date" : null, p.CompareHash ? "SHA-256 content" : null }.Where(x => x != null)),
            $"Timestamps / attributes:\t{(p.PreserveTimestamps ? "preserved" : "not preserved")} / {(p.PreserveAttributes ? "preserved" : "not preserved")}",
            "Ignored:\t" + (string.Join(", ", new[] { p.IgnoreHidden ? "hidden files" : null, p.IgnoreSystem ? "system files" : null, p.IgnoreTemporary ? "temporary files" : null, p.IgnoreEmptyFolders ? "empty folders" : null }.Where(x => x != null)) is { Length: > 0 } ig ? ig : "nothing"),
            "Excluded extensions:\t" + (p.ExcludedExtensions.Count > 0 ? string.Join(" ", p.ExcludedExtensions) : "none"),
            "Excluded folders:\t" + (p.ExcludedFolders.Count > 0 ? string.Join(", ", p.ExcludedFolders) : "none"),
            "Verify copies:\t" + (p.VerifyAfterCopy ? "yes (SHA-256)" : "no"),
        };
        if (ShowDeletionOption) lines.Add("Deleted files:\t" + (p.DeletionBehavior == DeletionBehavior.MoveToArchive ? "moved to a dated archive folder" : "permanently deleted"));
        if (ShowConflictOption) lines.Add("Conflicts:\t" + ConflictText(p.ConflictPolicy));
        if (ShowBackupOption || Mode == SyncMode.Incremental) lines.Add("Overwritten files:\t" + (p.BackupOverwrittenFiles || Mode == SyncMode.Incremental ? "previous version kept" : "replaced"));
        ReviewText = string.Join("\n", lines);
        OnPropertyChanged(nameof(ReviewText));
    }

    [RelayCommand]
    void Save()
    {
        if (!Reviewed) return;
        var p = Apply();
        try
        {
            Svc.Profiles.Save(p);
            Svc.Log.Information("Profile saved: {Name} ({Mode})", p.Name, p.Mode);
            CloseRequested?.Invoke(true);
        }
        catch (Exception ex) { Views.Dialogs.Info("The profile could not be saved", ex.Message, true); }
    }
}
