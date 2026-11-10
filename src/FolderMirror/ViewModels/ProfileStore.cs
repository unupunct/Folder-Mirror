using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderMirror.Core.Models;
using FolderMirror.Core.Scheduling;
using FolderMirror.Services;
using FolderMirror.Views;
using Microsoft.Win32;

namespace FolderMirror.ViewModels;

public sealed partial class ProfileCardViewModel : ObservableObject
{
    public SyncProfile Profile { get; }
    public string Name => Profile.Name;
    public string Source => Profile.SourcePath;
    public string Destination => Profile.DestinationPath;
    public string ModeTitle => ModeInfo.Title(Profile.Mode);
    public string ModeGlyph => Profile.Mode switch { SyncMode.Mirror => "", SyncMode.OneWay => "", SyncMode.TwoWay => "", _ => "" };
    public string LastSyncText { get; }
    public string ScheduleText => Profile.Schedule.Enabled ? ScheduleCalculator.Describe(Profile.Schedule) : "Not scheduled";
    public string? AvailabilityText { get; }
    public ProfileCardViewModel(SyncProfile p, DateTime? last)
    {
        Profile = p;
        if (Svc.Settings.CheckDestinationAvailability)
        {
            try
            {
                if (!PathTools_IsAvailable(p.SourcePath)) AvailabilityText = "Source drive or folder is not available";
                else if (!PathTools_IsAvailable(p.DestinationPath)) AvailabilityText = "Destination drive is not connected";
            }
            catch { }
        }
        LastSyncText = last == null ? "Never" : last.Value.ToString("g");
    }
    static bool PathTools_IsAvailable(string path) => !string.IsNullOrWhiteSpace(path) && Core.Engine.PathTools.IsRootAvailable(path);
    [RelayCommand] Task Run() => MainViewModel.Instance.RunProfileAsync(Profile);
    [RelayCommand] void Edit() => ProfileActions.Edit(Profile);
    [RelayCommand] void Delete() => ProfileActions.Delete(Profile);
    [RelayCommand] void Duplicate() => ProfileActions.Duplicate(Profile);
    [RelayCommand] void Export() => ProfileActions.Export(new[] { Profile });
    [RelayCommand] void EditSchedule() => ProfileActions.EditSchedule(Profile);
}

/// <summary>Shared, observable list of profiles used by the dashboard, profiles, schedule and sync pages.</summary>
public static class ProfileStore
{
    public static ObservableCollection<ProfileCardViewModel> Cards { get; } = new();
    public static ObservableCollection<SyncProfile> Profiles { get; } = new();

    public static void Reload()
    {
        var list = Svc.Profiles.GetAll();
        Cards.Clear(); Profiles.Clear();
        foreach (var p in list)
        {
            Profiles.Add(p);
            Cards.Add(new ProfileCardViewModel(p, Svc.History.LastSyncFor(p.Id)));
        }
        Changed?.Invoke();
    }

    public static event Action? Changed;
}

public static class ProfileActions
{
    public static void NewProfile()
    {
        var p = new SyncProfile { Name = "New synchronization" };
        Svc.Settings.ApplyDefaultsTo(p);
        OpenWizard(p, true);
    }

    public static void Edit(SyncProfile p) => OpenWizard(p.Clone(), false);

    static void OpenWizard(SyncProfile p, bool isNew)
    {
        var win = new WizardWindow(new WizardViewModel(p, isNew));
        var owner = Dialogs.Owner; if (owner != null && owner.IsVisible) win.Owner = owner;
        if (win.ShowDialog() == true)
        {
            ProfileStore.Reload();
            if (p.Schedule.Enabled) Svc.Scheduler.Apply(p);
        }
    }

    public static void Delete(SyncProfile p)
    {
        if (!Dialogs.Confirm("Delete profile?", $"'{p.Name}' will be removed together with its schedule and stored two-way state. Your files in the source and destination folders are not touched. History entries are kept.", "Delete profile", true)) return;
        Svc.Scheduler.Remove(p.Id);
        Svc.Profiles.Delete(p.Id);
        Svc.Log.Information("Profile deleted: {Name}", p.Name);
        ProfileStore.Reload();
    }

    public static void Duplicate(SyncProfile p)
    {
        var c = p.Clone();
        c.Id = Guid.NewGuid(); c.Name = p.Name + " (copy)"; c.CreatedUtc = DateTime.UtcNow;
        c.Schedule.Enabled = false; c.Schedule.Kind = ScheduleKind.None; c.Schedule.RunAtUserLogon = false; c.Schedule.RunAtWindowsStartup = false;
        Svc.Profiles.Save(c);
        ProfileStore.Reload();
    }

    public static void Export(IEnumerable<SyncProfile> profiles)
    {
        var list = profiles.ToList();
        if (list.Count == 0) { Dialogs.Info("Nothing to export", "There are no profiles to export yet."); return; }
        var dlg = new SaveFileDialog { Title = "Export profiles", Filter = "Folder Mirror profiles (*.fmprofiles.json)|*.fmprofiles.json|JSON (*.json)|*.json", FileName = list.Count == 1 ? list[0].Name + ".fmprofiles.json" : "FolderMirror-profiles.fmprofiles.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, SyncProfile.ProfilesToJson(list));
            Dialogs.Info("Export complete", $"{list.Count} profile(s) exported to:\n{dlg.FileName}");
        }
        catch (Exception ex) { Dialogs.Info("Export failed", ex.Message, true); }
    }

    public static void Import()
    {
        var dlg = new OpenFileDialog { Title = "Import profiles", Filter = "Folder Mirror profiles (*.json)|*.json|All files (*.*)|*.*" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var list = SyncProfile.ProfilesFromJson(File.ReadAllText(dlg.FileName));
            if (list.Count == 0) { Dialogs.Info("Nothing imported", "The file does not contain any profiles.", true); return; }
            var existing = Svc.Profiles.GetAll().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var p in list)
            {
                p.Id = Guid.NewGuid();
                if (existing.Contains(p.Name)) p.Name += " (imported)";
                existing.Add(p.Name);
                p.Schedule.Enabled = false;   // never activate a schedule from a foreign file silently
                Svc.Profiles.Save(p);
            }
            ProfileStore.Reload();
            Dialogs.Info("Import complete", $"{list.Count} profile(s) imported. Schedules were left disabled; review them under Scheduled Tasks.");
        }
        catch (Exception ex) { Dialogs.Info("Import failed", "The file could not be read as a Folder Mirror profile file.\n" + ex.Message, true); }
    }

    public static void EditSchedule(SyncProfile p)
    {
        var win = new ScheduleWindow(new ScheduleViewModel(p.Clone()));
        var owner = Dialogs.Owner; if (owner != null && owner.IsVisible) win.Owner = owner;
        if (win.ShowDialog() == true) { ProfileStore.Reload(); MainViewModel.Instance?.Schedules.Refresh(); }
    }
}

public sealed partial class ProfilesViewModel : ObservableObject
{
    public ObservableCollection<ProfileCardViewModel> Cards => ProfileStore.Cards;
    [ObservableProperty] bool _hasCards;
    public ProfilesViewModel()
    {
        HasCards = Cards.Count > 0;
        ProfileStore.Changed += () => HasCards = ProfileStore.Cards.Count > 0;
    }
    [RelayCommand] void New() => ProfileActions.NewProfile();
    [RelayCommand] void Import() => ProfileActions.Import();
    [RelayCommand] void ExportAll() => ProfileActions.Export(Svc.Profiles.GetAll());
}

/// <summary>Maps a RadioButton group to an enum property: ConverterParameter is the enum member name.</summary>
public sealed class ModeRadio : System.Windows.Data.IValueConverter
{
    public static readonly ModeRadio Instance = new();
    public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) => value?.ToString() == p?.ToString();
    public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c) =>
        value is true ? Enum.Parse<SyncMode>(p.ToString()!) : System.Windows.Data.Binding.DoNothing;
}
