using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using SharpCompress.Archives;
using KroModIx.Plugin.Icarus.Services.Archive;
using KroModIx.Plugin.Icarus.Services.Ue4ss;

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

    /// <param name="lua">v1.23.0: der UE4SS-Dienst, damit ein Archiv seine
    /// Lua-Mods gleich mit installieren kann. Null heißt nur, dass der
    /// UE4SS-Teil eines Archivs übersprungen und gemeldet wird — der
    /// PAK-Teil funktioniert weiter.</param>
    public PakInstallService(string manualModsDir, string? workshopContentDir, string downloadsDir,
        Ue4ssLuaModService? lua = null)
    {
        _manualModsDir = manualModsDir;
        _workshopContentDir = workshopContentDir;
        _downloadsDir = downloadsDir;
        _lua = lua;
    }

    public string ModsDir => _manualModsDir;
    public string? WorkshopDir => _workshopContentDir;
    public string DownloadsDir => _downloadsDir;
    public Ue4ssLuaModService? Lua => _lua;

    public IReadOnlyList<InstalledPakMod> ListInstalled()
    {
        var result = new List<InstalledPakMod>();
        ScanManual(result);
        ScanWorkshop(result);
        ScanUe4ssLua(result);
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
            result.Add(new InstalledPakMod(
                FilePath: file,
                FileName: Path.GetFileName(file),
                FileSizeBytes: info.Length,
                InstalledUtc: info.LastWriteTimeUtc,
                IsEnabled: isPak,
                Source: PakModSource.Manual));
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
            if (!IcarusArchive.HasSupportedExtension(file)) continue;
            var kind = IcarusArchive.DetectKind(file);
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
    public ModInstallResult InstallAny(string sourcePath, bool overwrite = false)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Datei existiert nicht", sourcePath);

        var fileName = Path.GetFileName(sourcePath);
        switch (IcarusArchive.DetectKind(sourcePath))
        {
            case IcarusFileKind.Pak:
                return ModInstallResult.FromPak(Install(sourcePath, overwrite), fileName);

            case IcarusFileKind.Archive:
                return InstallArchive(sourcePath, fileName, overwrite);

            default:
                throw new InvalidDataException(
                    $"Unbekanntes Dateiformat: {fileName}. Erwartet wird ein Unreal-PAK " +
                    "oder ein ZIP-, RAR- oder 7z-Archiv.");
        }
    }

    /// <summary>Packt ein Mod-Archiv aus und sortiert seinen Inhalt ein:
    /// PAKs in den Mods-Ordner, UE4SS-Lua-Mods nach
    /// <c>Binaries/Win64/Mods/</c>. Enthaltene <c>.EXMODZ</c>-Dateien werden
    /// gezählt und gemeldet, aber noch nicht installiert — dafür braucht es
    /// den Datentabellen-Zusammenbau aus v1.24.</summary>
    private ModInstallResult InstallArchive(string archivePath, string fileName, bool overwrite)
    {
        var contents = IcarusArchive.Inspect(archivePath);
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
            using var archive = ArchiveFactory.Open(archivePath);
            foreach (var entry in archive.Entries)
            {
                if (entry.IsDirectory || string.IsNullOrEmpty(entry.Key)) continue;
                var key = entry.Key!.Replace('\\', '/');
                if (!key.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)) continue;

                // PAKs werden flach einsortiert: Icarus liest nur die erste
                // Ebene von Content/Paks/mods, eine Ordnerstruktur aus dem
                // Archiv waere dort wirkungslos.
                var pakName = Path.GetFileName(key);
                if (!IcarusArchive.TryResolveSafe(_manualModsDir, pakName, out var dst))
                {
                    Log.Warn("Zip-Slip im PAK-Namen uebersprungen: {Key}", key);
                    continue;
                }
                if (File.Exists(dst) && !overwrite)
                    throw new IOException($"Mod ist bereits installiert: {pakName}");
                IcarusArchive.ExtractOne(entry, dst);
                var info = new FileInfo(dst);
                paks.Add(new InstalledPakMod(dst, pakName, info.Length, info.LastWriteTimeUtc,
                    IsEnabled: true, Source: PakModSource.Manual));
                Log.Info("PAK aus Archiv installiert: {Name} → {Path}", pakName, dst);
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

        return new ModInstallResult(paks, luaMods, contents.ExmodzEntries, fileName, luaError);
    }

    public InstalledPakMod Install(string sourcePakPath, bool overwrite = false)
    {
        if (!File.Exists(sourcePakPath))
            throw new FileNotFoundException("PAK-Datei existiert nicht", sourcePakPath);
        if (IcarusArchive.DetectKind(sourcePakPath) != IcarusFileKind.Pak)
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
        if (mod.Source == PakModSource.Ue4ssLua)
        {
            if (_lua is null)
                throw new InvalidOperationException("UE4SS-Pfade unbekannt.");
            _lua.Uninstall(ToLuaMod(mod));
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

    public InstalledPakMod SetEnabled(InstalledPakMod mod, bool enabled)
    {
        if (mod.Source == PakModSource.Workshop)
            throw new InvalidOperationException(
                "Workshop-Mods können nicht deaktiviert werden — Abo in Steam pausieren.");
        if (mod.Source == PakModSource.Ue4ssLua)
        {
            if (_lua is null)
                throw new InvalidOperationException("UE4SS-Pfade unbekannt.");
            var updated = _lua.SetEnabled(ToLuaMod(mod), enabled);
            return mod with { IsEnabled = updated.IsEnabled };
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
        if (!IcarusArchive.HasSupportedExtension(fileName))
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
/// <para><see cref="ExmodzFiles"/> sind gefundene, aber noch nicht
/// installierte Datentabellen-Mods. Sie werden bewusst gemeldet statt
/// stillschweigend übersprungen: sonst klickt der User „installieren", sieht
/// eine Erfolgsmeldung und wundert sich im Spiel, warum das neue Item
/// fehlt.</para></summary>
public sealed record ModInstallResult(
    IReadOnlyList<InstalledPakMod> Paks,
    IReadOnlyList<string> Ue4ssMods,
    IReadOnlyList<string> ExmodzFiles,
    string SourceFileName,
    string? Warning = null)
{
    public static ModInstallResult FromPak(InstalledPakMod pak, string sourceFileName)
        => new([pak], [], [], sourceFileName);

    /// <summary>Ob überhaupt etwas installiert wurde. Ein Archiv, das nur
    /// .EXMODZ enthält, ist in v1.23 genau dieser Fall — und darf nicht als
    /// Erfolg gemeldet werden.</summary>
    public bool InstalledAnything => Paks.Count > 0 || Ue4ssMods.Count > 0;

    public bool HasPendingExmodz => ExmodzFiles.Count > 0;

    /// <summary>Kurzfassung für die Benachrichtigung — nennt, was wirklich
    /// passiert ist, statt eines pauschalen „installiert".</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Paks.Count == 1) parts.Add(Paks[0].FileName);
        else if (Paks.Count > 1) parts.Add($"{Paks.Count} PAK-Mods");
        if (Ue4ssMods.Count == 1) parts.Add($"Lua-Mod {Ue4ssMods[0]}");
        else if (Ue4ssMods.Count > 1) parts.Add($"{Ue4ssMods.Count} Lua-Mods");
        return parts.Count == 0 ? SourceFileName : string.Join(" + ", parts);
    }
}
