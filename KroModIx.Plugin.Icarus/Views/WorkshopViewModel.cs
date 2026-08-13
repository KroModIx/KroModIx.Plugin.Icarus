using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KroModIx.Plugin.Contracts;
using KroModIx.Plugin.Icarus.Services;

namespace KroModIx.Plugin.Icarus.Views;

/// <summary>Workshop-Tab (v1.17): listet die vom User abonnierten Steam-
/// Workshop-Items fuer Icarus (AppId 1149460) via Host-Contract
/// <see cref="IHostServices.Workshop"/>. Discovery ist offline (Filesystem-
/// Scan der <c>workshop/content/1149460/</c>-Ordner), Enrichment via
/// Steam-Web-API (<c>GetPublishedFileDetails</c>, public — kein Key noetig).
///
/// <para>Read-only: Steam verwaltet die Items (Un/Subscribe via In-Game oder
/// Web-Workshop). Der Tab bietet nur Aktionen die keinen Konflikt mit dem
/// Steam-Client haben: „In Steam oeffnen" (Deep-Link) und „Ordner oeffnen".</para></summary>
public sealed partial class WorkshopViewModel : ObservableObject
{
    private readonly DetectedGame _game;
    private readonly IHostServices _host;
    private readonly PreviewCoverCache _covers;

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _filterText = "";

    public ObservableCollection<WorkshopRow> Rows { get; } = new();
    private List<WorkshopRow> _allRows = new();

    public WorkshopViewModel(DetectedGame game, IHostServices host)
    {
        _game = game;
        _host = host;
        _covers = new PreviewCoverCache(host, host.CreateHttpClient("workshop-covers"));
        _ = LoadAsync();
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var q = FilterText?.Trim() ?? "";
        Rows.Clear();
        var matched = string.IsNullOrEmpty(q)
            ? _allRows
            : _allRows.Where(r =>
                (r.Title ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
                || (r.Author ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.PublishedFileId.ToString().Contains(q)).ToList();
        foreach (var r in matched) Rows.Add(r);
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (_game.Target.SteamAppId is not int appId)
        {
            StatusText = Strings.T("workshop.no_steam_app");
            return;
        }
        if (!_host.Workshop.IsAvailable)
        {
            StatusText = Strings.T("workshop.host_too_old");
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = Strings.T("workshop.scanning");
            var items = await _host.Workshop.DiscoverAsync(appId);
            _allRows = items
                .OrderByDescending(i => i.LastUpdatedLocalUtc ?? DateTime.MinValue)
                .Select(i => new WorkshopRow(i))
                .ToList();
            ApplyFilter();
            if (_allRows.Count == 0)
            {
                StatusText = Strings.T("workshop.no_items");
                return;
            }
            StatusText = string.Format(Strings.T("workshop.count"), _allRows.Count);

            // Enrichment (Titel, Beschreibung, PreviewUrl) im Hintergrund —
            // Discovery ist offline und liefert nur IDs + Filesystem-Meta.
            _ = EnrichAsync(_allRows.ToArray());
        }
        catch (Exception ex)
        {
            _host.Logger.Warn(ex, "Workshop-Discovery fehlgeschlagen");
            StatusText = Strings.T("status.error_prefix") + ex.Message;
        }
        finally { IsBusy = false; }
    }

    private async Task EnrichAsync(WorkshopRow[] rows)
    {
        try
        {
            var enriched = await _host.Workshop.EnrichAsync(
                rows.Select(r => r.Source).ToList());
            var byId = enriched.ToDictionary(e => e.PublishedFileId);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var row in rows)
                {
                    if (!byId.TryGetValue(row.PublishedFileId, out var det)) continue;
                    row.Title = det.Title;
                    row.Description = det.Description;
                    row.Author = det.Author;
                    row.SubscriberCount = det.SubscriberCount;
                    row.PreviewUrl = det.PreviewUrl;
                    row.UpdatedUtc = det.UpdatedUtc;
                    row.OnEnrichmentChanged();
                }
            });
            // Cover-Load pro Row (throttled 200 ms)
            foreach (var row in rows)
            {
                if (string.IsNullOrEmpty(row.PreviewUrl)) continue;
                if (row.Cover is not null) continue;
                var path = await _covers.GetOrDownloadAsync(row.PreviewUrl);
                if (path is null) continue;
                try
                {
                    var bmp = await Task.Run(() =>
                    {
                        using var s = File.OpenRead(path);
                        return new Bitmap(s);
                    });
                    await Dispatcher.UIThread.InvokeAsync(() => row.Cover = bmp);
                }
                catch (Exception ex)
                {
                    _host.Logger.Debug(ex, "Workshop-Cover-Bitmap-Load fuer {Id} fehlgeschlagen", row.PublishedFileId);
                }
                await Task.Delay(200);
            }
        }
        catch (Exception ex)
        {
            _host.Logger.Debug(ex, "Workshop-Enrichment fehlgeschlagen");
        }
    }

    [RelayCommand]
    private void OpenInSteam(WorkshopRow? row)
    {
        if (row is null) return;
        // Steam-Client-Deep-Link auf die Workshop-Item-Seite.
        _host.Shell.OpenExternalUrl(
            $"steam://url/CommunityFilePage/{row.PublishedFileId}");
    }

    [RelayCommand]
    private void OpenInBrowser(WorkshopRow? row)
    {
        if (row is null) return;
        _host.Shell.OpenExternalUrl(
            $"https://steamcommunity.com/sharedfiles/filedetails/?id={row.PublishedFileId}");
    }

    [RelayCommand]
    private void OpenFolder(WorkshopRow? row)
    {
        if (row is null) return;
        _host.Shell.OpenDirectory(row.LocalDir);
    }
}

public sealed partial class WorkshopRow : ObservableObject
{
    public WorkshopRow(WorkshopItem source) => Source = source;
    public WorkshopItem Source { get; }

    public ulong PublishedFileId => Source.PublishedFileId;
    public string LocalDir => Source.LocalDir;

    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Author { get; set; }
    public string? PreviewUrl { get; set; }
    public int? SubscriberCount { get; set; }
    public DateTime? UpdatedUtc { get; set; }

    [ObservableProperty] private Bitmap? _cover;

    public string DisplayTitle => string.IsNullOrEmpty(Title)
        ? $"Workshop #{PublishedFileId}" : Title;

    public string SubtitleText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Author)) parts.Add(Author);
            if (SubscriberCount is > 0) parts.Add($"👥 {SubscriberCount:N0}");
            if (Source.SizeOnDiskBytes > 0) parts.Add(FormatSize(Source.SizeOnDiskBytes));
            if (UpdatedUtc is DateTime updated) parts.Add(updated.ToLocalTime().ToString("yyyy-MM-dd"));
            return string.Join(" · ", parts);
        }
    }

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public void OnEnrichmentChanged()
    {
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(SubtitleText));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(HasDescription));
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };
}

/// <summary>Kleiner file-basierter Cover-Cache fuer Steam-Workshop-Preview-URLs.
/// Cache-Key: SHA1 des vollen URLs. Wir nutzen keinen Referer — Steam-CDN
/// braucht keinen. 5 MB Max pro Bild reicht auch fuer WQHD-Preview-Screenshots.</summary>
internal sealed class PreviewCoverCache
{
    private readonly string _dir;
    private readonly HttpClient _http;

    public PreviewCoverCache(IHostServices host, HttpClient http)
    {
        _dir = Path.Combine(host.PluginCacheDir, "workshop-covers");
        Directory.CreateDirectory(_dir);
        _http = http;
    }

    public async Task<string?> GetOrDownloadAsync(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var path = Path.Combine(_dir, Sha1(url) + ".img");
        if (File.Exists(path) && new FileInfo(path).Length > 0) return path;
        try
        {
            using var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0) return null;
            var tmp = path + $".tmp.{Guid.NewGuid():N}";
            await File.WriteAllBytesAsync(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
            return path;
        }
        catch { return null; }
    }

    private static string Sha1(string s)
    {
        using var sha = System.Security.Cryptography.SHA1.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes);
    }
}
