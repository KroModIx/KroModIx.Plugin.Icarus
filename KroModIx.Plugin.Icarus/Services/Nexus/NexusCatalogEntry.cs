using System;

namespace KroModIx.Plugin.Icarus.Services.Nexus;

/// <summary>Ein Icarus-Mod-Eintrag aus dem Nexus-Katalog. Untermenge der
/// Nexus-API-Response — nur Felder die wir tatsächlich anzeigen.</summary>
public sealed record NexusCatalogEntry(
    int ModId,
    string Name,
    string Author,
    string Summary,
    string Category,
    string Version,
    string PictureUrl,
    DateTime UpdatedUtc,
    long Downloads,
    long Endorsements,
    bool Available)
{
    /// <summary>Die Detail-URL auf nexusmods.com — der User klickt hier zum
    /// Download (Free-Users müssen den Slow-Download-Wall durchklicken,
    /// direkter API-Download nur mit Premium-Account).</summary>
    public string DetailUrl(string gameSlug) =>
        $"https://www.nexusmods.com/{gameSlug}/mods/{ModId}";
}
