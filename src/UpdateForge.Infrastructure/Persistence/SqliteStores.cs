using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using UpdateForge.Application;
using UpdateForge.Core.Models;
using UpdateForge.Core.Updates;

namespace UpdateForge.Infrastructure.Persistence;

internal static class Sql
{
    public static SqliteCommand Cmd(this SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string Name, object? Value)[] ps)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    public static string Iso(DateTimeOffset d) => d.ToString("O", CultureInfo.InvariantCulture);
    public static DateTimeOffset ParseIso(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    public static string? Str(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}

public sealed class SqliteInventoryStore(Database db) : IInventoryStore
{
    public Task SaveSnapshotAsync(IReadOnlyList<InstalledApplication> apps, CancellationToken ct)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        c.Cmd("DELETE FROM Applications;", tx).ExecuteNonQuery();
        foreach (var a in apps)
        {
            c.Cmd("""
                INSERT OR REPLACE INTO Applications (Id,Name,ProviderId,PackageId,PackageSource,Publisher,InstalledVersion,AvailableVersion,Status,Support,Confidence,Scope,Architecture,InstallLocation,IconSource,IsPinned,IsExcluded,Note,LastChecked)
                VALUES ($id,$name,$prov,$pkg,$src,$pub,$iv,$av,$st,$sup,$conf,$scope,$arch,$loc,$icon,$pin,$ex,$note,$chk);
                """, tx,
                ("$id", a.Id), ("$name", a.Name), ("$prov", a.ProviderId), ("$pkg", a.PackageId), ("$src", a.PackageSource),
                ("$pub", a.Publisher), ("$iv", a.InstalledVersion), ("$av", a.AvailableVersion), ("$st", (int)a.Status),
                ("$sup", (int)a.Support), ("$conf", (int)a.Confidence), ("$scope", (int)a.Scope), ("$arch", a.Architecture),
                ("$loc", a.InstallLocation), ("$icon", a.IconSource), ("$pin", a.IsPinned ? 1 : 0), ("$ex", a.IsExcluded ? 1 : 0),
                ("$note", a.Note), ("$chk", a.LastChecked is { } d ? Sql.Iso(d) : null)).ExecuteNonQuery();
            foreach (var e in a.Evidence)
                c.Cmd("INSERT OR IGNORE INTO ApplicationSources (ApplicationId,ProviderId,SourceKey,Detail) VALUES ($a,$p,$k,$d);", tx,
                    ("$a", a.Id), ("$p", e.ProviderId), ("$k", e.SourceKey), ("$d", e.Detail)).ExecuteNonQuery();
        }
        tx.Commit();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<InstalledApplication>> LoadSnapshotAsync(CancellationToken ct)
    {
        using var c = db.Open();
        using var cmd = c.Cmd("SELECT Id,Name,ProviderId,PackageId,PackageSource,Publisher,InstalledVersion,AvailableVersion,Status,Support,Confidence,Scope,Architecture,InstallLocation,IconSource,IsPinned,IsExcluded,Note,LastChecked FROM Applications ORDER BY Name COLLATE NOCASE;");
        using var r = cmd.ExecuteReader();
        var list = new List<InstalledApplication>();
        while (r.Read())
            list.Add(new InstalledApplication
            {
                Id = r.GetString(0), Name = r.GetString(1), ProviderId = r.GetString(2), PackageId = r.Str(3), PackageSource = r.Str(4),
                Publisher = r.Str(5), InstalledVersion = r.Str(6), AvailableVersion = r.Str(7), Status = (UpdateStatus)r.GetInt32(8),
                Support = (UpdateSupport)r.GetInt32(9), Confidence = (IdentityConfidence)r.GetInt32(10), Scope = (InstallScope)r.GetInt32(11),
                Architecture = r.Str(12), InstallLocation = r.Str(13), IconSource = r.Str(14), IsPinned = r.GetInt32(15) == 1,
                IsExcluded = r.GetInt32(16) == 1, Note = r.Str(17), LastChecked = r.Str(18) is { } s ? Sql.ParseIso(s) : null,
            });
        return Task.FromResult<IReadOnlyList<InstalledApplication>>(list);
    }
}

public sealed class SqliteScanHistoryStore(Database db) : IScanHistoryStore
{
    public Task AddAsync(ScanRecord record, CancellationToken ct)
    {
        using var c = db.Open();
        c.Cmd("INSERT INTO ScanHistory (StartedAt,DurationMs,ApplicationCount,UpdatesAvailable,ProviderSummary) VALUES ($s,$d,$n,$u,$p);", null,
            ("$s", Sql.Iso(record.StartedAt)), ("$d", (long)record.Duration.TotalMilliseconds), ("$n", record.ApplicationCount), ("$u", record.UpdatesAvailable), ("$p", record.ProviderSummary)).ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<ScanRecord?> GetLastAsync(CancellationToken ct)
    {
        using var c = db.Open();
        using var r = c.Cmd("SELECT StartedAt,DurationMs,ApplicationCount,UpdatesAvailable,ProviderSummary FROM ScanHistory ORDER BY Id DESC LIMIT 1;").ExecuteReader();
        return Task.FromResult<ScanRecord?>(r.Read()
            ? new ScanRecord(Sql.ParseIso(r.GetString(0)), TimeSpan.FromMilliseconds(r.GetInt64(1)), r.GetInt32(2), r.GetInt32(3), r.Str(4)) : null);
    }
}

public sealed class SqliteExclusionStore(Database db) : IExclusionStore
{
    public Task<IReadOnlySet<string>> GetExcludedAsync(CancellationToken ct)
    {
        using var c = db.Open();
        using var r = c.Cmd("SELECT ApplicationId FROM Exclusions;").ExecuteReader();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (r.Read()) set.Add(r.GetString(0));
        return Task.FromResult<IReadOnlySet<string>>(set);
    }

    public Task SetExcludedAsync(string applicationId, string applicationName, bool excluded, CancellationToken ct)
    {
        using var c = db.Open();
        if (excluded) c.Cmd("INSERT OR REPLACE INTO Exclusions (ApplicationId,ApplicationName,CreatedAt) VALUES ($i,$n,$t);", null,
            ("$i", applicationId), ("$n", applicationName), ("$t", Sql.Iso(DateTimeOffset.UtcNow))).ExecuteNonQuery();
        else c.Cmd("DELETE FROM Exclusions WHERE ApplicationId=$i;", null, ("$i", applicationId)).ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SkippedVersion>> GetSkippedVersionsAsync(CancellationToken ct)
    {
        using var c = db.Open();
        using var r = c.Cmd("SELECT ApplicationId,Version FROM SkippedVersions;").ExecuteReader();
        var list = new List<SkippedVersion>();
        while (r.Read()) list.Add(new SkippedVersion(r.GetString(0), r.GetString(1)));
        return Task.FromResult<IReadOnlyList<SkippedVersion>>(list);
    }

    public Task AddSkippedVersionAsync(string applicationId, string version, CancellationToken ct)
    {
        using var c = db.Open();
        c.Cmd("INSERT OR IGNORE INTO SkippedVersions (ApplicationId,Version,CreatedAt) VALUES ($i,$v,$t);", null,
            ("$i", applicationId), ("$v", version), ("$t", Sql.Iso(DateTimeOffset.UtcNow))).ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task RemoveSkippedVersionAsync(string applicationId, string version, CancellationToken ct)
    {
        using var c = db.Open();
        c.Cmd("DELETE FROM SkippedVersions WHERE ApplicationId=$i AND Version=$v;", null, ("$i", applicationId), ("$v", version)).ExecuteNonQuery();
        return Task.CompletedTask;
    }
}

public sealed class SqliteJobStore(Database db) : IJobStore
{
    public Task<long> CreateItemAsync(Guid batchId, InstalledApplication app, JobState state, CancellationToken ct)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var now = Sql.Iso(DateTimeOffset.UtcNow);
        c.Cmd("INSERT OR IGNORE INTO UpdateJobs (Id,CreatedAt) VALUES ($j,$t);", tx, ("$j", batchId.ToString("D")), ("$t", now)).ExecuteNonQuery();
        c.Cmd("INSERT INTO UpdateJobItems (JobId,ApplicationId,ApplicationName,ProviderId,State,UpdatedAt,PackageId,PackageSource,PreviousVersion,TargetVersion) VALUES ($j,$a,$n,$p,$s,$t,$pk,$ps,$pv,$tv);", tx,
            ("$j", batchId.ToString("D")), ("$a", app.Id), ("$n", app.Name), ("$p", app.ProviderId), ("$s", (int)state), ("$t", now),
            ("$pk", app.PackageId), ("$ps", app.PackageSource), ("$pv", app.InstalledVersion), ("$tv", app.AvailableVersion)).ExecuteNonQuery();
        var id = Convert.ToInt64(Database.Scalar(c, tx, "SELECT last_insert_rowid();"), CultureInfo.InvariantCulture);
        tx.Commit();
        return Task.FromResult(id);
    }

    public Task SetStateAsync(long itemId, JobState state, string? message, CancellationToken ct)
    {
        using var c = db.Open();
        c.Cmd("UPDATE UpdateJobItems SET State=$s,Message=$m,UpdatedAt=$t WHERE Id=$i;", null,
            ("$s", (int)state), ("$m", message), ("$t", Sql.Iso(DateTimeOffset.UtcNow)), ("$i", itemId)).ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<JobItemRecord>> RecoverInterruptedAsync(CancellationToken ct)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var inFlight = new[] { JobState.Queued, JobState.Downloading, JobState.Verifying, JobState.Installing, JobState.VerifyingInstallation }.Select(s => (int)s).ToList();
        var list = new List<JobItemRecord>();
        using (var r = c.Cmd($"SELECT Id,ApplicationId,ApplicationName,ProviderId,State,UpdatedAt,PackageId,PackageSource,PreviousVersion,TargetVersion FROM UpdateJobItems WHERE State IN ({string.Join(",", inFlight)});", tx).ExecuteReader())
            while (r.Read()) list.Add(ReadItem(r));
        foreach (var item in list)
            c.Cmd("UPDATE UpdateJobItems SET State=$s,Message=$m,UpdatedAt=$t WHERE Id=$i;", tx,
                ("$s", (int)JobState.Failed), ("$m", "Interrupted before completion; the outcome is unknown. Rescan to see the current version."),
                ("$t", Sql.Iso(DateTimeOffset.UtcNow)), ("$i", item.Id)).ExecuteNonQuery();
        tx.Commit();
        return Task.FromResult<IReadOnlyList<JobItemRecord>>(list);
    }

    private static JobItemRecord ReadItem(Microsoft.Data.Sqlite.SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), (JobState)r.GetInt32(4), Sql.ParseIso(r.GetString(5)),
            r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9));

    public Task<IReadOnlyList<JobItemRecord>> GetRecentAsync(int limit, CancellationToken ct)
    {
        using var c = db.Open();
        using var r = c.Cmd("SELECT Id,ApplicationId,ApplicationName,ProviderId,State,UpdatedAt,PackageId,PackageSource,PreviousVersion,TargetVersion FROM UpdateJobItems ORDER BY Id DESC LIMIT $l;", null, ("$l", limit)).ExecuteReader();
        var list = new List<JobItemRecord>();
        while (r.Read()) list.Add(ReadItem(r));
        return Task.FromResult<IReadOnlyList<JobItemRecord>>(list);
    }
}

public sealed class SqliteHistoryStore(Database db) : IHistoryStore
{
    public Task AddAsync(HistoryRecord record, CancellationToken ct)
    {
        using var c = db.Open();
        c.Cmd("""
            INSERT INTO UpdateHistory (CompletedAt,ApplicationId,ApplicationName,ProviderId,PreviousVersion,RequestedVersion,NewVersion,FinalState,DurationMs,ExitCode,ErrorCategory,RestartRequired,Message,TechnicalDetails)
            VALUES ($t,$a,$n,$p,$pv,$rv,$nv,$fs,$d,$ec,$er,$rr,$m,$td);
            """, null,
            ("$t", Sql.Iso(record.CompletedAt)), ("$a", record.ApplicationId), ("$n", record.ApplicationName), ("$p", record.ProviderId), ("$pv", record.PreviousVersion),
            ("$rv", record.RequestedVersion), ("$nv", record.NewVersion), ("$fs", (int)record.FinalState), ("$d", (long)record.Duration.TotalMilliseconds),
            ("$ec", record.ExitCode), ("$er", (int)record.Error), ("$rr", record.RestartRequired ? 1 : 0), ("$m", record.Message), ("$td", record.TechnicalDetails)).ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<HistoryRecord>> QueryAsync(string? search, int limit, CancellationToken ct)
    {
        using var c = db.Open();
        using var r = c.Cmd("""
            SELECT Id,CompletedAt,ApplicationId,ApplicationName,ProviderId,PreviousVersion,RequestedVersion,NewVersion,FinalState,DurationMs,ExitCode,ErrorCategory,RestartRequired,Message,TechnicalDetails
            FROM UpdateHistory WHERE ($q IS NULL OR ApplicationName LIKE '%' || $q || '%' ESCAPE '\' OR ApplicationId LIKE '%' || $q || '%' ESCAPE '\')
            ORDER BY Id DESC LIMIT $l;
            """, null, ("$q", string.IsNullOrWhiteSpace(search) ? null : search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")), ("$l", limit)).ExecuteReader();
        var list = new List<HistoryRecord>();
        while (r.Read())
            list.Add(new HistoryRecord(r.GetInt64(0), Sql.ParseIso(r.GetString(1)), r.GetString(2), r.GetString(3), r.GetString(4), r.Str(5), r.Str(6), r.Str(7),
                (JobState)r.GetInt32(8), TimeSpan.FromMilliseconds(r.GetInt64(9)), r.IsDBNull(10) ? null : r.GetInt32(10), (ErrorCategory)r.GetInt32(11),
                r.GetInt32(12) == 1, r.Str(13), r.Str(14)));
        return Task.FromResult<IReadOnlyList<HistoryRecord>>(list);
    }

    public Task<DateTimeOffset?> GetLastSuccessAsync(CancellationToken ct)
    {
        using var c = db.Open();
        var v = Database.Scalar(c, null, $"SELECT CompletedAt FROM UpdateHistory WHERE FinalState IN ({(int)JobState.Completed},{(int)JobState.RestartRequired}) ORDER BY Id DESC LIMIT 1;") as string;
        return Task.FromResult<DateTimeOffset?>(v is null ? null : Sql.ParseIso(v));
    }
}

public sealed class SqliteSettingsStore(Database db) : ISettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public Task<AppSettings> LoadAsync(CancellationToken ct)
    {
        using var c = db.Open();
        var json = Database.Scalar(c, null, "SELECT Value FROM Settings WHERE Key='app';") as string;
        if (json is null) return Task.FromResult(new AppSettings());
        try { return Task.FromResult((JsonSerializer.Deserialize<AppSettings>(json, Json) ?? new AppSettings()).Validated()); }
        catch (JsonException) { return Task.FromResult(new AppSettings()); } // corrupt settings fall back to safe defaults
    }

    public Task SaveAsync(AppSettings settings, CancellationToken ct)
    {
        using var c = db.Open();
        c.Cmd("INSERT OR REPLACE INTO Settings (Key,Value) VALUES ('app',$v);", null, ("$v", JsonSerializer.Serialize(settings.Validated(), Json))).ExecuteNonQuery();
        return Task.CompletedTask;
    }
}
