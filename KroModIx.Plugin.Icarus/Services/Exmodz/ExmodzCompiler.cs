using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KroModIx.Plugin.Contracts;
using NLog;

namespace KroModIx.Plugin.Icarus.Services.Exmodz;

/// <summary>Eine Quelle für den Zusammenbau: eine installierte
/// <c>.EXMODZ</c>-Datei samt Anzeigename. Die Reihenfolge der Liste ist die
/// Ladereihenfolge.</summary>
public sealed record ExmodzSource(string ModId, string DisplayName, string FilePath);

/// <summary>Was ein Zusammenbau ergeben hat.</summary>
public sealed record MergeResult(
    bool Ok,
    string Message,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> FailedMods,
    int TableCount,
    int AssetCount);

/// <summary>Baut aus allen installierten <c>.EXMODZ</c> ein einzelnes Pak,
/// gerechnet gegen die <b>aktuelle</b> <c>Content/Data/data.pak</c> des
/// Spiels.
///
/// <para><b>Warum ein gemeinsames Pak und nicht eins je Mod:</b> ein
/// Tabellen-Override ist immer die ganze Tabelle. Zwei Mods, die dieselbe
/// Tabelle anfassen, würden sich als getrennte Paks ganztabellig
/// überschatten — der eine gewinnt, der andere wirkt gar nicht. In einem
/// gemeinsamen Pak komponieren sie dagegen auf Feldebene, und zwar
/// kostenlos: <see cref="DataTablePatcher.ApplyRowPatch"/> führt die Felder
/// eines Items immer in den aktuellen Stand der Zielzeile ein. Die Bytes von
/// Mod A wieder als „Basis" für Mod B zu nehmen, statt die unberührte
/// Basistabelle neu zu lesen, <b>ist</b> der ganze Merge-Algorithmus. Zwei
/// Mods, die verschiedene Felder derselben Zeile oder verschiedene Zeilen
/// derselben Tabelle setzen, überleben beide; nur ein echter
/// gleiche-Zeile-gleiches-Feld-Schreibzugriff ist Letzter-gewinnt — ein
/// gewöhnlicher Upsert-Ausgang, keine Warnung wert.</para>
///
/// <para><b>Mitgelieferte Assets können so nicht komponieren.</b> Eine
/// Pfad-Kollision zwischen zwei Mods ist notwendig Letzter-gewinnt und wird
/// deshalb als Warnung gemeldet, die beide Mods benennt.</para>
///
/// <para><b>Die Basistabellen kommen direkt aus der installierten
/// <c>data.pak</c>.</b> Damit ist das Ergebnis per Konstruktion
/// wochen-aktuell und der ganze Vorgang offline — es gibt keinen Abgleich
/// mit einer Tabellen-Sammlung, die veralten könnte.</para>
///
/// <para>Portiert aus lmms <c>internal/source/icarus</c> (MIT, Donovan C.
/// Young).</para></summary>
public sealed class ExmodzCompiler
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>Der Mount-Point, den ein gebautes <c>_P.pak</c> deklarieren
    /// muss, damit Icarus' Datentabellen-Lader es findet.
    ///
    /// <para><c>../../../</c> führt vom Verzeichnis der Spiel-Exe zum äußeren
    /// Spielordner; von dort steigen echte Icarus-Mods mit einem wörtlichen
    /// <c>Icarus/Content/</c> wieder ab — der UProject-Ordner heißt selbst
    /// „Icarus". Belegt sowohl durch die Mount-Strings echter Mod-Paks als
    /// auch durch die Verschachtelung der <c>data.pak</c> selbst
    /// (<c>…/Icarus/Icarus/Content/Data/data.pak</c>).</para>
    ///
    /// <para><b>Steht hier und nicht im Host</b>, weil es Icarus-Wissen ist:
    /// der Host-Baukasten nimmt den Mount-Point vom Aufrufer, damit er für
    /// Satisfactory genauso taugt.</para></summary>
    public const string IcarusContentMountPoint = "../../../Icarus/Content/";

    /// <summary>Wird dem mount-relativen Pfad einer gepatchten Basistabelle
    /// vorangestellt, bevor sie ins gebaute Pak wandert. Echte Mods legen
    /// ihre Tabellen-Überschreibungen unter
    /// <c>Icarus/Content/data/&lt;derselbe Pfad&gt;</c> ab.
    ///
    /// <para><b>Nicht</b> auf mitgelieferte Assets anwenden: die sind
    /// Content-Pakete, keine Tabellen-Überschreibungen. Ein Pak kann beide
    /// Klassen nicht mit demselben Präfix adressieren.</para></summary>
    public const string IcarusDataTablePrefix = "data/";

    private readonly IUnrealPakService _paks;

    public ExmodzCompiler(IUnrealPakService paks) => _paks = paks;

    /// <summary>Baut das gemeinsame Pak und schreibt es nach
    /// <paramref name="outputPakPath"/>.
    ///
    /// <para>Eine Mod, die scheitert, bricht nicht den ganzen Lauf ab: sie
    /// wird in <see cref="MergeResult.FailedMods"/> genannt, und die anderen
    /// werden fertig gebaut. Der Grund ist praktisch — eine einzige kaputte
    /// oder zur Spielwoche nicht passende Mod würde sonst alle anderen
    /// mitnehmen, und der User hätte kein Mittel außer Raten, welche es
    /// war.</para></summary>
    public MergeResult Merge(string basePakPath, IReadOnlyList<ExmodzSource> sources,
        string outputPakPath)
    {
        if (sources.Count == 0)
            return new MergeResult(false, "Keine .EXMODZ installiert.", [], [], 0, 0);
        if (!File.Exists(basePakPath))
            return new MergeResult(false,
                $"Basis-Datentabellen nicht gefunden: {basePakPath}", [], [], 0, 0);

        var warnings = new List<string>();
        var failed = new List<string>();

        // mountPath -> aktueller (moeglicherweise schon gepatchter) Stand
        var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        // Asset-Pfad -> Daten, und wer ihn zuletzt gesetzt hat
        var assets = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var assetOwner = new Dictionary<string, string>(StringComparer.Ordinal);

        using var basePak = _paks.OpenRead(basePakPath);
        var basePaths = basePak.Entries.Select(e => e.Path).ToList();

        foreach (var src in sources)
        {
            try
            {
                ApplyOne(basePak, basePaths, src, tables, assets, assetOwner, warnings);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "EXMODZ konnte nicht angewandt werden: {Mod}", src.DisplayName);
                failed.Add($"{src.DisplayName}: {ex.Message}");
            }
        }

        if (tables.Count == 0 && assets.Count == 0)
        {
            return new MergeResult(false,
                failed.Count > 0
                    ? "Keine der installierten .EXMODZ ließ sich anwenden."
                    : "Die installierten .EXMODZ ändern nichts.",
                warnings, failed, 0, 0);
        }

        var builder = _paks.CreateBuilder(IcarusContentMountPoint);
        foreach (var (mountPath, data) in tables)
            builder.Add(IcarusDataTablePrefix + mountPath, data);
        foreach (var (assetPath, data) in assets)
        {
            // Kein data/-Praefix: mitgelieferte Assets sind Content-Pakete,
            // keine Tabellen-Ueberschreibungen. Sie brauchen nur den
            // Mount-Point, um unter Icarus/Content/ an ihrem eigenen
            // Namensraum-Pfad zu landen.
            builder.Add(assetPath, data);
        }
        builder.Write(outputPakPath);

        var msg = $"{tables.Count} Datentabelle(n)"
                  + (assets.Count > 0 ? $" und {assets.Count} Asset(s)" : "")
                  + $" aus {sources.Count - failed.Count} Mod(s) zusammengebaut.";
        Log.Info("EXMODZ-Zusammenbau fertig: {Message}", msg);
        return new MergeResult(true, msg, warnings, failed, tables.Count, assets.Count);
    }

    private static void ApplyOne(IUnrealPakReader basePak, List<string> basePaths, ExmodzSource src,
        Dictionary<string, byte[]> tables, Dictionary<string, byte[]> assets,
        Dictionary<string, string> assetOwner, List<string> warnings)
    {
        var bundle = ExmodzParser.ParseFile(src.FilePath);

        foreach (var row in bundle.Diff.Rows)
        {
            if (row.CurrentFile == ExmodParser.EndOfModSentinel) continue;
            if (row.CurrentFile.Length == 0) continue;
            if (row.FileItems.Count == 0)
                throw new InvalidDataException(
                    $"Zeile {row.CurrentFile} hat keine File_Items (fehlerhaftes .EXMOD-Manifest).");

            var mountPath = MatchMountPath(basePaths, row.CurrentFile);
            // Hier liegt der Merge: der aktuelle Stand der Tabelle ist die
            // Basis fuer die naechste Mod. Nur beim ersten Zugriff kommt die
            // unberuehrte Tabelle aus dem Pak.
            if (!tables.TryGetValue(mountPath, out var current))
                current = basePak.Read(mountPath);
            tables[mountPath] = DataTablePatcher.ApplyRowPatch(current, row);
        }

        foreach (var (rawPath, data) in bundle.Assets)
        {
            var safePath = SanitizeAssetPath(rawPath);
            if (assetOwner.TryGetValue(safePath, out var previous)
                && !string.Equals(previous, src.DisplayName, StringComparison.Ordinal))
            {
                warnings.Add(
                    $"Asset-Kollision bei {safePath}: „{src.DisplayName}“ überschreibt „{previous}“. " +
                    "Assets können nicht wie Datentabellen zusammengeführt werden — " +
                    "hier gewinnt die Mod, die in der Ladereihenfolge weiter unten steht.");
            }
            assets[safePath] = data;
            assetOwner[safePath] = src.DisplayName;
        }
    }

    /// <summary>Findet die Basistabelle, die eine Zeile meint.
    ///
    /// <para>Das <c>.EXMOD</c>-Schema flacht den mount-relativen
    /// Verzeichnispfad in <c>CurrentFile</c> ab, indem es jeden <c>/</c> durch
    /// <c>-</c> ersetzt — der echte Pak-Pfad
    /// <c>Audio/MusicConditions/D_MusicLocationConditions.json</c> steht dort
    /// als <c>Audio-MusicConditions-D_MusicLocationConditions.json</c>. Die
    /// Umkehrung rekonstruiert den Pfad genau. Geprüft gegen eine echte
    /// Installation: keiner der 299 Basis-Pfade enthält einen wörtlichen
    /// Bindestrich, die Abbildung ist also eindeutig.</para>
    ///
    /// <para>Scheitert laut bei null oder mehreren Treffern. Zu raten, welcher
    /// gemeint ist, wäre genau der stille Ausweichpfad, den dieses Plugin
    /// vermeidet.</para></summary>
    internal static string MatchMountPath(IReadOnlyList<string> paths, string currentFile)
    {
        var candidate = currentFile.Replace('-', '/');
        var matches = paths.Where(p => string.Equals(p, candidate, StringComparison.Ordinal)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidDataException(
                $"{currentFile}: keine passende Tabelle in der data.pak (erwarteter Pfad {candidate}). " +
                "Meist heißt das: die Mod passt nicht zur installierten Spielwoche."),
            _ => throw new InvalidDataException(
                $"{currentFile}: mehrdeutig, passt auf {string.Join(", ", matches)}."),
        };
    }

    /// <summary>Prüft den Pfad eines mitgelieferten Assets, bevor er ins Pak
    /// wandert. <c>.EXMODZ</c> sind fremde ZIP-Dateien, und der Parser trägt
    /// den Eintragsnamen unverändert weiter. Ohne diese Prüfung könnte ein
    /// gebauter Name (<c>../</c>, ein absoluter Pfad, ein
    /// Laufwerksbuchstabe) den Namensraum der Mod verlassen, sobald das Pak
    /// ausgepackt wird — das Pak-Gegenstück zu Zip-Slip.</summary>
    internal static string SanitizeAssetPath(string rawZipName)
    {
        var normalized = rawZipName.Replace('\\', '/');
        if (normalized.Contains('\0'))
            throw new InvalidDataException($"Asset {rawZipName}: enthält ein NUL-Byte.");
        if (normalized.StartsWith('/') || IsWindowsDriveAbsolute(normalized))
            throw new InvalidDataException($"Asset {rawZipName}: absolute Pfade sind nicht erlaubt.");

        // Segmentweise aufloesen statt Path.GetFullPath: der Pfad ist
        // pak-intern und soll nicht gegen das Dateisystem aufgeloest werden.
        var segments = new List<string>();
        foreach (var seg in normalized.Split('/'))
        {
            if (seg.Length == 0 || seg == ".") continue;
            if (seg == "..")
            {
                if (segments.Count == 0)
                    throw new InvalidDataException(
                        $"Asset {rawZipName}: verlässt den eigenen Pfad der Mod.");
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(seg);
        }
        if (segments.Count == 0)
            throw new InvalidDataException($"Asset {rawZipName}: leerer Pfad.");
        return string.Join('/', segments);
    }

    private static bool IsWindowsDriveAbsolute(string p)
        => p.Length >= 2 && p[1] == ':' && char.IsAsciiLetter(p[0]);
}
