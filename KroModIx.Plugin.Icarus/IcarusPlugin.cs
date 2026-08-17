using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using KroModIx.Plugin.Contracts;
using KroModIx.Plugin.Icarus.Services;
using KroModIx.Plugin.Icarus.Services.Nexus;
using KroModIx.Plugin.Icarus.Views;

namespace KroModIx.Plugin.Icarus;

public sealed class IcarusPlugin : IGameModPlugin, IUpdateNotifier
{
    public PluginMetadata Metadata { get; } = new(
        Id: "kroste.icarus",
        DisplayName: "Icarus Mod-Manager",
        Version: "1.19.0",
        Author: "Kroste",
        Description: "Mod-Manager für Icarus (RocketWerkz). Manuelle PAK-Mods im " +
            "Content/Paks/mods-Ordner UND Steam-Workshop-Abos werden gemeinsam gelistet " +
            "(Workshop-Rows read-only). Nexus-Mods-Katalog mit Personal-API-Key. " +
            "Auto-Refresh via FileSystemWatcher, Backup/Restore, Kroste-Card-Look. " +
            "v1.7.0: grüner ↑-Badge bei neuen Nexus-Einträgen (IUpdateNotifier). " +
            "v1.16.0: DE+EN-Uebersetzung aller User-facing Strings. " +
            "v1.17.0: Steam-Workshop-Tab (Consumer fuer Host-Contract IHostServices.Workshop) + sprachabhaengige KI-Prompts. " +
            "v1.18.0: Cover-Decode ueber Host-IImageDecoder-Baukasten (Contracts v1.18.0). " +
            "v1.19.0: HTML/BBCode-Description-Parser aus _host.Descriptions (zentraler Baukasten Contracts v1.20).");

    public IReadOnlyList<GameTarget> Targets { get; } = new[]
    {
        new GameTarget("icarus", "Icarus",
            SteamAppId: 1149460,
            AlternativeExecutableNames: new[] { "Icarus.exe", "IcarusClient.exe" },
            Platforms: Platforms.Both),
    };

    private IHostServices? _host;
    private IcarusPaths? _paths;
    private NexusSettingsService? _nexusSettings;
    private NexusApiClient? _nexusApi;
    private NexusCatalogService? _nexusCatalog;
    private NexusCategoryService? _nexusCategories;
    private NexusUpdateTracker? _updateTracker;
    private InstalledUpdatesTracker? _installedUpdatesTracker;
    private DownloadEventBus? _downloadBus;
    private IReadOnlyList<DetectedGame> _activatedGames = Array.Empty<DetectedGame>();
    private readonly Dictionary<string, PakInstallService> _installers = new();
    private readonly Dictionary<string, PakBackupService> _backups = new();
    private readonly Dictionary<string, InstalledUpdatesChecker> _updateCheckers = new();
    private readonly IcarusPathResolver _pathResolver = new();

    public Task InitializeAsync(IHostServices host, IReadOnlyList<DetectedGame> activatedGames, CancellationToken ct)
    {
        _host = host;
        Strings.Init(host.Localization);
        _paths = new IcarusPaths(host);
        // v1.15: Nexus wandert in den Host — API-Key + HTTP-Client werden
        // zentral verwaltet (Contracts v1.14.0+). Adapter wrappt host.Nexus.
        _nexusSettings = new NexusSettingsService(_paths, host.Secrets, host.Nexus);
        _nexusApi = new NexusApiClient(host.Nexus);
        // Migration: alter Icarus-Key aus plugin-data uebernehmen wenn der Host
        // noch keinen hat — Notification an User, muss den Key im Host-Settings
        // neu eintragen (direkter Copy ohne Contract-Erweiterung nicht moeglich).
        TryNotifyLegacyKeyMigration();
        _nexusCatalog = new NexusCatalogService(_nexusApi, _nexusSettings, _paths);
        _nexusCategories = new NexusCategoryService(_nexusApi, _nexusSettings);
        _updateTracker = new NexusUpdateTracker(_paths);
        _installedUpdatesTracker = new InstalledUpdatesTracker(_paths);
        _downloadBus = new DownloadEventBus();
        _activatedGames = activatedGames;

        foreach (var game in activatedGames)
        {
            var manualDir = _pathResolver.GetManualModsDir(game);
            var workshopDir = _pathResolver.GetWorkshopContentDir(game);
            if (manualDir is null)
            {
                host.Logger.Warn("Icarus: konnte keinen Manual-Mods-Pfad ableiten für {Game}",
                    game.Target.DisplayName);
                continue;
            }
            var installer = new PakInstallService(manualDir, workshopDir, _paths.DownloadsDir);
            _installers[game.Target.GameId] = installer;
            _backups[game.Target.GameId] = new PakBackupService(installer);
            _updateCheckers[game.Target.GameId] = new InstalledUpdatesChecker(
                installer, _nexusApi, _nexusSettings, _installedUpdatesTracker);
            host.Logger.Info("Icarus initialisiert: manual={Manual}, workshop={Workshop}, downloads={Downloads}",
                manualDir, workshopDir ?? "(none)", _paths.DownloadsDir);
        }

        // Auto-Check bei Plugin-Init.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
            foreach (var checker in _updateCheckers.Values)
            {
                try { await checker.CheckAsync(ct: ct); }
                catch (Exception ex) { host.Logger.Debug(ex, "Auto-Update-Check fehlgeschlagen"); }
            }
        }, ct);

        // Skill Kernprinzip 6b: nach jedem Install/Update (Row/Bulk/Update-Row)
        // Checker re-triggern damit Sidebar-Kachel-Badge aktuell bleibt.
        _downloadBus.ModInstalled += (_, _) =>
        {
            _ = Task.Run(async () =>
            {
                foreach (var checker in _updateCheckers.Values)
                {
                    try { await checker.CheckAsync(); }
                    catch (Exception ex) { host.Logger.Debug(ex, "Post-Install Update-Check fehlgeschlagen"); }
                }
            });
        };

        return Task.CompletedTask;
    }

    public IEnumerable<IGameTabContribution> GetTabContributions(DetectedGame game)
    {
        if (!_installers.TryGetValue(game.Target.GameId, out var installer) || _host is null
            || _paths is null || _downloadBus is null || _nexusSettings is null
            || _nexusApi is null || _nexusCatalog is null || _nexusCategories is null
            || _installedUpdatesTracker is null
            || !_backups.TryGetValue(game.Target.GameId, out var backup)
            || !_updateCheckers.TryGetValue(game.Target.GameId, out var updatesChecker))
            yield break;

        yield return new InstalledTab(installer, backup, _paths, _downloadBus, _host,
            _nexusApi, _nexusSettings, _nexusCategories, updatesChecker);
        yield return new NexusTab(_nexusCatalog, _nexusSettings, _nexusApi, _nexusCategories,
            installer, _downloadBus, _paths, _host);
        yield return new DownloadsTab(installer, _downloadBus, _host,
            _nexusApi, _nexusSettings, _paths, _nexusCategories);
        // v1.17: Steam-Workshop-Tab — nutzt Host-Contract IHostServices.Workshop
        // fuer Discovery + Enrichment. Read-only (Steam verwaltet die Items).
        yield return new WorkshopTab(game, _host);
        // v1.15: kein plugin-eigener Nexus-Einstellungen-Tab mehr — der User
        // pflegt den API-Key jetzt zentral im Host-Settings-Fenster
        // (Tab „🌐 Nexus"). Alle Nexus-Plugins teilen ihn.
    }

    public Task ShutdownAsync()
    {
        // v1.15: NexusApiClient hat kein eigenes IDisposable mehr (nur noch
        // Adapter auf host.Nexus, HTTP-Handle lebt im Host).
        _host?.Logger.Info("Icarus shutdown");
        return Task.CompletedTask;
    }

    // ---- IUpdateNotifier (Contracts v1.7.0) ----

    /// <summary>Meldet ausstehende Mod-Updates fuer INSTALLIERTE Mods
    /// (aus <see cref="InstalledUpdatesTracker"/>). Neue Katalog-Eintraege
    /// zaehlen bewusst NICHT als Badge-Trigger — der gruene ↑-Pfeil steht
    /// fuer „User hat etwas installiert das ein Update braucht", nicht
    /// „es gibt neue Community-Uploads". Katalog-News koennen im Nexus-
    /// Tab-Status oder als optionale Toast-Meldung angezeigt werden,
    /// verdienen aber keinen Actionable-Badge.</summary>
    public Task<IReadOnlyList<GameUpdateInfo>> GetPendingUpdatesAsync(CancellationToken cancellationToken)
    {
        if (_installedUpdatesTracker is null || _activatedGames.Count == 0)
            return Task.FromResult<IReadOnlyList<GameUpdateInfo>>(Array.Empty<GameUpdateInfo>());

        var installedCount = _installedUpdatesTracker.PendingCount;
        if (installedCount <= 0) return Task.FromResult<IReadOnlyList<GameUpdateInfo>>(Array.Empty<GameUpdateInfo>());

        var summary = _installedUpdatesTracker.Summary is { Length: > 0 } s
            ? s
            : $"{installedCount} Mod-Update(s) verfügbar";
        var result = _activatedGames
            .Where(g => g.Target.SteamAppId is int)
            .Select(g => new GameUpdateInfo(g.Target.SteamAppId!.Value, installedCount, summary))
            .ToList();
        return Task.FromResult<IReadOnlyList<GameUpdateInfo>>(result);
    }

    private sealed class InstalledTab : IGameTabContribution
    {
        private readonly PakInstallService _installer;
        private readonly PakBackupService _backup;
        private readonly IcarusPaths _paths;
        private readonly DownloadEventBus _bus;
        private readonly IHostServices _host;
        private readonly NexusApiClient _api;
        private readonly NexusSettingsService _settings;
        private readonly NexusCategoryService _categories;
        private readonly InstalledUpdatesChecker _updatesChecker;
        public InstalledTab(PakInstallService installer, PakBackupService backup,
            IcarusPaths paths, DownloadEventBus bus, IHostServices host,
            NexusApiClient api, NexusSettingsService settings, NexusCategoryService categories,
            InstalledUpdatesChecker updatesChecker)
        { _installer = installer; _backup = backup; _paths = paths; _bus = bus; _host = host;
          _api = api; _settings = settings; _categories = categories; _updatesChecker = updatesChecker; }
        public string Id => "installed";
        public string Label => Strings.T("tab.installed");
        public string Icon => "\U0001F5FB"; // 🗻
        public int Order => 0;
        public bool IsVisible(DetectedGame game) => true;
        public Control CreateView(DetectedGame game, IHostServices host) =>
            new InstalledPaksView { DataContext = new InstalledPaksViewModel(
                _installer, _backup, _paths, _bus, _host, _api, _settings, _categories, _updatesChecker) };
    }

    private sealed class NexusTab : IGameTabContribution
    {
        private readonly NexusCatalogService _catalog;
        private readonly NexusSettingsService _settings;
        private readonly NexusApiClient _api;
        private readonly NexusCategoryService _categories;
        private readonly PakInstallService _installer;
        private readonly DownloadEventBus _downloadBus;
        private readonly IcarusPaths _paths;
        private readonly IHostServices _host;
        public NexusTab(NexusCatalogService catalog, NexusSettingsService settings,
            NexusApiClient api, NexusCategoryService categories,
            PakInstallService installer, DownloadEventBus downloadBus,
            IcarusPaths paths, IHostServices host)
        { _catalog = catalog; _settings = settings; _api = api; _categories = categories;
          _installer = installer; _downloadBus = downloadBus; _paths = paths; _host = host; }
        public string Id => "nexus";
        public string Label => Strings.T("tab.nexus");
        public string Icon => "\U0001F30D"; // 🌍
        public int Order => 10;
        public bool IsVisible(DetectedGame game) => true;
        public Control CreateView(DetectedGame game, IHostServices host) =>
            new NexusView { DataContext = new NexusViewModel(_catalog, _settings, _api, _categories,
                _installer, _downloadBus, _paths, _host) };
    }

    private sealed class DownloadsTab : IGameTabContribution
    {
        private readonly PakInstallService _installer;
        private readonly DownloadEventBus _bus;
        private readonly IHostServices _host;
        private readonly NexusApiClient _api;
        private readonly NexusSettingsService _settings;
        private readonly IcarusPaths _paths;
        private readonly NexusCategoryService _categories;
        public DownloadsTab(PakInstallService installer, DownloadEventBus bus, IHostServices host,
            NexusApiClient api, NexusSettingsService settings, IcarusPaths paths,
            NexusCategoryService categories)
        { _installer = installer; _bus = bus; _host = host; _api = api; _settings = settings; _paths = paths; _categories = categories; }
        public string Id => "downloads";
        public string Label => Strings.T("tab.downloads");
        public string Icon => "\U0001F4E5"; // 📥
        public int Order => 20;
        public bool IsVisible(DetectedGame game) => true;
        public Control CreateView(DetectedGame game, IHostServices host) =>
            new DownloadsView { DataContext = new DownloadsViewModel(_installer, _bus, _host, _api, _settings, _paths, _categories) };
    }

    private sealed class WorkshopTab : IGameTabContribution
    {
        private readonly DetectedGame _game;
        private readonly IHostServices _host;
        public WorkshopTab(DetectedGame game, IHostServices host) { _game = game; _host = host; }
        public string Id => "workshop";
        public string Label => Strings.T("tab.workshop");
        public string Icon => "\U0001F310"; // 🌐
        public int Order => 15;
        public bool IsVisible(DetectedGame game) => true;
        public Control CreateView(DetectedGame game, IHostServices host) =>
            new WorkshopView { DataContext = new WorkshopViewModel(_game, _host) };
    }

    // v1.15: NexusSettingsTab entfernt — der User pflegt den API-Key jetzt
    // zentral im Host-Settings-Fenster (Tab „🌐 Nexus"). Alle Nexus-basierten
    // Plugins teilen sich den Key.

    /// <summary>v1.15-Migration: prueft ob das Plugin einen alten API-Key
    /// hatte (im plugin-data/nexus.json) waehrend der Host noch keinen hat,
    /// und benachrichtigt den User via Toast dass er ihn einmalig im Host-
    /// Settings-Tab „🌐 Nexus" neu eintragen soll. Direktes Uebernehmen
    /// wuerde eine Contract-Erweiterung (SetApiKey) brauchen — fuer einen
    /// einmaligen Migrations-Schritt ist der User-Roundtrip (Copy-Paste aus
    /// nexusmods.com/users/myaccount?tab=api+access) akzeptabel.</summary>
    private void TryNotifyLegacyKeyMigration()
    {
        if (_host is null || _nexusSettings is null) return;
        if (_host.Nexus.HasApiKey) return;      // Host hat schon einen
        var legacy = _nexusSettings.GetLegacyApiKey();
        if (string.IsNullOrEmpty(legacy)) return; // Plugin hatte auch keinen
        _host.Notifications.Notify(
            "Nexus-Migration: Der API-Key wird ab jetzt zentral im Host verwaltet. " +
            "Bitte einmalig unter Einstellungen → 🌐 Nexus neu eintragen — " +
            "danach nutzen alle Plugins (Icarus, Cyberpunk 2077) denselben Key.",
            NotificationLevel.Info);
        _host.Logger.Info("Icarus v1.15 Migration: alter API-Key gefunden, User informiert");
    }
}
