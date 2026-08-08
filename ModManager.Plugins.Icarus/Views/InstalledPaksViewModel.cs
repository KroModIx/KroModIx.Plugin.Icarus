using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModManager.PluginContracts;
using ModManager.Plugins.Icarus.Services;

namespace ModManager.Plugins.Icarus.Views;

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

    private readonly List<PakRow> _allMods = new();
    private FileSystemWatcher? _manualWatcher;
    private FileSystemWatcher? _workshopWatcher;

    public InstalledPaksViewModel(PakInstallService installer, PakBackupService backup,
        IcarusPaths paths, DownloadEventBus downloadBus, IHostServices host)
    {
        _installer = installer;
        _backup = backup;
        _paths = paths;
        _downloadBus = downloadBus;
        _host = host;
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
                _allMods.Add(new PakRow(m));

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

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F0} KB";
        return $"{bytes / 1024.0 / 1024.0:F1} MB";
    }
}
