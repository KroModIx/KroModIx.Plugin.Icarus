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
using KroModIx.Plugin.Icarus.Services.Archive;
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
            // v1.23.0: kein "*.pak"-Filter mehr — ein Browser-Download
            // ist ein ZIP/RAR/7z, und mit dem alten Filter blieb der
            // Downloads-Tab bei genau dem Normalfall still stehen.
            _watcher = new FileSystemWatcher(DownloadsDir)
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

    /// <summary>Snapshot VOR jedem File-Write (Kernprinzip 6). Fehler
    /// duerfen den Install NIEMALS blockieren — der User will installieren,
    /// nicht den Backup-Service debuggen. Zurueckspielen laeuft ueber das
    /// Backups-Fenster (Sidebar-Kontextmenue), bewusst ohne Auto-Rollback.</summary>
    private async Task TrySnapshotAsync(string label)
    {
        try
        {
            var dirs = new List<string>();
            if (Directory.Exists(_installer.ModsDir)) dirs.Add(_installer.ModsDir);
            // v1.23.0: der UE4SS-Mods-Ordner gehoert dazu, seit ein Archiv
            // auch dorthin schreibt — ein Snapshot, der nur den Pak-Ordner
            // sichert, wuerde beim Zurueckspielen die Lua-Mods vergessen.
            var luaDir = _installer.Lua?.Paths.FindModsDir();
            if (luaDir is not null && Directory.Exists(luaDir)) dirs.Add(luaDir);
            if (dirs.Count == 0) return;
            var gameKey = _installer.ModsDir;
            await _host.Backup.CreateSnapshotAsync(
                pluginId: "kroste.icarus", gameKey: gameKey,
                directories: dirs, label: label);
            await _host.Backup.PruneAsync("kroste.icarus", gameKey, keepLast: 10);
        }
        catch (Exception ex)
        {
            _host.Logger.Warn(ex, "Snapshot fehlgeschlagen (Install laeuft trotzdem): {Label}", label);
        }
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
                ? Strings.T("status.no_downloads")
                : string.Format(Strings.T("status.downloads_summary"), Rows.Count, totalBytes / 1024.0 / 1024.0);

            // Async-Enrichment im Hintergrund: pro Row mit erkannter ModId
            // Nexus-Detail holen + Cover laden. Kein Blocking der UI.
            _ = EnrichRowsAsync(Rows.ToArray());
            // v1.23.0: zusaetzlich in jedes Archiv schauen, damit die Row
            // sagt, was drin ist. Getrennt vom Nexus-Enrichment, weil es
            // keinen API-Key braucht und auch fuer selbst hineinkopierte
            // Dateien funktioniert.
            _ = InspectArchivesAsync(Rows.ToArray());
        }
        catch (Exception ex)
        {
            _host.Logger.Warn(ex, "Icarus: Downloads-Liste konnte nicht geladen werden");
            Summary = Strings.T("status.downloads_load_error");
        }
    }

    /// <summary>Liest bei jeder Archiv-Row die Eintragsliste und schreibt
    /// eine Kurzfassung des Inhalts in die Row („1 PAK · 1 Lua-Mod").
    ///
    /// <para>Läuft off-UI: das Öffnen eines Archivs liest zwar nur das
    /// Inhaltsverzeichnis, bei 7z auf einer drehenden Platte reicht das aber
    /// für eine sichtbare Pause. Geschrieben wird nur auf dem UI-Thread —
    /// sonst sehen die Bindings die Änderung nicht zuverlässig.</para></summary>
    private async Task InspectArchivesAsync(DownloadRow[] rows)
    {
        foreach (var row in rows)
        {
            if (row.Source.Kind != IcarusFileKind.Archive) continue;
            try
            {
                var contents = await Task.Run(() => _installer.Archive.Inspect(row.Source.FilePath));
                var text = DescribeContents(contents);
                await Dispatcher.UIThread.InvokeAsync(() => row.ContentInfo = text);
            }
            catch (Exception ex)
            {
                _host.Logger.Debug(ex, "Archiv-Inhalt nicht lesbar: {File}", row.FileName);
            }
        }
    }

    internal static string DescribeContents(IcarusArchiveContents c)
    {
        if (!c.IsReadable) return Strings.T("row.content.unreadable");
        var parts = new List<string>();
        if (c.PakEntries.Count > 0)
            parts.Add(string.Format(Strings.T("row.content.paks"), c.PakEntries.Count));
        if (c.Ue4ssModNames.Count > 0)
            parts.Add(string.Format(Strings.T("row.content.lua"), c.Ue4ssModNames.Count));
        if (c.ExmodzEntries.Count > 0)
            parts.Add(string.Format(Strings.T("row.content.exmodz"), c.ExmodzEntries.Count));
        return parts.Count == 0 ? Strings.T("row.kind.archive") : string.Join(" · ", parts);
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
            byte[]? bytes = null;
            if (File.Exists(localPath) && new FileInfo(localPath).Length > 0)
            {
                bytes = await File.ReadAllBytesAsync(localPath);
            }
            else
            {
                using var http = _host.CreateHttpClient("nexus-covers");
                bytes = await http.GetByteArrayAsync(pictureUrl);
                if (bytes.Length > 0)
                {
                    Directory.CreateDirectory(_paths.NexusCoverDir);
                    await File.WriteAllBytesAsync(localPath, bytes);
                }
            }
            if (bytes is null || bytes.Length == 0) return;
            // v1.18: Cover-Decode ueber Host-Baukasten.
            var bmp = await _host.Images.DecodeAsync(bytes);
            if (bmp is null)
            {
                _host.Logger.Debug("Downloads-Cover-Decode fehlgeschlagen fuer {Id}", modId);
                return;
            }
            await Dispatcher.UIThread.InvokeAsync(() => row.Cover = bmp);
        }
        catch (Exception ex)
        {
            _host.Logger.Debug(ex, "Downloads-Cover-Load fehlgeschlagen für {Id}", modId);
        }
    }

    [RelayCommand]
    private async Task InstallRowAsync(DownloadRow? row)
    {
        if (row is null) return;
        try
        {
            await TrySnapshotAsync($"Vor Install von {row.Source.FileName}");
            var result = _installer.InstallAny(row.Source.FilePath, overwrite: true);
            if (ModInstallReporter.Report(_host, result, "notify.installed_prefix"))
                _downloadBus.RaiseModInstalled(result.Describe());
            Refresh();
        }
        catch (Exception ex)
        {
            _host.Logger.Warn(ex, "Icarus Install-from-download fehlgeschlagen");
            _host.Notifications.Notify(Strings.T("notify.error_prefix") + ex.Message, NotificationLevel.Error);
        }
    }

    /// <summary>Bulk-Install aller Downloads (Skill Kernprinzip 6a).
    /// overwrite=true damit Updates funktionieren. Fehler pro Row werden
    /// geloggt, der Loop läuft weiter.</summary>
    [RelayCommand]
    private async Task InstallAllAsync()
    {
        var rows = Rows.ToArray();
        if (rows.Length == 0)
        {
            _host.Notifications.Notify(Strings.T("notify.no_downloads_install"), NotificationLevel.Info);
            return;
        }
        // Bulk: EIN Snapshot vor der ganzen Schleife, nicht pro Row — beim
        // Rollback will der User zurueck auf den Stand VOR dem Batch.
        await TrySnapshotAsync($"Vor Bulk-Install ({rows.Length} Archive)");
        using var scope = _host.BeginProgress(string.Format(Strings.T("progress.install_downloads"), rows.Length));
        int done = 0, failed = 0, skipped = 0;
        var touchedExmodz = false;
        for (int i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            scope.Report((double)i / rows.Length,
                string.Format(Strings.T("progress.install_row"), i + 1, rows.Length, row.DisplayName));
            try
            {
                // autoRebuild aus: ein Neubau liest alle betroffenen
                // Basistabellen, das je Datei zu tun waere verschwendete
                // Arbeit. Einmal nach der Schleife genuegt.
                var result = _installer.InstallAny(row.Source.FilePath, overwrite: true,
                    autoRebuild: false);
                if (result.TouchedExmodz) touchedExmodz = true;
                if (!result.InstalledAnything)
                {
                    // Reines .EXMODZ-Archiv: kein Fehler, aber auch kein
                    // Erfolg. Als „uebersprungen" zaehlen, sonst meldet der
                    // Stapel am Ende mehr Installationen als es gab.
                    skipped++;
                    continue;
                }
                _downloadBus.RaiseModInstalled(result.Describe());
                done++;
            }
            catch (Exception ex)
            {
                _host.Logger.Warn(ex, "Icarus Bulk-Install fehlgeschlagen für {File}", row.FileName);
                failed++;
            }
        }
        if (touchedExmodz && _installer.Exmodz is not null)
        {
            scope.Report(1.0, Strings.T("exmodz.rebuilding"));
            var merge = await Task.Run(_installer.Exmodz.Rebuild);
            foreach (var w in merge.Warnings)
                _host.Notifications.Notify(w, NotificationLevel.Warning);
            if (!merge.Ok)
                _host.Notifications.Notify(
                    string.Format(Strings.T("exmodz.rebuild_failed"), merge.Message),
                    NotificationLevel.Error);
        }

        var msg = failed == 0
            ? string.Format(Strings.T("notify.bulk_install_ok"), done)
            : string.Format(Strings.T("notify.bulk_install_partial"), done, failed);
        if (skipped > 0)
            msg += " " + string.Format(Strings.T("notify.bulk_install_skipped"), skipped);
        _host.Notifications.Notify(msg,
            failed == 0 && skipped == 0 ? NotificationLevel.Success : NotificationLevel.Warning);
        Refresh();
    }

    [RelayCommand]
    private async Task DeleteRowAsync(DownloadRow? row)
    {
        if (row is null) return;
        bool ok = await _host.Dialogs.ConfirmAsync(
            Strings.T("dialog.delete_download_title"),
            string.Format(Strings.T("dialog.delete_download_msg"), row.Source.FileName),
            okLabel: Strings.T("dialog.btn.delete"), cancelLabel: Strings.T("dialog.btn.cancel"));
        if (!ok) return;
        try
        {
            _installer.DeleteDownload(row.Source.FilePath);
            _host.Notifications.Notify(Strings.T("notify.deleted_prefix") + row.Source.FileName, NotificationLevel.Success);
            _downloadBus.RaiseDownloadsChanged(row.Source.FileName);
            Refresh();
        }
        catch (Exception ex)
        {
            _host.Notifications.Notify(Strings.T("notify.error_prefix") + ex.Message, NotificationLevel.Error);
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
                Strings.T("notify.nexus_detail_unavailable"),
                NotificationLevel.Warning);
            return;
        }
        if (row.NexusModId is not int modId)
        {
            _host.Notifications.Notify(
                string.Format(Strings.T("notify.no_nexus_id"), row.FileName),
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

    /// <summary>Was in einem Archiv steckt, als kurze Zeile für die Row
    /// („1 PAK · 1 Lua-Mod"). Null bei PAK-Rows und solange die Prüfung
    /// im Hintergrund läuft.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContentInfo))]
    private string? _contentInfo;

    public bool HasContentInfo => !string.IsNullOrWhiteSpace(ContentInfo);

    public bool HasCover => Cover is not null;
    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);

    public string DisplayName => !string.IsNullOrWhiteSpace(ModName) ? ModName! : FileName;

    partial void OnModNameChanged(string? value) => OnPropertyChanged(nameof(DisplayName));
}
