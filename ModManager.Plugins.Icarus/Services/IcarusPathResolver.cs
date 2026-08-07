using System;
using System.IO;
using ModManager.PluginContracts;

namespace ModManager.Plugins.Icarus.Services;

/// <summary>
/// Löst den Mods-Ordner für Icarus auf. Icarus ist ein Unreal-Engine-Spiel
/// und akzeptiert PAK-Mods im <c>Icarus/Content/Paks/~mods/</c>-Unterordner
/// der Spiel-Installation (Steam-Standard).
/// </summary>
public sealed class IcarusPathResolver
{
    public string? GetModsDir(DetectedGame game)
    {
        if (string.IsNullOrEmpty(game.InstallDir) || !Directory.Exists(game.InstallDir))
            return null;
        return Path.Combine(game.InstallDir, "Icarus", "Content", "Paks", "~mods");
    }
}
