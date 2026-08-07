using System;
using System.Collections.Generic;
using System.IO;
using NLog;

namespace ModManager.Plugins.Icarus.Services;

/// <summary>
/// PAK-Mod-Verwaltung (analog zu ZIP-Mods bei LS25 nur mit .pak/.pak.disabled).
/// </summary>
public sealed class PakInstallService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly string _modsDir;

    public PakInstallService(string modsDir) => _modsDir = modsDir;

    public string ModsDir => _modsDir;

    public IReadOnlyList<InstalledPakMod> ListInstalled()
    {
        if (!Directory.Exists(_modsDir))
        {
            Log.Info("Icarus-Mods-Ordner existiert nicht: {Path}", _modsDir);
            return Array.Empty<InstalledPakMod>();
        }
        var result = new List<InstalledPakMod>();
        foreach (var file in Directory.EnumerateFiles(_modsDir))
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
                IsEnabled: isPak));
        }
        return result;
    }

    public InstalledPakMod Install(string sourcePakPath, bool overwrite = false)
    {
        if (!File.Exists(sourcePakPath))
            throw new FileNotFoundException("PAK-Datei existiert nicht", sourcePakPath);
        if (!sourcePakPath.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Nur .pak-Dateien werden akzeptiert.");

        Directory.CreateDirectory(_modsDir);
        var fileName = Path.GetFileName(sourcePakPath);
        var destination = Path.Combine(_modsDir, fileName);
        if (File.Exists(destination) && !overwrite)
            throw new IOException($"Mod ist bereits installiert: {fileName}");

        File.Copy(sourcePakPath, destination, overwrite: true);
        Log.Info("Icarus-Mod installiert: {Name} → {Path}", fileName, destination);

        var info = new FileInfo(destination);
        return new InstalledPakMod(destination, fileName, info.Length, info.LastWriteTimeUtc, IsEnabled: true);
    }

    public void Uninstall(InstalledPakMod mod)
    {
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
}
