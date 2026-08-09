using System;

namespace KroModIx.Plugin.Icarus.Services.Nexus;

/// <summary>Ein Datei-Eintrag eines Nexus-Mods (aus /files.json).
/// Ein Mod hat oft mehrere Files: MAIN (Main), UPDATE, OPTIONAL,
/// MISCELLANEOUS, OLD_VERSION. Wir wählen für den Auto-Download primär
/// das <see cref="IsMainAndPrimary"/>-File.</summary>
public sealed record NexusFileEntry(
    long FileId,
    string Name,
    string FileName,
    string Version,
    string Description,
    /// <summary>Nexus-Kategorie: 1=MAIN, 2=UPDATE, 3=OPTIONAL, 4=OLD,
    /// 5=MISCELLANEOUS, 6=DELETED, 7=ARCHIVED.</summary>
    int CategoryId,
    string CategoryName,
    bool IsPrimary,
    long SizeInBytes,
    DateTime UploadedUtc)
{
    /// <summary>Der beste Kandidat für Auto-Download: MAIN (category_id=1)
    /// UND als primary vom Autor markiert.</summary>
    public bool IsMainAndPrimary => CategoryId == 1 && IsPrimary;

    public bool IsMain => CategoryId == 1;
}

/// <summary>Ergebnis von <c>NexusApiClient.ValidateAsync</c>. Neben dem
/// Erfolg-Flag noch strukturiert Name + Premium-Status, damit der Aufrufer
/// die Info persistieren kann (Download-Buttons enable/disable).</summary>
public sealed record NexusValidateResult(bool Ok, string UserName, bool IsPremium, string Info);
