using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KroModIx.Plugin.Contracts;
using KroModIx.Plugin.Icarus.Services;

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
    private FileSystemWatcher? _watcher;

    public DownloadsViewModel(PakInstallService installer, DownloadEventBus downloadBus, IHostServices host)
    {
        _installer = installer;
        _downloadBus = downloadBus;
        _host = host;
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
            foreach (var d in files) Rows.Add(new DownloadRow(d));
            var totalBytes = Rows.Sum(r => r.Source.FileSizeBytes);
            Summary = Rows.Count == 0
                ? "Keine PAK-Dateien im Downloads-Ordner."
                : $"{Rows.Count} PAKs · {totalBytes / 1024.0 / 1024.0:F1} MB gesamt";
        }
        catch (Exception ex)
        {
            _host.Logger.Warn(ex, "Icarus: Downloads-Liste konnte nicht geladen werden");
            Summary = "Fehler beim Lesen des Downloads-Ordners.";
        }
    }

    [RelayCommand]
    private void InstallRow(DownloadRow? row)
    {
        if (row is null) return;
        try
        {
            var installed = _installer.Install(row.Source.FilePath, overwrite: false);
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

    public void Dispose() => _watcher?.Dispose();
}

public sealed class DownloadRow
{
    public DownloadedPak Source { get; }
    public DownloadRow(DownloadedPak s) => Source = s;

    public string FileName => Source.FileName;
    public string Size => Source.FileSizeBytes < 1024 * 1024
        ? $"{Source.FileSizeBytes / 1024.0:F0} KB"
        : $"{Source.FileSizeBytes / 1024.0 / 1024.0:F1} MB";
    public string DownloadedText => Source.DownloadedUtc.ToLocalTime().ToString("g");
}
