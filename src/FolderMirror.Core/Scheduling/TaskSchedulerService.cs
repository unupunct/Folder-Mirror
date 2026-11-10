using System.Diagnostics;
using System.Security;
using System.Text;
using FolderMirror.Core.Models;

namespace FolderMirror.Core.Scheduling;

public static class ScheduleCalculator
{
    public static string Describe(ScheduleSettings s)
    {
        if (s.Kind == ScheduleKind.None) return "Not scheduled";
        var parts = new List<string>();
        switch (s.Kind)
        {
            case ScheduleKind.Every15Minutes: parts.Add("Every 15 minutes"); break;
            case ScheduleKind.Every30Minutes: parts.Add("Every 30 minutes"); break;
            case ScheduleKind.Hourly: parts.Add("Every hour"); break;
            case ScheduleKind.Daily: parts.Add($"Daily at {s.TimeOfDay}"); break;
            case ScheduleKind.Weekly: parts.Add($"Weekly on {string.Join(", ", s.DaysOfWeek.Select(d => d.ToString()[..3]))} at {s.TimeOfDay}"); break;
            case ScheduleKind.Custom:
                parts.Add($"Every {s.CustomIntervalMinutes} min on {string.Join(", ", s.DaysOfWeek.Select(d => d.ToString()[..3]))} from {s.TimeOfDay}");
                break;
        }
        if (s.RunAtWindowsStartup) parts.Add("at Windows start");
        if (s.RunAtUserLogon) parts.Add("at logon");
        return string.Join(" · ", parts);
    }

    public static int? IntervalMinutes(ScheduleSettings s) => s.Kind switch
    {
        ScheduleKind.Every15Minutes => 15,
        ScheduleKind.Every30Minutes => 30,
        ScheduleKind.Hourly => 60,
        ScheduleKind.Custom => Math.Max(1, s.CustomIntervalMinutes),
        _ => null
    };

    /// <summary>Next time (local) a time-based trigger fires after <paramref name="after"/>; null when there is none.</summary>
    public static DateTime? NextRun(ScheduleSettings s, DateTime after)
    {
        if (!s.Enabled || s.Kind == ScheduleKind.None) return null;
        var tod = s.TimeOfDayValue;
        switch (s.Kind)
        {
            case ScheduleKind.Every15Minutes:
            case ScheduleKind.Every30Minutes:
            case ScheduleKind.Hourly:
            {
                var step = TimeSpan.FromMinutes(IntervalMinutes(s)!.Value);
                var anchor = s.AnchorLocal;
                if (after < anchor) return anchor;
                var n = Math.Floor((after - anchor).TotalMinutes / step.TotalMinutes) + 1;
                return anchor + TimeSpan.FromMinutes(n * step.TotalMinutes);
            }
            case ScheduleKind.Daily:
            {
                var t = after.Date + tod;
                return t > after ? t : t.AddDays(1);
            }
            case ScheduleKind.Weekly:
            {
                for (int i = 0; i < 8; i++)
                {
                    var day = after.Date.AddDays(i);
                    if (!s.DaysOfWeek.Contains(day.DayOfWeek)) continue;
                    var t = day + tod;
                    if (t > after) return t;
                }
                return null;
            }
            case ScheduleKind.Custom:
            {
                var step = TimeSpan.FromMinutes(Math.Max(1, s.CustomIntervalMinutes));
                for (int i = 0; i < 8; i++)
                {
                    var day = after.Date.AddDays(i);
                    if (!s.DaysOfWeek.Contains(day.DayOfWeek)) continue;
                    var start = day + tod;
                    var end = day.AddDays(1);
                    var t = start;
                    if (after >= start) t = start + TimeSpan.FromMinutes(Math.Floor((after - start).TotalMinutes / step.TotalMinutes + 1) * step.TotalMinutes);
                    if (t < end && t > after) return t;
                }
                return null;
            }
        }
        return null;
    }
}

/// <summary>Creates/removes Windows Task Scheduler tasks (via schtasks.exe) that start this same executable in background mode.</summary>
public sealed class TaskSchedulerService
{
    public const string Folder = "FolderMirror";
    public string ExePath { get; }
    public string UserId { get; }

    public TaskSchedulerService(string? exePath = null)
    {
        ExePath = exePath ?? Environment.ProcessPath ?? "FolderMirror.exe";
        UserId = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
    }

    public static string TaskName(Guid id) => $"\\{Folder}\\Profile_{id:N}";

    public string BuildXml(SyncProfile p)
    {
        var s = p.Schedule;
        var sb = new StringBuilder();
        sb.AppendLine("""<?xml version="1.0" encoding="UTF-16"?>""");
        sb.AppendLine("""<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">""");
        sb.AppendLine($"  <RegistrationInfo><Description>Folder Mirror – {SecurityElement.Escape(p.Name)}</Description><Author>Folder Mirror</Author></RegistrationInfo>");
        sb.AppendLine("  <Triggers>");
        var start = s.AnchorLocal.ToString("yyyy-MM-dd'T'HH:mm:ss");
        var interval = ScheduleCalculator.IntervalMinutes(s);
        switch (s.Kind)
        {
            case ScheduleKind.Every15Minutes:
            case ScheduleKind.Every30Minutes:
            case ScheduleKind.Hourly:
                sb.AppendLine($"    <TimeTrigger><Repetition><Interval>PT{interval}M</Interval></Repetition><StartBoundary>{start}</StartBoundary><Enabled>true</Enabled></TimeTrigger>");
                break;
            case ScheduleKind.Daily:
                sb.AppendLine($"    <CalendarTrigger><StartBoundary>{DateTime.Today:yyyy-MM-dd}T{s.TimeOfDayValue:hh\\:mm\\:ss}</StartBoundary><Enabled>true</Enabled><ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay></CalendarTrigger>");
                break;
            case ScheduleKind.Weekly:
                sb.AppendLine($"    <CalendarTrigger><StartBoundary>{DateTime.Today:yyyy-MM-dd}T{s.TimeOfDayValue:hh\\:mm\\:ss}</StartBoundary><Enabled>true</Enabled><ScheduleByWeek><WeeksInterval>1</WeeksInterval><DaysOfWeek>{Days(s)}</DaysOfWeek></ScheduleByWeek></CalendarTrigger>");
                break;
            case ScheduleKind.Custom:
                sb.AppendLine($"    <CalendarTrigger><Repetition><Interval>PT{interval}M</Interval><Duration>P1D</Duration></Repetition><StartBoundary>{DateTime.Today:yyyy-MM-dd}T{s.TimeOfDayValue:hh\\:mm\\:ss}</StartBoundary><Enabled>true</Enabled><ScheduleByWeek><WeeksInterval>1</WeeksInterval><DaysOfWeek>{Days(s)}</DaysOfWeek></ScheduleByWeek></CalendarTrigger>");
                break;
        }
        if (s.RunAtWindowsStartup) sb.AppendLine("    <BootTrigger><Delay>PT1M</Delay><Enabled>true</Enabled></BootTrigger>");
        if (s.RunAtUserLogon) sb.AppendLine($"    <LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(UserId)}</UserId><Delay>PT30S</Delay></LogonTrigger>");
        sb.AppendLine("  </Triggers>");
        sb.AppendLine($"  <Principals><Principal id=\"Author\"><UserId>{SecurityElement.Escape(UserId)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>");
        sb.AppendLine("  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><StartWhenAvailable>true</StartWhenAvailable><AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>false</Hidden><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>7</Priority></Settings>");
        sb.AppendLine($"  <Actions Context=\"Author\"><Exec><Command>{SecurityElement.Escape(ExePath)}</Command><Arguments>--run-profile {p.Id} --background</Arguments></Exec></Actions>");
        sb.AppendLine("</Task>");
        return sb.ToString();
    }

    static string Days(ScheduleSettings s) =>
        string.Concat((s.DaysOfWeek.Count == 0 ? new List<DayOfWeek> { DayOfWeek.Monday } : s.DaysOfWeek).Distinct().Select(d => $"<{d}/>"));

    static bool HasTrigger(ScheduleSettings s) => s.Kind != ScheduleKind.None || s.RunAtUserLogon || s.RunAtWindowsStartup;

    /// <summary>Creates, updates or removes the task so it matches the profile's schedule.</summary>
    public (bool ok, string message) Apply(SyncProfile p)
    {
        var name = TaskName(p.Id);
        if (!p.Schedule.Enabled || !HasTrigger(p.Schedule))
        {
            Remove(p.Id);
            return (true, "Schedule removed.");
        }
        var tmp = Path.Combine(Path.GetTempPath(), $"fm_task_{p.Id:N}.xml");
        try
        {
            File.WriteAllText(tmp, BuildXml(p), Encoding.Unicode);
            var (code, output) = Run("/Create", "/TN", name, "/XML", tmp, "/F");
            return code == 0 ? (true, "Scheduled task saved.") : (false, "Windows Task Scheduler refused the task: " + output.Trim());
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    public bool Remove(Guid id)
    {
        var (code, _) = Run("/Delete", "/TN", TaskName(id), "/F");
        return code == 0;
    }

    public bool Exists(Guid id) => Run("/Query", "/TN", TaskName(id)).code == 0;

    public (int code, string output) Run(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var o = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
        proc.WaitForExit(30000);
        return (proc.ExitCode, o);
    }
}
