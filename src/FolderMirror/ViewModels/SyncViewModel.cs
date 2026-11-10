using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderMirror.Core.Engine;
using FolderMirror.Core.Models;
using FolderMirror.Services;
using FolderMirror.Views;

namespace FolderMirror.ViewModels;

public partial class SyncViewModel : ObservableObject
{
    public ObservableCollection<SyncProfile> Profiles => ProfileStore.Profiles;
    public RunCoordinator Coordinator => Svc.Coordinator;
    public static ConflictResolution[] Resolutions { get; } = Enum.GetValues<ConflictResolution>();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanPreview))] SyncProfile? _selectedProfile;
    [ObservableProperty] SyncPlan? _plan;
    [ObservableProperty] ICollectionView? _planView;
    [ObservableProperty] bool _hasPlan;
    [ObservableProperty] string _filterText = "";
    [ObservableProperty] bool _showNew = true, _showModified = true, _showDelete = true, _showUnchanged = true, _showConflict = true;
    [ObservableProperty] string _sumCopy = "0", _sumUpdate = "0", _sumDelete = "0", _sumUnchanged = "0", _sumConflict = "0", _sumTransfer = "0 B", _sumSpace = "—", _sumShown = "";
    [ObservableProperty] bool _hasConflicts;
    [ObservableProperty] bool _hasDeletes;
    [ObservableProperty] string _planHeader = "";
    [ObservableProperty] string _resultText = "";
    [ObservableProperty] bool _resultIsError;
    [ObservableProperty] bool _hasResult;
    [ObservableProperty] ConflictResolution _bulkResolution = ConflictResolution.KeepBoth;
    public ObservableCollection<SafetyIssue> Issues { get; } = new();
    public ObservableCollection<string> ResultErrors { get; } = new();

    public bool CanPreview => SelectedProfile != null && !Coordinator.IsBusy;
    public bool CanStart => Plan != null && !Plan.HasBlockingIssues && !Coordinator.IsBusy &&
                            Plan.Items.Any(i => i.Action is PlanAction.New or PlanAction.Modified or PlanAction.Delete || (i.Action == PlanAction.Conflict && i.Resolution != ConflictResolution.Skip));

    public SyncViewModel()
    {
        Coordinator.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(RunCoordinator.IsBusy) or nameof(RunCoordinator.IsRunning) or nameof(RunCoordinator.IsPlanning))
            { OnPropertyChanged(nameof(CanPreview)); OnPropertyChanged(nameof(CanStart)); }
        };
    }

    public void Refresh()
    {
        if (SelectedProfile != null && Profiles.All(p => p.Id != SelectedProfile.Id)) SelectedProfile = null;
        if (SelectedProfile == null)
        {
            var saved = Svc.Settings.SelectedProfileId;
            SelectedProfile = Profiles.FirstOrDefault(p => p.Id.ToString() == saved) ?? Profiles.FirstOrDefault();
        }
    }

    partial void OnSelectedProfileChanged(SyncProfile? value)
    {
        if (value != null && Svc.Settings.SelectedProfileId != value.Id.ToString())
        { Svc.Settings.SelectedProfileId = value.Id.ToString(); try { Svc.SaveSettings(); } catch { } }
        if (!Coordinator.IsBusy) ClearPlan();
    }

    void ClearPlan() { Plan = null; PlanView = null; HasPlan = false; Issues.Clear(); HasConflicts = false; HasDeletes = false; OnPropertyChanged(nameof(CanStart)); }

    partial void OnFilterTextChanged(string value) => PlanView?.Refresh();
    partial void OnShowNewChanged(bool value) => PlanView?.Refresh();
    partial void OnShowModifiedChanged(bool value) => PlanView?.Refresh();
    partial void OnShowDeleteChanged(bool value) => PlanView?.Refresh();
    partial void OnShowUnchangedChanged(bool value) => PlanView?.Refresh();
    partial void OnShowConflictChanged(bool value) => PlanView?.Refresh();

    bool Filter(object o)
    {
        var i = (PlanItem)o;
        var ok = i.Action switch
        {
            PlanAction.New => ShowNew, PlanAction.Modified => ShowModified, PlanAction.Delete => ShowDelete,
            PlanAction.Unchanged => ShowUnchanged, _ => ShowConflict
        };
        if (!ok) return false;
        return FilterText.Length == 0 || i.RelPath.Contains(FilterText, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand(CanExecute = nameof(CanPreview))]
    Task Preview() => PreviewAsync();

    public async Task PreviewAsync()
    {
        if (SelectedProfile == null) return;
        HasResult = false; ResultErrors.Clear();
        ClearPlan();
        try
        {
            var plan = await Coordinator.PlanAsync(SelectedProfile);
            ShowPlan(plan);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Preview failed");
            Dialogs.Info("The folders could not be analyzed", ErrorTranslator.Describe(ex), true);
        }
    }

    void ShowPlan(SyncPlan plan)
    {
        Plan = plan;
        Issues.Clear();
        foreach (var i in plan.Issues) Issues.Add(i);
        if (plan.ScanWarnings.Count > 0)
            Issues.Add(new SafetyIssue(IssueSeverity.Warning, "ScanWarnings",
                $"{plan.ScanWarnings.Count} item(s) could not be read and were skipped (e.g. {plan.ScanWarnings[0]})"));
        var s = plan.Summary;
        SumCopy = s.FilesToCopy.ToString("N0"); SumUpdate = s.FilesToUpdate.ToString("N0"); SumDelete = s.FilesToDelete.ToString("N0");
        SumUnchanged = s.FilesUnchanged.ToString("N0"); SumConflict = s.Conflicts.ToString("N0");
        SumTransfer = Format.Bytes(s.BytesToTransfer);
        SumSpace = s.DestinationFreeBytes is long f ? Format.Bytes(f) + " free" : "unknown";
        HasConflicts = s.Conflicts > 0; HasDeletes = s.FilesToDelete > 0;
        var view = new ListCollectionView(plan.Items) { Filter = Filter };
        PlanView = view; HasPlan = true;
        PlanHeader = $"Preview of '{plan.Profile.Name}' · {ModeInfo.Title(plan.Profile.Mode)} · generated {DateTime.Now:T}. Nothing has been changed yet.";
        OnPropertyChanged(nameof(CanStart));
    }

    [RelayCommand]
    void ApplyBulkResolution()
    {
        if (Plan == null) return;
        foreach (var i in Plan.Items.Where(i => i.Action == PlanAction.Conflict && !(i.ConflictIsDeletion && BulkResolution == ConflictResolution.KeepBoth)))
            i.Resolution = BulkResolution;
        PlanView?.Refresh();
        OnPropertyChanged(nameof(CanStart));
    }

    [RelayCommand] void ResolutionChanged() => OnPropertyChanged(nameof(CanStart));

    [RelayCommand] void Cancel() => Coordinator.Cancel();

    [RelayCommand] void ClearPreview() { ClearPlan(); HasResult = false; }

    [RelayCommand(CanExecute = nameof(CanStart))]
    Task Start() => StartAsync(false);

    public async Task StartAsync(bool skipConfirm)
    {
        var plan = Plan;
        if (plan == null) return;
        var p = plan.Profile;
        bool confirmedDeletes = false;
        if (plan.HasDeletions)
        {
            bool archive = p.DeletionBehavior == DeletionBehavior.MoveToArchive;
            int n = plan.Summary.FilesToDelete;
            var where = p.Mode == SyncMode.TwoWay ? "on one side (the other side deleted them)" : "from the destination";
            var msg = archive
                ? $"{n:N0} file(s) ({Format.Bytes(plan.Summary.BytesToDelete)}) will be removed {where}. They are moved to a dated folder named '{PathTools.ReservedFolder}\\Archive' so you can restore them."
                : $"{n:N0} file(s) ({Format.Bytes(plan.Summary.BytesToDelete)}) will be PERMANENTLY DELETED {where}. This cannot be undone.";
            var check = plan.DestinationLooksUnrelated ? "I understand that the destination holds files that are not in the source and they will be removed." : null;
            if (!Dialogs.Confirm("Confirm file deletions", msg, "Delete and synchronize", danger: true, mustCheck: check)) return;
            confirmedDeletes = true;
        }
        else if (Svc.Settings.ConfirmBeforeSync && !skipConfirm)
        {
            var ops = plan.Items.Count(i => !i.IsDirectory && i.Action is PlanAction.New or PlanAction.Modified);
            if (!Dialogs.Confirm("Start synchronization?", $"'{p.Name}' will copy {ops:N0} file(s), {Format.Bytes(plan.Summary.BytesToTransfer)} in total.", "Start")) return;
        }

        var options = new ExecuteOptions
        {
            ConfirmedDeletions = confirmedDeletes,
            MaxParallel = Svc.Settings.MaxParallelOperations,
            Trigger = "Manual"
        };
        var result = await Coordinator.ExecuteAsync(plan, options);
        ShowResult(result);
        ClearPlan();
    }

    void ShowResult(SyncResult r)
    {
        ResultText = RunCoordinator.Describe(r);
        ResultIsError = r.Status is SyncStatus.Failed or SyncStatus.CompletedWithErrors;
        ResultErrors.Clear();
        foreach (var e in r.Errors.Take(12)) ResultErrors.Add($"{e.Path}: {e.Message}");
        if (r.Errors.Count > 12) ResultErrors.Add($"…and {r.Errors.Count - 12} more (see History for the full report).");
        if (r.ConflictsLeft > 0) ResultErrors.Add($"{r.ConflictsLeft} conflict(s) were left untouched. Run Preview to resolve them.");
        HasResult = true;
    }
}
