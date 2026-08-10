using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace KroModIx.Plugin.Icarus.Services.Nexus;

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
                "KroModIx.Plugin.Icarus/0.2 (github.com/Kroste)");
        }
        if (!_http.DefaultRequestHeaders.Contains("Accept"))
        {
            _http.DefaultRequestHeaders.Add("Accept", "application/json");
        }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Prüft ob der aktuelle API-Key gültig ist. Liefert bei Erfolg
    /// <see cref="NexusValidateResult"/> mit Name + Premium-Flag, damit der
    /// Aufrufer den Premium-Status persistieren und Download-Features
    /// entsprechend enablen kann.</summary>
    public async Task<NexusValidateResult> ValidateAsync(CancellationToken ct = default)
    {
        var key = _apiKeyProvider();
        if (string.IsNullOrEmpty(key))
            return new NexusValidateResult(false, "", false, "Kein API-Key konfiguriert.");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "v1/users/validate.json");
            req.Headers.Add("apikey", key);
            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            LogRateLimit(resp);
            if (!resp.IsSuccessStatusCode)
                return new NexusValidateResult(false, "", false,
                    $"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}");

            var user = JsonSerializer.Deserialize<NexusUser>(body, JsonOpts);
            if (user is null)
                return new NexusValidateResult(false, "", false, "Leere Antwort von Nexus.");
            return new NexusValidateResult(true, user.Name ?? "", user.IsPremium,
                $"{user.Name} (Premium: {(user.IsPremium ? "ja" : "nein")})");
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Nexus validate fehlgeschlagen");
            return new NexusValidateResult(false, "", false, ex.Message);
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

    /// <summary>Liste aller <c>mod_id</c>s die im angegebenen Zeitraum
    /// aktualisiert wurden — der Nexus-Endpoint gibt nur (mod_id, latest_file_update)
    /// zurück, keine Meta. Für vollen Katalog-Warmup: diese IDs enumerieren
    /// und pro ID <see cref="GetModDetailAsync"/> aufrufen.
    ///
    /// <para><paramref name="period"/> muss <c>1d</c>, <c>1w</c> oder <c>1m</c>
    /// sein — das sind die einzigen Werte die Nexus akzeptiert. Für Icarus
    /// (216 Mods gesamt, wenig Neu-Uploads) deckt <c>1m</c> praktisch die
    /// gesamte aktive Community ab.</para></summary>
    public async Task<IReadOnlyList<int>> GetUpdatedModIdsAsync(
        string gameSlug, string period, CancellationToken ct = default)
    {
        var key = _apiKeyProvider();
        if (string.IsNullOrEmpty(key)) throw new InvalidOperationException(
            "Nexus-API-Key fehlt — bitte im Nexus-Settings-Tab eintragen.");
        if (period is not ("1d" or "1w" or "1m"))
            throw new ArgumentException("period muss 1d | 1w | 1m sein", nameof(period));

        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"v1/games/{gameSlug}/mods/updated.json?period={period}");
        req.Headers.Add("apikey", key);
        using var resp = await _http.SendAsync(req, ct);
        LogRateLimit(resp);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct);
        var list = JsonSerializer.Deserialize<List<NexusUpdatedEntry>>(body, JsonOpts) ?? new();
        return list.Select(e => e.ModId).Distinct().ToList();
    }

    private sealed class NexusUpdatedEntry
    {
        [JsonPropertyName("mod_id")] public int ModId { get; set; }
    }

    /// <summary>Volles Mod-Detail (Beschreibung, Downloads, Kategorie-ID, …)
    /// via <c>GET /v1/games/{slug}/mods/{id}.json</c>. Ein Extra-API-Call
    /// pro Detail-Öffnung — nutzt einen der 2500/h Personal-Rate-Limit-Slots.</summary>
    public async Task<NexusModDetail?> GetModDetailAsync(string gameSlug, int modId,
        CancellationToken ct = default)
    {
        var key = _apiKeyProvider();
        if (string.IsNullOrEmpty(key)) return null;

        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"v1/games/{gameSlug}/mods/{modId}.json");
        req.Headers.Add("apikey", key);
        using var resp = await _http.SendAsync(req, ct);
        LogRateLimit(resp);
        if (!resp.IsSuccessStatusCode)
        {
            Log.Warn("Nexus detail HTTP {Code} für mod_id={Id}", (int)resp.StatusCode, modId);
            return null;
        }
        var body = await resp.Content.ReadAsStringAsync(ct);
        var m = JsonSerializer.Deserialize<NexusModFull>(body, JsonOpts);
        if (m is null) return null;

        return new NexusModDetail(
            ModId: m.ModId,
            Name: m.Name ?? "",
            Author: m.User?.Name ?? m.Author ?? "",
            Summary: m.Summary ?? "",
            DescriptionHtml: m.Description ?? "",
            Version: m.Version ?? "",
            PictureUrl: m.PictureUrl ?? "",
            CategoryId: m.CategoryId,
            CreatedUtc: FromUnixSeconds(m.CreatedTimestamp),
            UpdatedUtc: FromUnixSeconds(m.UpdatedTimestamp),
            EndorsementCount: m.EndorsementCount,
            ContainsAdultContent: m.ContainsAdultContent,
            Available: m.Available,
            DomainName: m.DomainName ?? gameSlug);
    }

    /// <summary>Alle Files eines Mods (Main, Update, Optional, Old, …).
    /// Für Premium-Download suchen wir das MAIN+primary-File der neuesten
    /// Version; siehe <see cref="NexusFileEntry.IsMainAndPrimary"/>.</summary>
    public async Task<IReadOnlyList<NexusFileEntry>> GetFilesAsync(string gameSlug, int modId,
        CancellationToken ct = default)
    {
        var key = _apiKeyProvider();
        if (string.IsNullOrEmpty(key)) return Array.Empty<NexusFileEntry>();

        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"v1/games/{gameSlug}/mods/{modId}/files.json");
        req.Headers.Add("apikey", key);
        using var resp = await _http.SendAsync(req, ct);
        LogRateLimit(resp);
        if (!resp.IsSuccessStatusCode)
        {
            Log.Warn("Nexus files HTTP {Code} für mod_id={Id}", (int)resp.StatusCode, modId);
            return Array.Empty<NexusFileEntry>();
        }
        var body = await resp.Content.ReadAsStringAsync(ct);
        var wrap = JsonSerializer.Deserialize<NexusFilesResponse>(body, JsonOpts);
        if (wrap?.Files is null) return Array.Empty<NexusFileEntry>();

        var result = new List<NexusFileEntry>(wrap.Files.Count);
        foreach (var f in wrap.Files)
        {
            result.Add(new NexusFileEntry(
                FileId: f.FileId,
                Name: f.Name ?? "",
                FileName: f.FileName ?? "",
                Version: f.Version ?? "",
                Description: f.Description ?? "",
                CategoryId: f.CategoryId,
                CategoryName: f.CategoryName ?? "",
                IsPrimary: f.IsPrimary,
                SizeInBytes: f.SizeInBytes,
                UploadedUtc: FromUnixSeconds(f.UploadedTimestamp)));
        }
        return result;
    }

    /// <summary>Direkter Download-URL für ein File — <b>nur mit Premium-Key</b>
    /// oder mit einem gültigen NXM-Session-Key. Ohne Premium bekommt man
    /// HTTP 403 mit „You don't have permission to get download link.".
    /// Der zurückgelieferte URL ist ein S3-Presigned-URL, kurzlebig
    /// (~30 min TTL) — direkt danach downloaden.</summary>
    public async Task<string?> GetDownloadLinkAsync(string gameSlug, int modId, long fileId,
        CancellationToken ct = default)
    {
        var key = _apiKeyProvider();
        if (string.IsNullOrEmpty(key)) return null;

        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"v1/games/{gameSlug}/mods/{modId}/files/{fileId}/download_link.json");
        req.Headers.Add("apikey", key);
        using var resp = await _http.SendAsync(req, ct);
        LogRateLimit(resp);
        if (!resp.IsSuccessStatusCode)
        {
            Log.Warn("Nexus download_link HTTP {Code} — Premium-Key nötig? (mod={Mod}, file={File})",
                (int)resp.StatusCode, modId, fileId);
            return null;
        }
        var body = await resp.Content.ReadAsStringAsync(ct);
        var arr = JsonSerializer.Deserialize<List<NexusDownloadLink>>(body, JsonOpts);
        // Nexus liefert ein Array mit CDN-Optionen (verschiedene Regionen).
        // Erste nehmen — API sortiert nach Nähe.
        return arr is { Count: > 0 } ? arr[0].Uri : null;
    }

    /// <summary>Alle Kategorien für ein Spiel — Nexus liefert nur category_id
    /// in den Mod-Responses, mit dieser Liste können wir auf Namen mappen.
    /// Lädt nur einmal pro Session (Cache im <see cref="NexusCategoryService"/>).</summary>
    public async Task<IReadOnlyList<NexusCategory>> GetCategoriesAsync(string gameSlug,
        CancellationToken ct = default)
    {
        var key = _apiKeyProvider();
        if (string.IsNullOrEmpty(key)) return Array.Empty<NexusCategory>();

        // Die Categories stehen als Sub-Objekt in /v1/games/{slug}.json.
        using var req = new HttpRequestMessage(HttpMethod.Get, $"v1/games/{gameSlug}.json");
        req.Headers.Add("apikey", key);
        using var resp = await _http.SendAsync(req, ct);
        LogRateLimit(resp);
        if (!resp.IsSuccessStatusCode)
        {
            Log.Warn("Nexus game-info HTTP {Code} für slug={Slug}", (int)resp.StatusCode, gameSlug);
            return Array.Empty<NexusCategory>();
        }
        var body = await resp.Content.ReadAsStringAsync(ct);
        var game = JsonSerializer.Deserialize<NexusGameInfo>(body, JsonOpts);
        if (game?.Categories is null) return Array.Empty<NexusCategory>();

        var result = new List<NexusCategory>(game.Categories.Count);
        foreach (var c in game.Categories)
            result.Add(new NexusCategory(c.CategoryId, c.Name ?? "", c.ParentCategory));
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

    /// <summary>Volles Mod-Objekt aus /mods/{id}.json. Mehr Felder als
    /// <see cref="NexusMod"/> — insbesondere <c>description</c> (HTML) und
    /// <c>category_id</c>.</summary>
    private sealed class NexusModFull
    {
        [JsonPropertyName("mod_id")] public int ModId { get; set; }
        public string? Name { get; set; }
        public string? Summary { get; set; }
        public string? Description { get; set; }
        public string? Version { get; set; }
        public string? PictureUrl { get; set; }
        public string? Author { get; set; }
        public NexusModUser? User { get; set; }
        [JsonPropertyName("category_id")] public int CategoryId { get; set; }
        [JsonPropertyName("created_timestamp")] public long CreatedTimestamp { get; set; }
        [JsonPropertyName("updated_timestamp")] public long UpdatedTimestamp { get; set; }
        [JsonPropertyName("endorsement_count")] public long EndorsementCount { get; set; }
        [JsonPropertyName("contains_adult_content")] public bool ContainsAdultContent { get; set; }
        public bool Available { get; set; } = true;
        [JsonPropertyName("domain_name")] public string? DomainName { get; set; }
    }

    private sealed class NexusGameInfo
    {
        public List<NexusGameCategory>? Categories { get; set; }
    }

    private sealed class NexusGameCategory
    {
        [JsonPropertyName("category_id")] public int CategoryId { get; set; }
        public string? Name { get; set; }
        /// <summary>Nexus liefert entweder int (parent id) oder bool false
        /// (Root-Kategorie). Mit AllowReadingFromString+Custom-Handling geht das
        /// nicht sauber — deshalb JsonElement lesen und selbst parsen.</summary>
        [JsonPropertyName("parent_category")]
        [JsonConverter(typeof(FalseOrIntConverter))]
        public int ParentCategory { get; set; }
    }

    private sealed class NexusFilesResponse
    {
        public List<NexusFile>? Files { get; set; }
    }

    private sealed class NexusFile
    {
        [JsonPropertyName("file_id")] public long FileId { get; set; }
        public string? Name { get; set; }
        [JsonPropertyName("file_name")] public string? FileName { get; set; }
        public string? Version { get; set; }
        public string? Description { get; set; }
        [JsonPropertyName("category_id")] public int CategoryId { get; set; }
        [JsonPropertyName("category_name")] public string? CategoryName { get; set; }
        [JsonPropertyName("is_primary")] public bool IsPrimary { get; set; }
        [JsonPropertyName("size_in_bytes")] public long SizeInBytes { get; set; }
        [JsonPropertyName("uploaded_timestamp")] public long UploadedTimestamp { get; set; }
    }

    private sealed class NexusDownloadLink
    {
        public string? Name { get; set; }         // CDN-Region-Name
        [JsonPropertyName("short_name")] public string? ShortName { get; set; }
        [JsonPropertyName("URI")] public string? Uri { get; set; }
    }

    /// <summary>Nexus liefert <c>parent_category: false</c> für Root-Kategorien und
    /// eine int-ID für Subkategorien. System.Text.Json würde ohne diesen Converter
    /// mit JsonException aussteigen.</summary>
    private sealed class FalseOrIntConverter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.False) return 0;
            if (reader.TokenType == JsonTokenType.True) return 0;
            if (reader.TokenType == JsonTokenType.Number) return reader.GetInt32();
            return 0;
        }
        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
            => writer.WriteNumberValue(value);
    }
}
