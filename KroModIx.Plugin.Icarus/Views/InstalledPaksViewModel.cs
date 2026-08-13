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
        WorkshopDir = installer.WorkshopDir ?? Strings.T("label.workshop_none");
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
        SelectedRows.Count > 1 ? string.Format(Strings.T("status.selection_count"), SelectedRows.Count) : "";

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
                ? Strings.T("status.no_mods")
                : string.Format(Strings.T("status.mod_summary"), enabled, manualCount, workshopCount);
        }
        catch (Exception ex)
        {
            _host.Logger.Warn(ex, "Icarus: Mod-Liste konnte nicht geladen werden");
            Summary = Strings.T("status.mods_load_error");
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
                Strings.T("notify.nexus_detail_unavailable"),
                NotificationLevel.Warning);
            return;
        }
        if (row.NexusModId is not int modId)
        {
            _host.Notifications.Notify(
                row.IsWorkshop
                    ? Strings.T("notify.workshop_no_nexus")
                    : string.Format(Strings.T("notify.no_nexus_id"), row.FileName),
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
                (updated.IsEnabled ? Strings.T("notify.mod_enabled_prefix") : Strings.T("notify.mod_disabled_prefix"))
                    + updated.FileName,
                NotificationLevel.Success);
            Refresh();
        }
        catch (Exception ex)
        {
            _host.Logger.Warn(ex, "Icarus: Toggle fehlgeschlagen");
            _host.Notifications.Notify(Strings.T("notify.error_prefix") + ex.Message, NotificationLevel.Error);
        }
    }

    [RelayCommand]
    private void ToggleEnabledBulk()
    {
        if (SelectedRows.Count == 0) return;
        var rows = SelectedRows.Where(r => r.Source.Source == PakModSource.Manual).ToList();
        if (rows.Count == 0)
        {
            _host.Notifications.Notify(Strings.T("notify.bulk_only_workshop"),
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
            string.Format(target ? Strings.T("notify.bulk_enable_result") : Strings.T("notify.bulk_disable_result"), done),
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
                Strings.T("notify.workshop_readonly"),
                NotificationLevel.Info);
            return;
        }
        bool ok = await _host.Dialogs.ConfirmAsync(
            Strings.T("dialog.uninstall_title"),
            string.Format(Strings.T("dialog.uninstall_msg"), row.Source.FileName),
            okLabel: Strings.T("dialog.btn.delete"), cancelLabel: Strings.T("dialog.btn.cancel"));
        if (!ok) return;
        try
        {
            _installer.Uninstall(row.Source);
            _host.Notifications.Notify(Strings.T("notify.uninstalled_prefix") + row.Source.FileName,
                NotificationLevel.Success);
            Refresh();
        }
        catch (Exception ex)
        {
            _host.Notifications.Notify(Strings.T("notify.error_prefix") + ex.Message, NotificationLevel.Error);
        }
    }

    [RelayCommand]
    private async Task UninstallBulkAsync()
    {
        var rows = SelectedRows.Where(r => r.Source.Source == PakModSource.Manual).ToList();
        if (rows.Count == 0) return;
        bool ok = await _host.Dialogs.ConfirmAsync(
            Strings.T("dialog.uninstall_bulk_title"),
            string.Format(Strings.T("dialog.uninstall_bulk_msg"), rows.Count) + "\n\n" +
            string.Join("\n", rows.Take(10).Select(r => "• " + r.FileName)) +
            (rows.Count > 10 ? "\n" + string.Format(Strings.T("dialog.uninstall_bulk_more"), rows.Count - 10) : ""),
            okLabel: Strings.T("dialog.btn.delete"), cancelLabel: Strings.T("dialog.btn.cancel"));
        if (!ok) return;
        int done = 0;
        foreach (var r in rows)
        {
            try { _installer.Uninstall(r.Source); done++; }
            catch (Exception ex) { _host.Logger.Warn(ex, "Bulk-Uninstall für {F}", r.FileName); }
        }
        _host.Notifications.Notify(string.Format(Strings.T("notify.bulk_uninstall_result"), done), NotificationLevel.Success);
        Refresh();
    }

    [RelayCommand]
    private async Task InstallFromFileAsync()
    {
        var picked = await _host.Dialogs.PickFileAsync(
            Strings.T("dialog.pick_pak_title"),
            (Strings.T("dialog.pick_pak_filter"), new[] { "*.pak" }));
        if (picked is null) return;
        try
        {
            var installed = _installer.Install(picked, overwrite: false);
            _host.Notifications.Notify(Strings.T("notify.installed_prefix") + installed.FileName,
                NotificationLevel.Success);
            _downloadBus.RaiseModInstalled(installed.FileName);
            Refresh();
        }
        catch (Exception ex)
        {
            _host.Logger.Warn(ex, "Icarus: Install fehlgeschlagen");
            _host.Notifications.Notify(Strings.T("notify.error_prefix") + ex.Message, NotificationLevel.Error);
        }
    }

    public void InstallDroppedPak(string pakPath)
    {
        try
        {
            var installed = _installer.Install(pakPath, overwrite: false);
            _host.Notifications.Notify(Strings.T("notify.installed_drop_prefix") + installed.FileName,
                NotificationLevel.Success);
            _downloadBus.RaiseModInstalled(installed.FileName);
        }
        catch (Exception ex)
        {
            _host.Logger.Warn(ex, "Icarus: Drop-Install fehlgeschlagen");
            _host.Notifications.Notify(
                string.Format(Strings.T("notify.drop_install_fail"), Path.GetFileName(pakPath), ex.Message),
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
                Strings.T("notify.no_workshop_folder"),
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
                Strings.T("notify.no_backup_manual"),
                NotificationLevel.Warning);
            return;
        }
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        var target = Path.Combine(_paths.BackupsDir, $"icarus-backup-{timestamp}.zip");
        using var scope = _host.BeginProgress(Strings.T("progress.backup"));
        var progress = new Progress<BackupProgress>(p =>
            scope.Report(p.Fraction, string.Format(Strings.T("progress.backup_row"), p.Current, p.Total, p.CurrentFileName)));
        try
        {
            var result = await _backup.CreateBackupAsync(target, progress);
            _host.Notifications.Notify(
                string.Format(Strings.T("notify.backup_summary"),
                    result.ModCount, FormatBytes(result.FileSizeBytes), Path.GetFileName(result.FilePath)),
                NotificationLevel.Success);
            _host.Shell.OpenDirectory(_paths.BackupsDir);
        }
        catch (Exception ex)
        {
            _host.Logger.Warn(ex, "Icarus: Backup fehlgeschlagen");
            _host.Notifications.Notify(Strings.T("notify.backup_error") + ex.Message, NotificationLevel.Error);
        }
    }

    [RelayCommand]
    private async Task RestoreBackupAsync()
    {
        var picked = await _host.Dialogs.PickFileAsync(
            Strings.T("dialog.pick_backup_title"),
            (Strings.T("dialog.pick_backup_filter"), new[] { "*.zip" }));
        if (picked is null) return;

        BackupManifest manifest;
        try { manifest = PakBackupService.ReadManifest(picked); }
        catch (Exception ex)
        {
            _host.Notifications.Notify(Strings.T("notify.backup_invalid") + ex.Message, NotificationLevel.Error);
            return;
        }

        bool ok = await _host.Dialogs.ConfirmAsync(
            Strings.T("dialog.restore_title"),
            string.Format(Strings.T("dialog.restore_msg"),
                manifest.CreatedUtc.ToLocalTime().ToString("g"), manifest.Mods.Count),
            okLabel: Strings.T("dialog.btn.restore"), cancelLabel: Strings.T("dialog.btn.cancel"));
        if (!ok) return;

        using var scope = _host.BeginProgress(Strings.T("progress.restore"));
        var progress = new Progress<BackupProgress>(p =>
            scope.Report(p.Fraction, string.Format(Strings.T("progress.backup_row"), p.Current, p.Total, p.CurrentFileName)));
        try
        {
            var result = await _backup.RestoreBackupAsync(picked, progress);
            _host.Notifications.Notify(
                string.Format(Strings.T("notify.restore_summary"), result.RestoredCount, result.SkippedCount),
                NotificationLevel.Success);
            _downloadBus.RaiseModInstalled("(restore)");
            Refresh();
        }
        catch (Exception ex)
        {
            _host.Logger.Warn(ex, "Icarus: Restore fehlgeschlagen");
            _host.Notifications.Notify(Strings.T("notify.restore_error") + ex.Message, NotificationLevel.Error);
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
            _host.Notifications.Notify(Strings.T("notify.nexus_api_unavailable"), NotificationLevel.Warning);
            return;
        }
        if (!_nexusSettings.HasApiKey)
        {
            _host.Notifications.Notify(
                Strings.T("notify.no_nexus_key_check"),
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
                ? string.Format(Strings.T("status.updates_found"), updated)
                : Strings.T("status.no_updates");
            _host.Notifications.Notify(Summary,
                updated > 0 ? NotificationLevel.Success : NotificationLevel.Info);
            OnPropertyChanged(nameof(HasAnyUpdate));
        }
        finally { IsCheckingUpdates = false; }
    }

    /// <summary>Mindestens eine Row mit Nexus-Update? Steuert den „⬆ Alle
    /// updaten"-Button.</summary>
    public bool HasAnyUpdate => _allMods.Any(r => r.HasUpdate);

    /// <summary>Bulk-Update aller Rows mit HasUpdate — sequenziell wegen
    /// Nexus-Rate-Limit (250/h Free, 2500/h Premium). Skill Kernprinzip 6c.</summary>
    [RelayCommand]
    private async Task UpdateAllAsync()
    {
        var candidates = _allMods.Where(r => r.HasUpdate).ToList();
        if (candidates.Count == 0)
        {
            _host.Notifications.Notify(
                Strings.T("notify.no_updates_hint"),
                NotificationLevel.Info);
            return;
        }
        using var scope = _host.BeginProgress(string.Format(Strings.T("progress.updates"), candidates.Count));
        int done = 0, failed = 0;
        for (int i = 0; i < candidates.Count; i++)
        {
            var row = candidates[i];
            scope.Report((double)i / candidates.Count,
                string.Format(Strings.T("progress.update_row"), i + 1, candidates.Count, row.DisplayName));
            try
            {
                await UpdateModAsync(row);
                done++;
            }
            catch (Exception ex)
            {
                _host.Logger.Warn(ex, "Bulk-Update fehlgeschlagen für {Mod}", row.DisplayName);
                failed++;
            }
        }
        _host.Notifications.Notify(
            failed == 0
                ? string.Format(Strings.T("notify.updates_installed"), done)
                : string.Format(Strings.T("notify.updates_result"), done, failed),
            failed == 0 ? NotificationLevel.Success : NotificationLevel.Warning);
        OnPropertyChanged(nameof(HasAnyUpdate));
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
            _host.Notifications.Notify(Strings.T("notify.no_mod_id_update"),
                NotificationLevel.Warning);
            return;
        }
        if (_nexusApi is null || _nexusSettings is null)
        {
            _host.Notifications.Notify(Strings.T("notify.nexus_api_unavailable"),
                NotificationLevel.Warning);
            return;
        }
        if (!_nexusSettings.Current.IsPremium)
        {
            _host.Notifications.Notify(
                Strings.T("notify.update_needs_premium"),
                NotificationLevel.Warning);
            return;
        }

        var slug = _nexusSettings.Current.GameSlug;
        using var scope = _host.BeginProgress(string.Format(Strings.T("progress.update_scope"), row.DisplayName));
        scope.Report(0, Strings.T("progress.update_files_load"));
        try
        {
            var files = await _nexusApi.GetFilesAsync(slug, modId);
            var file = NexusViewModel.PickMainFile(files);
            if (file is null)
            {
                _host.Notifications.Notify(Strings.T("notify.no_main_file"),
                    NotificationLevel.Warning);
                return;
            }

            scope.Report(0, string.Format(Strings.T("progress.update_url"), file.FileName));
            var link = await _nexusApi.GetDownloadLinkAsync(slug, modId, file.FileId);
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
                Strings.T("notify.update_installed_prefix") + $"{row.DisplayName} → v{row.LatestVersion}",
                NotificationLevel.Success);
            _downloadBus.RaiseModInstalled(installed.FileName);
            Refresh();

            // Skill Kernprinzip 6b: Re-Check triggern damit der Sidebar-Kachel-
            // Badge sofort sinkt statt bis zum nächsten Auto-Check zu warten.
            if (_updatesChecker is not null)
                _ = _updatesChecker.CheckAsync();
        }
        catch (Exception ex)
        {
            _host.Logger.Warn(ex, "Update fehlgeschlagen für mod_id={Id}", modId);
            _host.Notifications.Notify(Strings.T("notify.update_error_prefix") + ex.Message, NotificationLevel.Error);
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
        ? Strings.T("row.state.workshop")
        : (Source.IsEnabled ? Strings.T("row.state.active") : Strings.T("row.state.inactive"));
    public string Size => FormatBytes(Source.FileSizeBytes);
    public bool IsWorkshop => Source.Source == PakModSource.Workshop;
    public bool IsManual => Source.Source == PakModSource.Manual;
    public string SourceBadge => Source.Source == PakModSource.Workshop
        ? Strings.T("badge.workshop")
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
        HasUpdate && LatestVersion is not null ? Strings.T("row.update_badge_prefix") + LatestVersion : "";

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
