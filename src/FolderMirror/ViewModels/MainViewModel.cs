using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderMirror.Core.Models;
using FolderMirror.Services;
using FolderMirror.Views;

namespace FolderMirror.ViewModels;

public enum Page { Dashboard, Synchronization, Profiles, Schedules, History, Settings, About }

public partial class NavItem : ObservableObject
{
    public Page Page { get; }
    public string Glyph { get; }
    readonly string _key;
    [ObservableProperty] bool _isSelected;
    public string Title => Loc.T(_key);
    public NavItem(Page page, string glyph, string key) { Page = page; Glyph = glyph; _key = key; Loc.Instance.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Title)); }
}

public partial class MainViewModel : ObservableObject
{
    public static MainViewModel Instance { get; private set; } = null!;
    public ObservableCollection<NavItem> NavItems { get; }
    public DashboardViewModel Dashboard { get; }
    public SyncViewModel Sync { get; }
    public ProfilesViewModel Profiles { get; }
    public SchedulesViewModel Schedules { get; }
    public HistoryViewModel History { get; }
    public SettingsViewModel SettingsPage { get; }
    public AboutViewModel About { get; }
    public RunCoordinator Coordinator => Svc.Coordinator;

    [ObservableProperty] object? _current;
    [ObservableProperty] Page _currentPage;
    public event Action<Page>? PageChanged;

    public MainViewModel()
    {
        Instance = this;
        NavItems = new()
        {
            new(Page.Dashboard, "", "nav.dashboard"), new(Page.Synchronization, "", "nav.sync"),
            new(Page.Profiles, "", "nav.profiles"), new(Page.Schedules, "", "nav.schedules"),
            new(Page.History, "", "nav.history"), new(Page.Settings, "", "nav.settings"), new(Page.About, "", "nav.about")
        };
        ProfileStore.Reload();
        Dashboard = new(); Sync = new(); Profiles = new(); Schedules = new(); History = new(); SettingsPage = new(); About = new();
        Go(Page.Dashboard);
    }

    [RelayCommand] void Navigate(NavItem item) => Go(item.Page);

    public void Go(Page page)
    {
        foreach (var n in NavItems) n.IsSelected = n.Page == page;
        CurrentPage = page;
        switch (page)
        {
            case Page.Dashboard: Dashboard.Refresh(); Current = Dashboard; break;
            case Page.Synchronization: Sync.Refresh(); Current = Sync; break;
            case Page.Profiles: ProfileStore.Reload(); Current = Profiles; break;
            case Page.Schedules: Schedules.Refresh(); Current = Schedules; break;
            case Page.History: History.Refresh(); Current = History; break;
            case Page.Settings: Current = SettingsPage; break;
            case Page.About: Current = About; break;
        }
        PageChanged?.Invoke(page);
    }

    /// <summary>Open the Synchronization page for a profile, build the preview, and start straight away only when that is safe.</summary>
    public async Task RunProfileAsync(SyncProfile profile)
    {
        if (Coordinator.IsBusy) { Dialogs.Info("Synchronization in progress", "Another synchronization is running. Wait for it to finish or cancel it first."); return; }
        Go(Page.Synchronization);
        Sync.SelectedProfile = ProfileStore.Profiles.FirstOrDefault(p => p.Id == profile.Id) ?? profile;
        await Sync.PreviewAsync();
        var plan = Sync.Plan;
        if (plan == null) return;
        bool safeToAuto = profile.Mode != SyncMode.Mirror && !plan.HasDeletions && !plan.HasBlockingIssues
                          && plan.Items.All(i => i.Action != PlanAction.Conflict)
                          && !Svc.Settings.ConfirmBeforeSync
                          && plan.Items.Any(i => i.Action != PlanAction.Unchanged);
        if (safeToAuto) await Sync.StartAsync(skipConfirm: true);
    }
}
