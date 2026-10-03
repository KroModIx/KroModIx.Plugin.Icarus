using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KroModIx.Plugin.Contracts;
using NLog;

namespace KroModIx.Plugin.Icarus.Services.Exmodz;

/// <summary>Bindet Ablage, Compiler und Spielpfade zusammen — die eine
/// Schnittstelle, die ViewModel und Installer für Datentabellen-Mods
/// nutzen.</summary>
public sealed class ExmodzService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>Der Name des gebauten Paks. Das <c>zzz_</c> ist Absicht:
    /// <c>_P.pak</c>-Dateien werden in alphabetischer Reihenfolge
    /// eingehängt, die spätere gewinnt bei gleichen Pfaden. Unser Pak enthält
    /// das Ergebnis aller Datentabellen-Mods und soll deshalb zuletzt
    /// kommen.</summary>
    public const string MergedPakFileName = "zzz_KroModIx_Merged_P.pak";

    /// <summary>Andere Werkzeuge legen ihr zusammengebautes Pak nach
    /// derselben Konvention ab. Liegt eines davon im Mods-Ordner, gewinnt
    /// nach Alphabet unter Umständen es statt unserem — und der User sucht
    /// den Fehler bei uns. Deshalb wird darauf hingewiesen statt es
    /// anzufassen: es ist nicht unsere Datei.</summary>
    private static readonly string[] ForeignMergedPakNames = ["zzz_LMM_Merged_P.pak"];

    private readonly ExmodzStore _store;
    private readonly ExmodzCompiler _compiler;
    private readonly IUnrealPakService _paks;
    private readonly string _modsDir;
    private readonly string? _basePakPath;

    public ExmodzService(ExmodzStore store, string modsDir, DetectedGame game,
        IUnrealPakService paks)
    {
        _store = store;
        _paks = paks;
        _compiler = new ExmodzCompiler(paks);
        _modsDir = modsDir;
        _basePakPath = ResolveBasePak(game);
        if (_basePakPath is null)
            Log.Info("Icarus: Content/Data/data.pak nicht gefunden — Datentabellen-Mods bleiben aus");
    }

    /// <summary>Die Basis-Datentabellen des Spiels, oder null wenn sie nicht
    /// gefunden wurden.</summary>
    public string? BasePakPath => _basePakPath;

    public bool IsSupported => _basePakPath is not null;

    public string MergedPakPath => Path.Combine(_modsDir, MergedPakFileName);

    public ExmodzStore Store => _store;

    public IReadOnlyList<InstalledExmodz> ListInstalled() => _store.ListInstalled();

    public ExmodzStore.Staleness CheckStaleness()
        => _basePakPath is null
            ? ExmodzStore.Staleness.NothingToBuild
            : _store.CheckStaleness(_basePakPath, MergedPakPath);

    /// <summary>Fremde zusammengebaute Paks im Mods-Ordner, die unser
    /// eigenes nach Alphabet überstimmen würden.</summary>
    public IReadOnlyList<string> FindConflictingMergedPaks()
    {
        try
        {
            if (!Directory.Exists(_modsDir)) return [];
            return ForeignMergedPakNames
                .Where(n => File.Exists(Path.Combine(_modsDir, n)))
                // Nur melden, wenn der fremde Name nach dem unseren kommt —
                // sonst gewinnen wir ohnehin und es gibt nichts zu sagen.
                .Where(n => string.CompareOrdinal(n, MergedPakFileName) > 0)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Mods-Ordner nicht lesbar");
            return [];
        }
    }

    /// <summary>Nimmt eine <c>.EXMODZ</c>-Datei auf. Baut <b>nicht</b> gleich
    /// neu — bei einem Stapel-Install wäre das ein Neubau je Datei, und der
    /// liest jedes Mal alle Basistabellen. Der Aufrufer ruft danach einmal
    /// <see cref="Rebuild"/>.</summary>
    public InstalledExmodz Install(string sourcePath, int? nexusModId = null)
        => _store.Install(sourcePath, nexusModId);

    public void Uninstall(string id) => _store.Uninstall(id);

    public void SetEnabled(string id, bool enabled) => _store.SetEnabled(id, enabled);

    /// <summary>Baut das gemeinsame Pak aus allen aktiven
    /// Datentabellen-Mods neu.
    ///
    /// <para>Sind keine aktiv, wird das Pak <b>entfernt</b> statt leer
    /// gelassen: ein Pak mit den unveränderten Basistabellen wäre nicht
    /// wirkungslos, sondern würde jede andere Mod überschatten, die dieselben
    /// Tabellen anfasst.</para></summary>
    public MergeResult Rebuild()
    {
        if (_basePakPath is null)
            return new MergeResult(false,
                "Content/Data/data.pak nicht gefunden — ist das wirklich eine Icarus-Installation?",
                [], [], 0, 0);

        var sources = _store.ActiveSources();
        if (sources.Count == 0)
        {
            RemoveMergedPak();
            _store.ClearMerge();
            return new MergeResult(true, "Keine Datentabellen-Mods aktiv — Pak entfernt.", [], [], 0, 0);
        }

        Directory.CreateDirectory(_modsDir);
        var result = _compiler.Merge(_basePakPath, sources, MergedPakPath);
        if (!result.Ok)
        {
            // Ein fehlgeschlagener Bau darf kein altes Pak zuruecklassen, das
            // nicht mehr zum Zustand passt — sonst laeuft der User mit einem
            // Pak aus einer fruheren Mod-Zusammenstellung weiter und wundert
            // sich, warum seine Aenderung nichts tut.
            RemoveMergedPak();
            _store.ClearMerge();
            return result;
        }

        var baseHash = ExmodzStore.TryReadBaseIndexHash(_paks, _basePakPath) ?? "";
        // Nur die Mods vermerken, die wirklich eingebaut wurden — eine
        // gescheiterte Mod darf die Staleness-Pruefung nicht als
        // „eingebaut" glauben lassen.
        var failedLabels = result.FailedMods
            .Select(f => f.Split(':')[0]).ToHashSet(StringComparer.Ordinal);
        var builtIds = sources
            .Where(s => !failedLabels.Contains(s.DisplayName))
            .Select(s => s.ModId).ToList();
        _store.RecordMerge(baseHash, builtIds, result.TableCount, result.AssetCount);
        return result;
    }

    private void RemoveMergedPak()
    {
        try
        {
            if (File.Exists(MergedPakPath))
            {
                File.Delete(MergedPakPath);
                Log.Info("Zusammengebautes Pak entfernt: {Path}", MergedPakPath);
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Zusammengebautes Pak konnte nicht entfernt werden: {Path}", MergedPakPath);
        }
    }

    /// <summary>Findet <c>Icarus/Content/Data/data.pak</c> — ohne Rücksicht
    /// auf Groß- und Kleinschreibung, denn unter Linux entscheidet sie, und
    /// die Schreibweise der Ordner schwankt zwischen Installationen.</summary>
    internal static string? ResolveBasePak(DetectedGame game)
    {
        if (string.IsNullOrEmpty(game.InstallDir) || !Directory.Exists(game.InstallDir))
            return null;
        var dataDir = ModFolderDiscovery.Find(game.InstallDir,
            "Icarus/Content/Data", "Icarus/Content/data");
        if (dataDir is null) return null;
        foreach (var name in new[] { "data.pak", "Data.pak" })
        {
            var p = Path.Combine(dataDir, name);
            if (File.Exists(p)) return p;
        }
        // Letzter Versuch: irgendeine .pak in dem Ordner. Dort liegt in jeder
        // gesehenen Installation genau eine.
        try
        {
            return Directory.EnumerateFiles(dataDir, "*.pak").FirstOrDefault();
        }
        catch { return null; }
    }
}
