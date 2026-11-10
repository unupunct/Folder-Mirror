using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FolderMirror.Core.Models;
using FolderMirror.ViewModels;
using FolderMirror.Views;

namespace FolderMirror.Services;

/// <summary>
/// Developer tool (FolderMirror.exe --screenshots OUT --demo-root DEMO): builds a small demo setup in DEMO
/// (using the FOLDERMIRROR_DATA data folder) and renders every page to PNG for the README. Never touches real data.
/// </summary>
public static class ScreenshotTool
{
    public static async Task RunAsync(string outDir, string demoRoot, MainWindow main)
    {
        Directory.CreateDirectory(outDir);
        SeedDemo(demoRoot);
        await Task.Delay(300);

        // run initial syncs so History has content, then create changes so previews are interesting
        var profiles = Svc.Profiles.GetAll();
        foreach (var p in profiles)
        {
            if (p.Mode == SyncMode.Mirror) continue;           // mirror starts with a populated, slightly different destination
            var plan = await Svc.Engine.PlanAsync(p, 4, null, default);
            await Svc.Coordinator.ExecuteAsync(plan, new ExecuteOptions { ConfirmedDeletions = true, Trigger = p.Mode == SyncMode.Incremental ? "Scheduled" : "Manual" });
        }
        var mirror = profiles.First(p => p.Mode == SyncMode.Mirror);
        var mplan0 = await Svc.Engine.PlanAsync(mirror, 4, null, default);
        await Svc.Coordinator.ExecuteAsync(mplan0, new ExecuteOptions { ConfirmedDeletions = true, Trigger = "Manual" });
        ApplyDemoChanges(demoRoot);

        var vm = MainViewModel.Instance;
        main.Width = 1240; main.Height = 800; main.UpdateLayout();

        async Task Shot(string name, Func<Task>? prep = null)
        {
            if (prep != null) await prep();
            await Task.Delay(900);
            await main.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var cc = (System.Windows.Controls.ContentControl)((System.Windows.Controls.Grid)main.Content).Children[1];
            var ch = cc.Content as FrameworkElement; var kid = System.Windows.Media.VisualTreeHelper.GetChildrenCount(cc) > 0 ? System.Windows.Media.VisualTreeHelper.GetChild(cc, 0) as FrameworkElement : null;
            Svc.Log.Information("SHOT {Name}: cc {W}x{H} child {CW}x{CH}", name, cc.ActualWidth, cc.ActualHeight, kid?.ActualWidth, kid?.ActualHeight);
            Capture(main, Path.Combine(outDir, name + ".png"));
        }

        await Shot("dashboard", () => { vm.Go(Page.Dashboard); return Task.CompletedTask; });
        await Shot("preview-mirror", async () =>
        {
            vm.Go(Page.Synchronization);
            vm.Sync.SelectedProfile = ProfileStore.Profiles.First(p => p.Mode == SyncMode.Mirror);
            await vm.Sync.PreviewAsync();
        });
        await Shot("preview-conflicts", async () =>
        {
            vm.Sync.SelectedProfile = ProfileStore.Profiles.First(p => p.Mode == SyncMode.TwoWay);
            await vm.Sync.PreviewAsync();
        });
        await Shot("profiles", () => { vm.Go(Page.Profiles); return Task.CompletedTask; });
        await Shot("schedules", () => { vm.Go(Page.Schedules); return Task.CompletedTask; });
        await Shot("history", () =>
        {
            vm.Go(Page.History);
            vm.History.Selected = vm.History.Runs.FirstOrDefault(r => r.FilesCopied > 0) ?? vm.History.Runs.FirstOrDefault();
            return Task.CompletedTask;
        });
        await Shot("settings", () => { vm.Go(Page.Settings); return Task.CompletedTask; });
        await Shot("about", () => { vm.Go(Page.About); return Task.CompletedTask; });

        // wizard + schedule dialog
        var wp = Svc.Profiles.GetAll().First(p => p.Mode == SyncMode.Mirror);
        var wvm = new WizardViewModel(wp.Clone(), false);
        var wiz = new WizardWindow(wvm) { Owner = main, Width = 840, Height = 740 };
        wiz.Show();
        await Task.Delay(700); Capture(wiz, Path.Combine(outDir, "wizard-1-folders.png"));
        wvm.NextCommand.Execute(null); await Task.Delay(500); Capture(wiz, Path.Combine(outDir, "wizard-2-mode.png"));
        wvm.NextCommand.Execute(null); await Task.Delay(500); Capture(wiz, Path.Combine(outDir, "wizard-3-options.png"));
        wvm.NextCommand.Execute(null); wvm.Reviewed = true; await Task.Delay(500); Capture(wiz, Path.Combine(outDir, "wizard-4-review.png"));
        wiz.Close();
        var sw = new ScheduleWindow(new ScheduleViewModel(wp.Clone())) { Owner = main };
        sw.Show(); await Task.Delay(600); Capture(sw, Path.Combine(outDir, "schedule-dialog.png")); sw.Close();

        // light theme sample
        ThemeManager.Apply(false, "Blue");
        await Shot("dashboard-light", () => { vm.Go(Page.Dashboard); return Task.CompletedTask; });
        ThemeManager.Apply(true, "Green");
    }

    public static void Capture(Window w, string file)
    {
        w.UpdateLayout();
        var content = (FrameworkElement)VisualTreeHelper.GetChild(w, 0);
        content.UpdateLayout();
        int width = (int)Math.Ceiling(content.ActualWidth), height = (int)Math.Ceiling(content.ActualHeight);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(w.Background, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, width, height));
        }
        var rtb = new RenderTargetBitmap(width * 2, height * 2, 192, 192, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(file); enc.Save(fs);
    }

    static void W(string root, string rel, string content, int mtimeMinutesAgo = 600)
    {
        var f = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllText(f, content);
        File.SetLastWriteTime(f, DateTime.Now.AddMinutes(-mtimeMinutesAgo));
    }
    static void Big(string root, string rel, int kb, int mtimeMinutesAgo = 600)
    {
        var f = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        var b = new byte[kb * 1024]; new Random(kb).NextBytes(b);
        File.WriteAllBytes(f, b);
        File.SetLastWriteTime(f, DateTime.Now.AddMinutes(-mtimeMinutesAgo));
    }

    static void SeedDemo(string demo)
    {
        if (Directory.Exists(demo)) Directory.Delete(demo, true);
        foreach (var old in Svc.Profiles.GetAll()) Svc.Profiles.Delete(old.Id);
        Svc.History.Clear();
        var docs = Path.Combine(demo, "Documents"); var docsBk = Path.Combine(demo, "Backup", "Documents");
        var pics = Path.Combine(demo, "Pictures"); var picsBk = Path.Combine(demo, "Backup", "Pictures");
        var projA = Path.Combine(demo, "Projects"); var projB = Path.Combine(demo, "Laptop", "Projects");
        var week = Path.Combine(demo, "Work"); var weekBk = Path.Combine(demo, "Backup", "Work (versions)");

        foreach (var r in new[] { docs, pics, projA, projB, week })
        {
            Directory.CreateDirectory(r);
        }
        W(docs, "Annual Report 2026.docx", "report v1 " + new string('a', 800)); Big(docs, "Budget 2026.xlsx", 340); W(docs, @"Notes\meeting-notes.txt", "Meeting notes");
        W(docs, @"Notes\ideas.md", "# Ideas"); Big(docs, @"Contracts\lease-agreement.pdf", 1450); W(docs, @"Contracts\summary.txt", "Summary");
        W(docs, "Travel itinerary.pdf", "itinerary"); W(docs, "Recipes.txt", "pancakes");
        Big(pics, "Holiday\\IMG_0001.jpg", 2400); Big(pics, "Holiday\\IMG_0002.jpg", 3100); Big(pics, "Holiday\\IMG_0003.jpg", 2750); Big(pics, "Family\\Birthday.png", 1800);
        W(projA, "design.md", "# Design"); W(projA, "todo.txt", "todo"); Big(projA, "assets\\logo.png", 90); W(projA, "src\\main.cs", "class Program {}");
        W(week, "plan.docx", "plan"); Big(week, "data.csv", 220);

        Svc.Profiles.Save(new SyncProfile
        {
            Name = "Documents mirror", SourcePath = docs, DestinationPath = docsBk, Mode = SyncMode.Mirror,
            ExcludedExtensions = { ".tmp", ".log" }, IgnoreTemporary = true
        });
        Svc.Profiles.Save(new SyncProfile { Name = "Photo archive", SourcePath = pics, DestinationPath = picsBk, Mode = SyncMode.OneWay, VerifyAfterCopy = true });
        var two = new SyncProfile { Name = "Project files (desktop ⇄ laptop)", SourcePath = projA, DestinationPath = projB, Mode = SyncMode.TwoWay };
        Svc.Profiles.Save(two);
        var weekly = new SyncProfile { Name = "Work – weekly backup", SourcePath = week, DestinationPath = weekBk, Mode = SyncMode.Incremental };
        weekly.Schedule = new ScheduleSettings { Enabled = true, Kind = ScheduleKind.Weekly, TimeOfDay = "21:30", DaysOfWeek = { DayOfWeek.Friday }, AnchorLocal = DateTime.Now };
        Svc.Profiles.Save(weekly);
        var hourly = new SyncProfile { Name = "Photos to NAS (hourly)", SourcePath = pics, DestinationPath = Path.Combine(demo, "NAS", "Pictures"), Mode = SyncMode.OneWay };
        hourly.Schedule = new ScheduleSettings { Enabled = true, Kind = ScheduleKind.Hourly, AnchorLocal = DateTime.Now.AddMinutes(-20) };
        Svc.Profiles.Save(hourly);
        ProfileStore.Reload();
    }

    static void ApplyDemoChanges(string demo)
    {
        var docs = Path.Combine(demo, "Documents");
        // mirror: modified, new, and removed files
        W(docs, "Annual Report 2026.docx", "report v2 – updated figures " + new string('b', 1400), 5);
        Big(docs, "Budget 2026.xlsx", 410, 8);
        W(docs, @"Notes\action-items.txt", "New action items", 2);
        Big(docs, @"Contracts\signed-contract.pdf", 880, 3);
        File.Delete(Path.Combine(docs, "Travel itinerary.pdf"));
        File.Delete(Path.Combine(docs, "Recipes.txt"));
        // two-way: edit the same file on both sides -> conflict, plus one-sided edits
        var a = Path.Combine(demo, "Projects"); var b = Path.Combine(demo, "Laptop", "Projects");
        W(a, "design.md", "# Design (edited on desktop)", 12);
        W(b, "design.md", "# Design (edited on laptop, different change)", 4);
        W(b, "todo.txt", "todo\n- laptop item", 6);
        W(a, @"src\utils.cs", "static class Utils {}", 9);
    }
}
