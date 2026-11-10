using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using FolderMirror.Core.Engine;
using FolderMirror.Core.Models;

namespace FolderMirror.Services;

/// <summary>Runs one synchronization at a time, records history, and exposes live progress to the UI.</summary>
public sealed partial class RunCoordinator : ObservableObject
{
    CancellationTokenSource? _cts;

    [ObservableProperty] bool _isRunning;
    [ObservableProperty] bool _isPlanning;
    [ObservableProperty] string _profileName = "";
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

    public async Task<SyncPlan> PlanAsync(SyncProfile profile, CancellationToken ct = default)
    {
        IsPlanning = true; IsIndeterminate = true;
        ProfileName = profile.Name;
        StatusText = "Analyzing '" + profile.Name + "'…";
        try
        {
            return await Svc.Engine.PlanAsync(profile, Svc.Settings.MaxParallelOperations,
                s => Application.Current?.Dispatcher.BeginInvoke(() => { DetailText = s; }), ct);
        }
        finally
        {
            IsPlanning = false; IsIndeterminate = false;
            if (!IsRunning) { StatusText = Loc.T("status.idle"); DetailText = ""; }
        }
    }

    public async Task<SyncResult> ExecuteAsync(SyncPlan plan, ExecuteOptions options, CancellationToken externalCt = default)
    {
        if (IsRunning) throw new InvalidOperationException("Another synchronization is already running.");
        var profile = plan.Profile;
        using var gate = new Mutex(false, @"Local\FolderMirror.Run." + profile.Id.ToString("N"));
        bool owned;
        try { owned = gate.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            var skipped = new SyncResult { Status = SyncStatus.Skipped, StartUtc = DateTime.UtcNow, EndUtc = DateTime.UtcNow };
            skipped.Messages.Add("This profile is already being synchronized by another Folder Mirror process.");
            Record(profile, skipped, options.Trigger);
            return skipped;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        IsRunning = true; ProfileName = profile.Name; IsIndeterminate = true;
        StatusText = "Synchronizing '" + profile.Name + "'…"; Fraction = 0; CurrentFile = ""; DetailText = "Starting…";
        var runId = Svc.History.StartRun(profile, options.Trigger);
        SyncResult result;
        var dispatcher = Application.Current?.Dispatcher;
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
            result.Messages.Add(Core.Engine.ErrorTranslator.Describe(ex));
            Svc.Log.Error(ex, "Unexpected failure");
        }
        try { Svc.History.FinishRun(runId, result); } catch (Exception ex) { Svc.Log.Error(ex, "Could not write history"); }
        _cts.Dispose(); _cts = null;
        IsRunning = false; IsIndeterminate = false; Fraction = 0; CurrentFile = "";
        StatusText = Loc.T("status.idle"); DetailText = "";
        RunFinished?.Invoke(profile, result);
        return result;
    }

    static void Record(SyncProfile p, SyncResult r, string trigger)
    {
        var id = Svc.History.StartRun(p, trigger);
        Svc.History.FinishRun(id, r);
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
