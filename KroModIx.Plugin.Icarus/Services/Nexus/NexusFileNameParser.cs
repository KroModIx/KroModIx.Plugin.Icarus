using System.Text.RegularExpressions;

namespace KroModIx.Plugin.Icarus.Services.Nexus;

/// <summary>
/// Extrahiert Nexus-Mod-Id, Name und Version aus einem Nexus-CDN-Dateinamen.
/// Nexus vergibt <b>zwei</b> Formate, und beide müssen gematcht werden:
///
/// <list type="number">
/// <item><b>Space-Format (ISO-Zeitstempel)</b> — kommt aus der CDN-URL, also
/// bei jedem Browser-Download:
/// <c>&lt;Name&gt; &lt;modId&gt; &lt;Version&gt; &lt;yyyy-MM-ddTHH-mmZ&gt; &lt;Hash&gt;.&lt;ext&gt;</c>
/// Beispiel: <c>OreDepot V1.0.1 347 2 2026-10-01T20-47Z yxOAgLyJG.zip</c> → 347</item>
/// <item><b>Dash-Format (Unix-Zeitstempel)</b> — Server-Dateiname aus
/// <c>NexusFileEntry.FileName</c>, also beim Premium-Direct-Download:
/// <c>&lt;Name&gt;-&lt;modId&gt;-&lt;Version mit Bindestrichen&gt;-&lt;unix-ts&gt;.&lt;ext&gt;</c>
/// Beispiel: <c>IcarusStutterFix-294-0-2-0-1703155833.zip</c> → 294</item>
/// </list>
///
/// <para><b>Endungen:</b> <c>.pak</c>, <c>.zip</c>, <c>.rar</c>, <c>.7z</c> —
/// und jede davon optional mit angehängtem <c>.pak</c>. Letzteres ist keine
/// Nexus-Konvention, sondern Altlast: bis v1.22 hängte
/// <c>DownloadPakAsync</c> jedem Download ein <c>.pak</c> an. Dateien wie
/// <c>… yxOAgLyJG.zip.pak</c> liegen deshalb in bestehenden Downloads-Ordnern
/// und müssen weiter erkannt werden.</para>
///
/// <para><b>Warum beide Formate Pflicht sind:</b> bis v1.22 verlangte das
/// Muster hier ein abschließendes <c>.pak</c> und kannte das Dash-Format
/// nicht. Ein echter Nexus-ZIP fiel damit durch —
/// <see cref="TryExtractModId"/> gab <c>null</c> zurück, der Enricher
/// übersprang die Row sauber, und im Downloads-Tab blieb die Mod ohne Cover,
/// Autor und mit totem Details-Knopf. Kein Fehler, kein Log-Eintrag. Genau
/// dieser stille Ausfall ist in DSP v0.6.0 wochenlang unbemerkt geblieben.</para>
///
/// <para>Die Version wird beim Dash-Format normalisiert (Bindestriche zu
/// Punkten), aus <c>0-2-0</c> wird also <c>0.2.0</c>.</para>
/// </summary>
public static class NexusFileNameParser
{
    /// <summary>Gemeinsames Endungs-Muster beider Formate: eine der vier
    /// echten Endungen, optional mit angehängtem <c>.pak</c>.</summary>
    private const string ExtPattern = @"(?:pak|zip|rar|7z)(?:\.pak)?";

    // <Name> <modId> <Version> <ISO-Zeitstempel> <Hash>.<ext>
    private static readonly Regex SpacePattern = new(
        @"^(?<name>.*?)\s+(?<modId>\d+)\s+(?<version>\S+)\s+(?<timestamp>\d{4}-\d{2}-\d{2}T\d{2}-\d{2}Z)\s+[A-Za-z0-9]+\." + ExtPattern + "$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // <Name>-<modId>-<Version-Teile>-<unix10>.<ext>
    private static readonly Regex DashPattern = new(
        @"^(?<name>.+?)-(?<modId>\d+)(?<version>(?:-\d+)+)-(?<ts>\d{10})\." + ExtPattern + "$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Die Mod-Id, oder null wenn der Dateiname keinem der beiden
    /// Nexus-Muster folgt (eine selbst hineinkopierte Datei etwa).</summary>
    public static int? TryExtractModId(string fileName)
    {
        var m = SpacePattern.Match(fileName);
        if (m.Success && int.TryParse(m.Groups["modId"].Value, out var id1)) return id1;
        m = DashPattern.Match(fileName);
        if (m.Success && int.TryParse(m.Groups["modId"].Value, out var id2)) return id2;
        return null;
    }

    /// <summary>Falls der Detail-Abruf fehlschlägt: aus dem Dateinamen einen
    /// halbwegs lesbaren Anzeigenamen ableiten. Die echten Metadaten kommen
    /// normal über die API, das hier ist der Ausweichpfad.</summary>
    public static string? TryExtractModName(string fileName)
    {
        var m = SpacePattern.Match(fileName);
        if (m.Success) return m.Groups["name"].Value.Trim();
        m = DashPattern.Match(fileName);
        return m.Success ? m.Groups["name"].Value.Trim() : null;
    }

    /// <summary>Die Version aus dem Dateinamen — für den Update-Vergleich
    /// gegen <c>NexusModDetail.Version</c>.</summary>
    public static string? TryExtractVersion(string fileName)
    {
        var m = SpacePattern.Match(fileName);
        if (m.Success) return m.Groups["version"].Value.Trim();
        m = DashPattern.Match(fileName);
        if (!m.Success) return null;
        return m.Groups["version"].Value.TrimStart('-').Replace('-', '.');
    }
}
