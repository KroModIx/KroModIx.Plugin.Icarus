using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModManager.PluginContracts;
using ModManager.Plugins.Icarus.Services;
using ModManager.Plugins.Icarus.Services.Nexus;
using NLog;

namespace ModManager.Plugins.Icarus.Views;

/// <summary>VM für den Nexus-Katalog-Tab. Aggregiert die drei Nexus-Endpunkte
/// (latest_added, latest_updated, trending) und bietet Browser-Download-
/// Buttons pro Row. Kein In-App-Download — Nexus-Free-Users müssen den
/// Slow-Download-Wall durchklicken, Premium-Downloads sind API-basiert aber
/// den bauen wir wenn ein Premium-User uns danach fragt.</summary>
public sealed partial class NexusViewModel : ObservableObject
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly NexusCatalogService _catalog;
    private readonly NexusSettingsService _settings;
    private readonly IcarusPaths _paths;
    private readonly IHostServices _host;

    public NexusViewModel(NexusCatalogService catalog, NexusSettingsService settings,
        IcarusPaths paths, IHostServices host)
    {
        _catalog = catalog;
        _settings = settings;
        _paths = paths;
        _host = host;
        _ = InitializeAsync();
    }

    public ObservableCollection<NexusRow> Rows { get; } = new();

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _status = "Nexus-Katalog wird geladen …";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _needsApiKey;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private async Task InitializeAsync()
    {
        if (!_settings.HasApiKey)
        {
            NeedsApiKey = true;
            Status = "Kein Nexus-API-Key konfiguriert — bitte im Nexus-Settings-Tab eintragen.";
            return;
        }
        await LoadAsync(forceRefresh: false);
    }

    private async Task LoadAsync(bool forceRefresh)
    {
        IsBusy = true;
        try
        {
            var snap = await _catalog.LoadAsync(forceRefresh);
            _all = snap.Entries.ToList();
            var ageH = (int)(DateTime.UtcNow - snap.SavedUtc).TotalHours;
            Status = $"{snap.Entries.Count} Mods (Cache-Alter: {ageH} h)";
            ApplyFilter();
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Nexus-Katalog-Load fehlgeschlagen");
            Status = $"Fehler beim Laden: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private System.Collections.Generic.List<NexusCatalogEntry> _all = new();

    private void ApplyFilter()
    {
        Rows.Clear();
        var q = SearchText?.Trim() ?? "";
        foreach (var e in _all)
        {
            if (q.Length > 0 && !(e.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || e.Author.Contains(q, StringComparison.OrdinalIgnoreCase)
                || e.Summary.Contains(q, StringComparison.OrdinalIgnoreCase)))
                continue;
            Rows.Add(new NexusRow(e));
        }
        _ = LoadCoversAsync(Rows.ToArray());
    }

    private async Task LoadCoversAsync(NexusRow[] rows)
    {
        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.Source.PictureUrl)) continue;
            try
            {
                var localPath = Path.Combine(_paths.NexusCoverDir, $"{row.Source.ModId}.jpg");
                if (!File.Exists(localPath))
                {
                    using var http = _host.CreateHttpClient("nexus-covers");
                    var bytes = await http.GetByteArrayAsync(row.Source.PictureUrl);
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
                Log.Debug(ex, "Nexus-Cover-Load fehlgeschlagen für {Id}", row.Source.ModId);
            }
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync(forceRefresh: true);

    [RelayCommand]
    private void OpenRowInBrowser(NexusRow? row)
    {
        if (row is null) return;
        _host.Shell.OpenExternalUrl(row.Source.DetailUrl(_settings.Current.GameSlug));
    }

    [RelayCommand]
    private void OpenDownloadsFolder() => _host.Shell.OpenDirectory(_paths.DownloadsDir);
}

public sealed partial class NexusRow : ObservableObject
{
    public NexusRow(NexusCatalogEntry source) => Source = source;
    public NexusCatalogEntry Source { get; }

    public string Name => Source.Name;
    public string Author => Source.Author;
    public string Summary => Source.Summary;
    public string Version => Source.Version;
    public string EndorsementsText => Source.Endorsements > 0 ? $"👍 {Source.Endorsements}" : "";
    public string UpdatedText => Source.UpdatedUtc.ToLocalTime().ToString("g");

    [ObservableProperty]
    private Bitmap? _cover;
}

