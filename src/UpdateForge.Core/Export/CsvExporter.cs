using System.Text;
using UpdateForge.Core.Models;

namespace UpdateForge.Core.Export;

/// <summary>
/// Writes inventory data as RFC 4180 CSV. Cells that a spreadsheet would interpret as a formula (leading =, +, -, @, tab or CR)
/// are prefixed with an apostrophe, so package metadata can never become a formula when the file is opened in Excel.
/// </summary>
public static class CsvExporter
{
    private static readonly string[] Header =
        ["Name", "Publisher", "Installed", "Available", "Source", "PackageId", "Provider", "Status", "Support", "Pinned", "Excluded", "Note"];

    public static string Applications(IEnumerable<InstalledApplication> apps)
    {
        var sb = new StringBuilder();
        AppendRow(sb, Header);
        foreach (var a in apps)
        {
            AppendRow(sb,
            [
                a.Name, a.Publisher, a.InstalledVersion, a.AvailableVersion, a.PackageSource, a.PackageId, a.ProviderId,
                a.Status.ToString(), a.Support.ToString(), a.IsPinned ? "yes" : "no", a.IsExcluded ? "yes" : "no", a.Note,
            ]);
        }
        return sb.ToString();
    }

    /// <summary>UTF-8 with a byte order mark so Excel detects the encoding.</summary>
    public static void WriteFile(string path, string csv) => File.WriteAllText(path, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

    internal static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var v = value.Replace("\r\n", " ").Replace('\n', ' ');
        if (v[0] is '=' or '+' or '-' or '@' or '\t' or '\r') v = "'" + v;
        return v.IndexOfAny([',', '"', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    private static void AppendRow(StringBuilder sb, string?[] cells)
    {
        for (var i = 0; i < cells.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Escape(cells[i]));
        }
        sb.Append("\r\n");
    }
}
