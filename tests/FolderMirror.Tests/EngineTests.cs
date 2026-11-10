using System.Security.Cryptography;
using FolderMirror.Core.Data;
using FolderMirror.Core.Engine;
using FolderMirror.Core.Models;

namespace FolderMirror.Tests;

public class EngineTests
{
    // 1
    [Fact]
    public async Task EmptySource_NothingCopied()
    {
        using var sb = new Sandbox();
        var plan = await sb.Plan();
        Assert.Empty(plan.Items);
        var r = await sb.Sync();
        Assert.Equal(SyncStatus.Completed, r.Status);
        Assert.Equal(0, r.FilesCopied);
        Assert.Empty(Directory.GetFileSystemEntries(sb.Dst));
    }

    // 2
    [Fact]
    public async Task EmptyDestination_AllFilesCopied_WithSubfolders()
    {
        using var sb = new Sandbox();
        sb.Write(sb.Src, "a.txt", "A"); sb.Write(sb.Src, @"sub\b.txt", "B"); sb.Write(sb.Src, @"sub\deep\c.txt", "C");
        Directory.CreateDirectory(Path.Combine(sb.Src, "emptyfolder"));
        var r = await sb.Sync();
        Assert.Equal(3, r.FilesCopied);
        Assert.Equal("C", Sandbox.Read(sb.Dst, @"sub\deep\c.txt"));
        Assert.True(Directory.Exists(Path.Combine(sb.Dst, "emptyfolder")));
    }

    // 3
    [Fact]
    public async Task NewFile_IsCopied_WithTimestampPreserved()
    {
        using var sb = new Sandbox();
        var t = new DateTime(2024, 3, 5, 10, 11, 12, DateTimeKind.Utc);
        sb.Write(sb.Src, "new.txt", "hello", t);
        var plan = await sb.Plan();
        Assert.Equal(PlanAction.New, plan.Items.Single().Action);
        var r = await sb.Sync();
        Assert.Equal(1, r.FilesCopied);
        Assert.Equal("hello", Sandbox.Read(sb.Dst, "new.txt"));
        Assert.Equal(t, File.GetLastWriteTimeUtc(Path.Combine(sb.Dst, "new.txt")));
        Assert.Empty(Directory.GetFiles(sb.Dst, "*" + PathTools.TempSuffix, SearchOption.AllDirectories));
    }

    // 4
    [Fact]
    public async Task ModifiedFile_IsUpdated_AndSecondRunIsIdle()
    {
        using var sb = new Sandbox();
        sb.Write(sb.Src, "doc.txt", "v1"); await sb.Sync();
        sb.Write(sb.Src, "doc.txt", "version two", DateTime.UtcNow);
        var plan = await sb.Plan();
        Assert.Equal(PlanAction.Modified, plan.Items.Single().Action);
        var r = await sb.Sync();
        Assert.Equal(1, r.FilesModified);
        Assert.Equal("version two", Sandbox.Read(sb.Dst, "doc.txt"));
        var again = await sb.Plan();
        Assert.All(again.Items, i => Assert.Equal(PlanAction.Unchanged, i.Action));
    }

    // 5
    [Fact]
    public async Task DeletedSourceFile_OneWay_KeepsDestinationCopy()
    {
        using var sb = new Sandbox(SyncMode.OneWay);
        sb.Write(sb.Src, "gone.txt", "x"); await sb.Sync();
        File.Delete(Path.Combine(sb.Src, "gone.txt"));
        var r = await sb.Sync();
        Assert.Equal(0, r.FilesDeleted);
        Assert.True(Sandbox.Exists(sb.Dst, "gone.txt"));
    }

    // 6
    [Fact]
    public async Task Mirror_PreviewNeverDeletes_ExecuteArchivesAndNeedsConfirmation()
    {
        using var sb = new Sandbox(SyncMode.Mirror);
        sb.Write(sb.Src, "keep.txt", "k");
        sb.Write(sb.Dst, "old.txt", "old"); sb.Write(sb.Dst, @"olddir\x.txt", "x");
        var plan = await sb.Plan();
        Assert.True(plan.HasDeletions);
        Assert.Equal(2, plan.Summary.FilesToDelete);
        Assert.True(Sandbox.Exists(sb.Dst, "old.txt")); // preview: untouched
        Assert.Contains(plan.Issues, i => i.Code == "DestinationUnrelated");

        // no confirmation -> refused, nothing deleted
        await Assert.ThrowsAsync<InvalidOperationException>(() => sb.Engine().ExecuteAsync(plan, new ExecuteOptions(), null, CancellationToken.None));
        Assert.True(Sandbox.Exists(sb.Dst, "old.txt"));

        var r = await sb.Sync();
        Assert.Equal(2, r.FilesDeleted);
        Assert.False(Sandbox.Exists(sb.Dst, "old.txt"));
        Assert.False(Directory.Exists(Path.Combine(sb.Dst, "olddir")));
        Assert.True(Sandbox.Exists(sb.Dst, "keep.txt"));
        var archived = Directory.GetFiles(Path.Combine(sb.Dst, PathTools.ReservedFolder, "Archive"), "old.txt", SearchOption.AllDirectories);
        Assert.Single(archived);

        // archive folder is never itself mirrored away
        var r2 = await sb.Sync();
        Assert.Equal(0, r2.FilesDeleted);
        Assert.Single(Directory.GetFiles(Path.Combine(sb.Dst, PathTools.ReservedFolder, "Archive"), "old.txt", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Mirror_PermanentDeletion_WhenConfigured()
    {
        using var sb = new Sandbox(SyncMode.Mirror);
        sb.Profile.DeletionBehavior = DeletionBehavior.DeletePermanently;
        sb.Write(sb.Dst, "old.txt", "old");
        await sb.Sync();
        Assert.False(Sandbox.Exists(sb.Dst, "old.txt"));
        Assert.False(Directory.Exists(Path.Combine(sb.Dst, PathTools.ReservedFolder, "Archive")));
    }

    [Fact]
    public async Task Mirror_Unattended_SkipsDeletionsUnlessAllowed()
    {
        using var sb = new Sandbox(SyncMode.Mirror);
        sb.Write(sb.Src, "a.txt", "a"); sb.Write(sb.Dst, "stale.txt", "s");
        var r = await sb.Sync(o: new ExecuteOptions { Unattended = true });
        Assert.Equal(1, r.FilesCopied); Assert.Equal(0, r.FilesDeleted);
        Assert.True(Sandbox.Exists(sb.Dst, "stale.txt"));
        sb.Profile.AllowUnattendedDeletions = true;
        var r2 = await sb.Sync(o: new ExecuteOptions { Unattended = true });
        Assert.Equal(1, r2.FilesDeleted);
        Assert.False(Sandbox.Exists(sb.Dst, "stale.txt"));
    }

    // 7
    [Fact]
    public async Task OneWay_CopiesToDestination_NeverTouchesDestinationOnlyFiles()
    {
        using var sb = new Sandbox(SyncMode.OneWay);
        sb.Write(sb.Src, "s.txt", "s"); sb.Write(sb.Dst, "d-only.txt", "d");
        var r = await sb.Sync();
        Assert.Equal(1, r.FilesCopied);
        Assert.True(Sandbox.Exists(sb.Dst, "d-only.txt"));
        Assert.False(Sandbox.Exists(sb.Src, "d-only.txt"));
    }

    // 8
    [Fact]
    public async Task TwoWay_PropagatesNewModifiedAndDeletedFiles()
    {
        using var sb = new Sandbox(SyncMode.TwoWay);
        sb.Write(sb.Src, "fromsrc.txt", "S"); sb.Write(sb.Dst, "fromdst.txt", "D");
        var r1 = await sb.Sync();
        Assert.Equal(2, r1.FilesCopied);
        Assert.Equal("S", Sandbox.Read(sb.Dst, "fromsrc.txt"));
        Assert.Equal("D", Sandbox.Read(sb.Src, "fromdst.txt"));

        sb.Write(sb.Src, "fromdst.txt", "D edited on source side", DateTime.UtcNow);       // modify on source
        sb.Write(sb.Dst, "fromsrc.txt", "S edited on destination side!", DateTime.UtcNow); // modify on destination
        var r2 = await sb.Sync();
        Assert.Equal(2, r2.FilesModified);
        Assert.Equal("D edited on source side", Sandbox.Read(sb.Dst, "fromdst.txt"));
        Assert.Equal("S edited on destination side!", Sandbox.Read(sb.Src, "fromsrc.txt"));

        File.Delete(Path.Combine(sb.Src, "fromsrc.txt"));        // delete on source
        var r3 = await sb.Sync();
        Assert.Equal(1, r3.FilesDeleted);
        Assert.False(Sandbox.Exists(sb.Dst, "fromsrc.txt"));
        Assert.True(Directory.GetFiles(Path.Combine(sb.Dst, PathTools.ReservedFolder, "Archive"), "fromsrc.txt", SearchOption.AllDirectories).Length == 1);

        File.Delete(Path.Combine(sb.Dst, "fromdst.txt"));        // delete on destination
        var r4 = await sb.Sync();
        Assert.Equal(1, r4.FilesDeleted);
        Assert.False(Sandbox.Exists(sb.Src, "fromdst.txt"));
    }

    // 9
    [Fact]
    public async Task TwoWay_Conflict_IsNeverSilentlyOverwritten()
    {
        using var sb = new Sandbox(SyncMode.TwoWay);
        sb.Write(sb.Src, "c.txt", "base"); await sb.Sync();
        sb.Write(sb.Src, "c.txt", "source edit", DateTime.UtcNow.AddMinutes(-2));
        sb.Write(sb.Dst, "c.txt", "destination edit (newer)", DateTime.UtcNow);

        var plan = await sb.Plan();
        var item = plan.Items.Single(i => i.RelPath == "c.txt");
        Assert.Equal(PlanAction.Conflict, item.Action);
        Assert.Equal(ConflictResolution.Skip, item.Resolution); // safe default

        var r = await sb.Sync();
        Assert.Equal(1, r.ConflictsLeft);
        Assert.Equal("source edit", Sandbox.Read(sb.Src, "c.txt"));
        Assert.Equal("destination edit (newer)", Sandbox.Read(sb.Dst, "c.txt")); // newest timestamp did NOT win

        // Resolve: keep both
        var r2 = await sb.Sync(tweak: p => p.Items.Single(i => i.RelPath == "c.txt").Resolution = ConflictResolution.KeepBoth);
        Assert.Equal(SyncStatus.Completed, r2.Status);
        Assert.Equal("source edit", Sandbox.Read(sb.Dst, "c.txt"));
        var copies = Directory.GetFiles(sb.Src, "c (conflict copy*").Concat(Directory.GetFiles(sb.Dst, "c (conflict copy*")).ToList();
        Assert.Equal(2, copies.Count);
        Assert.All(copies, f => Assert.Equal("destination edit (newer)", File.ReadAllText(f)));
        var after = await sb.Plan();
        Assert.DoesNotContain(after.Items, i => i.Action != PlanAction.Unchanged);
    }

    [Fact]
    public async Task TwoWay_Conflict_UseDestination_BacksUpOverwrittenSourceVersion()
    {
        using var sb = new Sandbox(SyncMode.TwoWay);
        sb.Write(sb.Src, "c.txt", "base"); await sb.Sync();
        sb.Write(sb.Src, "c.txt", "src version"); sb.Write(sb.Dst, "c.txt", "dst version!", DateTime.UtcNow);
        await sb.Sync(tweak: p => p.Items.Single(i => i.RelPath == "c.txt").Resolution = ConflictResolution.UseDestination);
        Assert.Equal("dst version!", Sandbox.Read(sb.Src, "c.txt"));
        var kept = Directory.GetFiles(Path.Combine(sb.Src, PathTools.ReservedFolder, "Versions"), "c.txt", SearchOption.AllDirectories).Single();
        Assert.Equal("src version", File.ReadAllText(kept));
    }

    [Fact]
    public async Task TwoWay_ModifiedVersusDeleted_IsAConflictNotADeletion()
    {
        using var sb = new Sandbox(SyncMode.TwoWay);
        sb.Write(sb.Src, "x.txt", "base"); await sb.Sync();
        sb.Write(sb.Src, "x.txt", "edited", DateTime.UtcNow);
        File.Delete(Path.Combine(sb.Dst, "x.txt"));
        var r = await sb.Sync();
        Assert.Equal(1, r.ConflictsLeft);
        Assert.True(Sandbox.Exists(sb.Src, "x.txt"));
    }

    // 10
    [Fact]
    public async Task InterruptedSync_LeavesNoPartialFiles_AndResumesCleanly()
    {
        using var sb = new Sandbox(SyncMode.OneWay);
        var big = new byte[4 << 20]; new Random(1).NextBytes(big);
        for (int i = 0; i < 12; i++) File.WriteAllBytes(Path.Combine(sb.Src, $"f{i}.bin"), big);

        using var cts = new CancellationTokenSource();
        var sink = new SyncProgressSink(p => { if (p.BytesDone > 6 << 20) cts.Cancel(); });
        var engine = sb.Engine();
        var plan = await engine.PlanAsync(sb.Profile, 1, null, CancellationToken.None);
        var res = await engine.ExecuteAsync(plan, new ExecuteOptions { MaxParallel = 1 }, sink, cts.Token);
        Assert.Equal(SyncStatus.Cancelled, res.Status);
        Assert.Empty(Directory.GetFiles(sb.Dst, "*" + PathTools.TempSuffix, SearchOption.AllDirectories));
        foreach (var f in Directory.GetFiles(sb.Dst, "*.bin")) Assert.Equal(big.Length, new FileInfo(f).Length); // only complete files

        var done = await sb.Sync();   // "restart": fresh plan from filesystem state
        Assert.Equal(SyncStatus.Completed, done.Status);
        Assert.Equal(12, Directory.GetFiles(sb.Dst, "*.bin").Length);
    }

    [Fact]
    public async Task StaleTempFilesFromACrash_AreCleanedUp()
    {
        using var sb = new Sandbox();
        sb.Write(sb.Src, "a.txt", "a");
        var stale = sb.Write(sb.Dst, "a.txt.1234abcd" + PathTools.TempSuffix, "partial");
        var r = await sb.Sync();
        Assert.Equal(SyncStatus.Completed, r.Status);
        Assert.False(File.Exists(stale));
    }

    // 11
    [Fact]
    public async Task LockedFile_ReportsFriendlyError_AndOtherFilesStillCopy()
    {
        using var sb = new Sandbox();
        var locked = sb.Write(sb.Src, "locked.txt", "L"); sb.Write(sb.Src, "free.txt", "F");
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var r = await sb.Sync();
        Assert.Equal(SyncStatus.CompletedWithErrors, r.Status);
        Assert.Single(r.Errors);
        Assert.Contains("in use", r.Errors[0].Message);
        Assert.Equal(1, r.FilesCopied);
        Assert.True(Sandbox.Exists(sb.Dst, "free.txt"));
        Assert.False(Sandbox.Exists(sb.Dst, "locked.txt"));
        Assert.Empty(Directory.GetFiles(sb.Dst, "*" + PathTools.TempSuffix, SearchOption.AllDirectories));
    }

    // 12
    [Fact]
    public async Task InsufficientDestinationSpace_BlocksSynchronization()
    {
        using var sb = new Sandbox();
        sb.Space = new FakeSpace(10);
        sb.Write(sb.Src, "big.txt", new string('x', 1000));
        var plan = await sb.Plan();
        Assert.Contains(plan.Issues, i => i.Code == "InsufficientSpace" && i.Severity == IssueSeverity.Error);
        await Assert.ThrowsAsync<SyncBlockedException>(() => sb.Engine().ExecuteAsync(plan, new ExecuteOptions(), null, CancellationToken.None));
        Assert.False(Sandbox.Exists(sb.Dst, "big.txt"));
    }

    // 13
    [Fact]
    public async Task IdenticalSourceAndDestination_IsRejected()
    {
        using var sb = new Sandbox();
        sb.Profile.DestinationPath = sb.Src + Path.DirectorySeparatorChar;
        var plan = await sb.Plan();
        Assert.Contains(plan.Issues, i => i.Code == "SamePath");
        await Assert.ThrowsAsync<SyncBlockedException>(() => sb.Engine().ExecuteAsync(plan, new ExecuteOptions(), null, CancellationToken.None));
    }

    // 14
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NestedSourceDestination_IsRejected(bool destInsideSource)
    {
        using var sb = new Sandbox(SyncMode.Mirror);
        var nested = Path.Combine(destInsideSource ? sb.Src : sb.Dst, "inner");
        Directory.CreateDirectory(nested);
        if (destInsideSource) sb.Profile.DestinationPath = nested; else sb.Profile.SourcePath = nested;
        var plan = await sb.Plan();
        Assert.Contains(plan.Issues, i => i.Code is "DestinationInsideSource" or "SourceInsideDestination");
        Assert.True(plan.HasBlockingIssues);
        Assert.Empty(plan.Items);
    }

    // 15
    [Fact]
    public async Task UnavailableExternalDrive_IsDetected()
    {
        using var sb = new Sandbox();
        var used = DriveInfo.GetDrives().Select(d => char.ToUpper(d.Name[0])).ToHashSet();
        var free = Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => (char)c).First(c => !used.Contains(c));
        sb.Profile.DestinationPath = $@"{free}:\Backup";
        sb.Write(sb.Src, "a.txt", "a");
        var plan = await sb.Plan();
        Assert.Contains(plan.Issues, i => i.Code == "DestinationDriveUnavailable");
        await Assert.ThrowsAsync<SyncBlockedException>(() => sb.Engine().ExecuteAsync(plan, new ExecuteOptions(), null, CancellationToken.None));

        sb.Profile.DestinationPath = sb.Dst; sb.Profile.SourcePath = $@"{free}:\Photos";
        var plan2 = await sb.Plan();
        Assert.Contains(plan2.Issues, i => i.Code == "SourceDriveUnavailable");
    }

    // 16
    [Fact]
    public async Task UnicodeFilenames_AreHandled()
    {
        using var sb = new Sandbox();
        var names = new[] { "résumé café.txt", "日本語のファイル.txt", @"папка\файл.txt", "emoji 😀 file.txt", "ñandú ΑΒΓ.txt" };
        foreach (var n in names) sb.Write(sb.Src, n, "unicode:" + n);
        var r = await sb.Sync();
        Assert.Equal(names.Length, r.FilesCopied);
        foreach (var n in names) Assert.Equal("unicode:" + n, Sandbox.Read(sb.Dst, n));
    }

    // 17
    [Fact]
    public async Task LargeFile_CopiedAndVerifiedByHash()
    {
        using var sb = new Sandbox();
        sb.Profile.VerifyAfterCopy = true; sb.Profile.CompareHash = true;
        var path = Path.Combine(sb.Src, "large.bin");
        var rnd = new Random(42); var buf = new byte[1 << 20];
        using (var fs = File.Create(path)) for (int i = 0; i < 160; i++) { rnd.NextBytes(buf); fs.Write(buf); }
        var r = await sb.Sync();
        Assert.Equal(SyncStatus.Completed, r.Status);
        Assert.Equal(160L << 20, r.BytesTransferred);
        using var a = File.OpenRead(path); using var b = File.OpenRead(Path.Combine(sb.Dst, "large.bin"));
        Assert.Equal(SHA256.HashData(a), SHA256.HashData(b));
        var again = await sb.Plan();
        Assert.Equal(PlanAction.Unchanged, again.Items.Single().Action);
    }

    // 18
    [Fact]
    public async Task DuplicateFilenamesInDifferentSubfolders_DoNotCollide()
    {
        using var sb = new Sandbox();
        sb.Write(sb.Src, @"a\readme.txt", "in a"); sb.Write(sb.Src, @"b\readme.txt", "in b"); sb.Write(sb.Src, "readme.txt", "root");
        var r = await sb.Sync();
        Assert.Equal(3, r.FilesCopied);
        Assert.Equal("in a", Sandbox.Read(sb.Dst, @"a\readme.txt"));
        Assert.Equal("in b", Sandbox.Read(sb.Dst, @"b\readme.txt"));
        Assert.Equal("root", Sandbox.Read(sb.Dst, "readme.txt"));
    }

    // 19
    [Fact]
    public async Task IncrementalBackup_RetainsEveryPreviousVersion_AndNeverDeletes()
    {
        using var sb = new Sandbox(SyncMode.Incremental);
        sb.Write(sb.Src, "f.txt", "version 1"); await sb.Sync();
        sb.Write(sb.Src, "f.txt", "version 2!", DateTime.UtcNow.AddMinutes(-10)); await sb.Sync();
        sb.Write(sb.Src, "f.txt", "version 3!!", DateTime.UtcNow); await sb.Sync();
        Assert.Equal("version 3!!", Sandbox.Read(sb.Dst, "f.txt"));
        var versions = Directory.GetFiles(Path.Combine(sb.Dst, PathTools.ReservedFolder, "Versions"), "f*.txt", SearchOption.AllDirectories)
            .Select(File.ReadAllText).OrderBy(s => s).ToList();
        Assert.Equal(new[] { "version 1", "version 2!" }, versions); // same-second runs must not overwrite each other

        File.Delete(Path.Combine(sb.Src, "f.txt"));
        var r = await sb.Sync();
        Assert.Equal(0, r.FilesDeleted);
        Assert.True(Sandbox.Exists(sb.Dst, "f.txt"));
    }

    // 20
    [Fact]
    public async Task RecoveryAfterApplicationRestart_UsesPersistedSyncState()
    {
        using var sb = new Sandbox(SyncMode.TwoWay);
        var dbFile = Path.Combine(sb.Root, "data", "state.db");
        sb.Write(sb.Src, "keep.txt", "k"); sb.Write(sb.Src, "remove-me.txt", "r");
        {
            var db = new Database(dbFile);
            sb.State = new StateRepository(db);
            var r = await sb.Sync();
            Assert.Equal(2, r.FilesCopied);
        } // "application exits"

        File.Delete(Path.Combine(sb.Src, "remove-me.txt"));
        {
            var db = new Database(dbFile);                    // "application restarts"
            sb.State = new StateRepository(db);
            var plan = await sb.Plan();
            var del = plan.Items.Single(i => i.RelPath == "remove-me.txt");
            Assert.Equal(PlanAction.Delete, del.Action);       // known to have existed -> deletion, not a "new file" resurrect
            var r = await sb.Sync();
            Assert.Equal(1, r.FilesDeleted);
        }
        Assert.False(Sandbox.Exists(sb.Dst, "remove-me.txt"));
        Assert.True(Sandbox.Exists(sb.Dst, "keep.txt"));
    }

    [Fact]
    public void History_RunLifecycle_AndInterruptedRecovery_AndCsv()
    {
        using var sb = new Sandbox();
        var db = new Database(Path.Combine(sb.Root, "data", "h.db"));
        var h = new HistoryRepository(db);
        sb.Profile.Name = "My \"quoted\" profile";
        var id1 = h.StartRun(sb.Profile, "Manual");
        var res = new SyncResult { Status = SyncStatus.Completed, StartUtc = DateTime.UtcNow.AddSeconds(-5), EndUtc = DateTime.UtcNow, FilesCopied = 3, BytesTransferred = 1234 };
        res.Operations.Add(new OpRecord("New", "a.txt", 10, "OK", ""));
        h.FinishRun(id1, res);
        var id2 = h.StartRun(sb.Profile, "Scheduled"); // never finished = crash
        Assert.Equal(1, h.MarkInterrupted());
        var all = h.Query();
        Assert.Equal(2, all.Count);
        Assert.Equal("Interrupted", all.Single(r => r.Id == id2).Status);
        Assert.Single(h.GetOps(id1));
        Assert.Single(h.Query(status: "Completed"));
        Assert.Single(h.Query(search: "quoted", status: "Completed"));
        var csv = HistoryRepository.ToCsv(all);
        Assert.Contains("\"My \"\"quoted\"\" profile\"", csv);
        Assert.Equal(3, h.Stats().FilesSynchronized);
        h.Clear();
        Assert.Empty(h.Query());
    }

    // --- additional safety / filter / persistence tests ---

    [Fact]
    public async Task Filters_ExcludeExtensionsFoldersHiddenAndTemporary()
    {
        using var sb = new Sandbox();
        sb.Profile.ExcludedExtensions = new() { "log", ".BAK" };
        sb.Profile.ExcludedFolders = new() { "node_modules" };
        sb.Profile.IgnoreTemporary = true; sb.Profile.IgnoreHidden = true;
        sb.Write(sb.Src, "ok.txt", "1"); sb.Write(sb.Src, "x.log", "2"); sb.Write(sb.Src, "y.bak", "3");
        sb.Write(sb.Src, @"node_modules\m.js", "4"); sb.Write(sb.Src, @"~$temp.docx", "5"); sb.Write(sb.Src, "t.tmp", "6");
        var hidden = sb.Write(sb.Src, "hidden.txt", "7"); File.SetAttributes(hidden, FileAttributes.Hidden);
        var r = await sb.Sync();
        Assert.Equal(1, r.FilesCopied);
        Assert.True(Sandbox.Exists(sb.Dst, "ok.txt"));
    }

    [Fact]
    public async Task NoSubdirectories_AndIgnoreEmptyFolders()
    {
        using var sb = new Sandbox();
        sb.Write(sb.Src, "top.txt", "t"); sb.Write(sb.Src, @"sub\s.txt", "s");
        sb.Profile.IncludeSubdirectories = false;
        var r = await sb.Sync();
        Assert.Equal(1, r.FilesCopied);
        Assert.False(Directory.Exists(Path.Combine(sb.Dst, "sub")));

        sb.Profile.IncludeSubdirectories = true; sb.Profile.IgnoreEmptyFolders = true;
        Directory.CreateDirectory(Path.Combine(sb.Src, "empty"));
        await sb.Sync();
        Assert.False(Directory.Exists(Path.Combine(sb.Dst, "empty")));
        Assert.True(Sandbox.Exists(sb.Dst, @"sub\s.txt"));
    }

    [Fact]
    public async Task HashComparison_DetectsSameSizeDifferentContent_IgnoresTimestampOnlyChanges()
    {
        using var sb = new Sandbox();
        sb.Profile.CompareHash = true;
        sb.Write(sb.Src, "h.txt", "AAAA"); await sb.Sync();
        sb.Write(sb.Src, "h.txt", "BBBB", DateTime.UtcNow.AddMinutes(-30)); // same size and time, different bytes
        File.SetLastWriteTimeUtc(Path.Combine(sb.Dst, "h.txt"), File.GetLastWriteTimeUtc(Path.Combine(sb.Src, "h.txt")));
        Assert.Equal(PlanAction.Modified, (await sb.Plan()).Items.Single().Action);
        await sb.Sync();
        File.SetLastWriteTimeUtc(Path.Combine(sb.Src, "h.txt"), DateTime.UtcNow); // time only
        Assert.Equal(PlanAction.Unchanged, (await sb.Plan()).Items.Single().Action);
    }

    [Fact]
    public async Task BackupOverwrittenFiles_KeepsOldVersionInOneWay()
    {
        using var sb = new Sandbox();
        sb.Profile.BackupOverwrittenFiles = true;
        sb.Write(sb.Src, "a.txt", "old"); await sb.Sync();
        sb.Write(sb.Src, "a.txt", "newer!", DateTime.UtcNow); await sb.Sync();
        var kept = Directory.GetFiles(Path.Combine(sb.Dst, PathTools.ReservedFolder, "Versions"), "a.txt", SearchOption.AllDirectories).Single();
        Assert.Equal("old", File.ReadAllText(kept));
    }

    [Fact]
    public async Task SourceChangedDuringCopy_IsDetected_AndLeavesNoPartialFile()
    {
        using var sb = new Sandbox();
        var big = Path.Combine(sb.Src, "growing.bin");
        File.WriteAllBytes(big, new byte[24 << 20]);
        var dst = Path.Combine(sb.Dst, "growing.bin");
        bool touched = false;
        var ex = await Assert.ThrowsAsync<SourceChangedException>(() => FileCopier.CopyAsync(big, dst, new CopyOptions(),
            _ => { if (!touched) { touched = true; File.AppendAllText(big, "more"); } }, null, CancellationToken.None));
        Assert.Contains("changed", ErrorTranslator.Describe(ex));
        Assert.False(File.Exists(dst));
        Assert.Empty(Directory.GetFiles(sb.Dst, "*" + PathTools.TempSuffix));
    }

    [Fact]
    public async Task LongPaths_Over260Characters_AreSupported()
    {
        using var sb = new Sandbox();
        var deep = string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat(new string('d', 40), 7)); // ~280 chars
        sb.Write(sb.Src, deep + "\\long.txt", "long");
        var r = await sb.Sync();
        Assert.Equal(SyncStatus.Completed, r.Status);
        Assert.Equal("long", Sandbox.Read(sb.Dst, deep + "\\long.txt"));
    }

    [Fact]
    public async Task MirrorToDriveRoot_IsRefused()
    {
        using var sb = new Sandbox(SyncMode.Mirror);
        sb.Profile.DestinationPath = Path.GetPathRoot(sb.Root)!;
        var plan = await sb.Plan();
        Assert.Contains(plan.Issues, i => i.Code == "MirrorToDriveRoot");
    }

    [Fact]
    public async Task MirrorWithEmptySource_WarnsAboutWipingDestination()
    {
        using var sb = new Sandbox(SyncMode.Mirror);
        sb.Write(sb.Dst, "precious.txt", "p");
        var plan = await sb.Plan();
        Assert.Contains(plan.Issues, i => i.Code == "SourceEmpty");
        var r = await sb.Sync(o: new ExecuteOptions { Unattended = true });
        Assert.True(Sandbox.Exists(sb.Dst, "precious.txt"));
    }

    [Fact]
    public void ProfileJson_RoundTrip_AndImportExport()
    {
        var p = new SyncProfile { Name = "Docs", SourcePath = @"C:\a", DestinationPath = @"D:\b", Mode = SyncMode.TwoWay, ExcludedExtensions = { ".tmp" } };
        p.Schedule = new ScheduleSettings { Enabled = true, Kind = ScheduleKind.Weekly, DaysOfWeek = { DayOfWeek.Friday } };
        var back = SyncProfile.ProfilesFromJson(SyncProfile.ProfilesToJson(new[] { p })).Single();
        Assert.Equal(p.Id, back.Id); Assert.Equal(SyncMode.TwoWay, back.Mode);
        Assert.Contains(DayOfWeek.Friday, back.Schedule.DaysOfWeek);
        Assert.Contains("\"TwoWay\"", p.ToJson());
    }

    [Fact]
    public void ProfileRepository_SaveLoadDeleteDuplicate()
    {
        using var sb = new Sandbox();
        var repo = new ProfileRepository(new Database(Path.Combine(sb.Root, "data", "p.db")));
        var p = new SyncProfile { Name = "One", SourcePath = "a", DestinationPath = "b" };
        repo.Save(p);
        var copy = p.Clone(); copy.Id = Guid.NewGuid(); copy.Name = "One (copy)"; repo.Save(copy);
        Assert.Equal(2, repo.GetAll().Count);
        p.Name = "Renamed"; repo.Save(p);
        Assert.Equal("Renamed", repo.Get(p.Id)!.Name);
        repo.Delete(p.Id);
        Assert.Single(repo.GetAll());
    }
}
