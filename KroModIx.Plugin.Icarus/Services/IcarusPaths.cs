using System.IO;
using KroModIx.Plugin.Contracts;

namespace KroModIx.Plugin.Icarus.Services;

/// <summary>Zentraler Pfad-Anbieter für das Icarus-Plugin. Kapselt die vom
/// Host gelieferten Data- und Cache-Verzeichnisse und leitet daraus die
/// plugin-eigenen Unter-Ordner ab (Downloads/Backups/Cover-Cache/Settings).
/// Analog zu <c>Ls25Paths</c> im LS25-Plugin.</summary>
public sealed class IcarusPaths
{
    private readonly IHostServices _host;

    public IcarusPaths(IHostServices host)
    {
        _host = host;
        Directory.CreateDirectory(DownloadsDir);
        Directory.CreateDirectory(BackupsDir);
        Directory.CreateDirectory(NexusCacheDir);
        Directory.CreateDirectory(NexusCoverDir);
    }

    public string PluginDataDir => _host.PluginDataDir;
    public string PluginCacheDir => _host.PluginCacheDir;

    /// <summary>Wohin heruntergeladene PAK-Dateien landen (User lädt via
    /// Browser aus Nexus, das Plugin überwacht diesen Ordner).</summary>
    public string DownloadsDir => Path.Combine(_host.PluginDataDir, "downloads");

    public string BackupsDir => Path.Combine(_host.PluginDataDir, "backups");

    public string NexusCacheDir => Path.Combine(_host.PluginCacheDir, "nexus");
    public string NexusCoverDir => Path.Combine(_host.PluginCacheDir, "covers");

    public string NexusSettingsPath => Path.Combine(_host.PluginDataDir, "nexus.json");
    public string NexusCatalogCachePath => Path.Combine(NexusCacheDir, "catalog.json");
}
