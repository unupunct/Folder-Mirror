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
}
