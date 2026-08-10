using System.Text.RegularExpressions;

namespace KroModIx.Plugin.Icarus.Services.Nexus;

/// <summary>
/// Extrahiert die Nexus-Mod-Id aus einem Download-Filename, den die Nexus-CDN
/// vergibt. Format (empirisch, Stand 2026):
/// <c>&lt;Mod Name&gt; &lt;mod_id&gt; &lt;version&gt; &lt;yyyy-MM-ddTHH-mmZ&gt; &lt;hash&gt;.&lt;ext&gt;.pak</c>
///
/// <para>Beispiele aus einem Icarus-Downloads-Ordner:</para>
/// <list type="bullet">
/// <item><c>IcarusStutterFix v0.2.0 294 0.2.0 2026-08-09T08-33Z KeUvDtKhb.zip.pak</c> → 294</item>
/// <item><c>Balanced Armor Protection X2.5 250 1.0.5 2026-08-10T01-15Z AHkozCAwW.rar.pak</c> → 250</item>
/// <item><c>Rada-CheatMenu 289 1.9 2026-08-09T21-35Z 6MsNEx6U7.rar.pak</c> → 289</item>
/// </list>
///
/// <para>Die mod_id-Position ist die letzte reine Integer-Gruppe VOR der
/// Version — die Version kann Punkte enthalten (`0.2.0`), die mod_id nicht.
/// Regex ankert am Timestamp-Muster + Hash + Extension.</para>
/// </summary>
public static class NexusFileNameParser
{
    // <name> <mod_id (digits)> <version (non-space, muss . oder digits enthalten)>
    // <timestamp yyyy-MM-ddTHH-mmZ> <hash (alnum)>.<ext (letters)>.pak
    private static readonly Regex Pattern = new(
        @"^(?<name>.*?)\s+(?<modId>\d+)\s+(?<version>\S+)\s+(?<timestamp>\d{4}-\d{2}-\d{2}T\d{2}-\d{2}Z)\s+[A-Za-z0-9]+\.[a-zA-Z0-9]+\.pak$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static int? TryExtractModId(string fileName)
    {
        var m = Pattern.Match(fileName);
        if (!m.Success) return null;
        return int.TryParse(m.Groups["modId"].Value, out var id) ? id : null;
    }

    /// <summary>Falls Detail-Fetch fehlschlägt: aus dem Filename einen halbwegs
    /// lesbaren Anzeigename ableiten (die Nexus-Metadaten hätte man normal
    /// per API-Call, das hier ist der Fallback).</summary>
    public static string? TryExtractModName(string fileName)
    {
        var m = Pattern.Match(fileName);
        return m.Success ? m.Groups["name"].Value.Trim() : null;
    }
}
