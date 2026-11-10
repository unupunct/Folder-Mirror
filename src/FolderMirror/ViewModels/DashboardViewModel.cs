using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderMirror.Core.Data;
using FolderMirror.Services;

namespace FolderMirror.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    public ObservableCollection<ProfileCardViewModel> Cards => ProfileStore.Cards;
    public ObservableCollection<RunRecord> Recent { get; } = new();
    public RunCoordinator Coordinator => Svc.Coordinator;

    [ObservableProperty] int _profileCount;
    [ObservableProperty] string _filesText = "0";
    [ObservableProperty] string _lastText = "Never";
    [ObservableProperty] string _nextText = "—";
    [ObservableProperty] bool _hasProfiles;
    [ObservableProperty] bool _hasRecent;

    public DashboardViewModel()
    {
        Svc.Coordinator.RunFinished += (_, _) => System.Windows.Application.Current.Dispatcher.Invoke(Refresh);
    }

    public void Refresh()
    {
        try
        {
            ProfileStore.Reload();
            ProfileCount = Cards.Count; HasProfiles = Cards.Count > 0;
            var st = Svc.History.Stats();
            FilesText = st.FilesSynchronized.ToString("N0");
            LastText = st.LastSyncLocal?.ToString("g") ?? "Never";
            var next = Svc.NextScheduledRun();
            NextText = Svc.Settings.SchedulingPaused ? "Paused" : next?.ToString("g") ?? "None scheduled";
            Recent.Clear();
            foreach (var r in Svc.History.Query(limit: 8)) Recent.Add(r);
            HasRecent = Recent.Count > 0;
        }
        catch (Exception ex) { Svc.Log.Error(ex, "Dashboard refresh failed"); }
    }

    [RelayCommand] void NewSync() => ProfileActions.NewProfile();
    [RelayCommand] void Cancel() => Svc.Coordinator.Cancel();
    [RelayCommand] void ShowHistory() => MainViewModel.Instance.Go(Page.History);
}
