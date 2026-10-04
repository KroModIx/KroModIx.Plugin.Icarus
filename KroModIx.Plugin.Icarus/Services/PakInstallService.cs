using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KroModIx.Plugin.Contracts;
using NLog;
using KroModIx.Plugin.Icarus.Services.Archive;
using KroModIx.Plugin.Icarus.Services.Ue4ss;
using KroModIx.Plugin.Icarus.Services.Exmodz;

namespace KroModIx.Plugin.Icarus.Services;

/// <summary>
/// PAK-Mod-Verwaltung für Icarus. Scannt BEIDE Quellen:
/// <list type="number">
/// <item>Manuell installierte PAKs im <c>Content/Paks/mods/</c>-Ordner
///   (Toggle/Uninstall/Install erlaubt).</item>
/// <item>Steam-Workshop-Abos unter <c>workshop/content/1149460/&lt;id&gt;/</c>
///   (read-only, Steam verwaltet die Ordner selbst).</item>
/// </list>
///
/// <para>Downloads landen im plugin-eigenen Downloads-Ordner
/// (<see cref="IcarusPaths.DownloadsDir"/>) — der User lädt via Browser aus
/// Nexus, das Plugin überwacht den Ordner und bietet Install-Buttons an.</para>
/// </summary>
public sealed class PakInstallService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly string _manualModsDir;
    private readonly string? _workshopContentDir;
    private readonly string _downloadsDir;
    private readonly Ue4ssLuaModService? _lua;
    private readonly ExmodzService? _exmodz;
    private readonly IArchiveService _archives;
    private readonly IcarusArchive _icarusArchive;

    /// <param name="lua">v1.23.0: der UE4SS-Dienst, damit ein Archiv seine
    /// Lua-Mods gleich mit installieren kann. Null heißt nur, dass der
    /// UE4SS-Teil eines Archivs übersprungen und gemeldet wird — der
    /// PAK-Teil funktioniert weiter.</param>
    /// <param name="exmodz">v1.24.0: der Dienst für Datentabellen-Mods.
    /// Null heißt, dass <c>.EXMODZ</c> im Archiv nur gemeldet werden.</param>
    /// <param name="archives">v1.25.0: der Archiv-Baukasten des Hosts
    /// (<c>IHostServices.Archives</c>). Vorher trug dieses Plugin eine eigene
    /// Kopie des Zip-Slip-Schutzes — wie zwei weitere Plugins.</param>
    /// <param name="unrealPaks">v1.25.0: der Pak-Baukasten des Hosts, hier
    /// nur fuer die Typ-Erkennung einer Download-Datei.</param>
    public PakInstallService(string manualModsDir, string? workshopContentDir, string downloadsDir,
        IArchiveService archives, IUnrealPakService unrealPaks,
        Ue4ssLuaModService? lua = null, ExmodzService? exmodz = null)
    {
        _manualModsDir = manualModsDir;
        _workshopContentDir = workshopContentDir;
        _downloadsDir = downloadsDir;
        _archives = archives;
        _icarusArchive = new IcarusArchive(archives, unrealPaks);
        _lua = lua;
        _exmodz = exmodz;
    }

    /// <summary>Die Icarus-Seite der Archiv-Behandlung — die ViewModels
    /// brauchen sie fuer die Inhalts-Anzeige einer Download-Row.</summary>
    public IcarusArchive Archive => _icarusArchive;

    public string ModsDir => _manualModsDir;
    public string? WorkshopDir => _workshopContentDir;
    public string DownloadsDir => _downloadsDir;
    public Ue4ssLuaModService? Lua => _lua;
    public ExmodzService? Exmodz => _exmodz;

    public IReadOnlyList<InstalledPakMod> ListInstalled()
    {
        var result = new List<InstalledPakMod>();
        ScanManual(result);
        ScanWorkshop(result);
        ScanUe4ssLua(result);
        ScanExmodz(result);
        return result;
    }

    /// <summary>v1.23.0: die UE4SS-Lua-Mods wandern in dieselbe Liste wie die
    /// PAKs. Dadurch bekommen sie Suche, Filter, Mehrfachauswahl und das
    /// Karten-Layout des Installiert-Tabs ohne eine zweite Liste — und der
    /// User sieht in einer Ansicht, was tatsächlich im Spiel liegt.</summary>
    private void ScanUe4ssLua(List<InstalledPakMod> result)
    {
        if (_lua is null) return;
        try
        {
            foreach (var m in _lua.ListInstalled())
            {
                result.Add(new InstalledPakMod(
                    FilePath: m.FolderPath,
                    FileName: m.Name,
                    FileSizeBytes: m.SizeBytes,
                    InstalledUtc: m.InstalledUtc,
                    IsEnabled: m.IsEnabled,
                    Source: PakModSource.Ue4ssLua,
                    ScriptCount: m.ScriptCount));
            }
        }
        catch (Exception ex)
        {
            // Ein kaputter UE4SS-Ordner darf die PAK-Liste nicht mitreissen.
            Log.Warn(ex, "UE4SS-Lua-Mods konnten nicht gelistet werden");
        }
    }

    /// <summary>v1.24.0: die Datentabellen-Mods, ebenfalls in derselben
    /// Liste. <see cref="InstalledPakMod.FilePath"/> zeigt dabei auf die
    /// <c>.EXMODZ</c>-Quelle im Plugin-Datenordner, nicht auf etwas im
    /// Spiel — ins Spiel geht nur das gebaute gemeinsame Pak.</summary>
    private void ScanExmodz(List<InstalledPakMod> result)
    {
        if (_exmodz is null) return;
        try
        {
            foreach (var m in _exmodz.ListInstalled())
            {
                result.Add(new InstalledPakMod(
                    FilePath: m.FilePath,
                    FileName: m.DisplayName,
                    FileSizeBytes: m.FileSizeBytes,
                    InstalledUtc: m.InstalledUtc,
                    IsEnabled: m.Enabled,
                    Source: PakModSource.Exmodz,
                    ModVersion: m.Version,
                    ModAuthor: m.Author,
                    NexusModId: m.NexusModId));
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Datentabellen-Mods konnten nicht gelistet werden");
        }
    }

    /// <summary>Die Kennung einer Datentabellen-Mod ist der Dateiname ihrer
    /// Quelle ohne Endung — so legt sie <c>ExmodzStore</c> ab.</summary>
    private static string ExmodzId(InstalledPakMod mod) => Path.GetFileNameWithoutExtension(mod.FilePath);

    /// <summary>Übersetzt eine Lua-Zeile aus <see cref="ListInstalled"/>
    /// zurück in das Modell des UE4SS-Dienstes.</summary>
    private static Ue4ssLuaMod ToLuaMod(InstalledPakMod mod) => new(
        Name: mod.FileName,
        FolderPath: mod.FilePath,
        IsEnabled: mod.IsEnabled,
        ScriptCount: mod.ScriptCount,
        SizeBytes: mod.FileSizeBytes,
        InstalledUtc: mod.InstalledUtc);

    private void ScanManual(List<InstalledPakMod> result)
    {
        if (!Directory.Exists(_manualModsDir))
        {
            Log.Info("Icarus manual mods dir nicht vorhanden: {Path}", _manualModsDir);
            return;
        }
        foreach (var file in Directory.EnumerateFiles(_manualModsDir))
        {
            var isPak = file.EndsWith(".pak", StringComparison.OrdinalIgnoreCase);
            var isDisabled = file.EndsWith(".pak.disabled", StringComparison.OrdinalIgnoreCase);
            if (!isPak && !isDisabled) continue;

            var info = new FileInfo(file);
            // v1.27.0: gehoert das Pak einem fremden Mod-Manager, wird es
            // gelistet aber nicht angefasst — siehe ForeignPakDetector.
            var fremd = ForeignManagerDetection.IsForeignManaged(file, out var verwalter);
            result.Add(new InstalledPakMod(
                FilePath: file,
                FileName: Path.GetFileName(file),
                FileSizeBytes: info.Length,
                InstalledUtc: info.LastWriteTimeUtc,
                IsEnabled: isPak,
                Source: fremd ? PakModSource.ForeignManaged : PakModSource.Manual,
                ManagedBy: fremd ? verwalter : null));
        }
    }

    private void ScanWorkshop(List<InstalledPakMod> result)
    {
        if (string.IsNullOrEmpty(_workshopContentDir) || !Directory.Exists(_workshopContentDir))
            return; // Kein Workshop-Abo — normal, kein Fehler.

        foreach (var itemDir in Directory.EnumerateDirectories(_workshopContentDir))
        {
            long workshopId = 0;
            long.TryParse(Path.GetFileName(itemDir), out workshopId);

            // Ein Workshop-Item kann mehrere PAKs enthalten (selten, aber möglich).
            foreach (var file in Directory.EnumerateFiles(itemDir, "*.pak", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                result.Add(new InstalledPakMod(
                    FilePath: file,
                    FileName: Path.GetFileName(file),
                    FileSizeBytes: info.Length,
                    InstalledUtc: info.LastWriteTimeUtc,
                    IsEnabled: true, // Workshop-Mods sind immer aktiv (Steam)
                    Source: PakModSource.Workshop,
                    WorkshopId: workshopId));
            }
        }
    }

    /// <summary>Listet die Mod-Dateien im Plugin-Downloads-Ordner (der User
    /// lädt aus dem Browser, die Datei landet hier, das Plugin bietet einen
    /// Install-Knopf).
    ///
    /// <para>v1.23.0: nicht mehr nur <c>*.pak</c>, sondern auch ZIP, RAR und
    /// 7z — so liefert Nexus Icarus-Mods tatsächlich aus. Die Einordnung
    /// macht <see cref="IcarusArchive.DetectKind"/> über die Magic-Bytes,
    /// nicht die Endung: im Ordner liegen Altlasten aus früheren Versionen,
    /// die ein angehängtes <c>.pak</c> tragen, obwohl sie ZIPs sind.</para></summary>
    public IReadOnlyList<DownloadedPak> ListDownloaded()
    {
        if (!Directory.Exists(_downloadsDir))
            return Array.Empty<DownloadedPak>();
        var result = new List<DownloadedPak>();
        foreach (var file in Directory.EnumerateFiles(_downloadsDir))
        {
            if (!_icarusArchive.HasSupportedExtension(file)) continue;
            var kind = _icarusArchive.DetectKind(file);
            if (kind == IcarusFileKind.Unknown) continue;
            var info = new FileInfo(file);
            result.Add(new DownloadedPak(file, Path.GetFileName(file),
                info.Length, info.LastWriteTimeUtc, kind));
        }
        return result;
    }

    /// <summary>Installiert eine Mod-Datei — PAK oder Archiv, die
    /// Einordnung macht der Dienst selbst.
    ///
    /// <para>Das ist ab v1.23.0 der Weg, den alle Aufrufer nehmen sollen.
    /// <see cref="Install"/> bleibt für den reinen PAK-Fall erhalten, wirft
    /// aber weiterhin bei allem anderen.</para></summary>
    /// <param name="autoRebuild">v1.24.0: ob nach dem Aufnehmen von
    /// <c>.EXMODZ</c> gleich neu gebaut wird. Beim Stapel-Install auf
    /// <c>false</c> setzen und einmal am Ende
    /// <see cref="ExmodzService.Rebuild"/> rufen — ein Neubau liest alle
    /// betroffenen Basistabellen, das je Datei zu tun ist verschwendete
    /// Arbeit.</param>
    public ModInstallResult InstallAny(string sourcePath, bool overwrite = false,
        bool autoRebuild = true)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Datei existiert nicht", sourcePath);

        var fileName = Path.GetFileName(sourcePath);
        switch (_icarusArchive.DetectKind(sourcePath))
        {
            case IcarusFileKind.Pak:
                return ModInstallResult.FromPak(Install(sourcePath, overwrite), fileName);

            case IcarusFileKind.Archive:
                return InstallArchive(sourcePath, fileName, overwrite, autoRebuild);

            default:
                throw new InvalidDataException(
                    $"Unbekanntes Dateiformat: {fileName}. Erwartet wird ein Unreal-PAK " +
                    "oder ein ZIP-, RAR- oder 7z-Archiv.");
        }
    }

    /// <summary>Packt ein Mod-Archiv aus und sortiert seinen Inhalt ein:
    /// PAKs in den Mods-Ordner, UE4SS-Lua-Mods nach
    /// <c>Binaries/Win64/Mods/</c>, <c>.EXMODZ</c> in die
    /// Datentabellen-Ablage (und von dort ins gemeinsame Pak).</summary>
    private ModInstallResult InstallArchive(string archivePath, string fileName, bool overwrite,
        bool autoRebuild)
    {
        var contents = _icarusArchive.Inspect(archivePath);
        if (!contents.IsReadable)
            throw new InvalidDataException($"Archiv nicht lesbar: {contents.Error}");
        if (!contents.HasInstallable)
            throw new InvalidDataException(
                $"Im Archiv ist keine Icarus-Mod zu finden ({contents.TotalEntries} Datei(en)). " +
                "Erwartet werden .pak-Dateien, ein Ordner „Icarus Mod Manager“ mit .EXMODZ " +
                "oder ein Ordner „UE4SS Mods“.");

        var paks = new List<InstalledPakMod>();
        if (contents.HasPaks)
        {
            Directory.CreateDirectory(_manualModsDir);
            // Flach einsortieren: Icarus liest nur die erste Ebene von
            // Content/Paks/mods, eine Ordnerstruktur aus dem Archiv waere
            // dort wirkungslos.
            var extracted = _archives.Extract(archivePath, _manualModsDir,
                new ArchiveExtractOptions(
                    Filter: key => key.EndsWith(".pak", StringComparison.OrdinalIgnoreCase),
                    Flatten: true,
                    Overwrite: overwrite));
            foreach (var skipped in extracted.SkippedUnsafe)
                Log.Warn("PAK-Eintrag aus Sicherheitsgruenden uebersprungen: {Key}", skipped);
            foreach (var dst in extracted.ExtractedPaths)
            {
                var info = new FileInfo(dst);
                paks.Add(new InstalledPakMod(dst, Path.GetFileName(dst), info.Length,
                    info.LastWriteTimeUtc, IsEnabled: true, Source: PakModSource.Manual));
                Log.Info("PAK aus Archiv installiert: {Name} → {Path}", Path.GetFileName(dst), dst);
            }
        }

        IReadOnlyList<string> luaMods = [];
        string? luaError = null;
        if (contents.HasUe4ssMods)
        {
            if (_lua is null)
            {
                luaError = "UE4SS-Pfade unbekannt — Lua-Mods wurden nicht installiert.";
                Log.Warn("Archiv enthaelt UE4SS-Mods, aber kein Ue4ssLuaModService gewired");
            }
            else
            {
                try
                {
                    luaMods = _lua.InstallFromArchive(archivePath, overwrite);
                }
                catch (Exception ex)
                {
                    // Der PAK-Teil ist schon installiert — ein Fehlschlag im
                    // Lua-Teil darf das nicht zurueckdrehen, er muss aber
                    // sichtbar werden.
                    luaError = ex.Message;
                    Log.Warn(ex, "UE4SS-Teil des Archivs fehlgeschlagen: {File}", fileName);
                }
            }
        }

        IReadOnlyList<string> exmodzMods = [];
        if (contents.HasExmodz)
        {
            if (_exmodz is null)
            {
                luaError = (luaError is null ? "" : luaError + " ")
                    + "Datentabellen-Dienst nicht verfügbar — die .EXMODZ wurden nicht aufgenommen.";
            }
            else
            {
                try
                {
                    exmodzMods = InstallExmodzFromArchive(archivePath, contents.ExmodzEntries, fileName);
                    if (exmodzMods.Count > 0 && autoRebuild) _exmodz.Rebuild();
                }
                catch (Exception ex)
                {
                    luaError = (luaError is null ? "" : luaError + " ") + ex.Message;
                    Log.Warn(ex, "Datentabellen-Teil des Archivs fehlgeschlagen: {File}", fileName);
                }
            }
        }

        return new ModInstallResult(paks, luaMods, exmodzMods, contents.ExmodzEntries.Count,
            fileName, luaError);
    }

    /// <summary>Holt die <c>.EXMODZ</c>-Dateien aus dem Archiv und übergibt
    /// sie der Ablage.
    ///
    /// <para><b>Nur eine pro Archiv wird aufgenommen</b>, nicht alle. Der
    /// Grund steckt in OreDepot: dort liegen <c>OreDepot.EXMODZ</c> und
    /// <c>OreDepot_PTBR.EXMODZ</c> nebeneinander — dieselbe Mod mit
    /// deutschem bzw. brasilianischem Item-Namen. Beide aufzunehmen würde
    /// dieselben Zeilen zweimal setzen und im Spiel doppelte Einträge
    /// erzeugen. Genommen wird die Datei mit dem kürzesten Namen; die
    /// sprachlichen Varianten tragen durchweg ein Suffix.</para>
    ///
    /// <para>Der Nexus-Bezug kommt aus dem Namen der <b>Archiv</b>-Datei,
    /// nicht aus dem der .EXMODZ: das Archiv ist der Download, und nur
    /// dessen Name folgt dem Nexus-Muster.</para></summary>
    private IReadOnlyList<string> InstallExmodzFromArchive(string archivePath,
        IReadOnlyList<string> exmodzEntries, string archiveFileName)
    {
        if (_exmodz is null || exmodzEntries.Count == 0) return [];

        var chosen = exmodzEntries
            .OrderBy(e => Path.GetFileName(e).Length)
            .ThenBy(e => e, StringComparer.OrdinalIgnoreCase)
            .First();
        if (exmodzEntries.Count > 1)
            Log.Info("Archiv enthält {Count} .EXMODZ — genommen wird {Chosen}, " +
                "die anderen sind sprachliche Varianten derselben Mod",
                exmodzEntries.Count, chosen);

        var nexusModId = Nexus.NexusFileNameParser.TryExtractModId(archiveFileName);
        var tmpDir = Path.Combine(Path.GetTempPath(), "kromodix-exmodz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            var tmpFile = Path.Combine(tmpDir, Path.GetFileName(chosen));
            _archives.ExtractEntry(archivePath, chosen, tmpFile);
            var installed = _exmodz.Install(tmpFile, nexusModId);
            return [installed.DisplayName];
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* Temp-Rest */ }
        }
    }

    public InstalledPakMod Install(string sourcePakPath, bool overwrite = false)
    {
        if (!File.Exists(sourcePakPath))
            throw new FileNotFoundException("PAK-Datei existiert nicht", sourcePakPath);
        if (_icarusArchive.DetectKind(sourcePakPath) != IcarusFileKind.Pak)
            throw new InvalidDataException(
                "Das ist kein Unreal-PAK. Für Archive ist InstallAny zuständig.");

        Directory.CreateDirectory(_manualModsDir);
        var fileName = Path.GetFileName(sourcePakPath);
        var destination = Path.Combine(_manualModsDir, fileName);
        if (File.Exists(destination) && !overwrite)
            throw new IOException($"Mod ist bereits installiert: {fileName}");

        File.Copy(sourcePakPath, destination, overwrite: true);
        Log.Info("Icarus-Mod installiert: {Name} → {Path}", fileName, destination);

        var info = new FileInfo(destination);
        return new InstalledPakMod(destination, fileName, info.Length, info.LastWriteTimeUtc,
            IsEnabled: true, Source: PakModSource.Manual);
    }

    public void Uninstall(InstalledPakMod mod)
    {
        if (mod.Source == PakModSource.Workshop)
            throw new InvalidOperationException(
                "Workshop-Mods können nicht deinstalliert werden — Abo in Steam kündigen.");
        if (mod.Source == PakModSource.ForeignManaged)
            throw new InvalidOperationException(FremdverwaltetMeldung(mod, "deinstallieren"));
        if (mod.Source == PakModSource.Ue4ssLua)
        {
            if (_lua is null)
                throw new InvalidOperationException("UE4SS-Pfade unbekannt.");
            _lua.Uninstall(ToLuaMod(mod));
            return;
        }
        if (mod.Source == PakModSource.Exmodz)
        {
            if (_exmodz is null)
                throw new InvalidOperationException("Datentabellen-Dienst nicht verfügbar.");
            _exmodz.Uninstall(ExmodzId(mod));
            // Das gebaute Pak enthaelt den Stand MIT dieser Mod — es muss
            // weg, sonst wirkt die deinstallierte Mod weiter.
            _exmodz.Rebuild();
            return;
        }
        if (!File.Exists(mod.FilePath))
        {
            Log.Warn("Icarus-Uninstall: Datei bereits weg: {Path}", mod.FilePath);
            return;
        }
        File.Delete(mod.FilePath);
        Log.Info("Icarus-Mod deinstalliert: {Path}", mod.FilePath);
    }

    /// <summary>Delegiert an <see cref="ForeignManagerDetection.Meldung"/> —
    /// der Text liegt seit Contracts v1.34.0 dort, damit alle neun Plugins
    /// dasselbe sagen. Die Signatur bleibt, weil die ViewModels sie rufen.</summary>
    internal static string FremdverwaltetMeldung(InstalledPakMod mod, string verb)
        => ForeignManagerDetection.Meldung(mod.FileName, mod.ManagedBy, verb);

    public InstalledPakMod SetEnabled(InstalledPakMod mod, bool enabled)
    {
        if (mod.Source == PakModSource.Workshop)
            throw new InvalidOperationException(
                "Workshop-Mods können nicht deaktiviert werden — Abo in Steam pausieren.");
        if (mod.Source == PakModSource.ForeignManaged)
            throw new InvalidOperationException(FremdverwaltetMeldung(mod, "umschalten"));
        if (mod.Source == PakModSource.Ue4ssLua)
        {
            if (_lua is null)
                throw new InvalidOperationException("UE4SS-Pfade unbekannt.");
            var updated = _lua.SetEnabled(ToLuaMod(mod), enabled);
            return mod with { IsEnabled = updated.IsEnabled };
        }
        if (mod.Source == PakModSource.Exmodz)
        {
            if (_exmodz is null)
                throw new InvalidOperationException("Datentabellen-Dienst nicht verfügbar.");
            _exmodz.SetEnabled(ExmodzId(mod), enabled);
            // Umschalten aendert das Ergebnis des Zusammenbaus, also neu
            // bauen. Anders als bei einem PAK gibt es hier keine Datei im
            // Spiel, die man einfach umbenennen koennte.
            _exmodz.Rebuild();
            return mod with { IsEnabled = enabled };
        }
        if (mod.IsEnabled == enabled) return mod;
        var current = mod.FilePath;
        var target = enabled
            ? current[..^".disabled".Length]
            : current + ".disabled";
        if (File.Exists(target))
            throw new IOException($"Zieldatei existiert bereits: {target}");
        File.Move(current, target);
        Log.Info("Icarus-Mod {State}: {Path} → {Target}",
            enabled ? "aktiviert" : "deaktiviert", current, target);
        return mod with { FilePath = target, FileName = Path.GetFileName(target), IsEnabled = enabled };
    }

    /// <summary>Lädt eine Mod-Datei streamend in den Downloads-Ordner, mit
    /// Fortschrittsbericht (0..1). Kollisionsprüfung: existiert die Datei
    /// schon UND overwrite=false → <see cref="InvalidOperationException"/>.
    /// Geschrieben wird atomar über <c>.tmp</c> und <c>File.Move</c>, damit
    /// ein abgebrochener Download nicht als „fertig" im Ordner landet.
    ///
    /// <para><b>v1.23.0: der Dateiname bleibt, wie Nexus ihn liefert.</b>
    /// Vorher wurde jedem Download ein <c>.pak</c> angehängt, wenn er nicht
    /// schon darauf endete — aus <c>OreDepot … yxOAgLyJG.zip</c> wurde also
    /// <c>… yxOAgLyJG.zip.pak</c>. Das hatte zwei Folgen, beide still: der
    /// Dateiname passte nicht mehr aufs Nexus-Muster (kein Enrichment, kein
    /// Cover, toter Details-Knopf), und der Installer behandelte das ZIP als
    /// PAK und kopierte es unverändert in den Mods-Ordner, wo Icarus es
    /// nicht lesen kann.</para>
    ///
    /// <para>Hat der Name gar keine brauchbare Endung, wird <c>.pak</c>
    /// ergänzt — das war die bisherige Annahme und bleibt der Ausweichfall
    /// für Nexus-Einträge ohne Endung im Dateinamen.</para></summary>
    public async Task<string> DownloadModFileAsync(HttpClient http, string url, string fileName,
        bool overwrite, IProgress<double>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_downloadsDir);
        if (!_icarusArchive.HasSupportedExtension(fileName))
            fileName += ".pak";
        var target = Path.Combine(_downloadsDir, fileName);
        if (File.Exists(target) && !overwrite)
            throw new InvalidOperationException($"Datei existiert schon: {fileName}");

        var tmp = target + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);

        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;

        await using (var input = await resp.Content.ReadAsStreamAsync(ct))
        await using (var output = File.Create(tmp))
        {
            var buf = new byte[64 * 1024];
            long done = 0;
            int read;
            var lastReport = DateTime.UtcNow;
            while ((read = await input.ReadAsync(buf, ct)) > 0)
            {
                await output.WriteAsync(buf.AsMemory(0, read), ct);
                done += read;
                // Progress höchstens 5×/Sekunde reporten — spart Dispatcher-Post-Flut.
                if (total > 0 && DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(200))
                {
                    progress?.Report((double)done / total);
                    lastReport = DateTime.UtcNow;
                }
            }
            progress?.Report(1.0);
        }
        File.Move(tmp, target, overwrite: true);
        Log.Info("Icarus-Download fertig: {File} ({Bytes} bytes)", fileName, total);
        return target;
    }

    public void DeleteDownload(string pakPath)
    {
        if (!File.Exists(pakPath)) return;
        if (!pakPath.StartsWith(_downloadsDir, StringComparison.Ordinal))
            throw new InvalidOperationException("Nur Dateien im Downloads-Ordner dürfen gelöscht werden.");
        File.Delete(pakPath);
        Log.Info("Icarus-Download gelöscht: {Path}", pakPath);
    }
}

/// <summary>Eine im Plugin-Downloads-Ordner liegende Mod-Datei (noch nicht
/// im Mods-Ordner installiert). <see cref="Kind"/> kommt aus der
/// Inhaltsprüfung, nicht aus der Endung.</summary>
public sealed record DownloadedPak(
    string FilePath,
    string FileName,
    long FileSizeBytes,
    DateTime DownloadedUtc,
    IcarusFileKind Kind = IcarusFileKind.Pak);

/// <summary>Was ein Install-Vorgang tatsächlich installiert hat. Ein Archiv
/// kann mehrere Dinge auf einmal mitbringen, deshalb Listen statt eines
/// einzelnen Rückgabewerts.
///
/// <para><see cref="ExmodzMods"/> sind die aufgenommenen
/// Datentabellen-Mods. <see cref="ExmodzFound"/> ist dagegen, wie viele
/// <c>.EXMODZ</c> im Archiv lagen — die Zahl kann höher sein, weil von
/// mehreren sprachlichen Varianten derselben Mod nur eine aufgenommen
/// wird.</para></summary>
public sealed record ModInstallResult(
    IReadOnlyList<InstalledPakMod> Paks,
    IReadOnlyList<string> Ue4ssMods,
    IReadOnlyList<string> ExmodzMods,
    int ExmodzFound,
    string SourceFileName,
    string? Warning = null)
{
    public static ModInstallResult FromPak(InstalledPakMod pak, string sourceFileName)
        => new([pak], [], [], 0, sourceFileName);

    /// <summary>Ob überhaupt etwas installiert wurde.</summary>
    public bool InstalledAnything
        => Paks.Count > 0 || Ue4ssMods.Count > 0 || ExmodzMods.Count > 0;

    /// <summary>Ob eine Datentabellen-Mod dabei war — dann muss der Aufrufer
    /// das gemeinsame Pak (neu) bauen, falls er den Automatik-Bau
    /// abgeschaltet hat.</summary>
    public bool TouchedExmodz => ExmodzMods.Count > 0;

    /// <summary>Kurzfassung für die Benachrichtigung — nennt, was wirklich
    /// passiert ist, statt eines pauschalen „installiert".</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Paks.Count == 1) parts.Add(Paks[0].FileName);
        else if (Paks.Count > 1) parts.Add($"{Paks.Count} PAK-Mods");
        if (Ue4ssMods.Count == 1) parts.Add($"Lua-Mod {Ue4ssMods[0]}");
        else if (Ue4ssMods.Count > 1) parts.Add($"{Ue4ssMods.Count} Lua-Mods");
        if (ExmodzMods.Count == 1) parts.Add($"Datentabellen-Mod {ExmodzMods[0]}");
        else if (ExmodzMods.Count > 1) parts.Add($"{ExmodzMods.Count} Datentabellen-Mods");
        return parts.Count == 0 ? SourceFileName : string.Join(" + ", parts);
    }
}
