using System;
using System.Globalization;
using System.IO;
using System.Linq;
using NLog;

namespace KroModIx.Plugin.Icarus.Services.Nexus;

/// <summary>
/// Zählt „neue" Nexus-Katalog-Einträge seit dem letzten Besuch des Nexus-Tabs
/// pro Zielspiel. Wird vom <see cref="KroModIx.Plugin.Icarus.IcarusPlugin"/>-
/// IUpdateNotifier verwendet — der Host rendert das als grünen ↑-Badge auf
/// der Icarus-Kachel.
///
/// <para>Speichert die Baseline (<c>lastSeenUtc</c>) als ISO-Timestamp im
/// PluginCache: <c>&lt;NexusCacheDir&gt;/last-seen.txt</c>. Kein RIA-Handling
/// nötig — bei Concurrency verlieren wir maximal einen Update-Count, nicht
/// mehr.</para>
///
/// <para>Ohne bestehende Baseline: die aktuelle Zeit wird als Baseline gesetzt
/// und 0 zurückgegeben — Benutzer sehen sofort einen sauberen Zustand, keine
/// „alle Mods sind neu"-Fehldeutung wie beim ersten CDN-Load.</para>
/// </summary>
public sealed class NexusUpdateTracker
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly IcarusPaths _paths;

    public NexusUpdateTracker(IcarusPaths paths) => _paths = paths;

    private string LastSeenPath => Path.Combine(_paths.NexusCacheDir, "last-seen.txt");

    /// <summary>Liest den bisher gespeicherten Baseline-Timestamp; wenn keiner
    /// existiert, legt „jetzt" als Baseline an und liefert null. Der aufrufende
    /// Notifier gibt in dem Fall 0 Updates zurück.</summary>
    public DateTime? GetOrInitBaseline()
    {
        try
        {
            Directory.CreateDirectory(_paths.NexusCacheDir);
            if (File.Exists(LastSeenPath))
            {
                var raw = File.ReadAllText(LastSeenPath).Trim();
                if (DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var parsed))
                    return parsed;
                Log.Warn("Nexus last-seen unparsbar ({Raw}) — neu setzen", raw);
            }
            var now = DateTime.UtcNow;
            File.WriteAllText(LastSeenPath, now.ToString("o", CultureInfo.InvariantCulture));
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Konnte Nexus last-seen nicht lesen/schreiben");
            return null;
        }
    }

    /// <summary>Wird vom Nexus-Tab beim Öffnen aufgerufen: setzt die Baseline
    /// auf jetzt. Danach ist der Badge-Zähler 0 bis neue Einträge im Katalog
    /// auftauchen deren <c>UpdatedUtc</c> jünger als jetzt ist.</summary>
    public void MarkSeen()
    {
        try
        {
            Directory.CreateDirectory(_paths.NexusCacheDir);
            File.WriteAllText(LastSeenPath, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Konnte Nexus last-seen nicht aktualisieren");
        }
    }

    /// <summary>Zählt Katalog-Einträge deren <see cref="NexusCatalogEntry.UpdatedUtc"/>
    /// jünger als die Baseline ist. Bei fehlender Baseline: 0
    /// (siehe <see cref="GetOrInitBaseline"/>).</summary>
    public int CountNewSince(CatalogSnapshot snapshot)
    {
        var baseline = GetOrInitBaseline();
        if (baseline is null) return 0;
        return snapshot.Entries.Count(e => e.UpdatedUtc > baseline.Value);
    }
}
