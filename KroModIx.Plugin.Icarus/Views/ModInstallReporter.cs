using KroModIx.Plugin.Contracts;
using KroModIx.Plugin.Icarus.Services;

namespace KroModIx.Plugin.Icarus.Views;

/// <summary>Meldet das Ergebnis eines Install-Vorgangs an den User — eine
/// Stelle für alle Tabs, damit dieselbe Lage nicht je Tab anders
/// beschrieben wird.
///
/// <para>Der Grund für eine eigene Klasse ist der Teil-Erfolg. Ein
/// Icarus-Mod-Archiv bringt bis zu drei Dinge mit (PAKs, UE4SS-Lua-Mods,
/// Datentabellen-Mods), und v1.23 kann die dritte Art noch nicht einbauen.
/// Ein pauschales „installiert" wäre in dem Fall eine Falschaussage: der
/// User sieht Erfolg, sucht im Spiel das neue Item und findet es nicht.</para></summary>
internal static class ModInstallReporter
{
    /// <summary>Gibt zurück, ob wirklich etwas installiert wurde — der
    /// Aufrufer entscheidet daran, ob er <c>ModInstalled</c> auf dem
    /// Ereignis-Bus feuert und die Liste neu lädt.</summary>
    public static bool Report(IHostServices host, ModInstallResult result, string? prefixKey = null)
    {
        if (!result.InstalledAnything)
        {
            // Der einzige Weg hierher: ein Archiv, das ausschliesslich
            // .EXMODZ enthaelt.
            host.Notifications.Notify(
                string.Format(Strings.T("notify.install_exmodz_only"),
                    result.SourceFileName, result.ExmodzFiles.Count),
                NotificationLevel.Warning);
            return false;
        }

        var what = result.Describe();
        var message = prefixKey is null
            ? string.Format(Strings.T("notify.install_ok"), what)
            : Strings.T(prefixKey) + what;

        var level = NotificationLevel.Success;
        if (result.Warning is { Length: > 0 } warning)
        {
            message += " " + string.Format(Strings.T("notify.install_lua_failed"), warning);
            level = NotificationLevel.Warning;
        }
        if (result.HasPendingExmodz)
        {
            message += " " + string.Format(Strings.T("notify.install_exmodz_pending"),
                result.ExmodzFiles.Count);
            if (level == NotificationLevel.Success) level = NotificationLevel.Info;
        }

        host.Notifications.Notify(message, level);
        return true;
    }
}
