using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using ModManager.PluginContracts;
using ModManager.Plugins.Icarus.Services;
using ModManager.Plugins.Icarus.Views;

namespace ModManager.Plugins.Icarus;

public sealed class IcarusPlugin : IGameModPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "kroste.icarus",
        DisplayName: "Icarus Mod-Manager",
        Version: "0.1.0",
        Author: "Kroste",
        Description: "PAK-Mod-Verwaltung für Icarus.");

    public IReadOnlyList<GameTarget> Targets { get; } = new[]
    {
        new GameTarget("icarus", "Icarus",
            SteamAppId: 1149460,
            AlternativeExecutableNames: new[] { "Icarus.exe", "IcarusClient.exe" },
            Platforms: Platforms.Both),
    };

    private IHostServices? _host;
    private readonly Dictionary<string, PakInstallService> _installers = new();
    private readonly IcarusPathResolver _paths = new();

    public Task InitializeAsync(IHostServices host, IReadOnlyList<DetectedGame> activatedGames, CancellationToken ct)
    {
        _host = host;
        foreach (var game in activatedGames)
        {
            var modsDir = _paths.GetModsDir(game);
            if (modsDir is null)
            {
                host.Logger.Warn("Icarus: konnte keinen Mods-Pfad ableiten für {Game}", game.Target.DisplayName);
                continue;
            }
            _installers[game.Target.GameId] = new PakInstallService(modsDir);
            host.Logger.Info("Icarus initialisiert: Mods-Ordner = {Path}", modsDir);
        }
        return Task.CompletedTask;
    }

    public IEnumerable<IGameTabContribution> GetTabContributions(DetectedGame game)
    {
        if (!_installers.TryGetValue(game.Target.GameId, out var installer) || _host is null)
            yield break;
        yield return new InstalledPaksTab(installer, _host);
    }

    public Task ShutdownAsync()
    {
        _host?.Logger.Info("Icarus shutdown");
        return Task.CompletedTask;
    }

    private sealed class InstalledPaksTab : IGameTabContribution
    {
        private readonly PakInstallService _installer;
        private readonly IHostServices _host;

        public InstalledPaksTab(PakInstallService installer, IHostServices host)
        {
            _installer = installer;
            _host = host;
        }

        public string Id => "installed";
        public string Label => "Installiert";
        public string Icon => "\U0001F5FB"; // 🗻 (Berg-Motiv passt zu Icarus/Prometheus)
        public int Order => 0;
        public bool IsVisible(DetectedGame game) => true;

        public Control CreateView(DetectedGame game, IHostServices host) =>
            new InstalledPaksView { DataContext = new InstalledPaksViewModel(_installer, _host) };
    }
}
