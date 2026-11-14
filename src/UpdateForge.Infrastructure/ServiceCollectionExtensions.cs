using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using UpdateForge.Application;
using UpdateForge.Core.Processes;
using UpdateForge.Infrastructure.Persistence;

namespace UpdateForge.Infrastructure;

/// <summary>
/// Cross-process "one update run at a time" lock (GUI, CLI and scheduled task share it).
/// Windows: a named semaphore, not a mutex: a Windows mutex must be released by the thread that acquired it, but the engine is
/// async and resumes on a different thread. If the holder dies, the kernel object disappears with its last handle.
/// Linux/macOS: named semaphores are unsupported, so an exclusive lock on a file under the runtime directory is used instead;
/// the OS drops it when the process exits, so a crash cannot leave it stuck either.
/// </summary>
public sealed class MutexEngineLock : IEngineLock
{
    private const string Name = @"Local\UpdateForge.UpdateEngine.Semaphore";
    private readonly string _name;

    public MutexEngineLock() : this(Name) { }

    public MutexEngineLock(string name) => _name = name;

    public IDisposable? TryAcquire() => OperatingSystem.IsWindows() ? TryAcquireSemaphore() : TryAcquireFile();

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private Releaser? TryAcquireSemaphore()
    {
        var s = new Semaphore(1, 1, _name);
        if (s.WaitOne(0)) return new Releaser(() => { s.Release(); s.Dispose(); });
        s.Dispose();
        return null;
    }

    private Releaser? TryAcquireFile()
    {
        var dir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } r && Directory.Exists(r) ? r : Path.GetTempPath();
        var file = Path.Combine(dir, new string([.. _name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_')]) + ".lock");
        try
        {
            // FileShare.None maps to an exclusive flock on Unix, so a second handle (this or another process) is refused.
            var fs = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            return new Releaser(fs.Dispose);
        }
        catch (IOException) { return null; }
    }

    private sealed class Releaser(Action release) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) release();
        }
    }
}

public static class ServiceCollectionExtensions
{
    public static string LogDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpdateForge", "logs");

    public static IServiceCollection AddUpdateForgeCore(this IServiceCollection services, string? databasePath = null)
    {
        var db = new Database(databasePath ?? Database.DefaultPath);
        db.Migrate();
        services.AddSingleton(db);
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IInventoryStore, SqliteInventoryStore>();
        services.AddSingleton<IScanHistoryStore, SqliteScanHistoryStore>();
        services.AddSingleton<IExclusionStore, SqliteExclusionStore>();
        services.AddSingleton<IJobStore, SqliteJobStore>();
        services.AddSingleton<IHistoryStore, SqliteHistoryStore>();
        services.AddSingleton<ISettingsStore, SqliteSettingsStore>();
        services.AddSingleton<IInterestStore, SqliteInterestStore>();
        services.AddSingleton<IEngineLock, MutexEngineLock>();
        services.AddSingleton<ProviderRegistry>();
        services.AddSingleton<PackageManagerService>();
        services.AddSingleton<PackageSearchService>();
        services.AddSingleton<InventoryService>();
        services.AddSingleton<UpdateEngine>();
        services.AddSingleton<BundleService>();
        services.AddSingleton<PackageInstallService>();
        services.AddSingleton<RecommendationService>();
        services.AddSingleton<UpdateForge.Infrastructure.Diagnostics.DiagnosticsExporter>();

        Directory.CreateDirectory(LogDirectory);
        var logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(LogDirectory, "updateforge-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14,
                formatProvider: System.Globalization.CultureInfo.InvariantCulture, outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        services.AddLogging(b => b.ClearProviders().AddSerilog(logger, dispose: true));
        return services;
    }
}
