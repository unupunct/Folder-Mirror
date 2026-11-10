using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using FolderMirror.Core.Models;

namespace FolderMirror.Services;

public sealed class TrayService : IDisposable
{
    readonly NotifyIcon _icon;
    readonly ContextMenuStrip _menu = new();
    public event Action? OpenRequested;
    public event Action<SyncProfile>? RunProfileRequested;
    public event Action? StatusRequested;
    public event Action? ExitRequested;

    public TrayService()
    {
        _icon = new NotifyIcon { Text = "Folder Mirror", Visible = false, ContextMenuStrip = _menu };
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/FolderMirror.ico");
            using var s = System.Windows.Application.GetResourceStream(uri).Stream;
            _icon.Icon = new Icon(s);
        }
        catch { _icon.Icon = SystemIcons.Application; }
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        _menu.Renderer = new ToolStripProfessionalRenderer(new DarkColors());
        _menu.Opening += (_, _) => Rebuild();
        _menu.ShowImageMargin = false;
        _menu.BackColor = Color.FromArgb(31, 34, 38);
        _menu.ForeColor = Color.FromArgb(232, 234, 237);
        _menu.Font = new Font("Segoe UI", 9.5f);
    }

    public bool Visible { get => _icon.Visible; set => _icon.Visible = value; }

    void Rebuild()
    {
        _menu.Items.Clear();
        _menu.Items.Add(new ToolStripMenuItem(Loc.T("tray.open"), null, (_, _) => OpenRequested?.Invoke()) { Font = new Font(_menu.Font, System.Drawing.FontStyle.Bold) });
        var run = new ToolStripMenuItem(Loc.T("tray.run"));
        List<SyncProfile> profiles = new();
        try { profiles = Svc.Profiles.GetAll(); } catch { }
        if (profiles.Count == 0) run.DropDownItems.Add(new ToolStripMenuItem("(no profiles yet)") { Enabled = false });
        foreach (var p in profiles)
        {
            var prof = p;
            run.DropDownItems.Add(new ToolStripMenuItem(prof.Name, null, (_, _) => RunProfileRequested?.Invoke(prof)) { Enabled = !Svc.Coordinator.IsBusy });
        }
        run.DropDown.BackColor = Color.FromArgb(31, 34, 38); run.DropDown.ForeColor = Color.FromArgb(232, 234, 237);
        _menu.Items.Add(run);
        _menu.Items.Add(new ToolStripMenuItem(Loc.T("tray.status"), null, (_, _) => StatusRequested?.Invoke()));
        _menu.Items.Add(new ToolStripSeparator());
        var pause = new ToolStripMenuItem(Loc.T("tray.pause")) { Checked = Svc.Settings.SchedulingPaused, CheckOnClick = false };
        pause.Click += (_, _) =>
        {
            Svc.Settings.SchedulingPaused = !Svc.Settings.SchedulingPaused;
            Svc.SaveSettings();
            Notify("Folder Mirror", Svc.Settings.SchedulingPaused ? "Scheduled synchronization is paused." : "Scheduled synchronization resumed.", false);
            PauseChanged?.Invoke();
        };
        _menu.Items.Add(pause);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem(Loc.T("tray.exit"), null, (_, _) => ExitRequested?.Invoke()));
    }

    public event Action? PauseChanged;

    public string StatusSummary()
    {
        var c = Svc.Coordinator;
        if (c.IsRunning) return $"{c.StatusText} {c.DetailText}";
        var last = Svc.History.Stats().LastSyncLocal;
        return (Svc.Settings.SchedulingPaused ? "Scheduling is paused. " : "") +
               (last == null ? "No synchronization has run yet." : $"Last synchronization: {last:g}.");
    }

    public void Notify(string title, string text, bool error)
    {
        if (!_icon.Visible) return;
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text.Length > 250 ? text[..247] + "…" : text;
        _icon.BalloonTipIcon = error ? ToolTipIcon.Error : ToolTipIcon.Info;
        _icon.ShowBalloonTip(5000);
    }

    public void SetTooltip(string text) => _icon.Text = text.Length > 60 ? text[..59] : text;

    public void Dispose() { _icon.Visible = false; _icon.Dispose(); _menu.Dispose(); }

    sealed class DarkColors : ProfessionalColorTable
    {
        static readonly Color Bg = Color.FromArgb(31, 34, 38), Hover = Color.FromArgb(55, 61, 68), Border = Color.FromArgb(52, 58, 65);
        public override Color ToolStripDropDownBackground => Bg;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemBorder => Hover;
        public override Color MenuBorder => Border;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
        public override Color CheckBackground => Hover;
        public override Color CheckSelectedBackground => Hover;
    }
}
