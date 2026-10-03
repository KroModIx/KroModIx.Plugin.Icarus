using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;

namespace KroModIx.Plugin.Icarus.Services.Ue4ss;

/// <summary>Was beim Setzen der DLL-Umleitung herauskam.</summary>
public enum DllOverrideResult
{
    /// <summary>Eingetragen — wirkt beim nächsten Spielstart.</summary>
    Added,
    /// <summary>Stand schon drin, nichts zu tun.</summary>
    AlreadySet,
    /// <summary>Kein Proton-Präfix vorhanden (Windows, oder Spiel nie
    /// gestartet).</summary>
    NoPrefix,
    /// <summary>Präfix da, aber <c>user.reg</c> nicht lesbar oder nicht
    /// beschreibbar.</summary>
    Failed,
}

/// <summary>Trägt die DLL-Umleitung in die <c>user.reg</c> eines
/// Proton-Präfix ein, die UE4SS unter Linux zum Laden braucht.
///
/// <para><b>Warum das nötig ist:</b> UE4SS hängt sich über eine
/// mitgelieferte <c>dwmapi.dll</c> ein, die neben der Spiel-Exe liegt. Proton
/// bringt aber seine eigene <c>dwmapi.dll</c> mit und bevorzugt sie — die
/// Datei des Mod-Loaders wird nie geladen. Es gibt dafür kein Symptom außer
/// Abwesenheit: das Spiel startet normal, keine Fehlermeldung, keine
/// <c>UE4SS.log</c>, die Lua-Mods tun nichts.</para>
///
/// <para><b>Warum die Registry und nicht die Steam-Startoptionen:</b> die
/// verbreitete Anleitung setzt <c>WINEDLLOVERRIDES="dwmapi=n,b"</c> als
/// Startoption. Das steht in Steams <c>localconfig.vdf</c>, die ein
/// laufender Steam-Client beim Beenden aus dem Speicher zurückschreibt —
/// jede Änderung von außen wäre also verloren, solange Steam läuft, und das
/// tut es, wenn der User gerade modded. Der Eintrag in der <c>user.reg</c>
/// des Präfix wirkt sofort und braucht keinen Steam-Neustart.</para>
///
/// <para><b>Grenze, die der Aufrufer dem User sagen muss:</b> legt Proton das
/// Präfix neu an (Proton-Wechsel, „Spieldateien überprüfen" mit Reset), ist
/// der Eintrag weg und UE4SS lädt wieder stillschweigend nicht. Deshalb
/// prüft das Plugin ihn bei jedem Refresh neu, statt sich das Ergebnis zu
/// merken.</para>
///
/// <para><b>Host-Kandidat (Kernprinzip 4):</b> jedes Plugin für ein
/// Unreal-Spiel mit UE4SS braucht genau diese Funktion. Sie liegt bewusst in
/// einer eigenen, abhängigkeitsfreien Klasse, damit die Wanderung in
/// <c>IHostServices</c> ein Verschieben wird und kein Neuschreiben.</para></summary>
public static class ProtonDllOverride
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const string SectionHeader = @"[Software\\Wine\\DllOverrides]";
    private const string BackupSuffix = ".kromodix-backup";

    /// <summary>Der Wert, den UE4SS braucht: erst die Datei neben der Exe,
    /// dann die von Proton.</summary>
    public const string NativeBuiltin = "native,builtin";

    /// <summary>Prüft, ob die Umleitung für <paramref name="dllName"/> im
    /// Präfix gesetzt ist. Null-Präfix oder fehlende Datei zählen als
    /// „nicht gesetzt" — für den Aufrufer ist das dasselbe.</summary>
    public static bool IsSet(string? protonPrefix, string dllName = "dwmapi")
    {
        var regPath = ResolveUserReg(protonPrefix);
        if (regPath is null) return false;
        try
        {
            return FindOverrideValue(File.ReadAllLines(regPath), dllName) is not null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "user.reg nicht lesbar: {Path}", regPath);
            return false;
        }
    }

    /// <summary>Setzt die Umleitung, falls sie fehlt. Idempotent: ein
    /// zweiter Aufruf liefert <see cref="DllOverrideResult.AlreadySet"/> und
    /// schreibt nicht.</summary>
    public static DllOverrideResult Ensure(string? protonPrefix, string dllName = "dwmapi")
    {
        var regPath = ResolveUserReg(protonPrefix);
        if (regPath is null) return DllOverrideResult.NoPrefix;

        try
        {
            var lines = File.ReadAllLines(regPath).ToList();
            var existing = FindOverrideValue(lines, dllName);
            if (existing is not null)
            {
                Log.Info("DLL-Umleitung steht schon: {Dll}={Value}", dllName, existing);
                return DllOverrideResult.AlreadySet;
            }

            // Einmalige Sicherung — die user.reg ist die einzige Datei, die
            // wir in einem fremden Praefix anfassen.
            var backup = regPath + BackupSuffix;
            if (!File.Exists(backup)) File.Copy(regPath, backup);

            var entry = $"\"{dllName}\"=\"{NativeBuiltin}\"";
            var insertAt = FindInsertIndex(lines);
            if (insertAt < 0)
            {
                // Keine DllOverrides-Sektion vorhanden — anlegen. Die Zahl
                // hinter dem Schluessel ist der Aenderungszeitpunkt in
                // Unix-Sekunden, die #time-Zeile derselbe Moment als
                // FILETIME in Hex. Wine schreibt beides selbst, deshalb
                // liefern wir es auch.
                var now = DateTimeOffset.UtcNow;
                lines.Add("");
                lines.Add($"{SectionHeader} {now.ToUnixTimeSeconds()}");
                lines.Add($"#time={now.UtcDateTime.ToFileTimeUtc():x}");
                lines.Add(entry);
            }
            else
            {
                lines.Insert(insertAt, entry);
            }

            var tmp = regPath + ".tmp";
            File.WriteAllLines(tmp, lines);
            File.Move(tmp, regPath, overwrite: true);
            Log.Info("DLL-Umleitung eingetragen: {Dll}={Value} in {Path}",
                dllName, NativeBuiltin, regPath);
            return DllOverrideResult.Added;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "DLL-Umleitung konnte nicht gesetzt werden: {Path}", regPath);
            return DllOverrideResult.Failed;
        }
    }

    /// <summary>Die <c>user.reg</c> im Präfix, oder null. Der Host liefert
    /// den Präfix-Pfad als <c>DetectedGame.ProtonPrefix</c>; unter Windows
    /// ist er null, unter Linux erst nach dem ersten Spielstart gesetzt.</summary>
    public static string? ResolveUserReg(string? protonPrefix)
    {
        if (string.IsNullOrWhiteSpace(protonPrefix)) return null;
        var p = Path.Combine(protonPrefix, "user.reg");
        return File.Exists(p) ? p : null;
    }

    /// <summary>Der gesetzte Wert, oder null wenn kein Eintrag für die DLL
    /// in der DllOverrides-Sektion steht. Die Suche bleibt bewusst in der
    /// Sektion: derselbe Schlüsselname kann in anderen Sektionen
    /// vorkommen.</summary>
    private static string? FindOverrideValue(IReadOnlyList<string> lines, string dllName)
    {
        var prefix = $"\"{dllName}\"=";
        var inSection = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inSection = line.StartsWith(SectionHeader, StringComparison.Ordinal);
                continue;
            }
            if (!inSection) continue;
            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            return line[prefix.Length..].Trim('"');
        }
        return null;
    }

    /// <summary>Zeilenindex, an dem der neue Eintrag landen soll: direkt
    /// nach der Sektions-Kopfzeile und deren <c>#time</c>-Zeile. −1, wenn
    /// die Sektion fehlt.</summary>
    private static int FindInsertIndex(IReadOnlyList<string> lines)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            if (!lines[i].TrimStart().StartsWith(SectionHeader, StringComparison.Ordinal)) continue;
            var at = i + 1;
            while (at < lines.Count && lines[at].StartsWith("#time=", StringComparison.Ordinal)) at++;
            return at;
        }
        return -1;
    }
}
