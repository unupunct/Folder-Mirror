using System.Diagnostics;
using System.Security;
using System.Text;
using FolderMirror.Core.Models;

namespace FolderMirror.Core.Scheduling;

/// <summary>
/// macOS scheduling: writes a per-profile launchd agent (~/Library/LaunchAgents) that starts this same
/// executable in background mode (<c>--run-profile {id} --background</c>). The Windows counterpart is
/// <see cref="TaskSchedulerService"/>.
/// </summary>
public sealed class LaunchdService
{
    public const string LabelPrefix = "com.foldermirror.profile.";
    public string ExePath { get; }
    public string AgentsFolder { get; }
    public string LogFolder { get; }

    public LaunchdService(string? exePath = null, string? agentsFolder = null, string? logFolder = null)
    {
        ExePath = exePath ?? Environment.ProcessPath ?? "FolderMirror";
        AgentsFolder = agentsFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents");
        LogFolder = logFolder ?? Data.AppPaths.DefaultLogFolder;
    }

    public static string Label(Guid id) => LabelPrefix + id.ToString("N");
    public string PlistPath(Guid id) => Path.Combine(AgentsFolder, Label(id) + ".plist");

    static bool HasTrigger(ScheduleSettings s) => s.Kind != ScheduleKind.None || s.RunAtUserLogon || s.RunAtWindowsStartup;

    /// <summary>launchd weekday numbers: 0 = Sunday … 6 = Saturday (same as <see cref="DayOfWeek"/>).</summary>
    public static List<(int? weekday, int hour, int minute)> CalendarSlots(ScheduleSettings s)
    {
        var tod = s.TimeOfDayValue;
        var days = s.DaysOfWeek.Count == 0 ? new List<DayOfWeek> { DayOfWeek.Monday } : s.DaysOfWeek.Distinct().ToList();
        var slots = new List<(int?, int, int)>();
        switch (s.Kind)
        {
            case ScheduleKind.Daily:
                slots.Add((null, tod.Hours, tod.Minutes));
                break;
            case ScheduleKind.Weekly:
                foreach (var d in days) slots.Add(((int)d, tod.Hours, tod.Minutes));
                break;
            case ScheduleKind.Custom:
                var step = Math.Max(1, s.CustomIntervalMinutes);
                foreach (var d in days)
                    for (var t = tod.TotalMinutes; t < 24 * 60; t += step)
                        slots.Add(((int)d, (int)(t / 60), (int)(t % 60)));
                break;
        }
        return slots;
    }

    public string BuildPlist(SyncProfile p)
    {
        var s = p.Schedule;
        var sb = new StringBuilder();
        sb.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        sb.AppendLine("""<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">""");
        sb.AppendLine("""<plist version="1.0">""");
        sb.AppendLine("<dict>");
        sb.AppendLine($"  <key>Label</key><string>{Label(p.Id)}</string>");
        sb.AppendLine("  <key>ProgramArguments</key>");
        sb.AppendLine("  <array>");
        sb.AppendLine($"    <string>{SecurityElement.Escape(ExePath)}</string>");
        sb.AppendLine("    <string>--run-profile</string>");
        sb.AppendLine($"    <string>{p.Id}</string>");
        sb.AppendLine("    <string>--background</string>");
        sb.AppendLine("  </array>");

        var interval = ScheduleCalculator.IntervalMinutes(s);
        if (s.Kind is ScheduleKind.Every15Minutes or ScheduleKind.Every30Minutes or ScheduleKind.Hourly)
            sb.AppendLine($"  <key>StartInterval</key><integer>{interval * 60}</integer>");
        else
        {
            var slots = CalendarSlots(s);
            if (slots.Count > 0)
            {
                sb.AppendLine("  <key>StartCalendarInterval</key>");
                sb.AppendLine("  <array>");
                foreach (var (wd, h, m) in slots)
                {
                    sb.Append("    <dict>");
                    if (wd != null) sb.Append($"<key>Weekday</key><integer>{wd}</integer>");
                    sb.AppendLine($"<key>Hour</key><integer>{h}</integer><key>Minute</key><integer>{m}</integer></dict>");
                }
                sb.AppendLine("  </array>");
            }
        }
        // launchd agents are loaded at login, so RunAtLoad covers "at logon" / "at startup".
        if (s.RunAtUserLogon || s.RunAtWindowsStartup) sb.AppendLine("  <key>RunAtLoad</key><true/>");
        sb.AppendLine($"  <key>StandardOutPath</key><string>{SecurityElement.Escape(Path.Combine(LogFolder, "launchd.out.log"))}</string>");
        sb.AppendLine($"  <key>StandardErrorPath</key><string>{SecurityElement.Escape(Path.Combine(LogFolder, "launchd.err.log"))}</string>");
        sb.AppendLine("  <key>ProcessType</key><string>Background</string>");
        sb.AppendLine("</dict>");
        sb.AppendLine("</plist>");
        return sb.ToString();
    }

    /// <summary>Creates, updates or removes the launch agent so it matches the profile's schedule.</summary>
    public (bool ok, string message) Apply(SyncProfile p)
    {
        if (!p.Schedule.Enabled || !HasTrigger(p.Schedule))
        {
            Remove(p.Id);
            return (true, "Schedule removed.");
        }
        try
        {
            Directory.CreateDirectory(AgentsFolder);
            Directory.CreateDirectory(LogFolder);
            var path = PlistPath(p.Id);
            Unload(path);
            File.WriteAllText(path, BuildPlist(p), new UTF8Encoding(false));
            var (code, output) = Launchctl("bootstrap", Domain(), path);
            if (code != 0)
            {
                // older macOS releases / already loaded: fall back to the legacy verb
                (code, output) = Launchctl("load", "-w", path);
            }
            return code == 0 ? (true, "Schedule saved.") : (false, "launchd refused the schedule: " + output.Trim());
        }
        catch (Exception ex) { return (false, "The schedule could not be saved: " + ex.Message); }
    }

    public bool Remove(Guid id)
    {
        var path = PlistPath(id);
        Unload(path);
        try { if (File.Exists(path)) File.Delete(path); return true; } catch { return false; }
    }

    public bool Exists(Guid id) => File.Exists(PlistPath(id));

    void Unload(string path)
    {
        if (!File.Exists(path)) return;
        var (code, _) = Launchctl("bootout", Domain(), path);
        if (code != 0) Launchctl("unload", path);
    }

    static string? _uid;
    static string Domain()
    {
        _uid ??= Run("/usr/bin/id", "-u").output.Trim();
        return "gui/" + _uid;
    }

    static (int code, string output) Launchctl(params string[] args) => Run("/bin/launchctl", args);

    static (int code, string output) Run(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi)!;
            var o = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            proc.WaitForExit(30000);
            return (proc.ExitCode, o);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }
}
