using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace ModManager.Plugins.Icarus.Services.Nexus;

/// <summary>Cached Katalog-Wrapper um <see cref="NexusApiClient"/>. Läuft
/// nach dem LS25-Katalog-Cache-Muster: Snapshot als JSON, Alters-Check
/// über <c>CatalogRefreshHours</c>, Stale-Fallback wenn API failt.</summary>
public sealed class NexusCatalogService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly NexusApiClient _api;
    private readonly NexusSettingsService _settings;
    private readonly IcarusPaths _paths;

    public NexusCatalogService(NexusApiClient api, NexusSettingsService settings, IcarusPaths paths)
    {
        _api = api;
        _settings = settings;
        _paths = paths;
    }

    /// <summary>Läd alle drei Nexus-Listen (latest_added, latest_updated,
    /// trending), merged deduped nach mod_id. Cached im PluginCacheDir.
    /// Bei Netzfehler: Stale-Cache zurückgeben.</summary>
    public async Task<CatalogSnapshot> LoadAsync(bool forceRefresh, CancellationToken ct = default)
    {
        var cached = LoadCache();
        var maxAge = TimeSpan.FromHours(Math.Max(1, _settings.Current.CatalogRefreshHours));
        var isFresh = cached is not null && DateTime.UtcNow - cached.SavedUtc < maxAge;

        if (!forceRefresh && isFresh)
        {
            Log.Info("Nexus-Katalog aus Cache ({Count} Einträge, {Age}h alt)",
                cached!.Entries.Count, (int)(DateTime.UtcNow - cached.SavedUtc).TotalHours);
            return cached;
        }

        try
        {
            var slug = _settings.Current.GameSlug;
            var latestAdded = await _api.GetCatalogAsync(slug, "latest_added", ct);
            var latestUpdated = await _api.GetCatalogAsync(slug, "latest_updated", ct);
            var trending = await _api.GetCatalogAsync(slug, "trending", ct);

            var merged = new Dictionary<int, NexusCatalogEntry>();
            foreach (var e in latestAdded) merged[e.ModId] = e;
            foreach (var e in latestUpdated) merged[e.ModId] = e;
            foreach (var e in trending) merged[e.ModId] = e;

            var snapshot = new CatalogSnapshot(
                SavedUtc: DateTime.UtcNow,
                Entries: merged.Values.OrderByDescending(e => e.UpdatedUtc).ToList());
            SaveCache(snapshot);
            Log.Info("Nexus-Katalog geladen: {Count} unique Mods", snapshot.Entries.Count);
            return snapshot;
        }
        catch (Exception ex) when (cached is not null)
        {
            Log.Warn(ex, "Nexus-API failed — Stale-Cache wird verwendet ({Age}h)",
                (int)(DateTime.UtcNow - cached.SavedUtc).TotalHours);
            return cached;
        }
    }

    private CatalogSnapshot? LoadCache()
    {
        try
        {
            if (!File.Exists(_paths.NexusCatalogCachePath)) return null;
            var json = File.ReadAllText(_paths.NexusCatalogCachePath);
            return JsonSerializer.Deserialize<CatalogSnapshot>(json);
        }
        catch (Exception ex) { Log.Debug(ex, "Nexus-Cache-Load fehlgeschlagen"); return null; }
    }

    private void SaveCache(CatalogSnapshot snapshot)
    {
        try
        {
            var tmp = _paths.NexusCatalogCachePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, JsonOpts));
            File.Move(tmp, _paths.NexusCatalogCachePath, overwrite: true);
        }
        catch (Exception ex) { Log.Warn(ex, "Nexus-Cache-Save fehlgeschlagen"); }
    }
}

public sealed record CatalogSnapshot(DateTime SavedUtc, List<NexusCatalogEntry> Entries);
