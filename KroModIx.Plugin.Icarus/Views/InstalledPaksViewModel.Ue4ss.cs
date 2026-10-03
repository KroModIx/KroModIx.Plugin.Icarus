using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KroModIx.Plugin.Contracts;
using KroModIx.Plugin.Icarus.Services;
using KroModIx.Plugin.Icarus.Services.Ue4ss;

namespace KroModIx.Plugin.Icarus.Views;

/// <summary>Der UE4SS-Teil des Installiert-Tabs: Zustand des Lua-Mod-Loaders
/// und die zwei Knöpfe, die ihn in Gang bringen.
///
/// <para>Eigene Datei, weil <c>InstalledPaksViewModel</c> schon über 800
/// Zeilen hat — der UE4SS-Block hängt nur über den gemeinsamen
/// <c>Refresh</c> mit dem Rest zusammen und liest sich getrennt
/// besser.</para>
///
/// <para><b>Warum der Zustand bei jedem Refresh neu gelesen wird und nicht
/// gemerkt:</b> beide Voraussetzungen können hinter dem Rücken des Plugins
/// verschwinden. Eine Spieldatei-Prüfung in Steam räumt die Loader-DLLs weg,
/// und ein neu angelegtes Proton-Präfix nimmt die DLL-Umleitung mit. Ein
/// gemerkter Zustand würde dann „alles in Ordnung" behaupten, während die
/// Lua-Mods still nichts tun.</para></summary>
public sealed partial class InstalledPaksViewModel
{
    private Ue4ssLuaModService? Lua => _installer.Lua;

    // ---- Zustand ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUe4ssSection))]
    private bool _isUe4ssSupported;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallUe4ssCommand))]
    private bool _isUe4ssInstalled;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallUe4ssCommand))]
    [NotifyCanExecuteChangedFor(nameof(FixProtonOverrideCommand))]
    private bool _ue4ssBusy;

    /// <summary>Wahr, wenn ein Proton-Präfix da ist, die DLL-Umleitung aber
    /// fehlt — der Fall, in dem UE4SS installiert ist und trotzdem nie
    /// lädt.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FixProtonOverrideCommand))]
    private bool _needsProtonFix;

    [ObservableProperty] private string _ue4ssStatusText = "";
    [ObservableProperty] private string _ue4ssProtonText = "";
    [ObservableProperty] private string _ue4ssLoaderText = "";

    /// <summary>Der Abschnitt bleibt verborgen, solange das
    /// Win64-Verzeichnis nicht gefunden wurde — dann ist nichts einzurichten
    /// und ein Knopf wäre nur eine Einladung zu einer Fehlermeldung.</summary>
    public bool ShowUe4ssSection => IsUe4ssSupported;

    private bool CanInstallUe4ss => IsUe4ssSupported && !Ue4ssBusy;
    private bool CanFixProton => NeedsProtonFix && !Ue4ssBusy;

    /// <summary>Liest den UE4SS-Zustand von der Platte und füllt die
    /// Anzeige-Eigenschaften. Reines Datei- und Registry-Lesen, läuft im
    /// Millisekundenbereich — darf vom UI-Thread kommen.</summary>
    private void RefreshUe4ssStatus()
    {
        var paths = Lua?.Paths;
        IsUe4ssSupported = paths?.IsGameLayoutKnown ?? false;
        if (paths is null || !IsUe4ssSupported)
        {
            IsUe4ssInstalled = false;
            NeedsProtonFix = false;
            Ue4ssStatusText = Strings.T("ue4ss.layout_unknown");
            Ue4ssProtonText = "";
            Ue4ssLoaderText = "";
            return;
        }

        IsUe4ssInstalled = paths.IsLoaderInstalled();
        Ue4ssStatusText = IsUe4ssInstalled
            ? Strings.T("ue4ss.installed")
            : Strings.T("ue4ss.not_installed");

        // Proton-Praefix: unter Windows null, unter Linux erst nach dem
        // ersten Spielstart gesetzt.
        var prefix = _game?.ProtonPrefix;
        if (string.IsNullOrEmpty(prefix))
        {
            NeedsProtonFix = false;
            // Ohne Praefix ist nichts kaputt — entweder Windows (dort
            // braucht es keine Umleitung) oder das Spiel lief noch nie.
            Ue4ssProtonText = OperatingSystem.IsWindows()
                ? ""
                : Strings.T("ue4ss.proton_no_prefix");
        }
        else if (ProtonDllOverride.IsSet(prefix))
        {
            NeedsProtonFix = false;
            Ue4ssProtonText = Strings.T("ue4ss.proton_ok");
        }
        else
        {
            NeedsProtonFix = true;
            Ue4ssProtonText = Strings.T("ue4ss.proton_missing");
        }

        // Der einzige echte Beleg, dass der Loader beim letzten Spielstart
        // auch geladen WURDE: seine Logdatei.
        if (!IsUe4ssInstalled)
            Ue4ssLoaderText = "";
        else if (paths.LoaderLastLoadedUtc() is DateTime at)
            Ue4ssLoaderText = string.Format(Strings.T("ue4ss.loader_loaded_at"),
                at.ToLocalTime().ToString("g"));
        else
            Ue4ssLoaderText = Strings.T("ue4ss.loader_never_loaded");
    }

    // ---- Befehle ----

    [RelayCommand(CanExecute = nameof(CanInstallUe4ss))]
    private async Task InstallUe4ssAsync()
    {
        if (Lua?.Paths is not Ue4ssPaths paths) return;
        Ue4ssBusy = true;
        try
        {
            using var scope = _host.BeginProgress(Strings.T("ue4ss.installing"));
            using var http = _host.CreateHttpClient("ue4ss");
            var bootstrapper = new Ue4ssBootstrapper(http);
            var progress = new Progress<double>(f => scope.Report(f, Strings.T("ue4ss.installing")));
            var result = await bootstrapper.InstallAsync(paths, progress);

            _host.Notifications.Notify(result.Message,
                result.Ok ? NotificationLevel.Success : NotificationLevel.Error);
            if (!result.Ok) return;

            // Direkt im Anschluss die DLL-Umleitung setzen. Ohne sie ist der
            // Loader unter Proton installiert und wirkungslos — das waere ein
            // halber Erfolg, den der User erst beim naechsten Spielstart als
            // Fehlschlag erlebt.
            TrySetProtonOverride(announceWhenAlreadySet: false);
            Refresh();
        }
        catch (Exception ex)
        {
            _host.Logger.Warn(ex, "UE4SS-Installation fehlgeschlagen");
            _host.Notifications.Notify(Strings.T("notify.error_prefix") + ex.Message,
                NotificationLevel.Error);
        }
        finally { Ue4ssBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanFixProton))]
    private void FixProtonOverride()
    {
        TrySetProtonOverride(announceWhenAlreadySet: true);
        RefreshUe4ssStatus();
    }

    private void TrySetProtonOverride(bool announceWhenAlreadySet)
    {
        var prefix = _game?.ProtonPrefix;
        switch (ProtonDllOverride.Ensure(prefix))
        {
            case DllOverrideResult.Added:
                _host.Notifications.Notify(Strings.T("ue4ss.proton_added"), NotificationLevel.Success);
                break;
            case DllOverrideResult.AlreadySet:
                if (announceWhenAlreadySet)
                    _host.Notifications.Notify(Strings.T("ue4ss.proton_ok"), NotificationLevel.Info);
                break;
            case DllOverrideResult.NoPrefix:
                // Unter Windows ist das der Normalfall und keine Meldung wert.
                if (!OperatingSystem.IsWindows())
                    _host.Notifications.Notify(Strings.T("ue4ss.proton_no_prefix"),
                        NotificationLevel.Info);
                break;
            default:
                _host.Notifications.Notify(Strings.T("ue4ss.proton_failed"), NotificationLevel.Error);
                break;
        }
    }

    [RelayCommand]
    private void OpenUe4ssFolder()
    {
        var dir = Lua?.Paths.FindModsDir() ?? Lua?.Paths.Win64Dir;
        if (dir is null)
        {
            _host.Notifications.Notify(Strings.T("ue4ss.layout_unknown"), NotificationLevel.Warning);
            return;
        }
        _host.Shell.OpenDirectory(dir);
    }
}
