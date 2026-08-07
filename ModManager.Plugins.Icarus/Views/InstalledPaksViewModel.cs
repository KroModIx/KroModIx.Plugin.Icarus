using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModManager.PluginContracts;
using ModManager.Plugins.Icarus.Services;

namespace ModManager.Plugins.Icarus.Views;

public sealed partial class InstalledPaksViewModel : ObservableObject
{
    private readonly PakInstallService _installer;
    private readonly IHostServices _host;

    public InstalledPaksViewModel(PakInstallService installer, IHostServices host)
    {
        _installer = installer;
        _host = host;
        ModsDir = installer.ModsDir;
        RefreshCommand.Execute(null);
    }

    public string ModsDir { get; }

    public ObservableCollection<PakRow> Mods { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private PakRow? _selected;

    public bool HasSelection => Selected is not null;

    [ObservableProperty]
    private string _summary = "";

    partial void OnSelectedChanged(PakRow? value) => OnPropertyChanged(nameof(HasSelection));

    [RelayCommand]
    private void Refresh()
    {
        Mods.Clear();
        try
        {
            foreach (var m in _installer.ListInstalled()
                         .OrderByDescending(m => m.IsEnabled)
                         .ThenBy(m => m.FileName, StringComparer.CurrentCultureIgnoreCase))
                Mods.Add(new PakRow(m));
            var enabled = Mods.Count(r => r.Source.IsEnabled);
            Summary = Mods.Count == 0
                ? "Keine PAK-Mods im ~mods/-Ordner."
                : $"{enabled} aktiv / {Mods.Count} total";
        }
        catch (Exception ex)
        {
            _host.Logger.Warn(ex, "Icarus: Mod-Liste-Load fehlgeschlagen");
            Summary = "Fehler beim Lesen des ~mods/-Ordners.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ToggleEnabled()
    {
        if (Selected is null) return;
        try
        {
            var updated = _installer.SetEnabled(Selected.Source, !Selected.Source.IsEnabled);
            _host.Notifications.Notify(
                $"Mod {(updated.IsEnabled ? "aktiviert" : "deaktiviert")}: {updated.FileName}",
                NotificationLevel.Success);
            Refresh();
        }
        catch (Exception ex)
        {
            _host.Notifications.Notify($"Fehler: {ex.Message}", NotificationLevel.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task UninstallAsync()
    {
        if (Selected is null) return;
        bool ok = await _host.Dialogs.ConfirmAsync(
            "PAK-Mod deinstallieren",
            $"„{Selected.Source.FileName}“ wirklich löschen?",
            okLabel: "Löschen", cancelLabel: "Abbrechen");
        if (!ok) return;
        try
        {
            _installer.Uninstall(Selected.Source);
            _host.Notifications.Notify($"Deinstalliert: {Selected.Source.FileName}", NotificationLevel.Success);
            Refresh();
        }
        catch (Exception ex)
        {
            _host.Notifications.Notify($"Fehler: {ex.Message}", NotificationLevel.Error);
        }
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
            _host.Notifications.Notify($"Installiert: {installed.FileName}", NotificationLevel.Success);
            Refresh();
        }
        catch (Exception ex)
        {
            _host.Notifications.Notify($"Fehler: {ex.Message}", NotificationLevel.Error);
        }
    }

    [RelayCommand]
    private void OpenModsFolder() => _host.Shell.OpenDirectory(ModsDir);
}

public sealed class PakRow
{
    public InstalledPakMod Source { get; }
    public PakRow(InstalledPakMod source) => Source = source;
    public string FileName => Source.FileName;
    public bool IsEnabled => Source.IsEnabled;
    public string StateLabel => Source.IsEnabled ? "aktiv" : "inaktiv";
    public string Size => Source.FileSizeBytes < 1024 * 1024
        ? $"{Source.FileSizeBytes / 1024.0:F0} KB"
        : $"{Source.FileSizeBytes / 1024.0 / 1024.0:F1} MB";
}
