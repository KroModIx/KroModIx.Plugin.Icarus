using System;

namespace KroModIx.Plugin.Icarus.Services;

/// <summary>Plugin-interner Event-Bus für Cross-Tab-Refresh. Konsistenz-
/// Regel (aus LS25-v0.11.1-Lesson): JEDER Mutation-Weg feuert das passende
/// Event — nicht nur der offensichtliche. Sonst muss der User „Aktualisieren"
/// klicken damit die Liste stimmt.
///
/// <para>Producer im Icarus-Plugin (Stand v0.2.0):</para>
/// <list type="bullet">
/// <item><b>DownloadsChanged</b>: NexusViewModel (Browser-Download-Return via
///   FileSystemWatcher), DownloadsViewModel (Delete)</item>
/// <item><b>ModInstalled</b>: DownloadsViewModel.InstallRow,
///   InstalledPaksViewModel.InstallFromFileAsync + InstallDroppedPak,
///   PakBackupService.RestoreBackupAsync (via Wrapper im VM)</item>
/// </list>
///
/// <para>Alle Consumer müssen Handler auf UI-Thread posten
/// (<c>Dispatcher.UIThread.Post</c>), weil der Event auf dem Producer-
/// Thread feuert.</para>
/// </summary>
public sealed class DownloadEventBus
{
    /// <summary>Datei im Downloads-Ordner erschienen/verschwunden.
    /// Payload = Dateiname (kann <c>null</c>/leer sein wenn Watcher nur
    /// „irgendwas hat sich geändert" signalisiert).</summary>
    public event EventHandler<string>? DownloadsChanged;

    /// <summary>Mod im Mods-Ordner installiert. Payload = Dateiname.</summary>
    public event EventHandler<string>? ModInstalled;

    public void RaiseDownloadsChanged(string fileName)
        => DownloadsChanged?.Invoke(this, fileName);

    public void RaiseModInstalled(string fileName)
        => ModInstalled?.Invoke(this, fileName);
}
