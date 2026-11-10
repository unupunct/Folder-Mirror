using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FolderMirror.Core.Engine;
using FolderMirror.Core.Models;
using Microsoft.Data.Sqlite;

namespace FolderMirror.Core.Data;

public static class AppPaths
{
    /// <summary>Data folder. Override with the FOLDERMIRROR_DATA environment variable (used for tests/demos).</summary>
    public static string DataFolder
    {
        get
        {
            var o = Environment.GetEnvironmentVariable("FOLDERMIRROR_DATA");
            var baseDir = OperatingSystem.IsMacOS()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = !string.IsNullOrWhiteSpace(o) ? o : Path.Combine(baseDir, "FolderMirror");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
    public static string DatabaseFile => Path.Combine(DataFolder, "foldermirror.db");
    public static string DefaultLogFolder => Path.Combine(DataFolder, "logs");
}

public sealed class Database
{
    public string FilePath { get; }

    public Database(string filePath)
    {
        FilePath = filePath;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        EnsureSchema();
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = FilePath, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=8000; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        cmd.ExecuteNonQuery();
        return c;
    }

    void EnsureSchema()
    {
        using var c = Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS profiles(
              id TEXT PRIMARY KEY, name TEXT NOT NULL, json TEXT NOT NULL, sort INTEGER NOT NULL DEFAULT 0, updated TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS runs(
              id INTEGER PRIMARY KEY AUTOINCREMENT, profile_id TEXT, profile_name TEXT, source TEXT, destination TEXT, mode TEXT,
              trigger TEXT, start_utc TEXT NOT NULL, end_utc TEXT, duration_ms INTEGER DEFAULT 0,
              files_copied INTEGER DEFAULT 0, files_modified INTEGER DEFAULT 0, files_deleted INTEGER DEFAULT 0, files_unchanged INTEGER DEFAULT 0,
              bytes INTEGER DEFAULT 0, error_count INTEGER DEFAULT 0, status TEXT NOT NULL, message TEXT);
            CREATE INDEX IF NOT EXISTS ix_runs_start ON runs(start_utc);
            CREATE TABLE IF NOT EXISTS run_ops(
              id INTEGER PRIMARY KEY AUTOINCREMENT, run_id INTEGER NOT NULL, action TEXT, rel_path TEXT, size INTEGER, status TEXT, message TEXT);
            CREATE INDEX IF NOT EXISTS ix_ops_run ON run_ops(run_id);
            CREATE TABLE IF NOT EXISTS file_state(
              profile_id TEXT NOT NULL, rel_path TEXT NOT NULL, s_size INTEGER, s_mtime INTEGER, d_size INTEGER, d_mtime INTEGER,
              PRIMARY KEY(profile_id, rel_path));
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT);
            """);
    }

    internal static void Exec(SqliteConnection c, string sql, params (string, object?)[] ps)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}

public sealed class ProfileRepository
{
    readonly Database _db;
    public ProfileRepository(Database db) => _db = db;

    public List<SyncProfile> GetAll()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM profiles ORDER BY sort, name COLLATE NOCASE";
        var list = new List<SyncProfile>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            try { var p = SyncProfile.FromJson(r.GetString(0)); if (p != null) list.Add(p); } catch { }
        }
        return list;
    }

    public SyncProfile? Get(Guid id) => GetAll().FirstOrDefault(p => p.Id == id);

    public void Save(SyncProfile p)
    {
        p.NormalizeLists();
        using var c = _db.Open();
        Database.Exec(c, """
            INSERT INTO profiles(id,name,json,sort,updated) VALUES($id,$n,$j,COALESCE((SELECT sort FROM profiles WHERE id=$id),(SELECT COALESCE(MAX(sort),0)+1 FROM profiles)),$u)
            ON CONFLICT(id) DO UPDATE SET name=$n, json=$j, updated=$u
            """, ("$id", p.Id.ToString()), ("$n", p.Name), ("$j", p.ToJson()), ("$u", DateTime.UtcNow.ToString("o")));
    }

    public void Delete(Guid id)
    {
        using var c = _db.Open();
        Database.Exec(c, "DELETE FROM profiles WHERE id=$id", ("$id", id.ToString()));
        Database.Exec(c, "DELETE FROM file_state WHERE profile_id=$id", ("$id", id.ToString()));
    }
}

public sealed class StateRepository : IStateStore
{
    readonly Database _db;
    public StateRepository(Database db) => _db = db;

    public Dictionary<string, FileState> Load(Guid profileId)
    {
        var d = new Dictionary<string, FileState>(PathTools.KeyComparer);
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT rel_path,s_size,s_mtime,d_size,d_mtime FROM file_state WHERE profile_id=$p";
        cmd.Parameters.AddWithValue("$p", profileId.ToString());
        using var r = cmd.ExecuteReader();
        while (r.Read()) d[r.GetString(0)] = new FileState(r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4));
        return d;
    }

    public void Save(Guid profileId, IEnumerable<KeyValuePair<string, FileState>> upserts, IEnumerable<string> deletes)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO file_state(profile_id,rel_path,s_size,s_mtime,d_size,d_mtime) VALUES($p,$r,$a,$b,$c,$d)";
            var pp = cmd.Parameters; pp.Add("$p", SqliteType.Text); pp.Add("$r", SqliteType.Text);
            pp.Add("$a", SqliteType.Integer); pp.Add("$b", SqliteType.Integer); pp.Add("$c", SqliteType.Integer); pp.Add("$d", SqliteType.Integer);
            foreach (var kv in upserts)
            {
                pp["$p"].Value = profileId.ToString(); pp["$r"].Value = kv.Key;
                pp["$a"].Value = kv.Value.SourceSize; pp["$b"].Value = kv.Value.SourceMtimeTicks;
                pp["$c"].Value = kv.Value.DestSize; pp["$d"].Value = kv.Value.DestMtimeTicks;
                cmd.ExecuteNonQuery();
            }
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM file_state WHERE profile_id=$p AND rel_path=$r";
            cmd.Parameters.Add("$p", SqliteType.Text).Value = profileId.ToString();
            var rp = cmd.Parameters.Add("$r", SqliteType.Text);
            foreach (var k in deletes) { rp.Value = k; cmd.ExecuteNonQuery(); }
        }
        tx.Commit();
    }

    public void Clear(Guid profileId)
    {
        using var c = _db.Open();
        Database.Exec(c, "DELETE FROM file_state WHERE profile_id=$p", ("$p", profileId.ToString()));
    }
}

public sealed class SettingsRepository
{
    readonly Database _db;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public SettingsRepository(Database db) => _db = db;

    public AppSettings Load()
    {
        try
        {
            using var c = _db.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT value FROM settings WHERE key='app'";
            var v = cmd.ExecuteScalar() as string;
            if (!string.IsNullOrEmpty(v)) return JsonSerializer.Deserialize<AppSettings>(v, Json) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save(AppSettings s)
    {
        using var c = _db.Open();
        Database.Exec(c, "INSERT INTO settings(key,value) VALUES('app',$v) ON CONFLICT(key) DO UPDATE SET value=$v",
            ("$v", JsonSerializer.Serialize(s, Json)));
    }
}

public sealed class RunRecord
{
    public long Id { get; set; }
    public string ProfileId { get; set; } = "";
    public string ProfileName { get; set; } = "";
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Mode { get; set; } = "";
    public string Trigger { get; set; } = "";
    public DateTime StartLocal { get; set; }
    public DateTime? EndLocal { get; set; }
    public long DurationMs { get; set; }
    public int FilesCopied { get; set; }
    public int FilesModified { get; set; }
    public int FilesDeleted { get; set; }
    public int FilesUnchanged { get; set; }
    public long Bytes { get; set; }
    public int ErrorCount { get; set; }
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
    public string DurationText => Format.Duration(TimeSpan.FromMilliseconds(DurationMs));
    public string BytesText => Format.Bytes(Bytes);
}

public sealed class OpRow
{
    public string Action { get; set; } = "";
    public string RelPath { get; set; } = "";
    public long Size { get; set; }
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
    public string SizeText => Format.Bytes(Size);
}

public sealed class DashboardStats
{
    public int Profiles { get; set; }
    public long FilesSynchronized { get; set; }
    public DateTime? LastSyncLocal { get; set; }
}

public sealed class HistoryRepository
{
    readonly Database _db;
    public HistoryRepository(Database db) => _db = db;

    public long StartRun(SyncProfile p, string trigger)
    {
        using var c = _db.Open();
        Database.Exec(c, """
            INSERT INTO runs(profile_id,profile_name,source,destination,mode,trigger,start_utc,status)
            VALUES($pid,$pn,$s,$d,$m,$t,$st,'Running')
            """, ("$pid", p.Id.ToString()), ("$pn", p.Name), ("$s", p.SourcePath), ("$d", p.DestinationPath),
            ("$m", ModeInfo.Title(p.Mode)), ("$t", trigger), ("$st", DateTime.UtcNow.ToString("o")));
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT last_insert_rowid()";
        return (long)cmd.ExecuteScalar()!;
    }

    public void FinishRun(long id, SyncResult r, string? extraMessage = null)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var msg = string.Join(" ", r.Messages.Concat(r.Errors.Take(1).Select(e => e.Message)).Append(extraMessage ?? "").Where(s => s.Length > 0));
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE runs SET end_utc=$e,duration_ms=$dur,files_copied=$fc,files_modified=$fm,files_deleted=$fd,files_unchanged=$fu,
                bytes=$b,error_count=$ec,status=$s,message=$msg WHERE id=$id
                """;
            cmd.Parameters.AddWithValue("$e", r.EndUtc.ToString("o"));
            cmd.Parameters.AddWithValue("$dur", (long)r.Duration.TotalMilliseconds);
            cmd.Parameters.AddWithValue("$fc", r.FilesCopied); cmd.Parameters.AddWithValue("$fm", r.FilesModified);
            cmd.Parameters.AddWithValue("$fd", r.FilesDeleted); cmd.Parameters.AddWithValue("$fu", r.FilesUnchanged);
            cmd.Parameters.AddWithValue("$b", r.BytesTransferred); cmd.Parameters.AddWithValue("$ec", r.Errors.Count);
            cmd.Parameters.AddWithValue("$s", r.Status.ToString()); cmd.Parameters.AddWithValue("$msg", msg);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO run_ops(run_id,action,rel_path,size,status,message) VALUES($r,$a,$p,$s,$st,$m)";
            var pp = cmd.Parameters;
            pp.Add("$r", SqliteType.Integer).Value = id; pp.Add("$a", SqliteType.Text); pp.Add("$p", SqliteType.Text);
            pp.Add("$s", SqliteType.Integer); pp.Add("$st", SqliteType.Text); pp.Add("$m", SqliteType.Text);
            foreach (var o in r.Operations)
            {
                pp["$a"].Value = o.Action; pp["$p"].Value = o.RelPath; pp["$s"].Value = o.Size; pp["$st"].Value = o.Status; pp["$m"].Value = o.Message;
                cmd.ExecuteNonQuery();
            }
            foreach (var e in r.Errors.Where(e => r.Operations.All(o => o.RelPath != e.Path)))
            {
                pp["$a"].Value = "Error"; pp["$p"].Value = e.Path; pp["$s"].Value = 0L; pp["$st"].Value = "Error"; pp["$m"].Value = e.Message;
                cmd.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    /// <summary>Mark runs that were still "Running" (crash / power loss) as interrupted.</summary>
    public int MarkInterrupted()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE runs SET status='Interrupted', end_utc=$e, message='The application stopped unexpectedly during this run. Run the profile again to finish.' WHERE status='Running'";
        cmd.Parameters.AddWithValue("$e", DateTime.UtcNow.ToString("o"));
        return cmd.ExecuteNonQuery();
    }

    static RunRecord Read(SqliteDataReader r)
    {
        DateTime P(string s) => DateTime.Parse(s, null, DateTimeStyles.RoundtripKind).ToLocalTime();
        return new RunRecord
        {
            Id = r.GetInt64(0), ProfileId = r.IsDBNull(1) ? "" : r.GetString(1), ProfileName = r.IsDBNull(2) ? "" : r.GetString(2),
            Source = r.IsDBNull(3) ? "" : r.GetString(3), Destination = r.IsDBNull(4) ? "" : r.GetString(4), Mode = r.IsDBNull(5) ? "" : r.GetString(5),
            Trigger = r.IsDBNull(6) ? "" : r.GetString(6), StartLocal = P(r.GetString(7)),
            EndLocal = r.IsDBNull(8) ? null : P(r.GetString(8)), DurationMs = r.IsDBNull(9) ? 0 : r.GetInt64(9),
            FilesCopied = r.GetInt32(10), FilesModified = r.GetInt32(11), FilesDeleted = r.GetInt32(12), FilesUnchanged = r.GetInt32(13),
            Bytes = r.GetInt64(14), ErrorCount = r.GetInt32(15), Status = r.GetString(16), Message = r.IsDBNull(17) ? "" : r.GetString(17)
        };
    }

    const string Cols = "id,profile_id,profile_name,source,destination,mode,trigger,start_utc,end_utc,duration_ms,files_copied,files_modified,files_deleted,files_unchanged,bytes,error_count,status,message";

    public List<RunRecord> Query(string? search = null, string? status = null, DateTime? fromLocal = null, DateTime? toLocal = null, int limit = 2000)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(search))
        {
            where.Add("(profile_name LIKE $q OR source LIKE $q OR destination LIKE $q OR message LIKE $q OR mode LIKE $q)");
            cmd.Parameters.AddWithValue("$q", "%" + search.Trim() + "%");
        }
        if (!string.IsNullOrWhiteSpace(status) && status != "All") { where.Add("status=$st"); cmd.Parameters.AddWithValue("$st", status); }
        if (fromLocal != null) { where.Add("start_utc>=$f"); cmd.Parameters.AddWithValue("$f", fromLocal.Value.ToUniversalTime().ToString("o")); }
        if (toLocal != null) { where.Add("start_utc<$t"); cmd.Parameters.AddWithValue("$t", toLocal.Value.AddDays(1).ToUniversalTime().ToString("o")); }
        cmd.CommandText = $"SELECT {Cols} FROM runs {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")} ORDER BY start_utc DESC LIMIT {limit}";
        var list = new List<RunRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    public RunRecord? GetRun(long id)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM runs WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    public List<OpRow> GetOps(long runId)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT action,rel_path,size,status,message FROM run_ops WHERE run_id=$r ORDER BY id";
        cmd.Parameters.AddWithValue("$r", runId);
        var list = new List<OpRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new OpRow { Action = r.GetString(0), RelPath = r.GetString(1), Size = r.GetInt64(2), Status = r.GetString(3), Message = r.IsDBNull(4) ? "" : r.GetString(4) });
        return list;
    }

    public void Clear()
    {
        using var c = _db.Open();
        Database.Exec(c, "DELETE FROM run_ops; DELETE FROM runs;");
    }

    public DashboardStats Stats()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(files_copied+files_modified),0), MAX(CASE WHEN status IN ('Completed','CompletedWithErrors') THEN end_utc END) FROM runs";
        using var r = cmd.ExecuteReader();
        var s = new DashboardStats();
        if (r.Read())
        {
            s.FilesSynchronized = r.GetInt64(0);
            if (!r.IsDBNull(1)) s.LastSyncLocal = DateTime.Parse(r.GetString(1), null, DateTimeStyles.RoundtripKind).ToLocalTime();
        }
        return s;
    }

    public DateTime? LastSyncFor(Guid profileId)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT MAX(end_utc) FROM runs WHERE profile_id=$p AND status IN ('Completed','CompletedWithErrors')";
        cmd.Parameters.AddWithValue("$p", profileId.ToString());
        var v = cmd.ExecuteScalar() as string;
        return v == null ? null : DateTime.Parse(v, null, DateTimeStyles.RoundtripKind).ToLocalTime();
    }

    public static string ToCsv(IEnumerable<RunRecord> runs)
    {
        static string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Id,Profile,Mode,Trigger,Source,Destination,Start,End,DurationSeconds,FilesCopied,FilesModified,FilesDeleted,FilesUnchanged,Bytes,Errors,Status,Message");
        foreach (var r in runs)
            sb.AppendLine(string.Join(",", r.Id, Q(r.ProfileName), Q(r.Mode), Q(r.Trigger), Q(r.Source), Q(r.Destination),
                Q(r.StartLocal.ToString("yyyy-MM-dd HH:mm:ss")), Q(r.EndLocal?.ToString("yyyy-MM-dd HH:mm:ss") ?? ""),
                (r.DurationMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture), r.FilesCopied, r.FilesModified, r.FilesDeleted,
                r.FilesUnchanged, r.Bytes, r.ErrorCount, Q(r.Status), Q(r.Message)));
        return sb.ToString();
    }
}
