using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace ModManager.Plugins.Icarus.Services.Nexus;

/// <summary>
/// Dünner Wrapper um die Nexus-Mods-API (https://api.nexusmods.com/v1).
/// Auth via <c>apikey</c>-Header, User-Agent gehört zum Pflicht-Header-Set
/// von Nexus (sonst 403).
///
/// <para>Rate-Limits: 250 requests/h anonymous, <b>2500 requests/h</b> für
/// Personal-Keys. Die Response-Header <c>X-RL-Hourly-Remaining</c> und
/// <c>X-RL-Daily-Remaining</c> zeigen die verbleibenden Requests — logged
/// bei jedem Call für Diagnose.</para>
///
/// <para>Wir nutzen die drei „latest"-Endpunkte (latest_added,
/// latest_updated, trending) — jeweils bis zu 20 Einträge. Für einen
/// größeren Katalog müsste man <c>updated.json</c> mit period+Paginierung
/// verwenden; für M5.2 reicht die Top-20-Aggregation.</para>
/// </summary>
public sealed class NexusApiClient : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private readonly HttpClient _http;
    private readonly Func<string> _apiKeyProvider;

    public NexusApiClient(HttpClient http, Func<string> apiKeyProvider)
    {
        _http = http;
        _apiKeyProvider = apiKeyProvider;
        _http.BaseAddress ??= new Uri("https://api.nexusmods.com/");
        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _http.DefaultRequestHeaders.Add("User-Agent",
                "ModManager.Plugins.Icarus/0.2 (github.com/Kroste)");
        }
        if (!_http.DefaultRequestHeaders.Contains("Accept"))
        {
            _http.DefaultRequestHeaders.Add("Accept", "application/json");
        }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Prüft ob der aktuelle API-Key gültig ist. Liefert
    /// <c>(true, "Name (Premium: yes/no)")</c> bei Erfolg oder
    /// <c>(false, Fehlermeldung)</c> sonst.</summary>
    public async Task<(bool Ok, string Info)> ValidateAsync(CancellationToken ct = default)
    {
        var key = _apiKeyProvider();
        if (string.IsNullOrEmpty(key)) return (false, "Kein API-Key konfiguriert.");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "v1/users/validate.json");
            req.Headers.Add("apikey", key);
            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            LogRateLimit(resp);
            if (!resp.IsSuccessStatusCode)
                return (false, $"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}");

            var user = JsonSerializer.Deserialize<NexusUser>(body, JsonOpts);
            return user is null
                ? (false, "Leere Antwort von Nexus.")
                : (true, $"{user.Name} (Premium: {(user.IsPremium ? "ja" : "nein")})");
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Nexus validate fehlgeschlagen");
            return (false, ex.Message);
        }
    }

    /// <summary>Holt eine der drei Nexus-Katalog-Listen. Endpoint muss
    /// „latest_added", „latest_updated" oder „trending" sein.</summary>
    public async Task<IReadOnlyList<NexusCatalogEntry>> GetCatalogAsync(
        string gameSlug, string endpoint, CancellationToken ct = default)
    {
        var key = _apiKeyProvider();
        if (string.IsNullOrEmpty(key)) throw new InvalidOperationException(
            "Nexus-API-Key fehlt — bitte im Nexus-Settings-Tab eintragen.");

        var allowed = endpoint is "latest_added" or "latest_updated" or "trending";
        if (!allowed) throw new ArgumentException(
            "endpoint muss latest_added | latest_updated | trending sein", nameof(endpoint));

        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"v1/games/{gameSlug}/mods/{endpoint}.json");
        req.Headers.Add("apikey", key);
        using var resp = await _http.SendAsync(req, ct);
        LogRateLimit(resp);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct);
        var mods = JsonSerializer.Deserialize<List<NexusMod>>(body, JsonOpts) ?? new();

        var result = new List<NexusCatalogEntry>(mods.Count);
        foreach (var m in mods)
        {
            result.Add(new NexusCatalogEntry(
                ModId: m.ModId,
                Name: m.Name ?? "",
                Author: m.User?.Name ?? m.Author ?? "",
                Summary: m.Summary ?? "",
                Category: "", // nexus-api liefert nur category_id, kein Name — wäre extra call
                Version: m.Version ?? "",
                PictureUrl: m.PictureUrl ?? "",
                UpdatedUtc: FromUnixSeconds(m.UpdatedTimestamp),
                Downloads: 0, // nur in /mods/{id}.json enthalten, spart hier den Extra-Call
                Endorsements: m.EndorsementCount,
                Available: m.Available));
        }
        return result;
    }

    private static void LogRateLimit(HttpResponseMessage resp)
    {
        if (resp.Headers.TryGetValues("X-RL-Hourly-Remaining", out var h))
            Log.Debug("Nexus rate-limit hourly-remaining: {H}", string.Join(",", h));
    }

    private static DateTime FromUnixSeconds(long s) =>
        DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private sealed class NexusUser
    {
        public string Name { get; set; } = "";
        [JsonPropertyName("is_premium")] public bool IsPremium { get; set; }
    }

    private sealed class NexusMod
    {
        [JsonPropertyName("mod_id")] public int ModId { get; set; }
        public string? Name { get; set; }
        public string? Summary { get; set; }
        public string? Version { get; set; }
        public string? PictureUrl { get; set; }
        public string? Author { get; set; }
        public NexusModUser? User { get; set; }
        [JsonPropertyName("updated_timestamp")] public long UpdatedTimestamp { get; set; }
        [JsonPropertyName("endorsement_count")] public long EndorsementCount { get; set; }
        public bool Available { get; set; } = true;
    }

    private sealed class NexusModUser
    {
        public string? Name { get; set; }
    }
}
