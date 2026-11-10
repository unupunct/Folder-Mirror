using System.Text.Json;
using System.Text.Json.Serialization;

namespace FolderMirror.Core.Models;

public enum SyncMode { Mirror, OneWay, TwoWay, Incremental }
public enum ConflictPolicy { Skip, KeepBoth, PreferSource, PreferDestination }
public enum DeletionBehavior { MoveToArchive, DeletePermanently }
public enum ComparisonMethod { SizeAndDate, SizeOnly, DateOnly, Sha256 }
public enum ScheduleKind { None, Every15Minutes, Every30Minutes, Hourly, Daily, Weekly, Custom }

public static class ModeInfo
{
    public static string Title(SyncMode m) => m switch
    {
        SyncMode.Mirror => "Mirror",
        SyncMode.OneWay => "One-way sync",
        SyncMode.TwoWay => "Two-way sync",
        SyncMode.Incremental => "Incremental backup",
        _ => m.ToString()
    };

    public static string Description(SyncMode m) => m switch
    {
        SyncMode.Mirror => "Destination becomes an exact copy of the source. New and modified files are copied; destination files that no longer exist in the source are removed (archived by default).",
        SyncMode.OneWay => "Copies new and modified files from source to destination. Nothing is ever deleted from the destination.",
        SyncMode.TwoWay => "Changes are carried in both directions. Deletions are detected, and files changed on both sides are reported as conflicts instead of being overwritten.",
        SyncMode.Incremental => "Copies only new or modified files and keeps every previous version of overwritten files. Nothing is deleted automatically.",
        _ => ""
    };
}

public sealed class ScheduleSettings
{
    public bool Enabled { get; set; }
    public ScheduleKind Kind { get; set; } = ScheduleKind.None;
    /// <summary>Time of day (local) for Daily / Weekly / Custom schedules.</summary>
    public string TimeOfDay { get; set; } = "20:00";
    public List<DayOfWeek> DaysOfWeek { get; set; } = new() { DayOfWeek.Monday };
    /// <summary>Repeat interval in minutes for the Custom schedule.</summary>
    public int CustomIntervalMinutes { get; set; } = 120;
    public bool RunAtWindowsStartup { get; set; }
    public bool RunAtUserLogon { get; set; }
    public bool OnlyWhenAvailable { get; set; } = true;
    /// <summary>When the schedule was created/changed; interval schedules count from here.</summary>
    public DateTime AnchorLocal { get; set; } = DateTime.Now;

    public TimeSpan TimeOfDayValue =>
        TimeSpan.TryParse(TimeOfDay, out var t) ? t : new TimeSpan(20, 0, 0);
}

public sealed class SyncProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New profile";
    public string SourcePath { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    public SyncMode Mode { get; set; } = SyncMode.OneWay;

    public bool IncludeSubdirectories { get; set; } = true;
    public bool PreserveTimestamps { get; set; } = true;
    public bool PreserveAttributes { get; set; } = true;
    public bool CompareSize { get; set; } = true;
    public bool CompareModifiedDate { get; set; } = true;
    public bool CompareHash { get; set; }
    public bool IgnoreHidden { get; set; }
    public bool IgnoreSystem { get; set; }
    public bool IgnoreTemporary { get; set; }
    public bool IgnoreEmptyFolders { get; set; }
    public List<string> ExcludedExtensions { get; set; } = new();
    public List<string> ExcludedFolders { get; set; } = new();

    public ConflictPolicy ConflictPolicy { get; set; } = ConflictPolicy.Skip;
    public DeletionBehavior DeletionBehavior { get; set; } = DeletionBehavior.MoveToArchive;
    public bool BackupOverwrittenFiles { get; set; }
    public bool VerifyAfterCopy { get; set; }
    /// <summary>Scheduled (unattended) runs may delete/archive files. Off by default.</summary>
    public bool AllowUnattendedDeletions { get; set; }

    public ScheduleSettings Schedule { get; set; } = new();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public SyncProfile Clone() => FromJson(ToJson())!;
    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static SyncProfile? FromJson(string json) => JsonSerializer.Deserialize<SyncProfile>(json, Json);

    public static string ProfilesToJson(IEnumerable<SyncProfile> profiles) =>
        JsonSerializer.Serialize(new ProfileExportFile { Profiles = profiles.ToList() }, Json);

    public static List<SyncProfile> ProfilesFromJson(string json)
    {
        var f = JsonSerializer.Deserialize<ProfileExportFile>(json, Json);
        return f?.Profiles ?? new();
    }

    public void NormalizeLists()
    {
        ExcludedExtensions = ExcludedExtensions
            .Select(e => e.Trim().ToLowerInvariant()).Where(e => e.Length > 0)
            .Select(e => e.StartsWith('.') ? e : "." + e.TrimStart('*')).Distinct().ToList();
        ExcludedFolders = ExcludedFolders
            .Select(e => e.Trim().Trim('\\', '/')).Where(e => e.Length > 0).Distinct().ToList();
    }
}

public sealed class ProfileExportFile
{
    public string Format { get; set; } = "FolderMirror.Profiles";
    public int Version { get; set; } = 1;
    public List<SyncProfile> Profiles { get; set; } = new();
}

public sealed class AppSettings
{
    // General
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool ConfirmBeforeSync { get; set; } = true;
    public bool CheckDestinationAvailability { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;
    // Synchronization
    public ComparisonMethod DefaultComparison { get; set; } = ComparisonMethod.SizeAndDate;
    public SyncMode DefaultMode { get; set; } = SyncMode.OneWay;
    public ConflictPolicy DefaultConflictPolicy { get; set; } = ConflictPolicy.Skip;
    public DeletionBehavior DefaultDeletionBehavior { get; set; } = DeletionBehavior.MoveToArchive;
    public int MaxParallelOperations { get; set; } = 4;
    public bool VerifyCopies { get; set; }
    // Interface
    public bool DarkTheme { get; set; } = true;
    public string AccentColor { get; set; } = "Green";
    public string Language { get; set; } = "English";
    // Logging
    public string LogLevel { get; set; } = "Information";
    public string LogFolder { get; set; } = "";
    public int MaxLogSizeMb { get; set; } = 10;
    // Scheduling
    public bool SchedulingPaused { get; set; }
    public string? SelectedProfileId { get; set; }

    public void ApplyDefaultsTo(SyncProfile p)
    {
        p.Mode = DefaultMode;
        p.ConflictPolicy = DefaultConflictPolicy;
        p.DeletionBehavior = DefaultDeletionBehavior;
        p.VerifyAfterCopy = VerifyCopies;
        switch (DefaultComparison)
        {
            case ComparisonMethod.SizeAndDate: p.CompareSize = true; p.CompareModifiedDate = true; p.CompareHash = false; break;
            case ComparisonMethod.SizeOnly: p.CompareSize = true; p.CompareModifiedDate = false; p.CompareHash = false; break;
            case ComparisonMethod.DateOnly: p.CompareSize = false; p.CompareModifiedDate = true; p.CompareHash = false; break;
            case ComparisonMethod.Sha256: p.CompareSize = true; p.CompareModifiedDate = false; p.CompareHash = true; break;
        }
    }
}
