using System;
using System.Collections.Generic;
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

/// <summary>VM für den „Installiert"-Tab. Zeigt sowohl manuelle Mods (im
/// Content/Paks/mods-Ordner) als auch Steam-Workshop-Abos. Workshop-Rows
/// sind read-only (Toggle/Uninstall disabled). Auto-Refresh via
/// <see cref="FileSystemWatcher"/> auf beiden Ordnern +
/// <see cref="DownloadEventBus.ModInstalled"/>.</summary>
public sealed partial class InstalledPaksViewModel : ObservableObject, IDisposable
{
    private readonly PakInstallService _installer;
    private readonly PakBackupService _backup;
    private readonly IcarusPaths _paths;
    private readonly DownloadEventBus _downloadBus;
    private readonly IHostServices _host;
    private readonly NexusApiClient? _nexusApi;
    private readonly NexusSettingsService? _nexusSettings;
    private readonly NexusCategoryService? _nexusCategories;
    private readonly InstalledUpdatesChecker? _updatesChecker;

    private readonly List<PakRow> _allMods = new();
    private FileSystemWatcher? _manualWatcher;
    private FileSystemWatcher? _workshopWatcher;

    /// <summary>Convenience-Ctor für Callsites die noch keine Nexus-Deps
    /// injizieren (Tests, ältere Wirings). Ohne Nexus → nur Filenames,
    /// keine Cover/Details.</summary>
    public InstalledPaksViewModel(PakInstallService installer, PakBackupService backup,
        IcarusPaths paths, DownloadEventBus downloadBus, IHostServices host)
        : this(installer, backup, paths, downloadBus, host, null, null, null, null) { }

    public InstalledPaksViewModel(PakInstallService installer, PakBackupService backup,
        IcarusPaths paths, DownloadEventBus downloadBus, IHostServices host,
        NexusApiClient? nexusApi, NexusSettingsService? nexusSettings,
        NexusCategoryService? nexusCategories,
        InstalledUpdatesChecker? updatesChecker = null)
    {
        _installer = installer;
        _backup = backup;
        _paths = paths;
        _downloadBus = downloadBus;
        _host = host;
        _nexusApi = nexusApi;
        _nexusSettings = nexusSettings;
        _nexusCategories = nexusCategories;
        _updatesChecker = updatesChecker;
        ModsDir = installer.ModsDir;
        WorkshopDir = installer.WorkshopDir ?? "(kein Workshop-Ordner erkannt)";
        InitEvents();
        SetupWatchers();
        RefreshCommand.Execute(null);

        _downloadBus.ModInstalled += (_, _) =>
            Dispatcher.UIThread.Post(() => Refresh());
    }

    public string ModsDir { get; }
    public string WorkshopDir { get; }

    public ObservableCollection<PakRow> Mods { get; } = new();
    public ObservableCollection<PakRow> SelectedRows { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(SelectedCountLabel))]
    private PakRow? _selected;

    public bool HasSelection => Selected is not null;
    public bool HasMultiSelection => SelectedRows.Count > 1;
    public string SelectedCountLabel =>
        SelectedRows.Count > 1 ? $"{SelectedRows.Count} ausgewählt" : "";

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _searchText = "";

    /// <summary>Filter: nur Manual, nur Workshop, oder beide.</summary>
    [ObservableProperty] private bool _showManual = true;
    [ObservableProperty] private bool _showWorkshop = true;

    partial void OnSelectedChanged(PakRow? value) => OnPropertyChanged(nameof(HasSelection));
    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnShowManualChanged(bool value) => ApplyFilter();
    partial void OnShowWorkshopChanged(bool value) => ApplyFilter();

    private void InitEvents()
    {
        SelectedRows.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasMultiSelection));
            OnPropertyChanged(nameof(SelectedCountLabel));
        };
    }

    /// <summary>Setzt FileSystemWatcher auf beide Mod-Ordner auf, damit
    /// externe Änderungen (Steam-Workshop-Update, manueller File-Copy
    /// außerhalb des Plugins) automatisch in der UI ankommen. Debounced
    /// via <see cref="_pendingRefresh"/> — Steam schreibt oft mehrere
    /// Files pro Sekunde.</summary>
    private void SetupWatchers()
    {
        _manualWatcher = TryCreateWatcher(_installer.ModsDir);
        _workshopWatcher = TryCreateWatcher(_installer.WorkshopDir);
    }

    private FileSystemWatcher? TryCreateWatcher(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
        try
        {
            var w = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            w.Created += (_, _) => ScheduleRefresh();
            w.Deleted += (_, _) => ScheduleRefresh();
            w.Renamed += (_, _) => ScheduleRefresh();
            _host.Logger.Info("Icarus watcher aktiv auf: {Dir}", dir);
            return w;
        }
        catch (Exception ex)
        {
            _host.Logger.Warn(ex, "Icarus watcher fehlgeschlagen für {Dir}", dir);
            return null;
        }
    }

    private DateTime _lastRefreshRequest = DateTime.MinValue;
    private bool _refreshPending;
    private void ScheduleRefresh()
    {
        // Debounce 500ms — Steam-Workshop-Downloads schreiben burst-artig.
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
        _allMods.Clear();
        try
        {
            foreach (var m in _installer.ListInstalled()
                         .OrderBy(m => m.Source == PakModSource.Workshop) // Manual zuerst
                         .ThenByDescending(m => m.IsEnabled)
                         .ThenBy(m => m.FileName, StringComparer.CurrentCultureIgnoreCase))
            {
                var row = new PakRow(m);
                // Nur bei Manual-Rows den Nexus-Filename-Parser probieren —
                // Workshop-Rows haben Steam-UGC-Naming und passen nie.
                if (m.Source == PakModSource.Manual)
                {
                    row.NexusModId = NexusFileNameParser.TryExtractModId(m.FileName);
                    row.ModName = NexusFileNameParser.TryExtractModName(m.FileName);
                }
                _allMods.Add(row);
            }

            var manualCount = _allMods.Count(r => r.IsManual);
            var workshopCount = _allMods.Count(r => r.IsWorkshop);
            var enabled = _allMods.Count(r => r.IsEnabled);
            Summary = _allMods.Count == 0
                ? "Keine Mods gefunden."
                : $"{enabled} aktiv · {manualCount} manuell · {workshopCount} Workshop";
        }
        catch (Exception ex)
        {
            _host.Logger.Warn(ex, "Icarus: Mod-Liste konnte nicht geladen werden");
            Summary = "Fehler beim Lesen der Mod-Ordner.";
        }
        ApplyFilter();

        // Async-Enrichment im Hintergrund für Manual-Rows mit Nexus-Filename.
        _ = EnrichRowsAsync(_allMods.Where(r => r.NexusModId is int).ToArray());
    }

    /// <summary>Iteriert über die Rows mit erkannter <see cref="PakRow.NexusModId"/>,
    /// holt Detail via Nexus-API + Cover-Bild. Throttled: 250ms zwischen
    /// Detail-Requests. Ohne Nexus-Client (Convenience-Ctor) macht die Methode nichts.</summary>
    private async Task EnrichRowsAsync(PakRow[] rows)
    {
        if (_nexusApi is null || _nexusSettings is null) return;
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
                _host.Logger.Debug(ex, "Installed-Enrichment fehlgeschlagen für mod_id={Id}", modId);
            }
            try { await Task.Delay(250); } catch { break; }
        }
    }

    private async Task LoadCoverAsync(PakRow row, string pictureUrl, int modId)
    {
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
            _host.Logger.Debug(ex, "Installed-Cover-Load fehlgeschlagen für {Id}", modId);
        }
    }

    /// <summary>Öffnet den Nexus-Mod-Detail-Dialog für die Row. Nur möglich
    /// wenn die Row aus einem Nexus-Download stammt (Filename-Muster matcht)
    /// UND die Nexus-Deps gewired sind.</summary>
    [RelayCommand]
    private void ShowDetail(PakRow? row)
    {
        if (row is null) return;
        if (_nexusApi is null || _nexusSettings is null || _nexusCategories is null)
        {
            _host.Notifications.Notify(
                "Nexus-Detail nicht verfügbar (Nexus-Client fehlt in dieser Session).",
                NotificationLevel.Warning);
            return;
        }
        if (row.NexusModId is not int modId)
        {
            _host.Notifications.Notify(
                row.IsWorkshop
                    ? "Workshop-Mods haben keine Nexus-Details."
                    : $"Keine Nexus-Mod-Id im Dateinamen erkennbar: {row.FileName}",
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
            initialCover: row.Cover);
        var window = new NexusModDetailWindow { DataContext = vm };
        var owner = (Avalonia.Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is not null) window.Show(owner); else window.Show();
    }

    private void ApplyFilter()
    {
        var q = SearchText?.Trim() ?? "";
        Mods.Clear();
        foreach (var row in _allMods)
        {
            if (row.IsManual && !ShowManual) continue;
            if (row.IsWorkshop && !ShowWorkshop) continue;
            if (q.Length > 0 && !row.FileName.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            Mods.Add(row);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ToggleEnabled() => ToggleEnabledRow(Selected);

    [RelayCommand]
    private void ToggleEnabledRow(PakRow? row)
    {
        if (row is null) return;
        try
        {
            var updated = _installer.SetEnabled(row.Source, !row.Source.IsEnabled);
            _host.Notifications.Notify(
                $"Mod {(updated.IsEnabled ? "aktiviert" : "deaktiviert")}: {updated.FileName}",
                NotificationLevel.Success);
            Refresh();
        }
        catch (Exception ex)
        {
            _host.Logger.Warn(ex, "Icarus: Toggle fehlgeschlagen");
            _host.Notifications.Notify($"Fehler: {ex.Message}", NotificationLevel.Error);
        }
    }

    [RelayCommand]
    private void ToggleEnabledBulk()
    {
        if (SelectedRows.Count == 0) return;
        var rows = SelectedRows.Where(r => r.Source.Source == PakModSource.Manual).ToList();
        if (rows.Count == 0)
        {
            _host.Notifications.Notify("Nur Workshop-Mods ausgewählt — die kann Steam nur.",
                NotificationLevel.Warning);
            return;
        }
        bool allEnabled = rows.All(r => r.Source.IsEnabled);
        bool target = !allEnabled;
        int done = 0;
        foreach (var r in rows)
        {
            try
            {
                if (r.Source.IsEnabled != target)
                    _installer.SetEnabled(r.Source, target);
                done++;
            }
            catch (Exception ex) { _host.Logger.Warn(ex, "Bulk-Toggle für {F}", r.FileName); }
        }
        _host.Notifications.Notify(
            $"{done} Mod(s) {(target ? "aktiviert" : "deaktiviert")}.",
            NotificationLevel.Success);
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task UninstallAsync() => await UninstallRowAsync(Selected);

    [RelayCommand]
    private async Task UninstallRowAsync(PakRow? row)
    {
        if (row is null) return;
        if (row.Source.Source == PakModSource.Workshop)
        {
            _host.Notifications.Notify(
                "Workshop-Mod: Abo in Steam kündigen, dann verschwindet er hier automatisch.",
                NotificationLevel.Info);
            return;
        }
        bool ok = await _host.Dialogs.ConfirmAsync(
            "PAK-Mod deinstallieren",
            $"„{row.Source.FileName}“ wirklich löschen?",
            okLabel: "Löschen", cancelLabel: "Abbrechen");
        if (!ok) return;
        try
        {
            _installer.Uninstall(row.Source);
            _host.Notifications.Notify($"Deinstalliert: {row.Source.FileName}",
                NotificationLevel.Success);
            Refresh();
        }
        catch (Exception ex)
        {
            _host.Notifications.Notify($"Fehler: {ex.Message}", NotificationLevel.Error);
        }
    }

    [RelayCommand]
    private async Task UninstallBulkAsync()
    {
        var rows = SelectedRows.Where(r => r.Source.Source == PakModSource.Manual).ToList();
        if (rows.Count == 0) return;
        bool ok = await _host.Dialogs.ConfirmAsync(
            "Mods deinstallieren",
            $"{rows.Count} manuelle Mod(s) wirklich löschen?\n\n" +
            string.Join("\n", rows.Take(10).Select(r => "• " + r.FileName)) +
            (rows.Count > 10 ? $"\n… und {rows.Count - 10} weitere" : ""),
            okLabel: "Löschen", cancelLabel: "Abbrechen");
        if (!ok) return;
        int done = 0;
        foreach (var r in rows)
        {
            try { _installer.Uninstall(r.Source); done++; }
            catch (Exception ex) { _host.Logger.Warn(ex, "Bulk-Uninstall für {F}", r.FileName); }
        }
        _host.Notifications.Notify($"{done} Mod(s) deinstalliert.", NotificationLevel.Success);
        Refresh();
    }

    [RelayCommand]
    private async Task InstallFromFileAsync()
    {
        var picked = await _host.Dialogs.PickFileAsync(
            "PAK-Mod wählen",
            ("Icarus PAK-Mod (.pak)", new[] { "*.pak" }));
        if (picked is null) return;
        try
        {
            var installed = _installer.Install(picked, overwrite: false);
            _host.Notifications.Notify($"Installiert: {installed.FileName}",
                NotificationLevel.Success);
            _downloadBus.RaiseModInstalled(installed.FileName);
            Refresh();
        }
        catch (Exception ex)
        {
            _host.Logger.Warn(ex, "Icarus: Install fehlgeschlagen");
            _host.Notifications.Notify($"Fehler: {ex.Message}", NotificationLevel.Error);
        }
    }

    public void InstallDroppedPak(string pakPath)
    {
        try
        {
            var installed = _installer.Install(pakPath, overwrite: false);
            _host.Notifications.Notify($"Installiert (Drop): {installed.FileName}",
                NotificationLevel.Success);
            _downloadBus.RaiseModInstalled(installed.FileName);
        }
        catch (Exception ex)
        {
            _host.Logger.Warn(ex, "Icarus: Drop-Install fehlgeschlagen");
            _host.Notifications.Notify(
                $"Drop-Install fehlgeschlagen ({Path.GetFileName(pakPath)}): {ex.Message}",
                NotificationLevel.Error);
        }
    }

    [RelayCommand]
    private void OpenModsFolder() => _host.Shell.OpenDirectory(ModsDir);

    [RelayCommand]
    private void OpenWorkshopFolder()
    {
        if (_installer.WorkshopDir is null || !Directory.Exists(_installer.WorkshopDir))
        {
            _host.Notifications.Notify(
                "Kein Workshop-Ordner — noch keine Workshop-Mods abonniert.",
                NotificationLevel.Info);
            return;
        }
        _host.Shell.OpenDirectory(_installer.WorkshopDir);
    }

    [RelayCommand]
    private async Task CreateBackupAsync()
    {
        var manualCount = _allMods.Count(r => r.Source.Source == PakModSource.Manual);
        if (manualCount == 0)
        {
            _host.Notifications.Notify(
                "Keine manuellen Mods zum Sichern (Workshop-Mods sichert Steam).",
                NotificationLevel.Warning);
            return;
        }
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        var target = Path.Combine(_paths.BackupsDir, $"icarus-backup-{timestamp}.zip");
        using var scope = _host.BeginProgress("Backup erstellen …");
        var progress = new Progress<BackupProgress>(p =>
            scope.Report(p.Fraction, $"{p.Current}/{p.Total} · {p.CurrentFileName}"));
        try
        {
            var result = await _backup.CreateBackupAsync(target, progress);
            _host.Notifications.Notify(
                $"Backup: {result.ModCount} Mods · {FormatBytes(result.FileSizeBytes)} → {Path.GetFileName(result.FilePath)}",
                NotificationLevel.Success);
            _host.Shell.OpenDirectory(_paths.BackupsDir);
        }
        catch (Exception ex)
        {
            _host.Logger.Warn(ex, "Icarus: Backup fehlgeschlagen");
            _host.Notifications.Notify($"Backup-Fehler: {ex.Message}", NotificationLevel.Error);
        }
    }

    [RelayCommand]
    private async Task RestoreBackupAsync()
    {
        var picked = await _host.Dialogs.PickFileAsync(
            "Backup-ZIP wählen",
            ("Icarus-Backup (.zip)", new[] { "*.zip" }));
        if (picked is null) return;

        BackupManifest manifest;
        try { manifest = PakBackupService.ReadManifest(picked); }
        catch (Exception ex)
        {
            _host.Notifications.Notify($"Backup ungültig: {ex.Message}", NotificationLevel.Error);
            return;
        }

        bool ok = await _host.Dialogs.ConfirmAsync(
            "Backup wiederherstellen",
            $"Backup vom {manifest.CreatedUtc.ToLocalTime():g} · {manifest.Mods.Count} Mods.\n" +
            "Vorhandene PAKs mit gleichem Namen werden überschrieben.\nFortfahren?",
            okLabel: "Wiederherstellen", cancelLabel: "Abbrechen");
        if (!ok) return;

        using var scope = _host.BeginProgress("Backup wiederherstellen …");
        var progress = new Progress<BackupProgress>(p =>
            scope.Report(p.Fraction, $"{p.Current}/{p.Total} · {p.CurrentFileName}"));
        try
        {
            var result = await _backup.RestoreBackupAsync(picked, progress);
            _host.Notifications.Notify(
                $"Restore: {result.RestoredCount} wiederhergestellt, {result.SkippedCount} übersprungen.",
                NotificationLevel.Success);
            _downloadBus.RaiseModInstalled("(restore)");
            Refresh();
        }
        catch (Exception ex)
        {
            _host.Logger.Warn(ex, "Icarus: Restore fehlgeschlagen");
            _host.Notifications.Notify($"Restore-Fehler: {ex.Message}", NotificationLevel.Error);
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:F1} KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return $"{mb:F1} MB";
        double gb = mb / 1024.0;
        return $"{gb:F2} GB";
    }

    [ObservableProperty] private bool _isCheckingUpdates;

    /// <summary>Delegiert an <see cref="InstalledUpdatesChecker"/>. Der schreibt
    /// nach dem Run automatisch in den <see cref="InstalledUpdatesTracker"/>
    /// (persistiert für Sidebar-Kachel-Badge). Der <c>onUpdateFound</c>-
    /// Callback setzt die Row-Update-Badges + zeigt Update-Button.</summary>
    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        if (IsCheckingUpdates) return;
        if (_updatesChecker is null || _nexusSettings is null)
        {
            _host.Notifications.Notify("Nexus-API nicht verfügbar.", NotificationLevel.Warning);
            return;
        }
        if (!_nexusSettings.HasApiKey)
        {
            _host.Notifications.Notify(
                "Kein Nexus-API-Key konfiguriert — Updates prüfen im Nexus-Settings-Tab.",
                NotificationLevel.Warning);
            return;
        }

        IsCheckingUpdates = true;
        try
        {
            var updated = await _updatesChecker.CheckAsync(
                onUpdateFound: (modId, oldVer, newVer) =>
                {
                    var row = _allMods.FirstOrDefault(r => r.NexusModId == modId);
                    if (row is not null)
                        Dispatcher.UIThread.Post(() => row.SetUpdateAvailable(newVer));
                },
                onProgress: msg => Summary = msg);
            Summary = updated > 0
                ? $"Updates gefunden: {updated} Mod(s)."
                : "Keine Updates.";
            _host.Notifications.Notify(Summary,
                updated > 0 ? NotificationLevel.Success : NotificationLevel.Info);
        }
        finally { IsCheckingUpdates = false; }
    }

    /// <summary>Führt Update aus: neue PAK-Version downloaden (Premium-
    /// Direct-URL), alte PAK deinstallieren, neue installieren. Enabled-
    /// State wird übertragen. Braucht Nexus-Premium — Free-User bekommen
    /// Toast mit Hinweis auf Browser-Weg.</summary>
    [RelayCommand]
    private async Task UpdateModAsync(PakRow? row)
    {
        if (row is null || !row.HasUpdate) return;
        if (row.NexusModId is not int modId)
        {
            _host.Notifications.Notify("Keine Nexus-Mod-Id — Update nicht auflösbar.",
                NotificationLevel.Warning);
            return;
        }
        if (_nexusApi is null || _nexusSettings is null)
        {
            _host.Notifications.Notify("Nexus-API nicht verfügbar.",
                NotificationLevel.Warning);
            return;
        }
        if (!_nexusSettings.Current.IsPremium)
        {
            _host.Notifications.Notify(
                "Update braucht Nexus-Premium für Direct-Download. Browser-Weg via Nexus-Katalog.",
                NotificationLevel.Warning);
            return;
        }

        var slug = _nexusSettings.Current.GameSlug;
        using var scope = _host.BeginProgress($"Update: {row.DisplayName}");
        scope.Report(0, "Datei-Liste laden …");
        try
        {
            var files = await _nexusApi.GetFilesAsync(slug, modId);
            var file = NexusViewModel.PickMainFile(files);
            if (file is null)
            {
                _host.Notifications.Notify("Keine Main-Datei bei Nexus gefunden.",
                    NotificationLevel.Warning);
                return;
            }

            scope.Report(0, $"Download-URL holen ({file.FileName}) …");
            var link = await _nexusApi.GetDownloadLinkAsync(slug, modId, file.FileId);
            if (link is null)
            {
                _host.Notifications.Notify(
                    "Nexus verweigert Direct-URL — Premium-Status im Nexus-Settings-Tab prüfen.",
                    NotificationLevel.Error);
                return;
            }

            using var http = _host.CreateHttpClient("nexus-download");
            var progress = new Progress<double>(f =>
                scope.Report(f, $"{file.FileName} · {(int)(f * 100)}%"));
            var wasEnabled = row.Source.IsEnabled;

            var newPakPath = await _installer.DownloadPakAsync(http, link, file.FileName,
                overwrite: true, progress);
            _downloadBus.RaiseDownloadsChanged(Path.GetFileName(newPakPath));

            // Alte Version deinstallieren, neue installieren (identisch zu LS25 v0.7-Update).
            _installer.Uninstall(row.Source);
            var installed = _installer.Install(newPakPath, overwrite: true);
            if (!wasEnabled)
                _installer.SetEnabled(installed, false);

            _host.Notifications.Notify(
                $"Update installiert: {row.DisplayName} → v{row.LatestVersion}",
                NotificationLevel.Success);
            _downloadBus.RaiseModInstalled(installed.FileName);
            Refresh();
        }
        catch (Exception ex)
        {
            _host.Logger.Warn(ex, "Update fehlgeschlagen für mod_id={Id}", modId);
            _host.Notifications.Notify($"Update-Fehler: {ex.Message}", NotificationLevel.Error);
        }
    }

    public void Dispose()
    {
        _manualWatcher?.Dispose();
        _workshopWatcher?.Dispose();
    }
}

public sealed partial class PakRow : ObservableObject
{
    public InstalledPakMod Source { get; }
    public PakRow(InstalledPakMod source) => Source = source;

    public string FileName => Source.FileName;
    public bool IsEnabled => Source.IsEnabled;
    public string StateLabel => Source.Source == PakModSource.Workshop
        ? "Workshop"
        : (Source.IsEnabled ? "aktiv" : "inaktiv");
    public string Size => FormatBytes(Source.FileSizeBytes);
    public bool IsWorkshop => Source.Source == PakModSource.Workshop;
    public bool IsManual => Source.Source == PakModSource.Manual;
    public string SourceBadge => Source.Source == PakModSource.Workshop
        ? "⚙ WORKSHOP"
        : "";

    /// <summary>Aus dem Filename extrahiert (nur bei Manual-Mods).
    /// null wenn nicht dem Nexus-Muster entspricht (Workshop, User-Copy).</summary>
    public int? NexusModId { get; set; }

    /// <summary>Mod-Metadaten vom Nexus-Detail-Fetch (async nach dem Refresh
    /// gefüllt). Initial aus dem Filename abgeleitet als Fallback.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string? _modName;

    [ObservableProperty] private string? _author;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    private string? _summary;

    [ObservableProperty] private string? _version;

    /// <summary>Cover-Bild aus dem Nexus-CDN (via NexusCoverDir-Cache).
    /// null wenn kein Bild vorhanden oder Load fehlgeschlagen — die View
    /// zeigt dann einen Emoji-Platzhalter.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCover))]
    private Bitmap? _cover;

    public bool HasCover => Cover is not null;
    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
    public bool CanShowDetail => NexusModId is int;
    public string DisplayName => !string.IsNullOrWhiteSpace(ModName) ? ModName! : FileName;

    /// <summary>Wird von <c>CheckUpdatesAsync</c> gesetzt wenn Nexus eine
    /// neuere Version anbietet als die installierte (aus dem Filename +
    /// Detail-Fetch). Steuert Update-Badge + ⬆ Update-Button. Nur bei
    /// Manual-Rows mit erkennbarer NexusModId möglich — Workshop-Updates
    /// macht Steam automatisch.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateBadgeText))]
    private bool _hasUpdate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateBadgeText))]
    private string? _latestVersion;

    public string UpdateBadgeText =>
        HasUpdate && LatestVersion is not null ? $"⬆ Update v{LatestVersion}" : "";

    public void SetUpdateAvailable(string catalogVersion)
    {
        LatestVersion = catalogVersion;
        HasUpdate = true;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F0} KB";
        return $"{bytes / 1024.0 / 1024.0:F1} MB";
    }
}
