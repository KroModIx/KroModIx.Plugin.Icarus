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
    /// <summary>UE4SS-Lua-Mod unter <c>Icarus/Binaries/Win64/Mods/&lt;Name&gt;/</c>
    /// (v1.23.0). Toggle und Uninstall sind erlaubt, laufen aber anders als
    /// bei den PAKs: nicht über eine Dateiendung, sondern über die
    /// <c>enabled.txt</c> im Mod-Ordner.</summary>
    Ue4ssLua,
}

/// <summary>Eine installierte Icarus-Mod — PAK im Mods-Ordner, Steam-Workshop-
/// Abo oder (ab v1.23.0) UE4SS-Lua-Mod.
///
/// <para>Bei den PAK-Quellen endet <see cref="FilePath"/> auf <c>.pak</c>
/// (aktiv) oder <c>.pak.disabled</c> (inaktiv). Bei
/// <see cref="PakModSource.Ue4ssLua"/> zeigt <see cref="FilePath"/> auf den
/// <b>Ordner</b> der Mod und <see cref="FileName"/> ist dessen Name — UE4SS
/// lädt pro Ordner, nicht pro Datei.</para>
///
/// <para><see cref="WorkshopId"/> ist die Steam-Workshop-Item-ID (nur bei
/// <see cref="PakModSource.Workshop"/> gesetzt, sonst 0).
/// <see cref="ScriptCount"/> ist die Zahl der <c>.lua</c>-Dateien (nur bei
/// <see cref="PakModSource.Ue4ssLua"/>, sonst 0).</para></summary>
public sealed record InstalledPakMod(
    string FilePath,
    string FileName,
    long FileSizeBytes,
    DateTime InstalledUtc,
    bool IsEnabled,
    PakModSource Source,
    long WorkshopId = 0,
    int ScriptCount = 0);
