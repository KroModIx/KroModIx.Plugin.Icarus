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
// v1.15: Contract-Namespace enthaelt jetzt gleichnamige Nexus-Records
// (INexusService). Wir wollen aber die Icarus-eigenen (long-Felder +
// DetailUrl-Extension) — Aliase klaeren den Ambiguity-Konflikt.
using NexusCatalogEntry = KroModIx.Plugin.Icarus.Services.Nexus.NexusCatalogEntry;
using NexusFileEntry = KroModIx.Plugin.Icarus.Services.Nexus.NexusFileEntry;
using NexusCategory = KroModIx.Plugin.Icarus.Services.Nexus.NexusCategory;
using NexusModDetail = KroModIx.Plugin.Icarus.Services.Nexus.NexusModDetail;
using NexusValidateResult = KroModIx.Plugin.Icarus.Services.Nexus.NexusValidateResult;
using NLog;

namespace KroModIx.Plugin.Icarus.Views;

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
    private readonly NexusApiClient _api;
    private readonly NexusCategoryService _categories;
    private readonly PakInstallService _installer;
    private readonly DownloadEventBus _downloadBus;
    private readonly IcarusPaths _paths;
    private readonly IHostServices _host;
    private readonly NexusUpdateTracker _updateTracker;

    public NexusViewModel(NexusCatalogService catalog, NexusSettingsService settings,
        NexusApiClient api, NexusCategoryService categories,
        PakInstallService installer, DownloadEventBus downloadBus,
        IcarusPaths paths, IHostServices host)
    {
        _catalog = catalog;
        _settings = settings;
        _api = api;
        _categories = categories;
        _installer = installer;
        _downloadBus = downloadBus;
        _paths = paths;
        _host = host;
        _updateTracker = new NexusUpdateTracker(paths);
        IsPremium = _settings.Current.IsPremium;
        _ = InitializeAsync();
    }

    /// <summary>Aus <see cref="NexusSettings.IsPremium"/> beim ctor gelesen.
    /// Steuert ob die Download-Buttons in den Rows enabled sind — Nexus
    /// gibt Direct-Download-URLs nur für Premium-Konten heraus. Änderung
    /// propagiert automatisch auf alle Row-Instanzen (siehe
    /// <see cref="OnIsPremiumChanged"/>).</summary>
    [ObservableProperty]
    private bool _isPremium;

    partial void OnIsPremiumChanged(bool value)
    {
        foreach (var row in Rows) row.IsPremium = value;
    }

    public ObservableCollection<NexusRow> Rows { get; } = new();

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _status = "Nexus-Katalog wird geladen …";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _needsApiKey;

    // Vollkatalog-Fetch: Progress unter der Status-Zeile
    [ObservableProperty] private bool _isExtendedLoading;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExtendedProgress))]
    private double? _extendedFraction;
    [ObservableProperty] private string _extendedProgressText = "";

    public bool HasExtendedProgress => IsExtendedLoading;

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
            Status = $"{snap.Entries.Count} Mods (Cache-Alter: {ageH} h) — Extended-Load läuft im Hintergrund …";
            ApplyFilter();

            // Update-Badge auf der Icarus-Kachel zurücksetzen: der User hat
            // den Katalog jetzt gesehen. GameUpdateBadgeService fragt beim
            // nächsten Tick (30min oder bei plugin.Loaded-Refresh) den
            // Notifier neu ab und findet dann 0 neue Einträge → Badge weg.
            _updateTracker.MarkSeen();

            // Extended-Load automatisch triggern (wenn nicht schon läuft und
            // Basis-Load klein war). Läuft im Hintergrund mit Progress-Bar —
            // die Nexus-API deckt in latest_added/updated/trending nur ca. 20
            // Einträge ab, die volle Icarus-Community (216 Mods) kommt erst
            // via updated.json?period=1m + Detail-Fetch.
            if (!IsExtendedLoading && _all.Count < 100)
                _ = LoadExtendedAsync();
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Nexus-Katalog-Load fehlgeschlagen");
            Status = $"Fehler beim Laden: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    /// <summary>Cache-Warmup: fetcht zusätzlich zu den 3 Top-Listen alle
    /// ModIds die im letzten Monat aktualisiert wurden (via updated.json)
    /// und lädt Detail-für-Detail nach. Rate-Limit-schonend (300ms Delay
    /// zwischen Requests) — dauert bei Icarus mit ~200 zusätzlichen Mods
    /// etwa 1 Minute. Progress in ExtendedFraction/ExtendedProgressText.</summary>
    [RelayCommand]
    private async Task LoadExtendedAsync()
    {
        if (IsExtendedLoading) return;
        IsExtendedLoading = true;
        ExtendedFraction = 0;
        ExtendedProgressText = "Extended-Katalog: Baseline via updated.json?period=1m …";

        try
        {
            var snap = await _catalog.LoadExtendedAsync(
                forceRefresh: false,
                onProgress: (done, total) => Dispatcher.UIThread.Post(() =>
                {
                    ExtendedFraction = total > 0 ? (double)done / total : null;
                    ExtendedProgressText = $"Detail {done}/{total} …";
                }));

            _all = snap.Entries.ToList();
            Status = $"{snap.Entries.Count} Mods im Katalog (vollständig).";
            ApplyFilter();
            _updateTracker.MarkSeen();
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Extended-Katalog-Load fehlgeschlagen");
            Status = $"Extended-Load fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            IsExtendedLoading = false;
            ExtendedProgressText = "";
        }
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
            Rows.Add(new NexusRow(e) { IsPremium = IsPremium });
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

    /// <summary>Öffnet den Detail-Dialog für die Row. Analog LS25-ShowDetail:
    /// eigenes Modal-Fenster mit Owner=MainWindow, VM lädt /mods/{id}.json
    /// async, KI-Zusammenfassung über <c>_host.Ai</c>, Premium-Download
    /// aus dem Footer.</summary>
    [RelayCommand]
    private void ShowDetail(NexusRow? row)
    {
        if (row is null) return;
        var vm = new NexusModDetailViewModel(row, _settings.Current.GameSlug, IsPremium,
            _api, _categories, _installer, _downloadBus, _host);
        var window = new NexusModDetailWindow { DataContext = vm };
        var owner = (Avalonia.Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is not null) window.Show(owner); else window.Show();
    }

    [RelayCommand]
    private void OpenDownloadsFolder() => _host.Shell.OpenDirectory(_paths.DownloadsDir);

    /// <summary>Direkt-Download für Premium-User: holt die File-Liste des
    /// Mods, wählt das MAIN+primary-File (Fallback: erstes MAIN, sonst
    /// erstes File überhaupt), löst den Premium-Direct-URL, streamed die
    /// PAK in den Downloads-Ordner mit Progress-Bar in der Host-Statusbar.
    /// Nach Erfolg feuert <see cref="DownloadEventBus.DownloadsChanged"/>
    /// → Downloads-Tab refresht sich automatisch.</summary>
    [RelayCommand]
    private async Task DownloadRowAsync(NexusRow? row)
    {
        if (row is null) return;
        if (!IsPremium)
        {
            _host.Notifications.Notify(
                "Direct-Download braucht Nexus-Premium. Klick \"Nexus öffnen\" für den Browser-Weg.",
                NotificationLevel.Warning);
            return;
        }

        using var scope = _host.BeginProgress($"Nexus: {row.Name}");
        scope.Report(0, "Datei-Liste laden …");
        try
        {
            var slug = _settings.Current.GameSlug;
            var files = await _api.GetFilesAsync(slug, row.Source.ModId);
            var file = PickMainFile(files);
            if (file is null)
            {
                _host.Notifications.Notify("Keine Main-Datei gefunden.", NotificationLevel.Warning);
                return;
            }
            scope.Report(0, $"Download-URL holen ({file.FileName}) …");
            var link = await _api.GetDownloadLinkAsync(slug, row.Source.ModId, file.FileId);
            if (link is null)
            {
                _host.Notifications.Notify(
                    "Nexus verweigert Download-URL — Premium-Status prüfen (Verify im Settings-Tab).",
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
            Log.Warn(ex, "Nexus-Download fehlgeschlagen für mod_id={Id}", row.Source.ModId);
            _host.Notifications.Notify($"Download-Fehler: {ex.Message}", NotificationLevel.Error);
        }
    }

    /// <summary>Wählt aus einer File-Liste den besten Kandidaten für Auto-
    /// Download: (1) MAIN+primary, (2) irgendein MAIN, (3) erstes File.
    /// Für Multi-File-Mods (verschiedene Varianten) müsste man einen
    /// Auswahl-Dialog anbieten — später wenn nötig.</summary>
    internal static NexusFileEntry? PickMainFile(IReadOnlyList<NexusFileEntry> files)
    {
        if (files.Count == 0) return null;
        foreach (var f in files) if (f.IsMainAndPrimary) return f;
        foreach (var f in files) if (f.IsMain) return f;
        return files[0];
    }
}

public sealed partial class NexusRow : ObservableObject
{
    public NexusRow(NexusCatalogEntry source) => Source = source;
    public NexusCatalogEntry Source { get; }

    /// <summary>Wird vom <see cref="NexusViewModel"/> beim Erzeugen und bei
    /// Änderungen des Premium-Status gesetzt. Muss auf der Row selbst liegen
    /// weil ein <c>RelativeSource FindAncestor</c>-Bind vom Button auf
    /// die ListBox-DataContext-Property in einem FuncDataTemplate in Avalonia
    /// nicht zuverlässig aufgelöst wird (Binding liefert null → default(bool)
    /// = false → Button disabled auch für Premium-User).</summary>
    [ObservableProperty] private bool _isPremium;

    public string Name => Source.Name;
    public string Author => Source.Author;
    public string Summary => Source.Summary;

    /// <summary>Version mit smartem Prefix — „v" nur wenn der String
    /// mit einer Ziffer beginnt. Verhindert „vV1.4.0" (Autor hat bereits
    /// eigenes v davor) oder „vweek244" (Nicht-SemVer, kein Versions-
    /// Zeichen erkannbar). Wenn Version leer: leerer String.</summary>
    public string VersionDisplay
    {
        get
        {
            var v = Source.Version?.Trim() ?? "";
            if (v.Length == 0) return "";
            return char.IsDigit(v[0]) ? "v" + v : v;
        }
    }

    public string EndorsementsText => Source.Endorsements > 0 ? $"👍 {Source.Endorsements}" : "";

    /// <summary>„Aktualisiert vor N Tagen" — relative statt absolut, damit
    /// die Meta-Zeile nicht mit Datum überladen wird. Bei > 1 Jahr fällt
    /// die Ausgabe auf ISO-Datum zurück.</summary>
    public string UpdatedText
    {
        get
        {
            var delta = DateTime.UtcNow - Source.UpdatedUtc;
            if (delta.TotalDays < 1) return "heute";
            if (delta.TotalDays < 2) return "gestern";
            if (delta.TotalDays < 30) return $"vor {(int)delta.TotalDays} Tagen";
            if (delta.TotalDays < 365) return $"vor {(int)(delta.TotalDays / 30)} Monaten";
            return Source.UpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd");
        }
    }

    [ObservableProperty]
    private Bitmap? _cover;
}

