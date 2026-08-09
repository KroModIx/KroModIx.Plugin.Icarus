using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace ModManager.Plugins.Icarus.Services;

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

    public PakInstallService(string manualModsDir, string? workshopContentDir, string downloadsDir)
    {
        _manualModsDir = manualModsDir;
        _workshopContentDir = workshopContentDir;
        _downloadsDir = downloadsDir;
    }

    public string ModsDir => _manualModsDir;
    public string? WorkshopDir => _workshopContentDir;
    public string DownloadsDir => _downloadsDir;

    public IReadOnlyList<InstalledPakMod> ListInstalled()
    {
        var result = new List<InstalledPakMod>();
        ScanManual(result);
        ScanWorkshop(result);
        return result;
    }

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

    /// <summary>Listet PAKs im Plugin-Downloads-Ordner (der User lädt aus dem
    /// Browser, Datei landet hier, Plugin bietet Install-Button).</summary>
    public IReadOnlyList<DownloadedPak> ListDownloaded()
    {
        if (!Directory.Exists(_downloadsDir))
            return Array.Empty<DownloadedPak>();
        var result = new List<DownloadedPak>();
        foreach (var file in Directory.EnumerateFiles(_downloadsDir, "*.pak"))
        {
            var info = new FileInfo(file);
            result.Add(new DownloadedPak(file, Path.GetFileName(file), info.Length, info.LastWriteTimeUtc));
        }
        return result;
    }

    public InstalledPakMod Install(string sourcePakPath, bool overwrite = false)
    {
        if (!File.Exists(sourcePakPath))
            throw new FileNotFoundException("PAK-Datei existiert nicht", sourcePakPath);
        if (!sourcePakPath.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Nur .pak-Dateien werden akzeptiert.");

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

    /// <summary>Lädt eine PAK-URL streamend in den Downloads-Ordner, mit
    /// Progress-Bericht (Fraction 0..1). Kollision-Check: existiert die
    /// Datei schon UND overwrite=false → InvalidOperationException. Datei
    /// wird atomar über <c>.tmp</c>+<c>File.Move</c> geschrieben, damit ein
    /// abgebrochener Download nicht als „fertig" im Ordner landet.</summary>
    public async Task<string> DownloadPakAsync(HttpClient http, string url, string fileName,
        bool overwrite, IProgress<double>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_downloadsDir);
        // Nexus-URLs kommen manchmal mit Query-Strings oder ohne .pak-Extension
        // im Path — Filename kommt vom Aufrufer (aus NexusFileEntry.FileName).
        if (!fileName.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
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

/// <summary>Eine im Plugin-Downloads-Ordner liegende PAK-Datei (noch nicht
/// im Mods-Ordner installiert).</summary>
public sealed record DownloadedPak(
    string FilePath,
    string FileName,
    long FileSizeBytes,
    DateTime DownloadedUtc);
