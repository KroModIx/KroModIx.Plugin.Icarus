using System;

namespace ModManager.Plugins.Icarus.Services;

/// <summary>Ein PAK-Mod im Icarus-Mods-Ordner (Unreal-Engine-Pakete).
/// FilePath endet auf <c>.pak</c> (aktiv) oder <c>.pak.disabled</c> (inaktiv).</summary>
public sealed record InstalledPakMod(
    string FilePath,
    string FileName,
    long FileSizeBytes,
    DateTime InstalledUtc,
    bool IsEnabled);
