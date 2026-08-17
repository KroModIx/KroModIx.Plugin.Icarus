using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KroModIx.Plugin.Contracts;
using KroModIx.Plugin.Icarus.Services;
using KroModIx.Plugin.Icarus.Services.Nexus;
using NLog;

namespace KroModIx.Plugin.Icarus.Views;

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
        : this(row.Source.ModId, gameSlug, isPremium, api, categories, installer,
               downloadBus, host,
               initialTitle: row.Name,
               initialAuthor: row.Author,
               initialSummary: row.Source.Summary,
               initialVersion: row.VersionDisplay,
               initialEndorsements: row.EndorsementsText,
               initialUpdated: row.UpdatedText,
               initialCover: row.Cover)
    { }

    /// <summary>Vollständiger Constructor mit expliziten Vorbelegungs-Werten —
    /// vom Downloads-Tab genutzt, wo die Row-Struktur eine andere ist als
    /// <see cref="NexusRow"/>. Die Vorbelegung wird sofort im Dialog gezeigt
    /// während <see cref="LoadDetailAsync"/> das Full-Detail async nachlädt.</summary>
    public NexusModDetailViewModel(int modId, string gameSlug, bool isPremium,
        NexusApiClient api, NexusCategoryService categories,
        PakInstallService installer, DownloadEventBus downloadBus,
        IHostServices host,
        string? initialTitle = null,
        string? initialAuthor = null,
        string? initialSummary = null,
        string? initialVersion = null,
        string? initialEndorsements = null,
        string? initialUpdated = null,
        Bitmap? initialCover = null)
    {
        _modId = modId;
        _gameSlug = gameSlug;
        _detailUrl = $"https://www.nexusmods.com/{gameSlug}/mods/{modId}";
        _api = api;
        _categories = categories;
        _installer = installer;
        _downloadBus = downloadBus;
        IsPremium = isPremium;
        _host = host;

        Title = initialTitle ?? "";
        Author = initialAuthor ?? "";
        Summary = initialSummary ?? "";
        Version = initialVersion ?? "";
        EndorsementsText = initialEndorsements ?? "";
        UpdatedText = initialUpdated ?? "";
        Cover = initialCover;
        Description = Strings.T("detail.desc_placeholder");

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
    // v1.19.1: Rich-HTML-View statt Plain-Text-TextBlock. Wird vom
    // Descriptions-Baukasten (Host v1.21+) erzeugt und im Detail-Window
    // per ContentControl.Content angezeigt. Plain-Text-Version bleibt in
    // Description fuer AI-Prompts + Loading-Placeholder.
    [ObservableProperty] private Control? _descriptionView;
    [ObservableProperty] private string _statusText = Strings.T("detail.status.loading");
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
                Description = Strings.T("detail.desc_load_error");
                StatusText = Strings.T("detail.status.load_error");
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

            var html = detail.DescriptionHtml ?? "";
            if (string.IsNullOrWhiteSpace(html))
            {
                Description = string.IsNullOrWhiteSpace(detail.Summary)
                    ? Strings.T("detail.desc_no_content")
                    : detail.Summary;
                DescriptionView = null;
            }
            else
            {
                // Plain-Text bleibt fuer AI-Prompts (der Prompt braucht keinen
                // HTML-Ballast). Rich-View fuer die UI-Anzeige.
                Description = _host.Descriptions.ToPlainText(html);
                if (string.IsNullOrWhiteSpace(Description))
                    Description = string.IsNullOrWhiteSpace(detail.Summary)
                        ? Strings.T("detail.desc_no_content")
                        : detail.Summary;
                var richHtml = _host.Descriptions.ToHtml(html);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    DescriptionView = _host.Descriptions.CreateRichView(richHtml);
                });
            }

            Category = await _categories.GetCategoryNameAsync(detail.CategoryId);
            StatusText = $"v{detail.Version} · {(detail.Available ? Strings.T("detail.status.available") : Strings.T("detail.status.unavailable"))}";
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Nexus-Detail-Load fehlgeschlagen für mod_id={Id}", _modId);
            Description = Strings.T("detail.error_prefix") + ex.Message;
            StatusText = Strings.T("detail.status.load_error");
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
                Strings.T("notify.premium_required_detail"),
                NotificationLevel.Warning);
            return;
        }
        DownloadBusy = true;
        using var scope = _host.BeginProgress(string.Format(Strings.T("progress.nexus_scope"), Title));
        scope.Report(0, Strings.T("progress.update_files_load"));
        try
        {
            var files = await _api.GetFilesAsync(_gameSlug, _modId);
            var file = NexusViewModel.PickMainFile(files);
            if (file is null)
            {
                _host.Notifications.Notify(Strings.T("notify.no_main_file_generic"), NotificationLevel.Warning);
                return;
            }
            scope.Report(0, string.Format(Strings.T("progress.update_url"), file.FileName));
            var link = await _api.GetDownloadLinkAsync(_gameSlug, _modId, file.FileId);
            if (link is null)
            {
                _host.Notifications.Notify(
                    Strings.T("notify.nexus_deny_url_settings"),
                    NotificationLevel.Error);
                return;
            }
            using var http = _host.CreateHttpClient("nexus-download");
            var progress = new Progress<double>(f =>
                scope.Report(f, string.Format(Strings.T("progress.download_percent"), file.FileName, (int)(f * 100))));
            var target = await _installer.DownloadPakAsync(http, link, file.FileName,
                overwrite: false, progress);
            _host.Notifications.Notify(Strings.T("notify.download_ok_prefix") + Path.GetFileName(target),
                NotificationLevel.Success);
            _downloadBus.RaiseDownloadsChanged(Path.GetFileName(target));
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Nexus-Detail-Download fehlgeschlagen für mod_id={Id}", _modId);
            _host.Notifications.Notify(Strings.T("notify.download_error_prefix") + ex.Message, NotificationLevel.Error);
        }
        finally { DownloadBusy = false; }
    }

    [RelayCommand]
    private async Task SummarizeAsync()
    {
        if (IsLoading || string.IsNullOrWhiteSpace(Description))
        {
            _host.Notifications.Notify(Strings.T("notify.detail_wait"), NotificationLevel.Info);
            return;
        }
        if (!await _host.Ai.IsAvailableAsync())
        {
            _host.Notifications.Notify(
                Strings.T("notify.ai_unavailable"),
                NotificationLevel.Warning);
            return;
        }
        SummaryBusy = true;
        AiSummary = string.Format(Strings.T("detail.ai.starting"), _host.Ai.ProviderInfo);
        try
        {
            var systemPrompt = Strings.T("ai.prompt.summary_system");
            var userPrompt = $"Titel: {Title}\nAutor: {Author}\n\nBeschreibung:\n{Description}";
            var answer = await _host.Ai.CompleteAsync(systemPrompt, userPrompt);
            AiSummary = string.IsNullOrWhiteSpace(answer)
                ? Strings.T("detail.ai.no_answer")
                : answer;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Nexus-Summarize fehlgeschlagen für {Id}", _modId);
            AiSummary = Strings.T("detail.error_prefix") + ex.Message;
        }
        finally { SummaryBusy = false; }
    }
}
