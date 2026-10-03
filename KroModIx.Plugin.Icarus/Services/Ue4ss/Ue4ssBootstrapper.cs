using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
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
/// <para><b>Seit v1.26.0 über <c>IHostServices.GitHub</c></b> (Host
/// v1.33.0), und <b>die fest hinterlegte Ausweich-URL ist weg.</b> Sie
/// zeigte auf <c>v3.0.1</c> und wäre mit jeder neuen UE4SS-Ausgabe weiter
/// veraltet — wer beim GitHub-Limit landet, hätte stillschweigend eine alte
/// Fassung bekommen, ohne es zu erfahren. Der Umleitungs-Pfad des
/// Baukastens liefert stattdessen immer den aktuellen Tag; der Dateiname ist
/// <c>UE4SS_&lt;tag&gt;.zip</c>, also aus dem Tag zu bilden.</para>
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

    private const string Repo = "UE4SS-RE/RE-UE4SS";

    private readonly HttpClient _http;
    private readonly IGitHubService _gitHub;
    private readonly IArchiveService _archives;

    public Ue4ssBootstrapper(HttpClient http, IGitHubService gitHub, IArchiveService archives)
    {
        _http = http;
        _gitHub = gitHub;
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
            var (url, tag) = await TryFindLatestAsync(ct);
            if (url is null)
                return Ue4ssInstallResult.Fail(
                    "Kein UE4SS-Release gefunden — weder über die GitHub-API noch über den "
                    + "Umleitungs-Pfad. Besteht eine Internetverbindung?");
            // Der Tag ist immer gesetzt, wenn eine URL gefunden wurde — beide
            // kommen aus derselben Antwort. Der Compiler weiss das nicht.
            var version = tag ?? "unbekannt";
            Log.Info("UE4SS {Version} wird geladen: {Url}", version, url);

            progress?.Report(0.15);
            var tmpZip = Path.Combine(Path.GetTempPath(),
                $"ue4ss-{Guid.NewGuid():N}.zip");
            try
            {
                await DownloadAsync(url, tmpZip, progress, ct);
                progress?.Report(0.85);
                var result = Extract(_archives, tmpZip, win64);
                if (result.SkippedUnsafe.Count > 0)
                {
                    // Bei UE4SS selbst waere das ein Alarmzeichen: das ist
                    // ein Release eines bekannten Projekts, nicht ein
                    // Nutzer-Archiv. Lieber abbrechen und melden, statt den
                    // Rest einzuspielen — bis v1.25.0 wurde hier nur
                    // protokolliert und trotzdem Erfolg gemeldet.
                    Log.Warn("UE4SS-Archiv enthielt {Count} Ausbruchsversuch(e): {Entries}",
                        result.SkippedUnsafe.Count, string.Join(", ", result.SkippedUnsafe));
                    return Ue4ssInstallResult.Fail(
                        $"Das UE4SS-Archiv enthielt {result.SkippedUnsafe.Count} Eintrag/Einträge, "
                        + "die aus dem Win64-Verzeichnis herausschreiben wollten. Abgebrochen — "
                        + "das sollte bei einem offiziellen Release nicht vorkommen.");
                }
                progress?.Report(1.0);
                Log.Info("UE4SS {Version} installiert: {Count} Datei(en) → {Dir}",
                    version, result.Count, win64);
                return Ue4ssInstallResult.Success(
                    $"UE4SS {version} installiert ({result.Count} Dateien).", version);
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

    /// <summary>Neueste stabile Ausgabe über den Host-Baukasten; greift die
    /// Raten-Sperre, kennt er nur den Tag — dann wird die URL aus Tag und
    /// Namenskonvention gebildet. Vorab-Ausgaben überspringt der Baukasten
    /// selbst: der experimentelle Bau hat unter Proton eine andere
    /// Einhäng-Mechanik und ist nicht der Pfad, den dieses Plugin
    /// absichert.</summary>
    private async Task<(string? Url, string? Version)> TryFindLatestAsync(CancellationToken ct)
    {
        var hit = await _gitHub.FindLatestAssetAsync(Repo, IsWantedAsset, ct)
            .ConfigureAwait(false);
        if (hit is not null)
            return (hit.Value.Asset.DownloadUrl, hit.Value.Release.Tag);

        var release = await _gitHub.GetLatestReleaseAsync(Repo, ct).ConfigureAwait(false);
        if (release is null) return (null, null);

        // Der Dateiname traegt den Tag: UE4SS_v3.0.1.zip zu v3.0.1.
        var name = $"UE4SS_{release.Tag}.zip";
        Log.Info("Dateiliste nicht abrufbar ({Grund}) — URL aus Tag {Tag} und Namenskonvention {Name}",
            _gitHub.IsRateLimited ? "GitHub-Limit erreicht" : "keine passende Datei gemeldet",
            release.Tag, name);
        return (_gitHub.BuildAssetUrl(Repo, release.Tag, name), release.Tag);
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
    private static ArchiveExtractResult Extract(IArchiveService archives, string zipPath,
        string win64Dir)
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

        return result;
    }
}
