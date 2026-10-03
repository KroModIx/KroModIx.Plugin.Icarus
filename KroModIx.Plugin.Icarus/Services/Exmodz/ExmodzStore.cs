using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using KroModIx.Plugin.Contracts;
using NLog;

namespace KroModIx.Plugin.Icarus.Services.Exmodz;

/// <summary>Eine installierte Datentabellen-Mod. <see cref="Id"/> ist der
/// Dateiname ohne Endung und damit die Identität — er bleibt über
/// Neuinstallationen derselben Mod stabil.</summary>
public sealed record InstalledExmodz
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Author { get; init; } = "";
    public string Version { get; init; } = "";
    public string SourceFileName { get; init; } = "";
    public int? NexusModId { get; init; }
    public bool Enabled { get; init; } = true;
    public DateTime InstalledUtc { get; init; }
    public List<string> TouchedTables { get; init; } = [];

    [JsonIgnore] public string FilePath { get; set; } = "";
    [JsonIgnore] public long FileSizeBytes { get; set; }
}

/// <summary>Der gemerkte Zustand des gebauten Paks.</summary>
public sealed record MergedPakState
{
    /// <summary>Der Index-Hash der <c>data.pak</c>, gegen die gebaut wurde.
    ///
    /// <para>Bewusst der Index-Hash des Paks und nicht eine SHA256 über die
    /// ganze Datei: der Hash steht im Footer, wird beim Öffnen ohnehin
    /// gelesen und geprüft, und ändert sich bei jeder Inhalts- oder
    /// Layout-Änderung. Die 2,5-MB-Datei dafür noch einmal durchzuhashen
    /// wäre Arbeit ohne Gegenwert.</para></summary>
    public string BaseIndexHash { get; init; } = "";

    /// <summary>Welche Mod-Kennungen in der Ladereihenfolge eingebaut
    /// wurden.</summary>
    public List<string> ModIds { get; init; } = [];

    public DateTime BuiltUtc { get; init; }
    public int TableCount { get; init; }
    public int AssetCount { get; init; }
}

internal sealed record ExmodzStoreState
{
    public List<InstalledExmodz> Mods { get; init; } = [];
    public MergedPakState? Merged { get; init; }
}

/// <summary>Verwaltet die installierten <c>.EXMODZ</c> und den Zustand des
/// daraus gebauten Paks.
///
/// <para>Die <c>.EXMODZ</c>-Dateien selbst bleiben im Plugin-Datenordner
/// liegen und werden <b>nicht</b> ins Spiel kopiert — ins Spiel geht nur das
/// gebaute Pak. Das ist der Grund, warum ein Neubau nach jedem
/// Spiel-Update überhaupt möglich ist: die Quelle ist noch da.</para>
///
/// <para>Der Zustand wird atomar geschrieben (<c>.tmp</c> + <c>File.Move</c>),
/// und eine unlesbare Zustandsdatei wird als <c>.broken</c> beiseitegelegt
/// statt überschrieben — bei einem Lesefehler ist der Inhalt vielleicht
/// intakt und nur die Platte kurz weg.</para></summary>
public sealed class ExmodzStore
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _dir;
    private readonly string _statePath;
    private readonly IUnrealPakService _paks;

    public ExmodzStore(IcarusPaths paths, IUnrealPakService paksService)
    {
        _dir = Path.Combine(paths.PluginDataDir, "exmodz");
        _statePath = Path.Combine(_dir, "state.json");
        _paks = paksService;
        Directory.CreateDirectory(_dir);
    }

    public string Dir => _dir;

    /// <summary>Die installierten Mods in Ladereihenfolge, mit aufgelöstem
    /// Dateipfad. Mods, deren Datei verschwunden ist, fallen heraus und
    /// werden aus dem Zustand entfernt — sonst meldet das Plugin eine Mod,
    /// die es nicht mehr bauen kann.</summary>
    public IReadOnlyList<InstalledExmodz> ListInstalled()
    {
        var state = Load();
        var alive = new List<InstalledExmodz>();
        var removed = new List<string>();
        foreach (var m in state.Mods)
        {
            var path = Path.Combine(_dir, m.Id + ".EXMODZ");
            if (!File.Exists(path))
            {
                removed.Add(m.Id);
                continue;
            }
            m.FilePath = path;
            m.FileSizeBytes = new FileInfo(path).Length;
            alive.Add(m);
        }
        if (removed.Count > 0)
        {
            Log.Info("Verwaiste .EXMODZ-Einträge entfernt: {Ids}", string.Join(", ", removed));
            Save(state with { Mods = alive });
        }
        return alive;
    }

    public MergedPakState? MergedState => Load().Merged;

    /// <summary>Nimmt eine <c>.EXMODZ</c>-Datei in den Verwaltungsordner auf
    /// und liest ihre Metadaten. Eine Mod mit derselben Kennung wird ersetzt
    /// (der Update-Weg), behält aber ihren Ein/Aus-Zustand und ihren Platz in
    /// der Ladereihenfolge.</summary>
    public InstalledExmodz Install(string sourcePath, int? nexusModId = null)
    {
        var bundle = ExmodzParser.ParseFile(sourcePath);
        var sourceFileName = Path.GetFileName(sourcePath);
        var id = SanitizeId(Path.GetFileNameWithoutExtension(sourcePath));
        var target = Path.Combine(_dir, id + ".EXMODZ");
        File.Copy(sourcePath, target, overwrite: true);

        var state = Load();
        var existing = state.Mods.FirstOrDefault(m => m.Id == id);
        var entry = new InstalledExmodz
        {
            Id = id,
            DisplayName = bundle.Diff.Name.Length > 0 ? bundle.Diff.Name : id,
            Author = bundle.Diff.Author,
            Version = bundle.Diff.Version,
            SourceFileName = sourceFileName,
            NexusModId = nexusModId ?? existing?.NexusModId,
            Enabled = existing?.Enabled ?? true,
            InstalledUtc = DateTime.UtcNow,
            TouchedTables = ExmodParser.TouchedTables(bundle.Diff).ToList(),
        };

        var mods = state.Mods.ToList();
        var at = mods.FindIndex(m => m.Id == id);
        if (at >= 0) mods[at] = entry; else mods.Add(entry);
        Save(state with { Mods = mods });

        entry.FilePath = target;
        entry.FileSizeBytes = new FileInfo(target).Length;
        Log.Info(".EXMODZ aufgenommen: {Id} ({Name} {Version}), {Tables} Tabelle(n)",
            id, entry.DisplayName, entry.Version, entry.TouchedTables.Count);
        return entry;
    }

    public void Uninstall(string id)
    {
        var path = Path.Combine(_dir, id + ".EXMODZ");
        if (File.Exists(path)) File.Delete(path);
        var state = Load();
        Save(state with { Mods = state.Mods.Where(m => m.Id != id).ToList() });
        Log.Info(".EXMODZ entfernt: {Id}", id);
    }

    public void SetEnabled(string id, bool enabled)
    {
        var state = Load();
        var mods = state.Mods.Select(m => m.Id == id ? m with { Enabled = enabled } : m).ToList();
        Save(state with { Mods = mods });
    }

    public void RecordMerge(string baseIndexHash, IReadOnlyList<string> modIds,
        int tableCount, int assetCount)
        => Save(Load() with
        {
            Merged = new MergedPakState
            {
                BaseIndexHash = baseIndexHash,
                ModIds = modIds.ToList(),
                BuiltUtc = DateTime.UtcNow,
                TableCount = tableCount,
                AssetCount = assetCount,
            },
        });

    public void ClearMerge() => Save(Load() with { Merged = null });

    /// <summary>Warum das gebaute Pak (nicht) neu gebaut werden muss.</summary>
    public enum Staleness
    {
        /// <summary>Keine Mods aktiv — es soll gar kein Pak geben.</summary>
        NothingToBuild,
        /// <summary>Das Pak passt zum aktuellen Stand.</summary>
        UpToDate,
        /// <summary>Es gibt noch kein Pak.</summary>
        NeverBuilt,
        /// <summary>Die Pak-Datei im Spiel fehlt, obwohl ein Bau vermerkt
        /// ist.</summary>
        FileMissing,
        /// <summary>Die <c>data.pak</c> des Spiels hat sich geändert — das
        /// Wochen-Update.</summary>
        GameUpdated,
        /// <summary>Die Liste der aktiven Mods oder ihre Reihenfolge hat sich
        /// geändert.</summary>
        ModsChanged,
    }

    /// <summary>Prüft, ob das gebaute Pak noch zum aktuellen Stand passt.
    ///
    /// <para>Geprüft wird gegen den Index-Hash der <b>jetzigen</b>
    /// <c>data.pak</c> und gegen die Liste der aktiven Mods samt Reihenfolge.
    /// Beides muss stimmen: ein Spiel-Update wechselt die Basistabellen (das
    /// alte Pak würde dann Werte des Vorwochen-Stands zurückdrehen), und eine
    /// Änderung an der Mod-Liste ändert das Ergebnis des
    /// Zusammenbaus.</para></summary>
    public Staleness CheckStaleness(string basePakPath, string mergedPakPath)
    {
        var state = Load();
        var activeIds = state.Mods.Where(m => m.Enabled)
            .Where(m => File.Exists(Path.Combine(_dir, m.Id + ".EXMODZ")))
            .Select(m => m.Id).ToList();

        if (activeIds.Count == 0) return Staleness.NothingToBuild;
        if (state.Merged is null) return Staleness.NeverBuilt;
        if (!File.Exists(mergedPakPath)) return Staleness.FileMissing;

        if (!state.Merged.ModIds.SequenceEqual(activeIds, StringComparer.Ordinal))
            return Staleness.ModsChanged;

        var baseHash = TryReadBaseIndexHash(_paks, basePakPath);
        // Lässt sich der Hash nicht lesen (Spiel gerade weg, Platte nicht
        // eingehängt), ist das kein Grund, einen Neubau zu behaupten — dann
        // wäre jede Anzeige ohne Spiel dauerhaft rot.
        if (baseHash is null) return Staleness.UpToDate;
        return baseHash == state.Merged.BaseIndexHash ? Staleness.UpToDate : Staleness.GameUpdated;
    }

    /// <summary>Der Index-Hash der Basis-<c>data.pak</c>, oder null wenn sie
    /// nicht lesbar ist.</summary>
    public static string? TryReadBaseIndexHash(IUnrealPakService paks, string basePakPath)
    {
        try
        {
            if (!File.Exists(basePakPath)) return null;
            using var r = paks.OpenRead(basePakPath);
            return r.IndexHash;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Index-Hash der data.pak nicht lesbar: {Path}", basePakPath);
            return null;
        }
    }

    /// <summary>Die aktiven Mods als Quellen für den Zusammenbau, in
    /// Ladereihenfolge.</summary>
    public IReadOnlyList<ExmodzSource> ActiveSources()
        => ListInstalled().Where(m => m.Enabled)
            .Select(m => new ExmodzSource(m.Id, m.DisplayName, m.FilePath))
            .ToList();

    /// <summary>Aus einem Dateinamen eine als Dateiname taugliche Kennung
    /// machen. Ein <c>.EXMODZ</c> kommt aus dem Netz, sein Name ist
    /// fremdbestimmt.</summary>
    internal static string SanitizeId(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(raw.Select(c => invalid.Contains(c) || c == '/' || c == '\\' ? '_' : c)
            .ToArray()).Trim(' ', '.');
        return cleaned.Length > 0 ? cleaned : "unbenannt";
    }

    private ExmodzStoreState Load()
    {
        try
        {
            if (!File.Exists(_statePath)) return new ExmodzStoreState();
            return JsonSerializer.Deserialize<ExmodzStoreState>(
                File.ReadAllText(_statePath), JsonOpts) ?? new ExmodzStoreState();
        }
        catch (JsonException ex)
        {
            // Nur bei kaputtem JSON beiseitelegen, nicht bei IO-Fehlern: bei
            // einem Lesefehler ist der Inhalt vielleicht intakt, und ein
            // .broken-Verschieben raeumte dann gute Daten weg.
            var broken = _statePath + ".broken";
            Log.Error(ex, "exmodz/state.json ist beschädigt — als {Broken} beiseitegelegt", broken);
            try { File.Move(_statePath, broken, overwrite: true); } catch { /* best effort */ }
            return new ExmodzStoreState();
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "exmodz/state.json nicht lesbar");
            return new ExmodzStoreState();
        }
    }

    private void Save(ExmodzStoreState state)
    {
        Directory.CreateDirectory(_dir);
        var tmp = _statePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOpts));
        File.Move(tmp, _statePath, overwrite: true);
    }
}
