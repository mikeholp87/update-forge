using Microsoft.Data.Sqlite;

namespace UpdateForge.Infrastructure.Persistence;

/// <summary>SQLite database with forward-only, transactional, versioned migrations (PRAGMA user_version).</summary>
public sealed class Database
{
    private readonly string _connectionString;

    public Database(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared, Pooling = true }.ToString();
    }

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpdateForge", "updateforge.db");

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL;";
        cmd.ExecuteNonQuery();
        return c;
    }

    public static readonly IReadOnlyList<string> Migrations =
    [
        // v1
        """
        CREATE TABLE Applications (
            Id TEXT PRIMARY KEY, Name TEXT NOT NULL, ProviderId TEXT NOT NULL, PackageId TEXT, PackageSource TEXT,
            Publisher TEXT, InstalledVersion TEXT, AvailableVersion TEXT, Status INTEGER NOT NULL, Support INTEGER NOT NULL,
            Confidence INTEGER NOT NULL, Scope INTEGER NOT NULL, Architecture TEXT, InstallLocation TEXT, IconSource TEXT,
            IsPinned INTEGER NOT NULL DEFAULT 0, IsExcluded INTEGER NOT NULL DEFAULT 0, Note TEXT, LastChecked TEXT);
        CREATE INDEX IX_Applications_Provider ON Applications(ProviderId);
        CREATE INDEX IX_Applications_Status ON Applications(Status);
        CREATE TABLE ApplicationSources (
            ApplicationId TEXT NOT NULL REFERENCES Applications(Id) ON DELETE CASCADE, ProviderId TEXT NOT NULL,
            SourceKey TEXT NOT NULL, Detail TEXT, PRIMARY KEY (ApplicationId, ProviderId, SourceKey));
        CREATE TABLE ProviderMetadata (ProviderId TEXT NOT NULL, Key TEXT NOT NULL, Value TEXT, PRIMARY KEY (ProviderId, Key));
        CREATE TABLE ProviderHealth (ProviderId TEXT PRIMARY KEY, Health INTEGER NOT NULL, Version TEXT, Message TEXT, CheckedAt TEXT NOT NULL);
        CREATE TABLE AvailableUpdates (
            ApplicationId TEXT PRIMARY KEY REFERENCES Applications(Id) ON DELETE CASCADE, AvailableVersion TEXT NOT NULL,
            Source TEXT, DetectedAt TEXT NOT NULL);
        CREATE TABLE ScanHistory (
            Id INTEGER PRIMARY KEY AUTOINCREMENT, StartedAt TEXT NOT NULL, DurationMs INTEGER NOT NULL,
            ApplicationCount INTEGER NOT NULL, UpdatesAvailable INTEGER NOT NULL, ProviderSummary TEXT);
        CREATE TABLE UpdateJobs (Id TEXT PRIMARY KEY, CreatedAt TEXT NOT NULL);
        CREATE TABLE UpdateJobItems (
            Id INTEGER PRIMARY KEY AUTOINCREMENT, JobId TEXT NOT NULL REFERENCES UpdateJobs(Id) ON DELETE CASCADE,
            ApplicationId TEXT NOT NULL, ApplicationName TEXT NOT NULL, ProviderId TEXT NOT NULL,
            State INTEGER NOT NULL, Message TEXT, UpdatedAt TEXT NOT NULL);
        CREATE INDEX IX_JobItems_State ON UpdateJobItems(State);
        CREATE TABLE UpdateHistory (
            Id INTEGER PRIMARY KEY AUTOINCREMENT, CompletedAt TEXT NOT NULL, ApplicationId TEXT NOT NULL, ApplicationName TEXT NOT NULL,
            ProviderId TEXT NOT NULL, PreviousVersion TEXT, RequestedVersion TEXT, NewVersion TEXT, FinalState INTEGER NOT NULL,
            DurationMs INTEGER NOT NULL, ExitCode INTEGER, ErrorCategory INTEGER NOT NULL, RestartRequired INTEGER NOT NULL,
            Message TEXT, TechnicalDetails TEXT);
        CREATE INDEX IX_History_CompletedAt ON UpdateHistory(CompletedAt);
        CREATE INDEX IX_History_App ON UpdateHistory(ApplicationId);
        CREATE TABLE Exclusions (ApplicationId TEXT PRIMARY KEY, ApplicationName TEXT NOT NULL, CreatedAt TEXT NOT NULL);
        CREATE TABLE SkippedVersions (ApplicationId TEXT NOT NULL, Version TEXT NOT NULL, CreatedAt TEXT NOT NULL, PRIMARY KEY (ApplicationId, Version));
        CREATE TABLE Schedules (Id INTEGER PRIMARY KEY AUTOINCREMENT, Kind TEXT NOT NULL, Definition TEXT NOT NULL, Enabled INTEGER NOT NULL DEFAULT 1);
        CREATE TABLE Settings (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
        """,
        // v2: remember what each job was doing so an interrupted job can be verified against the real installed version.
        """
        ALTER TABLE UpdateJobItems ADD COLUMN PackageId TEXT;
        ALTER TABLE UpdateJobItems ADD COLUMN PackageSource TEXT;
        ALTER TABLE UpdateJobItems ADD COLUMN PreviousVersion TEXT;
        ALTER TABLE UpdateJobItems ADD COLUMN TargetVersion TEXT;
        """,
    ];

    public void Migrate()
    {
        using var c = Open();
        var current = Convert.ToInt32(Scalar(c, null, "PRAGMA user_version;"), System.Globalization.CultureInfo.InvariantCulture);
        if (current > Migrations.Count)
            throw new InvalidOperationException($"Database schema v{current} is newer than this version of UpdateForge supports (v{Migrations.Count}). Refusing to modify it.");
        for (var v = current; v < Migrations.Count; v++)
        {
            using var tx = c.BeginTransaction();
            Exec(c, tx, Migrations[v]);
            Exec(c, tx, $"PRAGMA user_version = {v + 1};"); // integer literal, not user input
            tx.Commit();
        }
    }

    public static object? Scalar(SqliteConnection c, SqliteTransaction? tx, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    public static void Exec(SqliteConnection c, SqliteTransaction? tx, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
