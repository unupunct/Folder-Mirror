using FolderMirror.Core.Models;
using FolderMirror.Core.Scheduling;

namespace FolderMirror.Tests;

public class SchedulingTests
{
    [Fact]
    public void NextRun_Interval_AlignsToAnchor()
    {
        var s = new ScheduleSettings { Enabled = true, Kind = ScheduleKind.Every15Minutes, AnchorLocal = new DateTime(2026, 1, 1, 10, 0, 0) };
        Assert.Equal(new DateTime(2026, 1, 1, 10, 30, 0), ScheduleCalculator.NextRun(s, new DateTime(2026, 1, 1, 10, 20, 0)));
    }

    [Fact]
    public void NextRun_Daily_Weekly()
    {
        var d = new ScheduleSettings { Enabled = true, Kind = ScheduleKind.Daily, TimeOfDay = "08:30" };
        Assert.Equal(new DateTime(2026, 1, 2, 8, 30, 0), ScheduleCalculator.NextRun(d, new DateTime(2026, 1, 1, 9, 0, 0)));
        var w = new ScheduleSettings { Enabled = true, Kind = ScheduleKind.Weekly, TimeOfDay = "20:00", DaysOfWeek = { DayOfWeek.Friday } };
        w.DaysOfWeek.Remove(DayOfWeek.Monday);
        Assert.Equal(new DateTime(2026, 1, 2, 20, 0, 0), ScheduleCalculator.NextRun(w, new DateTime(2026, 1, 1, 9, 0, 0))); // 2026-01-02 is Friday
        Assert.Null(ScheduleCalculator.NextRun(new ScheduleSettings { Enabled = false, Kind = ScheduleKind.Daily }, DateTime.Now));
    }

    [Fact]
    public void TaskXml_IsWellFormed_ForEveryKind()
    {
        if (!OperatingSystem.IsWindows()) return;
        var svc = new TaskSchedulerService(@"C:\Program Files\Folder Mirror\FolderMirror.exe");
        foreach (var kind in Enum.GetValues<ScheduleKind>().Where(k => k != ScheduleKind.None))
        {
            var p = new SyncProfile { Name = "A & B <test>" };
            p.Schedule = new ScheduleSettings { Enabled = true, Kind = kind, RunAtUserLogon = true, RunAtWindowsStartup = true, DaysOfWeek = { DayOfWeek.Tuesday } };
            var doc = System.Xml.Linq.XDocument.Parse(svc.BuildXml(p));
            Assert.Contains("--run-profile " + p.Id, doc.ToString());
        }
    }

    [Fact]
    public void TaskScheduler_CreateQueryDelete_RealTask()
    {
        if (!OperatingSystem.IsWindows()) return;
        var svc = new TaskSchedulerService(Environment.ProcessPath);
        var p = new SyncProfile { Name = "UnitTest schedule" };
        p.Schedule = new ScheduleSettings { Enabled = true, Kind = ScheduleKind.Daily, TimeOfDay = "03:15" };
        try
        {
            var (ok, msg) = svc.Apply(p);
            Assert.True(ok, msg);
            Assert.True(svc.Exists(p.Id));
        }
        finally
        {
            svc.Remove(p.Id);
        }
        Assert.False(svc.Exists(p.Id));
    }

    [Fact]
    public void LaunchdPlist_IsWellFormed_ForEveryKind()
    {
        var svc = new LaunchdService("/Applications/Folder Mirror.app/Contents/MacOS/FolderMirror", "/tmp/agents", "/tmp/logs");
        foreach (var kind in Enum.GetValues<ScheduleKind>().Where(k => k != ScheduleKind.None))
        {
            var p = new SyncProfile { Name = "A & B <test>" };
            p.Schedule = new ScheduleSettings { Enabled = true, Kind = kind, RunAtUserLogon = true, DaysOfWeek = { DayOfWeek.Tuesday, DayOfWeek.Sunday }, TimeOfDay = "07:45", CustomIntervalMinutes = 240 };
            var xml = svc.BuildPlist(p);
            var doc = System.Xml.Linq.XDocument.Parse(xml.Replace("<!DOCTYPE", "<!-- "). Replace("PropertyList-1.0.dtd\">", "-->"));
            Assert.Contains("--run-profile", xml);
            Assert.Contains(p.Id.ToString(), xml);
            Assert.Contains("<key>RunAtLoad</key><true/>", xml);
            Assert.Contains(LaunchdService.Label(p.Id), xml);
        }
    }

    [Fact]
    public void LaunchdPlist_UsesInterval_And_CalendarSlots()
    {
        var svc = new LaunchdService("/x/FolderMirror", "/tmp/a", "/tmp/l");
        var p = new SyncProfile();
        p.Schedule = new ScheduleSettings { Enabled = true, Kind = ScheduleKind.Every30Minutes };
        Assert.Contains("<key>StartInterval</key><integer>1800</integer>", svc.BuildPlist(p));

        p.Schedule = new ScheduleSettings { Enabled = true, Kind = ScheduleKind.Weekly, TimeOfDay = "20:05", DaysOfWeek = { DayOfWeek.Friday } };
        p.Schedule.DaysOfWeek.Remove(DayOfWeek.Monday);
        var slots = LaunchdService.CalendarSlots(p.Schedule);
        Assert.Single(slots);
        Assert.Equal(((int?)5, 20, 5), slots[0]);
        Assert.Contains("<key>Weekday</key><integer>5</integer><key>Hour</key><integer>20</integer><key>Minute</key><integer>5</integer>", svc.BuildPlist(p));

        p.Schedule = new ScheduleSettings { Enabled = true, Kind = ScheduleKind.Custom, TimeOfDay = "06:00", CustomIntervalMinutes = 360, DaysOfWeek = { DayOfWeek.Tuesday } };
        p.Schedule.DaysOfWeek.Remove(DayOfWeek.Monday);
        Assert.Equal(3, LaunchdService.CalendarSlots(p.Schedule).Count); // 06:00, 12:00, 18:00
    }

    [Fact]
    public void LaunchdApply_WritesAndRemovesPlist()
    {
        if (!OperatingSystem.IsMacOS()) return; // needs launchctl
        var dir = Path.Combine(Path.GetTempPath(), "fm-launchd-" + Guid.NewGuid().ToString("N"));
        var svc = new LaunchdService("/usr/bin/true", dir, dir);
        var p = new SyncProfile { Name = "UnitTest" };
        p.Schedule = new ScheduleSettings { Enabled = true, Kind = ScheduleKind.Daily, TimeOfDay = "03:15" };
        try
        {
            var (ok, msg) = svc.Apply(p);
            Assert.True(ok, msg);
            Assert.True(svc.Exists(p.Id));
        }
        finally { svc.Remove(p.Id); try { Directory.Delete(dir, true); } catch { } }
        Assert.False(svc.Exists(p.Id));
    }
}
