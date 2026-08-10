using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace KroModIx.Plugin.Icarus.Services.Nexus;

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
            Log.Info("Nexus-Katalog geladen: {Count} unique Mods (Top-Listen)", snapshot.Entries.Count);
            return snapshot;
        }
        catch (Exception ex) when (cached is not null)
        {
            Log.Warn(ex, "Nexus-API failed — Stale-Cache wird verwendet ({Age}h)",
                (int)(DateTime.UtcNow - cached.SavedUtc).TotalHours);
            return cached;
        }
    }

    /// <summary>Cache-Warmup: sammelt zusätzlich zu den drei Top-Listen alle
    /// ModIds die im letzten Monat aktualisiert wurden (<c>updated.json?period=1m</c>)
    /// und lädt für jede noch unbekannte ModId ein Detail-Objekt nach. Startet
    /// von der bestehenden <see cref="LoadAsync"/>-Snapshot als Basis.
    ///
    /// <para>Rate-Limit-freundlich: 300ms Delay zwischen Detail-Requests (=
    /// max. 12 Req/Sekunde / 720 pro Minute). Free-User-Limit ist 250/h — bei
    /// 216 Icarus-Mods bricht der Warmup nach ca. 250 Requests still ab und
    /// nutzt was er hat. Premium-User (2500/h) schaffen problemlos alle.</para>
    ///
    /// <para><paramref name="onProgress"/> wird nach jedem Detail-Fetch mit
    /// (done, total) aufgerufen — für UI-Progress-Bar. Läuft auf beliebigem
    /// Thread; der Aufrufer marshalt selbst auf UI-Thread wenn nötig.</para></summary>
    public async Task<CatalogSnapshot> LoadExtendedAsync(bool forceRefresh,
        Action<int, int>? onProgress = null, CancellationToken ct = default)
    {
        var basisSnapshot = await LoadAsync(forceRefresh, ct);
        var byId = basisSnapshot.Entries.ToDictionary(e => e.ModId);
        var slug = _settings.Current.GameSlug;

        List<int> updatedIds;
        try { updatedIds = (await _api.GetUpdatedModIdsAsync(slug, "1m", ct)).ToList(); }
        catch (Exception ex)
        {
            Log.Warn(ex, "Nexus updated.json?period=1m fehlgeschlagen — nur Top-Listen");
            return basisSnapshot;
        }

        var missing = updatedIds.Where(id => !byId.ContainsKey(id)).ToList();
        Log.Info("Nexus-Extended: {Missing} zusätzliche Mods aus updated.json (period=1m) zu fetchen (Total-Katalog dann: {Total})",
            missing.Count, basisSnapshot.Entries.Count + missing.Count);

        int done = 0;
        int total = missing.Count;
        onProgress?.Invoke(done, total);

        foreach (var modId in missing)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var detail = await _api.GetModDetailAsync(slug, modId, ct);
                if (detail is not null)
                {
                    byId[modId] = new NexusCatalogEntry(
                        ModId: detail.ModId,
                        Name: detail.Name,
                        Author: detail.Author,
                        Summary: detail.Summary,
                        Category: "",
                        Version: detail.Version,
                        PictureUrl: detail.PictureUrl,
                        UpdatedUtc: detail.UpdatedUtc,
                        Downloads: 0,
                        Endorsements: detail.EndorsementCount,
                        Available: detail.Available);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Detail-Fetch für mod_id={Id} fehlgeschlagen — überspringe", modId);
                // Rate-Limit erreicht? Weitermachen wäre sinnlos — Break.
                if (ex is HttpRequestException http && (int?)http.StatusCode == 429)
                {
                    Log.Warn("Nexus rate-limit hit — Warmup gestoppt bei {Done}/{Total}", done, total);
                    break;
                }
            }
            done++;
            onProgress?.Invoke(done, total);

            // 300ms zwischen Requests → 200/min → passt für Premium (2500/h)
            // ohne den Slider zu treffen; Free-User haben ihr Kontingent
            // (250/h) nach ca. 4min voll — dann greift der 429-Break.
            try { await Task.Delay(300, ct); } catch { break; }
        }

        var extendedSnapshot = new CatalogSnapshot(
            SavedUtc: DateTime.UtcNow,
            Entries: byId.Values.OrderByDescending(e => e.UpdatedUtc).ToList());
        SaveCache(extendedSnapshot);
        Log.Info("Nexus-Extended fertig: {Count} unique Mods im Katalog", extendedSnapshot.Entries.Count);
        return extendedSnapshot;
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
