using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UpdateForge.Application;

namespace UpdateForge.Infrastructure.Diagnostics;

/// <summary>Removes personal data from text before it leaves the machine in a bug report.</summary>
public static partial class Redactor
{
    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    // The Authorization branch also consumes the scheme word ("Bearer", "Basic") so the credential after it is removed too.
    [GeneratedRegex(@"(?i)(?:password|passwd|token|secret|apikey|api_key)\s*[=:]\s*\S+|authorization\s*[=:]\s*(?:\w+\s+)?\S+", RegexOptions.CultureInvariant)]
    private static partial Regex Secret();

    [GeneratedRegex(@"\b[0-9a-fA-F]{40,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex LongHex();

    public static string Redact(string text, string? userName = null, string? machineName = null, string? userProfile = null)
    {
        userName ??= Environment.UserName;
        machineName ??= Environment.MachineName;
        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var s = text;
        if (!string.IsNullOrEmpty(userProfile)) s = s.Replace(userProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        if (userName.Length > 2) s = s.Replace(userName, "<user>", StringComparison.OrdinalIgnoreCase);
        if (machineName.Length > 2) s = s.Replace(machineName, "<machine>", StringComparison.OrdinalIgnoreCase);
        s = Email().Replace(s, "<email>");
        s = Secret().Replace(s, m => m.Value[..m.Value.IndexOfAny(['=', ':'])] + "=<redacted>");
        s = LongHex().Replace(s, "<hex>");
        return s;
    }
}

public sealed class DiagnosticsExporter(ISettingsStore settings, IScanHistoryStore scans, IHistoryStore history, ProviderRegistry providers)
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    public async Task ExportAsync(string zipPath, string logDirectory, CancellationToken ct)
    {
        // Never delete an arbitrary file because of a mistyped path: only an earlier .zip may be replaced.
        if (File.Exists(zipPath))
        {
            if (!string.Equals(Path.GetExtension(zipPath), ".zip", StringComparison.OrdinalIgnoreCase))
                throw new IOException($"'{zipPath}' already exists and is not a .zip file; refusing to overwrite it.");
            File.Delete(zipPath);
        }
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        void Add(string name, string content)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            w.Write(Redactor.Redact(content));
        }

        var provider = new StringBuilder();
        foreach (var p in providers.All)
        {
            var a = await p.CheckAvailabilityAsync(ct).ConfigureAwait(false);
            provider.Append(p.Id).Append(": ").Append(a.Health).Append(' ').Append(a.Version).Append(' ').AppendLine(a.Message);
        }

        Add("system.txt", $"UpdateForge {typeof(DiagnosticsExporter).Assembly.GetName().Version}\nOS: {Environment.OSVersion}\n64-bit OS: {Environment.Is64BitOperatingSystem}\nCulture: {System.Globalization.CultureInfo.CurrentCulture.Name}\n.NET: {Environment.Version}\n");
        Add("providers.txt", provider.ToString());
        Add("settings.json", JsonSerializer.Serialize(await settings.LoadAsync(ct).ConfigureAwait(false), IndentedJson));
        var last = await scans.GetLastAsync(ct).ConfigureAwait(false);
        Add("last-scan.txt", last?.ToString() ?? "No scan recorded.");
        var recent = await history.QueryAsync(null, 50, ct).ConfigureAwait(false);
        Add("recent-history.txt", string.Join('\n', recent.Select(h => $"{h.CompletedAt:u} {h.ProviderId} {h.ApplicationId} {h.PreviousVersion}->{h.NewVersion} {h.FinalState} exit={h.ExitCode} err={h.Error} {h.Message}")));

        if (Directory.Exists(logDirectory))
            foreach (var file in new DirectoryInfo(logDirectory).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).Take(7))
            {
                string text;
                using (var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs)) text = await sr.ReadToEndAsync(ct).ConfigureAwait(false);
                Add("logs/" + file.Name, text);
            }
    }
}
