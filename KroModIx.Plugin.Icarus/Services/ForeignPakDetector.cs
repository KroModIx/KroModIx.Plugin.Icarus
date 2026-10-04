using System;
using System.IO;

namespace KroModIx.Plugin.Icarus.Services;

/// <summary>Erkennt Paks im Mods-Ordner, die <b>einem anderen
/// Mod-Manager gehören</b>.
///
/// <para><b>Warum es das gibt</b> — am 03.10.2026 bezahlt: in
/// <c>Content/Paks/mods/</c> lag <c>zzz_LMM_Merged_P.pak</c>, das
/// zusammengeführte Datentabellen-Pak von
/// <a href="https://github.com/DonovanMods/linux-mod-manager">lmm</a>. Das
/// Plugin listete es als gewöhnliche manuelle Mod, mit Umschalten und
/// Deinstallieren. Ein Klick darauf — und alle Datentabellen-Mods, die lmm
/// dort zusammengeführt hatte, waren aus dem Spiel verschwunden. Nicht
/// kaputt, nur weg: die Quelle lag weiter in lmms Zwischenspeicher, und im
/// Spiel fehlte der Gegenstand ohne jede Meldung. Gesucht wurde der Fehler
/// danach stundenlang im Spiel.</para>
///
/// <para><b>Das Erkennungsmerkmal ist der Symlink, nicht der Name.</b> lmm
/// legt seine Paks als Verweis in den Spielordner und hält die Datei in
/// seinem eigenen Zwischenspeicher. Ein von Hand hineinkopiertes Pak ist
/// eine gewöhnliche Datei. Damit trägt die Erkennung auch für Mod-Manager,
/// deren Namensschema wir nicht kennen — und sie hängt nicht daran, dass
/// jemand sein Präfix beibehält. Der Name dient nur als zweites Netz für
/// den Fall, dass ein Manager kopiert statt zu verweisen.</para></summary>
public static class ForeignPakDetector
{
    /// <summary>Namens-Präfix der zusammengeführten Paks von lmm. Nur
    /// Rückfallebene — das verlässliche Merkmal ist der Symlink.</summary>
    private const string LmmPrefix = "zzz_LMM_";

    /// <summary>Prüft, ob <paramref name="pakPath"/> einem fremden
    /// Mod-Manager gehört, und nennt ihn beim Namen.</summary>
    /// <param name="owner">Der erkannte Verwalter („lmm") oder eine
    /// neutrale Bezeichnung, wenn nur klar ist <i>dass</i> es ein fremder
    /// ist.</param>
    public static bool IsForeignManaged(string pakPath, out string owner)
    {
        owner = "";
        try
        {
            var name = Path.GetFileName(pakPath);
            if (name.StartsWith(LmmPrefix, StringComparison.OrdinalIgnoreCase))
            {
                owner = "lmm";
                return true;
            }

            var info = new FileInfo(pakPath);
            if (info.LinkTarget is not null)
            {
                owner = OwnerFromTarget(info.LinkTarget);
                return true;
            }
        }
        catch (Exception)
        {
            // Eine unlesbare Datei ist kein Grund, die ganze Liste
            // abzubrechen — dann gilt sie als gewoehnliche Mod und
            // verhaelt sich wie bisher.
        }
        return false;
    }

    /// <summary>Leitet den Verwalter aus dem Ziel des Verweises ab. Mehr als
    /// eine Vermutung ist das nicht, deshalb bleibt es bei dem einen Fall,
    /// den wir belegt haben, und sonst neutral.</summary>
    private static string OwnerFromTarget(string linkTarget)
    {
        var ziel = linkTarget.Replace('\\', '/');
        return ziel.Contains("/lmm/", StringComparison.OrdinalIgnoreCase)
            ? "lmm"
            : "ein anderer Mod-Manager";
    }
}
