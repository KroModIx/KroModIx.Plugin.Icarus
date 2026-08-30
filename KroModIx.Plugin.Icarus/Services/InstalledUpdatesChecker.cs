using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KroModIx.Plugin.Contracts;
using KroModIx.Plugin.Icarus.Services.Nexus;
using NLog;

namespace KroModIx.Plugin.Icarus.Services;

/// <summary>Prüft für jede installierte Manual-PAK-Row mit erkennbarer
/// Nexus-Mod-Id ob Nexus eine neuere Version anbietet als die aus dem
/// Filename extrahierte. Wird von zwei Callsites konsumiert:
///
/// <list type="number">
/// <item><b>User-Klick</b> in <c>InstalledPaksViewModel.CheckUpdatesAsync</c></item>
/// <item><b>Auto-Check beim App-Start</b> in <c>IcarusPlugin.InitializeAsync</c></item>
/// </list>
///
/// <para>Beide Wege schreiben in <see cref="InstalledUpdatesTracker"/> für
/// den Sidebar-Kachel-Badge. Workshop-Rows ausgelassen — Steam updated
/// automatisch. Ohne Nexus-API-Key: 0 (kein Badge-Signal).</para></summary>
public sealed class InstalledUpdatesChecker
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly PakInstallService _installer;
    private readonly NexusApiClient _api;
    private readonly NexusSettingsService _settings;
    private readonly InstalledUpdatesTracker _tracker;

    public InstalledUpdatesChecker(PakInstallService installer, NexusApiClient api,
        NexusSettingsService settings, InstalledUpdatesTracker tracker)
    {
        _installer = installer;
        _api = api;
        _settings = settings;
        _tracker = tracker;
    }

    public async Task<int> CheckAsync(
        Action<int, string, string>? onUpdateFound = null,  // (modId, oldVer, newVer)
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        if (!_settings.HasApiKey)
        {
            Log.Debug("Kein Nexus-API-Key — Update-Check skipped");
            return 0;
        }

        var slug = _settings.Current.GameSlug;
        int checkedCount = 0, updatedCount = 0;

        var candidates = _installer.ListInstalled()
            .Where(m => m.Source == PakModSource.Manual)
            .Select(m => new
            {
                Mod = m,
                ModId = NexusFileNameParser.TryExtractModId(m.FileName),
                Version = NexusFileNameParser.TryExtractVersion(m.FileName),
            })
            .Where(x => x.ModId is int && !string.IsNullOrWhiteSpace(x.Version))
            .ToList();

        foreach (var c in candidates)
        {
            if (ct.IsCancellationRequested) break;
            var modId = c.ModId!.Value;
            checkedCount++;
            onProgress?.Invoke($"Updates prüfen: {checkedCount} · {c.Mod.FileName}");
            try
            {
                var detail = await _api.GetModDetailAsync(slug, modId);
                if (detail is null || string.IsNullOrWhiteSpace(detail.Version)) continue;
                if (IsVersionNewer(detail.Version, c.Version!))
                {
                    onUpdateFound?.Invoke(modId, c.Version!, detail.Version);
                    updatedCount++;
                    Log.Info("Update verfügbar {File}: {Old} → {New}",
                        c.Mod.FileName, c.Version, detail.Version);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Update-Check für mod_id={Id} fehlgeschlagen", modId);
            }
            try { await Task.Delay(250, ct); } catch { break; }
        }

        var summary = updatedCount > 0
            ? $"{updatedCount} Mod-Update(s) verfügbar (von {checkedCount} geprüft)"
            : "";
        _tracker.SetPending(updatedCount, summary);
        Log.Info("Icarus Update-Check fertig: {Updated}/{Checked}", updatedCount, checkedCount);
        return updatedCount;
    }

    /// <summary>Delegiert an den Contracts-Baukasten (v1.27). Der frueher
    /// hier stehende Eigenbau warf Suffixe weg und gab bei unparsebarem
    /// Format still false zurueck — Versionen wie "3" oder "1.2.3b"
    /// bekamen damit NIE ein Update gemeldet, ohne eine Zeile Log.</summary>
    private static bool IsVersionNewer(string candidate, string installed)
        => VersionCompare.IsNewer(candidate, installed);
}
