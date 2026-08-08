using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using NLog;

namespace ModManager.Plugins.Icarus.Services;

/// <summary>Backup/Restore für manuell installierte Icarus-PAK-Mods (Workshop-
/// Abos werden ausgelassen — Steam kümmert sich um die). Analog zum LS25-
/// <c>ModBackupService</c>. Backup ist eine ZIP mit allen PAKs + einem
/// <c>manifest.json</c>-Eintrag pro Datei.</summary>
public sealed class PakBackupService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private readonly PakInstallService _installer;

    public PakBackupService(PakInstallService installer) => _installer = installer;

    public async Task<BackupResult> CreateBackupAsync(string targetZipPath,
        IProgress<BackupProgress>? progress = null)
    {
        var mods = _installer.ListInstalled().Where(m => m.Source == PakModSource.Manual).ToList();
        if (mods.Count == 0) throw new InvalidOperationException("Keine manuellen Mods zum Sichern.");

        Directory.CreateDirectory(Path.GetDirectoryName(targetZipPath)!);
        var tmp = targetZipPath + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);

        var manifest = new BackupManifest(DateTime.UtcNow,
            mods.Select(m => new BackupEntry(m.FileName, m.IsEnabled, m.FileSizeBytes)).ToList());

        await Task.Run(() =>
        {
            using var zip = ZipFile.Open(tmp, ZipArchiveMode.Create);
            for (int i = 0; i < mods.Count; i++)
            {
                var m = mods[i];
                progress?.Report(new BackupProgress(i + 1, mods.Count, m.FileName));
                var entry = zip.CreateEntry(m.FileName, CompressionLevel.NoCompression);
                using var s = entry.Open();
                using var fs = File.OpenRead(m.FilePath);
                fs.CopyTo(s);
            }
            var manifestEntry = zip.CreateEntry("manifest.json", CompressionLevel.Fastest);
            using var ms = manifestEntry.Open();
            JsonSerializer.Serialize(ms, manifest, new JsonSerializerOptions { WriteIndented = true });
        });

        File.Move(tmp, targetZipPath, overwrite: true);
        var info = new FileInfo(targetZipPath);
        Log.Info("Icarus-Backup: {Count} Mods → {Path} ({Bytes} bytes)",
            mods.Count, targetZipPath, info.Length);
        return new BackupResult(targetZipPath, mods.Count, info.Length);
    }

    public async Task<RestoreResult> RestoreBackupAsync(string backupZipPath,
        IProgress<BackupProgress>? progress = null)
    {
        if (!File.Exists(backupZipPath))
            throw new FileNotFoundException("Backup-Datei nicht gefunden", backupZipPath);

        var restored = 0;
        var skipped = 0;
        var manifest = ReadManifest(backupZipPath);

        await Task.Run(() =>
        {
            using var zip = ZipFile.OpenRead(backupZipPath);
            var entries = zip.Entries
                .Where(e => e.FullName.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Directory.CreateDirectory(_installer.ModsDir);
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                progress?.Report(new BackupProgress(i + 1, entries.Count, e.Name));
                var manifestEntry = manifest.Mods.FirstOrDefault(x =>
                    string.Equals(x.FileName, e.Name, StringComparison.OrdinalIgnoreCase));
                var wasEnabled = manifestEntry?.WasEnabled ?? true;
                var finalName = wasEnabled ? e.Name : e.Name + ".disabled";
                var target = Path.Combine(_installer.ModsDir, finalName);
                try
                {
                    using var fs = File.Create(target);
                    using var s = e.Open();
                    s.CopyTo(fs);
                    restored++;
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "Icarus-Restore: {Name} übersprungen", e.Name);
                    skipped++;
                }
            }
        });

        return new RestoreResult(restored, skipped);
    }

    public static BackupManifest ReadManifest(string backupZipPath)
    {
        using var zip = ZipFile.OpenRead(backupZipPath);
        var mf = zip.GetEntry("manifest.json")
            ?? throw new InvalidDataException("Backup enthält kein manifest.json.");
        using var s = mf.Open();
        return JsonSerializer.Deserialize<BackupManifest>(s)
            ?? throw new InvalidDataException("manifest.json ist leer/kaputt.");
    }
}

public sealed record BackupProgress(int Current, int Total, string CurrentFileName)
{
    public double Fraction => Total == 0 ? 0 : (double)Current / Total;
}

public sealed record BackupResult(string FilePath, int ModCount, long FileSizeBytes);
public sealed record RestoreResult(int RestoredCount, int SkippedCount);

public sealed record BackupManifest(DateTime CreatedUtc, List<BackupEntry> Mods);
public sealed record BackupEntry(string FileName, bool WasEnabled, long SizeBytes);
