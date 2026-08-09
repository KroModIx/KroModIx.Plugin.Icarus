using System;

namespace KroModIx.Plugin.Icarus.Services;

/// <summary>Wo der PAK-Mod herkommt — bestimmt was mit ihm gemacht werden
/// darf. Workshop-Mods sind read-only (Steam verwaltet sie); Manual-Mods
/// können aktiviert/deaktiviert/deinstalliert werden.</summary>
public enum PakModSource
{
    /// <summary>Manuell in <c>Content/Paks/mods/</c> abgelegt (Toggle + Uninstall erlaubt).</summary>
    Manual,
    /// <summary>Steam-Workshop-Abo unter <c>steamapps/workshop/content/1149460/&lt;id&gt;/</c>
    /// (read-only; Uninstall geht nur über Steam).</summary>
    Workshop,
}

/// <summary>Ein PAK-Mod im Icarus-Mods-Ordner oder Steam-Workshop.
/// <see cref="FilePath"/> endet auf <c>.pak</c> (aktiv) oder <c>.pak.disabled</c>
/// (inaktiv). <see cref="WorkshopId"/> ist die Steam-Workshop-Item-ID (nur bei
/// <see cref="PakModSource.Workshop"/> gesetzt, sonst 0).</summary>
public sealed record InstalledPakMod(
    string FilePath,
    string FileName,
    long FileSizeBytes,
    DateTime InstalledUtc,
    bool IsEnabled,
    PakModSource Source,
    long WorkshopId = 0);
