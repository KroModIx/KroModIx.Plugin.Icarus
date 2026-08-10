using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KroModIx.Plugin.Contracts;
using KroModIx.Plugin.Icarus.Services;
using KroModIx.Plugin.Icarus.Services.Nexus;

namespace KroModIx.Plugin.Icarus.Views;

/// <summary>Downloads-Tab: zeigt PAK-Dateien im plugin-eigenen Downloads-
/// Ordner (dort landen Browser-Downloads aus dem Nexus-Tab). Bietet Install-
/// und Delete-Buttons pro Row. Auto-Refresh via
/// <see cref="DownloadEventBus.DownloadsChanged"/> UND
/// <see cref="FileSystemWatcher"/> auf dem Downloads-Ordner (Browser-Downloads
/// kommen von außerhalb des Plugins).</summary>
public sealed partial class DownloadsViewModel : ObservableObject, IDisposable
{
    private readonly PakInstallService _installer;
    private readonly DownloadEventBus _downloadBus;
    private readonly IHostServices _host;
    private readonly NexusApiClient? _nexusApi;
    private readonly NexusSettingsService? _nexusSettings;
    private readonly NexusCategoryService? _nexusCategories;
    private readonly IcarusPaths? _paths;
    private FileSystemWatcher? _watcher;

    /// <summary>Convenience-Ctor für Callsites die noch keinen Nexus-Client
    /// injizieren (Tests, ältere Wirings). Ohne Nexus-Enrichment → nur
    /// FileNames, keine Cover/Details.</summary>
    public DownloadsViewModel(PakInstallService installer, DownloadEventBus downloadBus, IHostServices host)
        : this(installer, downloadBus, host, null, null, null, null) { }

    public DownloadsViewModel(PakInstallService installer, DownloadEventBus downloadBus,
        IHostServices host, NexusApiClient? nexusApi, NexusSettingsService? nexusSettings,
        IcarusPaths? paths, NexusCategoryService? nexusCategories = null)
    {
        _installer = installer;
        _downloadBus = downloadBus;
        _host = host;
        _nexusApi = nexusApi;
        _nexusSettings = nexusSettings;
        _nexusCategories = nexusCategories;
        _paths = paths;
        DownloadsDir = installer.DownloadsDir;
        RefreshCommand.Execute(null);
        SetupWatcher();

        _downloadBus.DownloadsChanged += (_, _) =>
            Dispatcher.UIThread.Post(() => Refresh());
    }

    public string DownloadsDir { get; }

    public ObservableCollection<DownloadRow> Rows { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private DownloadRow? _selected;

    public bool HasSelection => Selected is not null;

    [ObservableProperty] private string _summary = "";

    partial void OnSelectedChanged(DownloadRow? value) => OnPropertyChanged(nameof(HasSelection));

    private void SetupWatcher()
    {
        try
        {
            if (!Directory.Exists(DownloadsDir)) Directory.CreateDirectory(DownloadsDir);
            _watcher = new FileSystemWatcher(DownloadsDir, "*.pak")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Created += (_, _) => ScheduleRefresh();
            _watcher.Deleted += (_, _) => ScheduleRefresh();
            _watcher.Renamed += (_, _) => ScheduleRefresh();
            _host.Logger.Info("Icarus downloads watcher aktiv: {Dir}", DownloadsDir);
        }
        catch (Exception ex)
        {
            _host.Logger.Warn(ex, "Icarus downloads watcher fehlgeschlagen");
        }
    }

    private DateTime _lastRefreshRequest = DateTime.MinValue;
    private bool _refreshPending;
    private void ScheduleRefresh()
    {
        _lastRefreshRequest = DateTime.UtcNow;
        if (_refreshPending) return;
        _refreshPending = true;
        _ = Task.Run(async () =>
        {
            while (DateTime.UtcNow - _lastRefreshRequest < TimeSpan.FromMilliseconds(500))
                await Task.Delay(200);
            _refreshPending = false;
            Dispatcher.UIThread.Post(() => Refresh());
        });
    }

    [RelayCommand]
    private void Refresh()
    {
        Rows.Clear();
        try
        {
            var files = _installer.ListDownloaded()
                .OrderByDescending(d => d.DownloadedUtc).ToList();
            foreach (var d in files)
            {
                var row = new DownloadRow(d);
                // Aus dem Nexus-Filename die Mod-Id extrahieren und schon mal
                // Name aus dem Filename als Fallback setzen — Nexus-Detail-
                // Fetch überschreibt die Werte gleich mit den echten aus der API.
                row.NexusModId = NexusFileNameParser.TryExtractModId(d.FileName);
                row.ModName = NexusFileNameParser.TryExtractModName(d.FileName);
                Rows.Add(row);
            }
            var totalBytes = Rows.Sum(r => r.Source.FileSizeBytes);
            Summary = Rows.Count == 0
                ? "Keine PAK-Dateien im Downloads-Ordner."
                : $"{Rows.Count} PAKs · {totalBytes / 1024.0 / 1024.0:F1} MB gesamt";

            // Async-Enrichment im Hintergrund: pro Row mit erkannter ModId
            // Nexus-Detail holen + Cover laden. Kein Blocking der UI.
            _ = EnrichRowsAsync(Rows.ToArray());
        }
        catch (Exception ex)
        {
            _host.Logger.Warn(ex, "Icarus: Downloads-Liste konnte nicht geladen werden");
            Summary = "Fehler beim Lesen des Downloads-Ordners.";
        }
    }

    /// <summary>Iteriert über die Rows mit erkannter <see cref="DownloadRow.NexusModId"/>,
    /// holt Detail via Nexus-API + Cover-Bild. Throttled: 250ms zwischen
    /// Detail-Requests damit wir bei 20+ Rows nicht die Rate-Limit-Wand
    /// treffen. Ohne Nexus-Client (Convenience-Ctor) macht die Methode nichts.</summary>
    private async Task EnrichRowsAsync(DownloadRow[] rows)
    {
        if (_nexusApi is null || _nexusSettings is null || _paths is null) return;
        if (!_nexusSettings.HasApiKey) return;

        var slug = _nexusSettings.Current.GameSlug;
        foreach (var row in rows)
        {
            if (row.NexusModId is not int modId) continue;
            try
            {
                var detail = await _nexusApi.GetModDetailAsync(slug, modId);
                if (detail is null) continue;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    row.ModName = detail.Name;
                    row.Author = detail.Author;
                    row.Summary = detail.Summary;
                    row.Version = detail.Version;
                });
                if (!string.IsNullOrEmpty(detail.PictureUrl))
                    await LoadCoverAsync(row, detail.PictureUrl, modId);
            }
            catch (Exception ex)
            {
                _host.Logger.Debug(ex, "Downloads-Enrichment fehlgeschlagen für mod_id={Id}", modId);
            }
            // Throttle
            try { await Task.Delay(250); } catch { break; }
        }
    }

    private async Task LoadCoverAsync(DownloadRow row, string pictureUrl, int modId)
    {
        if (_paths is null) return;
        try
        {
            var localPath = Path.Combine(_paths.NexusCoverDir, $"{modId}.jpg");
            if (!File.Exists(localPath))
            {
                using var http = _host.CreateHttpClient("nexus-covers");
                var bytes = await http.GetByteArrayAsync(pictureUrl);
                Directory.CreateDirectory(_paths.NexusCoverDir);
                await File.WriteAllBytesAsync(localPath, bytes);
            }
            var bmp = await Task.Run(() =>
            {
                using var s = File.OpenRead(localPath);
                return new Bitmap(s);
            });
            await Dispatcher.UIThread.InvokeAsync(() => row.Cover = bmp);
        }
        catch (Exception ex)
        {
            _host.Logger.Debug(ex, "Downloads-Cover-Load fehlgeschlagen für {Id}", modId);
        }
    }

    [RelayCommand]
    private void InstallRow(DownloadRow? row)
    {
        if (row is null) return;
        try
        {
            var installed = _installer.Install(row.Source.FilePath, overwrite: true);
            _host.Notifications.Notify($"Installiert: {installed.FileName}", NotificationLevel.Success);
            _downloadBus.RaiseModInstalled(installed.FileName);
            Refresh();
        }
        catch (Exception ex)
        {
            _host.Logger.Warn(ex, "Icarus Install-from-download fehlgeschlagen");
            _host.Notifications.Notify($"Fehler: {ex.Message}", NotificationLevel.Error);
        }
    }

    /// <summary>Bulk-Install aller Downloads (Skill Kernprinzip 6a).
    /// overwrite=true damit Updates funktionieren. Fehler pro Row werden
    /// geloggt, der Loop läuft weiter.</summary>
    [RelayCommand]
    private void InstallAll()
    {
        var rows = Rows.ToArray();
        if (rows.Length == 0)
        {
            _host.Notifications.Notify("Keine Downloads zu installieren.", NotificationLevel.Info);
            return;
        }
        using var scope = _host.BeginProgress($"Installiere {rows.Length} PAK-Downloads …");
        int done = 0, failed = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            scope.Report((double)i / rows.Length, $"Installiere {i + 1}/{rows.Length}: {row.DisplayName}");
            try
            {
                var installed = _installer.Install(row.Source.FilePath, overwrite: true);
                _downloadBus.RaiseModInstalled(installed.FileName);
                done++;
            }
            catch (Exception ex)
            {
                _host.Logger.Warn(ex, "Icarus Bulk-Install fehlgeschlagen für {File}", row.FileName);
                failed++;
            }
        }
        var msg = failed == 0
            ? $"{done} PAKs installiert."
            : $"{done} installiert, {failed} Fehler (siehe Log).";
        _host.Notifications.Notify(msg,
            failed == 0 ? NotificationLevel.Success : NotificationLevel.Warning);
        Refresh();
    }

    [RelayCommand]
    private async Task DeleteRowAsync(DownloadRow? row)
    {
        if (row is null) return;
        bool ok = await _host.Dialogs.ConfirmAsync(
            "Download löschen",
            $"„{row.Source.FileName}“ aus dem Downloads-Ordner löschen?",
            okLabel: "Löschen", cancelLabel: "Abbrechen");
        if (!ok) return;
        try
        {
            _installer.DeleteDownload(row.Source.FilePath);
            _host.Notifications.Notify($"Gelöscht: {row.Source.FileName}", NotificationLevel.Success);
            _downloadBus.RaiseDownloadsChanged(row.Source.FileName);
            Refresh();
        }
        catch (Exception ex)
        {
            _host.Notifications.Notify($"Fehler: {ex.Message}", NotificationLevel.Error);
        }
    }

    [RelayCommand]
    private void OpenDownloadsFolder() => _host.Shell.OpenDirectory(DownloadsDir);

    /// <summary>Öffnet den Nexus-Mod-Detail-Dialog für die Row. Nur möglich
    /// wenn der Filename dem Nexus-Muster entspricht (<see cref="DownloadRow.NexusModId"/>
    /// != null) UND die Nexus-Dependencies gewired sind. Der Dialog kriegt
    /// die schon vorhandenen Row-Werte als Initial-Anzeige und lädt das
    /// Full-Detail parallel nach.</summary>
    [RelayCommand]
    private void ShowDetail(DownloadRow? row)
    {
        if (row is null) return;
        if (_nexusApi is null || _nexusSettings is null || _nexusCategories is null || _paths is null)
        {
            _host.Notifications.Notify(
                "Nexus-Detail nicht verfügbar (Nexus-Client fehlt in dieser Session).",
                NotificationLevel.Warning);
            return;
        }
        if (row.NexusModId is not int modId)
        {
            _host.Notifications.Notify(
                $"Keine Nexus-Mod-Id im Dateinamen erkennbar: {row.FileName}",
                NotificationLevel.Info);
            return;
        }

        var vm = new NexusModDetailViewModel(
            modId,
            _nexusSettings.Current.GameSlug,
            _nexusSettings.Current.IsPremium,
            _nexusApi, _nexusCategories, _installer, _downloadBus, _host,
            initialTitle: row.ModName ?? row.FileName,
            initialAuthor: row.Author,
            initialSummary: row.Summary,
            initialVersion: row.Version,
            initialUpdated: row.DownloadedText,
            initialCover: row.Cover);
        var window = new NexusModDetailWindow { DataContext = vm };
        var owner = (Avalonia.Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is not null) window.Show(owner); else window.Show();
    }

    public void Dispose() => _watcher?.Dispose();
}

public sealed partial class DownloadRow : ObservableObject
{
    public DownloadedPak Source { get; }
    public DownloadRow(DownloadedPak s) => Source = s;

    public string FileName => Source.FileName;
    public string Size => Source.FileSizeBytes < 1024 * 1024
        ? $"{Source.FileSizeBytes / 1024.0:F0} KB"
        : $"{Source.FileSizeBytes / 1024.0 / 1024.0:F1} MB";
    public string DownloadedText => Source.DownloadedUtc.ToLocalTime().ToString("g");

    /// <summary>Aus dem Filename extrahiert (siehe <see cref="NexusFileNameParser"/>).
    /// null wenn der Filename nicht dem Nexus-Muster entspricht (z.B. Datei
    /// die der User selbst reingelegt hat).</summary>
    public int? NexusModId { get; set; }

    /// <summary>Mod-Metadaten vom Nexus-Detail-Fetch (async nach dem
    /// Refresh gefüllt). Initial aus dem Filename abgeleitet als Fallback.</summary>
    [ObservableProperty] private string? _modName;
    [ObservableProperty] private string? _author;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    private string? _summary;

    [ObservableProperty] private string? _version;

    /// <summary>Cover-Bild aus dem Nexus-CDN (via <c>NexusCoverDir</c>-Cache).
    /// null wenn kein Bild vorhanden oder Load fehlgeschlagen — die View
    /// zeigt dann einen Emoji-Platzhalter.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCover))]
    private Bitmap? _cover;

    public bool HasCover => Cover is not null;
    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);

    public string DisplayName => !string.IsNullOrWhiteSpace(ModName) ? ModName! : FileName;

    partial void OnModNameChanged(string? value) => OnPropertyChanged(nameof(DisplayName));
}
