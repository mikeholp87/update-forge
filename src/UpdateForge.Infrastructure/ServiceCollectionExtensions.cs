using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using UpdateForge.Application;
using UpdateForge.Core.Processes;
using UpdateForge.Infrastructure.Persistence;

namespace UpdateForge.Infrastructure;

/// <summary>Named mutex shared by every UpdateForge process of the same user session.</summary>
public sealed class MutexEngineLock : IEngineLock
{
    private const string Name = @"Local\UpdateForge.UpdateEngine";

    public IDisposable? TryAcquire()
    {
        var m = new Mutex(false, Name);
        try
        {
            if (m.WaitOne(0)) return new Releaser(m);
        }
        catch (AbandonedMutexException) { return new Releaser(m); } // previous holder crashed; we own it now
        m.Dispose();
        return null;
    }

    private sealed class Releaser(Mutex m) : IDisposable
    {
        public void Dispose() { m.ReleaseMutex(); m.Dispose(); }
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
