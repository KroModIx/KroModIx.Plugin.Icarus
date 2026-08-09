using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using ModManager.PluginContracts;
using ModManager.Plugins.Icarus.Services;
using ModManager.Plugins.Icarus.Services.Nexus;
using ModManager.Plugins.Icarus.Views;

namespace ModManager.Plugins.Icarus;

public sealed class IcarusPlugin : IGameModPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "kroste.icarus",
        DisplayName: "Icarus Mod-Manager",
        Version: "0.4.0",
        Author: "Kroste",
        Description: "Mod-Manager für Icarus (RocketWerkz). Manuelle PAK-Mods im " +
            "Content/Paks/mods-Ordner UND Steam-Workshop-Abos werden gemeinsam gelistet " +
            "(Workshop-Rows read-only). Nexus-Mods-Katalog mit Personal-API-Key. " +
            "Auto-Refresh via FileSystemWatcher, Backup/Restore, Kroste-Card-Look.");

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
    private DownloadEventBus? _downloadBus;
    private readonly Dictionary<string, PakInstallService> _installers = new();
    private readonly Dictionary<string, PakBackupService> _backups = new();
    private readonly IcarusPathResolver _pathResolver = new();

    public Task InitializeAsync(IHostServices host, IReadOnlyList<DetectedGame> activatedGames, CancellationToken ct)
    {
        _host = host;
        _paths = new IcarusPaths(host);
        _nexusSettings = new NexusSettingsService(_paths, host.Secrets);
        _nexusApi = new NexusApiClient(
            host.CreateHttpClient("nexus"),
            () => _nexusSettings.GetApiKey());
        _nexusCatalog = new NexusCatalogService(_nexusApi, _nexusSettings, _paths);
        _nexusCategories = new NexusCategoryService(_nexusApi, _nexusSettings);
        _downloadBus = new DownloadEventBus();

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
            host.Logger.Info("Icarus initialisiert: manual={Manual}, workshop={Workshop}, downloads={Downloads}",
                manualDir, workshopDir ?? "(none)", _paths.DownloadsDir);
        }
        return Task.CompletedTask;
    }

    public IEnumerable<IGameTabContribution> GetTabContributions(DetectedGame game)
    {
        if (!_installers.TryGetValue(game.Target.GameId, out var installer) || _host is null
            || _paths is null || _downloadBus is null || _nexusSettings is null
            || _nexusApi is null || _nexusCatalog is null || _nexusCategories is null
            || !_backups.TryGetValue(game.Target.GameId, out var backup))
            yield break;

        yield return new InstalledTab(installer, backup, _paths, _downloadBus, _host);
        yield return new NexusTab(_nexusCatalog, _nexusSettings, _nexusApi, _nexusCategories,
            installer, _downloadBus, _paths, _host);
        yield return new DownloadsTab(installer, _downloadBus, _host);
        yield return new NexusSettingsTab(_nexusSettings, _nexusApi, _host);
    }

    public Task ShutdownAsync()
    {
        _nexusApi?.Dispose();
        _host?.Logger.Info("Icarus shutdown");
        return Task.CompletedTask;
    }

    private sealed class InstalledTab : IGameTabContribution
    {
        private readonly PakInstallService _installer;
        private readonly PakBackupService _backup;
        private readonly IcarusPaths _paths;
        private readonly DownloadEventBus _bus;
        private readonly IHostServices _host;
        public InstalledTab(PakInstallService installer, PakBackupService backup,
            IcarusPaths paths, DownloadEventBus bus, IHostServices host)
        { _installer = installer; _backup = backup; _paths = paths; _bus = bus; _host = host; }
        public string Id => "installed";
        public string Label => "Installiert";
        public string Icon => "\U0001F5FB"; // 🗻
        public int Order => 0;
        public bool IsVisible(DetectedGame game) => true;
        public Control CreateView(DetectedGame game, IHostServices host) =>
            new InstalledPaksView { DataContext = new InstalledPaksViewModel(_installer, _backup, _paths, _bus, _host) };
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
        public string Label => "Nexus";
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
        public DownloadsTab(PakInstallService installer, DownloadEventBus bus, IHostServices host)
        { _installer = installer; _bus = bus; _host = host; }
        public string Id => "downloads";
        public string Label => "Downloads";
        public string Icon => "\U0001F4E5"; // 📥
        public int Order => 20;
        public bool IsVisible(DetectedGame game) => true;
        public Control CreateView(DetectedGame game, IHostServices host) =>
            new DownloadsView { DataContext = new DownloadsViewModel(_installer, _bus, _host) };
    }

    private sealed class NexusSettingsTab : IGameTabContribution
    {
        private readonly NexusSettingsService _settings;
        private readonly NexusApiClient _api;
        private readonly IHostServices _host;
        public NexusSettingsTab(NexusSettingsService settings, NexusApiClient api, IHostServices host)
        { _settings = settings; _api = api; _host = host; }
        public string Id => "nexus-settings";
        public string Label => "Nexus-Einstellungen";
        public string Icon => "\U0001F511"; // 🔑
        public int Order => 30;
        public bool IsVisible(DetectedGame game) => true;
        public Control CreateView(DetectedGame game, IHostServices host) =>
            new NexusSettingsView { DataContext = new NexusSettingsViewModel(_settings, _api, _host) };
    }
}
