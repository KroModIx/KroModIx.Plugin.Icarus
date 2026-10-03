using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KroModIx.Plugin.Contracts;
using KroModIx.Plugin.Icarus.Services;
using KroModIx.Plugin.Icarus.Services.Exmodz;

namespace KroModIx.Plugin.Icarus.Views;

/// <summary>Der Datentabellen-Teil des Installiert-Tabs: Zustand des
/// gemeinsam gebauten Paks und der Knopf, der es neu baut.
///
/// <para><b>Warum der Zustand überhaupt angezeigt werden muss:</b> ein
/// Datentabellen-Mod wirkt nicht über eine Datei, die man im Mods-Ordner
/// sehen könnte, sondern über ein Pak, das aus den Basistabellen des Spiels
/// gerechnet ist. Aktualisiert Icarus diese Tabellen — und das tut es
/// wöchentlich —, passt das alte Pak nicht mehr: es würde die Werte der
/// Vorwoche zurückdrehen. Das hat kein Symptom, das von „die Mod tut nichts"
/// unterscheidbar wäre. Deshalb prüft der Tab bei jedem Aktualisieren, ob die
/// <c>data.pak</c> noch dieselbe ist, und sagt es.</para></summary>
public sealed partial class InstalledPaksViewModel
{
    private ExmodzService? Exmodz => _installer.Exmodz;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowExmodzSection))]
    private bool _isExmodzSupported;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RebuildExmodzCommand))]
    private bool _exmodzBusy;

    /// <summary>Wahr, wenn das gebaute Pak nicht mehr zum aktuellen Stand
    /// passt — der Fall, in dem der Neubau-Knopf dringlich ist.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RebuildExmodzCommand))]
    private bool _exmodzNeedsRebuild;

    [ObservableProperty] private string _exmodzStatusText = "";
    [ObservableProperty] private string _exmodzConflictText = "";

    public bool ShowExmodzSection => IsExmodzSupported;

    public bool HasExmodzConflict => ExmodzConflictText.Length > 0;

    partial void OnExmodzConflictTextChanged(string value) => OnPropertyChanged(nameof(HasExmodzConflict));

    private bool CanRebuildExmodz => IsExmodzSupported && !ExmodzBusy;

    private void RefreshExmodzStatus()
    {
        var svc = Exmodz;
        IsExmodzSupported = svc?.IsSupported ?? false;
        if (svc is null || !IsExmodzSupported)
        {
            ExmodzNeedsRebuild = false;
            ExmodzStatusText = svc is null ? "" : Strings.T("exmodz.unsupported");
            ExmodzConflictText = "";
            return;
        }

        var active = svc.ListInstalled().Count(m => m.Enabled);
        var staleness = svc.CheckStaleness();
        var merged = svc.Store.MergedState;

        (ExmodzStatusText, ExmodzNeedsRebuild) = staleness switch
        {
            ExmodzStore.Staleness.NothingToBuild => (Strings.T("exmodz.none"), false),
            ExmodzStore.Staleness.NeverBuilt =>
                (string.Format(Strings.T("exmodz.never_built"), active), true),
            ExmodzStore.Staleness.FileMissing => (Strings.T("exmodz.file_missing"), true),
            ExmodzStore.Staleness.GameUpdated => (Strings.T("exmodz.game_updated"), true),
            ExmodzStore.Staleness.ModsChanged => (Strings.T("exmodz.mods_changed"), true),
            _ => (string.Format(Strings.T("exmodz.up_to_date"), active,
                    merged?.BuiltUtc.ToLocalTime().ToString("g") ?? "?",
                    merged?.TableCount ?? 0, merged?.AssetCount ?? 0), false),
        };

        // Ein fremdes zusammengebautes Pak im selben Ordner wuerde unseres
        // nach Alphabet ueberstimmen. Nicht anfassen — es ist nicht unsere
        // Datei —, aber sagen.
        var conflicts = svc.FindConflictingMergedPaks();
        ExmodzConflictText = conflicts.Count == 0
            ? ""
            : string.Format(Strings.T("exmodz.conflict_pak"), string.Join(", ", conflicts));
    }

    [RelayCommand(CanExecute = nameof(CanRebuildExmodz))]
    private async Task RebuildExmodzAsync()
    {
        if (Exmodz is not ExmodzService svc) return;
        ExmodzBusy = true;
        try
        {
            using var scope = _host.BeginProgress(Strings.T("exmodz.rebuilding"));
            // Off-UI: der Zusammenbau liest die betroffenen Basistabellen aus
            // der data.pak und schreibt bis zu einige Megabyte JSON. Bei
            // D_ItemsStatic allein sind das 7,4 MB — auf dem UI-Thread waere
            // das ein sichtbarer Aussetzer.
            var result = await Task.Run(svc.Rebuild);

            foreach (var w in result.Warnings)
                _host.Notifications.Notify(w, NotificationLevel.Warning);
            foreach (var f in result.FailedMods)
                _host.Logger.Warn("Datentabellen-Mod nicht eingebaut: {Failure}", f);

            if (result.Ok)
            {
                _host.Notifications.Notify(
                    string.Format(Strings.T("exmodz.rebuild_ok"), result.Message),
                    result.FailedMods.Count == 0 ? NotificationLevel.Success : NotificationLevel.Warning);
            }
            else
            {
                _host.Notifications.Notify(
                    string.Format(Strings.T("exmodz.rebuild_failed"), result.Message),
                    NotificationLevel.Error);
            }
            Refresh();
        }
        catch (Exception ex)
        {
            _host.Logger.Warn(ex, "Datentabellen-Zusammenbau fehlgeschlagen");
            _host.Notifications.Notify(
                string.Format(Strings.T("exmodz.rebuild_failed"), ex.Message), NotificationLevel.Error);
        }
        finally { ExmodzBusy = false; }
    }
}
