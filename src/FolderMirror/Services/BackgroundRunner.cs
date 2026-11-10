using System.Windows;
using FolderMirror.Core.Engine;
using FolderMirror.Core.Models;

namespace FolderMirror.Services;

/// <summary>Headless mode used by Windows Task Scheduler: FolderMirror.exe --run-profile {id} --background</summary>
public static class BackgroundRunner
{
    public static async Task<int> RunAsync(Guid profileId)
    {
        var log = Svc.Log;
        try
        {
            if (Svc.Settings.SchedulingPaused)
            {
                log.Information("Scheduled run skipped: scheduling is paused.");
                return 2;
            }
            var profile = Svc.Profiles.Get(profileId);
            if (profile == null) { log.Warning("Scheduled run: profile {Id} no longer exists.", profileId); Svc.Scheduler.Remove(profileId); return 3; }
            log.Information("Scheduled run starting for '{Name}'", profile.Name);

            using var tray = new TrayService { Visible = true };
            SyncResult result;

            var plan = await Svc.Engine.PlanAsync(profile, Svc.Settings.MaxParallelOperations, null, CancellationToken.None);
            var unavailable = plan.Issues.Where(i => i.Code is "SourceDriveUnavailable" or "DestinationDriveUnavailable" or "SourceMissing").ToList();
            if (unavailable.Count > 0 && profile.Schedule.OnlyWhenAvailable)
            {
                result = new SyncResult { Status = SyncStatus.Skipped, StartUtc = DateTime.UtcNow, EndUtc = DateTime.UtcNow };
                result.Messages.Add("Skipped: " + string.Join(" ", unavailable.Select(u => u.Message)));
                var id = Svc.History.StartRun(profile, "Scheduled");
                Svc.History.FinishRun(id, result);
                log.Information("Scheduled run skipped because a location is unavailable.");
                return 2;
            }
            if (plan.HasBlockingIssues)
            {
                result = new SyncResult { Status = SyncStatus.Failed, StartUtc = DateTime.UtcNow, EndUtc = DateTime.UtcNow };
                result.Messages.Add(string.Join(" ", plan.Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message)));
                var id = Svc.History.StartRun(profile, "Scheduled");
                Svc.History.FinishRun(id, result);
                Notify(tray, profile, result);
                return 1;
            }

            result = await Svc.Coordinator.ExecuteAsync(plan, new ExecuteOptions
            {
                Unattended = true, MaxParallel = Svc.Settings.MaxParallelOperations, Trigger = "Scheduled"
            });
            Notify(tray, profile, result);
            if (Svc.Settings.ShowNotifications && result.Status != SyncStatus.Skipped) await Task.Delay(6500);
            return result.Status is SyncStatus.Completed ? 0 : result.Status == SyncStatus.Skipped ? 2 : 1;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Scheduled run crashed");
            return 1;
        }
    }

    static void Notify(TrayService tray, SyncProfile p, SyncResult r)
    {
        if (!Svc.Settings.ShowNotifications) return;
        bool err = r.Status is SyncStatus.Failed or SyncStatus.CompletedWithErrors;
        tray.Notify(err ? $"Folder Mirror – '{p.Name}' needs attention" : $"Folder Mirror – '{p.Name}' finished", RunCoordinator.Describe(r), err);
    }
}
