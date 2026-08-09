using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModManager.PluginContracts;
using ModManager.Plugins.Icarus.Services;
using ModManager.Plugins.Icarus.Services.Nexus;
using NLog;

namespace ModManager.Plugins.Icarus.Views;

/// <summary>VM für den Nexus-Mod-Detail-Dialog. Lädt beim Öffnen das volle
/// Mod-Detail (<c>/mods/{id}.json</c>) im Hintergrund, dekodiert die HTML-
/// Beschreibung, mappt Kategorie-ID auf Namen, bietet Browser-Öffnen und
/// KI-Zusammenfassung über den Host-KI-Provider (<c>_host.Ai</c>).
///
/// <para>Analog zu <c>ModDetailViewModel</c> im LS25-Plugin — bewusste
/// Struktur-Parallele, damit weitere Plugins das Muster übernehmen können.</para>
/// </summary>
public sealed partial class NexusModDetailViewModel : ObservableObject
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly int _modId;
    private readonly string _gameSlug;
    private readonly string _detailUrl;
    private readonly NexusApiClient _api;
    private readonly NexusCategoryService _categories;
    private readonly PakInstallService _installer;
    private readonly DownloadEventBus _downloadBus;
    private readonly IHostServices _host;

    public NexusModDetailViewModel(NexusRow row, string gameSlug, bool isPremium,
        NexusApiClient api, NexusCategoryService categories,
        PakInstallService installer, DownloadEventBus downloadBus,
        IHostServices host)
    {
        _modId = row.Source.ModId;
        _gameSlug = gameSlug;
        _detailUrl = row.Source.DetailUrl(gameSlug);
        _api = api;
        _categories = categories;
        _installer = installer;
        _downloadBus = downloadBus;
        IsPremium = isPremium;
        _host = host;

        // Vorbelegen aus der Row (der Katalog-Snapshot hat schon Fallback-Daten),
        // damit der Dialog nicht leer aufmacht während der Detail-Load läuft.
        Title = row.Name;
        Author = row.Author;
        Summary = row.Source.Summary;
        Version = row.VersionDisplay;
        EndorsementsText = row.EndorsementsText;
        UpdatedText = row.UpdatedText;
        Cover = row.Cover;
        Description = "Detail-Beschreibung wird geladen …";

        _ = LoadDetailAsync();
    }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _author = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _version = "";
    [ObservableProperty] private string _endorsementsText = "";
    [ObservableProperty] private string _updatedText = "";
    [ObservableProperty] private string _category = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _statusText = "Detail wird geladen …";
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _containsAdultContent;
    [ObservableProperty] private Bitmap? _cover;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    private string _aiSummary = "";
    public bool HasSummary => !string.IsNullOrWhiteSpace(AiSummary);

    [ObservableProperty] private bool _summaryBusy;

    /// <summary>Nexus-Premium-Flag aus dem Settings-Cache — bestimmt ob
    /// der „⬇ Herunterladen"-Button im Footer enabled ist.</summary>
    [ObservableProperty] private bool _isPremium;
    [ObservableProperty] private bool _downloadBusy;

    private async Task LoadDetailAsync()
    {
        try
        {
            var detail = await _api.GetModDetailAsync(_gameSlug, _modId);
            if (detail is null)
            {
                Description = "Detail konnte nicht geladen werden (API-Fehler oder Rate-Limit).";
                StatusText = "Fehler beim Laden.";
                return;
            }

            if (!string.IsNullOrWhiteSpace(detail.Name)) Title = detail.Name;
            if (!string.IsNullOrWhiteSpace(detail.Author)) Author = detail.Author;
            if (!string.IsNullOrWhiteSpace(detail.Summary)) Summary = detail.Summary;
            if (!string.IsNullOrWhiteSpace(detail.Version)) Version =
                char.IsDigit(detail.Version.TrimStart()[0]) ? "v" + detail.Version : detail.Version;
            EndorsementsText = detail.EndorsementCount > 0 ? $"👍 {detail.EndorsementCount}" : "";
            UpdatedText = detail.UpdatedUtc.ToLocalTime().ToString("g");
            ContainsAdultContent = detail.ContainsAdultContent;

            Description = HtmlStrip.ToPlainText(detail.DescriptionHtml);
            if (string.IsNullOrWhiteSpace(Description))
                Description = string.IsNullOrWhiteSpace(detail.Summary)
                    ? "Keine Beschreibung im Detail-Endpoint."
                    : detail.Summary;

            Category = await _categories.GetCategoryNameAsync(detail.CategoryId);
            StatusText = $"v{detail.Version} · {(detail.Available ? "verfügbar" : "nicht verfügbar")}";
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Nexus-Detail-Load fehlgeschlagen für mod_id={Id}", _modId);
            Description = $"Fehler: {ex.Message}";
            StatusText = "Fehler beim Laden.";
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private void OpenInBrowser() => _host.Shell.OpenExternalUrl(_detailUrl);

    /// <summary>Premium-Direct-Download aus dem Detail-Dialog — analog
    /// <c>NexusViewModel.DownloadRowAsync</c>. Nutzt dieselbe File-Auswahl-
    /// Heuristik (MAIN+primary → MAIN → erstes). Nach Erfolg feuert
    /// <see cref="DownloadEventBus.DownloadsChanged"/>.</summary>
    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (!IsPremium)
        {
            _host.Notifications.Notify(
                "Direct-Download braucht Nexus-Premium. Klick \"Auf Nexus öffnen\" für den Browser-Weg.",
                NotificationLevel.Warning);
            return;
        }
        DownloadBusy = true;
        using var scope = _host.BeginProgress($"Nexus: {Title}");
        scope.Report(0, "Datei-Liste laden …");
        try
        {
            var files = await _api.GetFilesAsync(_gameSlug, _modId);
            var file = NexusViewModel.PickMainFile(files);
            if (file is null)
            {
                _host.Notifications.Notify("Keine Main-Datei gefunden.", NotificationLevel.Warning);
                return;
            }
            scope.Report(0, $"Download-URL holen ({file.FileName}) …");
            var link = await _api.GetDownloadLinkAsync(_gameSlug, _modId, file.FileId);
            if (link is null)
            {
                _host.Notifications.Notify(
                    "Nexus verweigert Download-URL — Premium-Status im Nexus-Settings-Tab prüfen.",
                    NotificationLevel.Error);
                return;
            }
            using var http = _host.CreateHttpClient("nexus-download");
            var progress = new Progress<double>(f => scope.Report(f, $"{file.FileName} · {(int)(f * 100)}%"));
            var target = await _installer.DownloadPakAsync(http, link, file.FileName,
                overwrite: false, progress);
            _host.Notifications.Notify($"Heruntergeladen: {Path.GetFileName(target)}",
                NotificationLevel.Success);
            _downloadBus.RaiseDownloadsChanged(Path.GetFileName(target));
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Nexus-Detail-Download fehlgeschlagen für mod_id={Id}", _modId);
            _host.Notifications.Notify($"Download-Fehler: {ex.Message}", NotificationLevel.Error);
        }
        finally { DownloadBusy = false; }
    }

    [RelayCommand]
    private async Task SummarizeAsync()
    {
        if (IsLoading || string.IsNullOrWhiteSpace(Description))
        {
            _host.Notifications.Notify("Bitte warten bis Detail geladen ist.", NotificationLevel.Info);
            return;
        }
        if (!await _host.Ai.IsAvailableAsync())
        {
            _host.Notifications.Notify(
                "KI-Provider nicht erreichbar — bitte in den ModManager-Einstellungen konfigurieren.",
                NotificationLevel.Warning);
            return;
        }
        SummaryBusy = true;
        AiSummary = $"KI-Zusammenfassung via {_host.Ai.ProviderInfo} …";
        try
        {
            var systemPrompt = "Du bist ein deutschsprachiger Icarus-Mod-Reviewer. " +
                "Fasse die Mod-Beschreibung in 3–5 Sätzen zusammen: " +
                "Was macht der Mod? Welche Features/Rezepte/Fahrzeuge/Balance-Änderungen? " +
                "Für welchen Spielstil (Survival, Cheat, QoL)? Sachlich, kein Werbe-Sprech.";
            var userPrompt = $"Titel: {Title}\nAutor: {Author}\n\nBeschreibung:\n{Description}";
            var answer = await _host.Ai.CompleteAsync(systemPrompt, userPrompt);
            AiSummary = string.IsNullOrWhiteSpace(answer)
                ? "KI hat keine Antwort geliefert."
                : answer;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Nexus-Summarize fehlgeschlagen für {Id}", _modId);
            AiSummary = $"Fehler: {ex.Message}";
        }
        finally { SummaryBusy = false; }
    }
}
