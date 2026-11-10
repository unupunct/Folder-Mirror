using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FolderMirror.Core.Models;
using FolderMirror.Mac.ViewModels;

namespace FolderMirror.Mac.Services;

/// <summary>
/// Developer tool: <c>FolderMirror --screenshots &lt;dir&gt;</c> builds throw-away demo data in a temp folder, drives the
/// UI through its pages and saves PNGs. Run it with FOLDERMIRROR_DATA pointing at an empty folder so no real data is shown.
/// </summary>
public static class ScreenshotTool
{
    public static async Task RunAsync(string outDir, Window main, IClassicDesktopStyleApplicationLifetime app)
    {
        try
        {
            Directory.CreateDirectory(outDir);
            var vm = (MainViewModel)main.DataContext!;
            var root = Path.Combine(Path.GetTempPath(), "FolderMirrorDemo-" + Environment.ProcessId);
            var profiles = await SeedAsync(root);
            await Task.Delay(500);

            foreach (var key in new[] { "dashboard", "profiles" })
            {
                vm.GoTo(key); await Task.Delay(500); Save(main, Path.Combine(outDir, $"mac-{key}.png"));
            }

            vm.Sync.Open(profiles[0]);
            await Task.Delay(1800); Save(main, Path.Combine(outDir, "mac-sync-preview.png"));

            vm.GoTo("schedules"); await Task.Delay(500); Save(main, Path.Combine(outDir, "mac-schedules.png"));
            vm.GoTo("history"); await Task.Delay(500);
            vm.History.Selected = vm.History.Runs.FirstOrDefault();
            await Task.Delay(500); Save(main, Path.Combine(outDir, "mac-history.png"));
            vm.GoTo("settings"); await Task.Delay(500); Save(main, Path.Combine(outDir, "mac-settings.png"));

            var editor = new Views.ProfileEditorWindow { DataContext = new ProfileEditorViewModel(profiles[1].Clone(), false) };
            editor.Show(main); await Task.Delay(700);
            Save(editor, Path.Combine(outDir, "mac-profile-editor.png"));
            editor.Close();
            try { Directory.Delete(root, true); } catch { }
        }
        catch (Exception ex) { Console.Error.WriteLine("Screenshot run failed: " + ex); }
        finally { Dispatcher.UIThread.Post(() => app.Shutdown(0)); }
    }

    static void Save(TopLevel w, string path)
    {
        var size = new PixelSize(Math.Max(1, (int)w.Bounds.Width), Math.Max(1, (int)w.Bounds.Height));
        using var bmp = new RenderTargetBitmap(size, new Vector(96, 96));
        bmp.Render(w);
        using (var fs = File.Create(path)) bmp.Save(fs);
    }

    static void Make(string dir, string name, int kb, string? text = null)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        var rnd = new Random(name.GetHashCode());
        var bytes = new byte[kb * 1024]; rnd.NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
    }

    static async Task<List<SyncProfile>> SeedAsync(string root)
    {
        var docs = Path.Combine(root, "Home", "Documents");
        var photos = Path.Combine(root, "Home", "Photos");
        var work = Path.Combine(root, "Home", "Projects");
        foreach (var n in new[] { "Budget 2026.xlsx", "Meeting notes.md", "Travel plan.pdf", "Contract draft.docx", "Recipes.txt" }) Make(docs, n, 120);
        foreach (var n in new[] { "IMG_0101.jpg", "IMG_0102.jpg", "IMG_0103.jpg", "IMG_0104.jpg" }) Make(Path.Combine(photos, "Summer"), n, 900);
        foreach (var n in new[] { "report.tex", "figures.zip", "todo.md" }) Make(work, n, 60);

        var list = new List<SyncProfile>
        {
            new() { Name = "Documents backup", Mode = SyncMode.OneWay, SourcePath = docs, DestinationPath = Path.Combine(root, "Backup", "Documents"),
                    Schedule = new ScheduleSettings { Enabled = true, Kind = ScheduleKind.Daily, TimeOfDay = "20:00" } },
            new() { Name = "Photos mirror", Mode = SyncMode.Mirror, SourcePath = photos, DestinationPath = Path.Combine(root, "External", "Photos") },
            new() { Name = "Projects two-way", Mode = SyncMode.TwoWay, SourcePath = work, DestinationPath = Path.Combine(root, "Cloud", "Projects"),
                    Schedule = new ScheduleSettings { Enabled = true, Kind = ScheduleKind.Every30Minutes } }
        };
        foreach (var p in list) { Svc.Profiles.Save(p); }
        foreach (var p in list)
        {
            var plan = await Svc.Engine.PlanAsync(p, 4, null, CancellationToken.None);
            await Svc.Coordinator.ExecuteAsync(plan, new ExecuteOptions { ConfirmedDeletions = true, Trigger = "Manual" });
        }
        // leave pending changes so the preview has something to show
        Make(docs, "Quarterly review.pptx", 340);
        Make(docs, "Invoice March.pdf", 80);
        File.WriteAllText(Path.Combine(docs, "Meeting notes.md"), new string('x', 2048));
        File.SetLastWriteTimeUtc(Path.Combine(docs, "Meeting notes.md"), DateTime.UtcNow.AddMinutes(5));
        return list;
    }
}
