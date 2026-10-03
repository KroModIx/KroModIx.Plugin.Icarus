using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KroModIx.Plugin.Contracts;
using NLog;

namespace KroModIx.Plugin.Icarus.Services.Ue4ss;

/// <summary>Ergebnis einer Loader-Installation.</summary>
public sealed record Ue4ssInstallResult(bool Ok, string Message, string? Version = null)
{
    public static Ue4ssInstallResult Fail(string message) => new(false, message);
    public static Ue4ssInstallResult Success(string message, string version)
        => new(true, message, version);
}

/// <summary>Lädt UE4SS vom GitHub-Release und entpackt es neben die
/// Spiel-Exe. Ohne diesen Schritt müsste der User die Release-Seite finden,
/// die richtige der vier ZIP-Dateien erkennen und sie von Hand nach
/// <c>Icarus/Binaries/Win64/</c> auspacken — genau die Reibung, die ein
/// Mod-Manager wegnehmen soll.
///
/// <para>Gebaut nach dem Muster von <c>BepInExBootstrapper</c> aus dem
/// DSP-Plugin: erst die GitHub-API für die neueste stabile Ausgabe, bei
/// Fehlschlag (Ratenbegrenzung, Netz weg) eine fest hinterlegte
/// Ausweich-URL. Das CDN von GitHub erlaubt anonyme Downloads ohne
/// Ratenbegrenzung, die API nicht.</para>
///
/// <para><b>Icarus läuft auf Unreal Engine 4</b> — belegt an den
/// Pfadangaben in <c>Icarus-Win64-Shipping.pdb</c>, die durchweg
/// <c>Engine/Source/Runtime</c> der 4er-Reihe nennen. Die stabile
/// UE4SS-Reihe 3.0.x deckt das ab; es braucht keine Spiel-spezifische
/// Konfiguration, UE4SS findet seine Einstiegspunkte per
/// Signatursuche.</para>
///
/// <para><b>Welches Archiv:</b> ein UE4SS-Release liefert vier ZIPs. Gebraucht
/// wird <c>UE4SS_v*.zip</c> — <c>zDEV-*</c> ist der Entwickler-Bau mit
/// 24 MB Symbolen, <c>zCustomGameConfigs</c> und <c>zMapGenBP</c> sind
/// Beigaben, die Icarus nicht braucht.</para></summary>
public sealed class Ue4ssBootstrapper
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const string ReleasesApi = "https://api.github.com/repos/UE4SS-RE/RE-UE4SS/releases";

    /// <summary>Ausweich-Ziel, wenn die GitHub-API nicht antwortet. v3.0.1
    /// ist die neueste stabile Ausgabe (Stand 10/2026) und die, gegen die
    /// der Lua-Mod-Pfad dieses Plugins entwickelt wurde.</summary>
    private const string FallbackAsset =
        "https://github.com/UE4SS-RE/RE-UE4SS/releases/download/v3.0.1/UE4SS_v3.0.1.zip";
    private const string FallbackVersion = "v3.0.1";

    private readonly HttpClient _http;
    private readonly IArchiveService _archives;

    public Ue4ssBootstrapper(HttpClient http, IArchiveService archives)
    {
        _http = http;
        _archives = archives;
    }

    /// <summary>Lädt UE4SS und entpackt es nach
    /// <c>&lt;InstallDir&gt;/Icarus/Binaries/Win64/</c>.</summary>
    public async Task<Ue4ssInstallResult> InstallAsync(Ue4ssPaths paths,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (paths.Win64Dir is not string win64)
            return Ue4ssInstallResult.Fail(
                "Icarus/Binaries/Win64 nicht gefunden — ist das wirklich eine " +
                "Icarus-Installation?");

        try
        {
            progress?.Report(0.05);
            var (url, version) = await TryFindLatestAsync(ct);
            Log.Info("UE4SS {Version} wird geladen: {Url}", version, url);

            progress?.Report(0.15);
            var tmpZip = Path.Combine(Path.GetTempPath(),
                $"ue4ss-{Guid.NewGuid():N}.zip");
            try
            {
                await DownloadAsync(url, tmpZip, progress, ct);
                progress?.Report(0.85);
                var files = Extract(_archives, tmpZip, win64);
                progress?.Report(1.0);
                Log.Info("UE4SS {Version} installiert: {Count} Datei(en) → {Dir}",
                    version, files, win64);
                return Ue4ssInstallResult.Success(
                    $"UE4SS {version} installiert ({files} Dateien).", version);
            }
            finally
            {
                try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { /* Temp-Rest */ }
            }
        }
        catch (OperationCanceledException)
        {
            return Ue4ssInstallResult.Fail("Abgebrochen.");
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "UE4SS-Installation fehlgeschlagen");
            return Ue4ssInstallResult.Fail($"Fehler: {ex.Message}");
        }
    }

    /// <summary>Neueste stabile Ausgabe über die GitHub-API; bei jedem
    /// Fehlschlag die Ausweich-URL. Vorab-Ausgaben werden übersprungen —
    /// der experimentelle Bau hat unter Proton eine andere Einhäng-Mechanik
    /// und ist nicht der Pfad, den dieses Plugin absichert.</summary>
    private async Task<(string Url, string Version)> TryFindLatestAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ReleasesApi + "?per_page=15");
            req.Headers.UserAgent.TryParseAdd("KroModIx-Icarus-Plugin/1.0");
            req.Headers.Accept.TryParseAdd("application/vnd.github+json");
            // Optional: GITHUB_TOKEN aus der Umgebung → 5000 statt 60
            // Anfragen pro Stunde (analog PluginUpdateService).
            var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (!string.IsNullOrEmpty(token))
                req.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                if (rel.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) continue;
                if (rel.TryGetProperty("draft", out var dr) && dr.GetBoolean()) continue;
                var tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                if (!rel.TryGetProperty("assets", out var assets)) continue;
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name is null || !IsWantedAsset(name)) continue;
                    var url = a.TryGetProperty("browser_download_url", out var u)
                        ? u.GetString() : null;
                    if (url is not null) return (url, tag ?? "unbekannt");
                }
            }
            Log.Info("Kein passendes UE4SS-Asset in der API-Antwort — Ausweich-URL");
        }
        catch (Exception ex)
        {
            Log.Info(ex, "GitHub-API nicht erreichbar — Ausweich-URL für UE4SS");
        }
        return (FallbackAsset, FallbackVersion);
    }

    /// <summary>Das Laufzeit-Archiv, nicht der Entwickler-Bau und nicht die
    /// Beigaben.</summary>
    private static bool IsWantedAsset(string assetName)
        => assetName.StartsWith("UE4SS_v", StringComparison.OrdinalIgnoreCase)
           && assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
           && !assetName.StartsWith("zDEV", StringComparison.OrdinalIgnoreCase);

    private async Task DownloadAsync(string url, string target,
        IProgress<double>? progress, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;

        await using var input = await resp.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(target);
        var buf = new byte[64 * 1024];
        long done = 0;
        int read;
        var lastReport = DateTime.UtcNow;
        while ((read = await input.ReadAsync(buf, ct)) > 0)
        {
            await output.WriteAsync(buf.AsMemory(0, read), ct);
            done += read;
            // Hoechstens 5 Meldungen pro Sekunde — sonst flutet der
            // Fortschritt den Dispatcher.
            if (total > 0 && DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(200))
            {
                progress?.Report(0.15 + 0.7 * ((double)done / total));
                lastReport = DateTime.UtcNow;
            }
        }
    }

    /// <summary>Packt das Loader-Archiv ins Win64-Verzeichnis aus. Vorhandene
    /// Dateien werden überschrieben (das ist der Update-Weg), aber die
    /// <c>UE4SS-settings.ini</c> und die <c>Mods/mods.txt</c> bleiben
    /// unangetastet, wenn sie schon existieren — in beiden stehen
    /// Einstellungen des Users, und ein Loader-Update darf sie nicht
    /// zurücksetzen.</summary>
    private static int Extract(IArchiveService archives, string zipPath, string win64Dir)
    {
        var keepIfPresent = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "UE4SS-settings.ini", "Mods/mods.txt",
        };

        var result = archives.Extract(zipPath, win64Dir, new ArchiveExtractOptions(
            Filter: key =>
            {
                if (!keepIfPresent.Contains(key)) return true;
                var dst = Path.Combine(win64Dir,
                    key.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(dst)) return true;
                Log.Info("Vorhandene Nutzer-Datei behalten: {Key}", key);
                return false;
            },
            Overwrite: true));

        foreach (var skipped in result.SkippedUnsafe)
            Log.Warn("Eintrag aus dem UE4SS-Archiv aus Sicherheitsgruenden uebersprungen: {Key}", skipped);
        return result.Count;
    }
}
