using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using UpdateForge.Application;
using UpdateForge.Core.Processes;
using UpdateForge.Infrastructure.Persistence;

namespace UpdateForge.Infrastructure;

/// <summary>
/// Cross-process "one update run at a time" lock (GUI, CLI and scheduled task share it).
/// A named semaphore, not a mutex: a Windows mutex must be released by the thread that acquired it, but the engine is
/// async and resumes on a different thread, which made ReleaseMutex throw. If the holder process dies, the kernel object
/// disappears with its last handle, so a crash cannot leave the lock stuck.
/// </summary>
public sealed class MutexEngineLock : IEngineLock
{
    private const string Name = @"Local\UpdateForge.UpdateEngine.Semaphore";
    private readonly string _name;

    public MutexEngineLock() : this(Name) { }

    public MutexEngineLock(string name) => _name = name;

    public IDisposable? TryAcquire()
    {
        var s = new Semaphore(1, 1, _name);
        if (s.WaitOne(0)) return new Releaser(s);
        s.Dispose();
        return null;
    }

    private sealed class Releaser(Semaphore s) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            s.Release();
            s.Dispose();
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
        services.AddSingleton<IEngineLock, MutexEngineLock>();
        services.AddSingleton<ProviderRegistry>();
        services.AddSingleton<InventoryService>();
        services.AddSingleton<UpdateEngine>();
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
