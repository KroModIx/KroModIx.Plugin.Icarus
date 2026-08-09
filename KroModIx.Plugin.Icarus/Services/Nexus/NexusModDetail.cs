using System;

namespace KroModIx.Plugin.Icarus.Services.Nexus;

/// <summary>Volles Mod-Detail von <c>GET /v1/games/{slug}/mods/{id}.json</c>.
/// Untermenge der Nexus-Response — nur die Felder die wir im Detail-Dialog
/// tatsächlich anzeigen.</summary>
public sealed record NexusModDetail(
    int ModId,
    string Name,
    string Author,
    string Summary,
    /// <summary>HTML-Description (mit &lt;p&gt;, &lt;br /&gt;, &lt;a&gt;, &lt;h1&gt; usw.).
    /// Vor Anzeige mit <see cref="HtmlStrip.ToPlainText"/> säubern.</summary>
    string DescriptionHtml,
    string Version,
    string PictureUrl,
    int CategoryId,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    long EndorsementCount,
    bool ContainsAdultContent,
    bool Available,
    string DomainName);

/// <summary>Kategorie-Eintrag aus <c>GET /v1/games/{slug}/categories.json</c>.
/// Nexus-API liefert nur category_id in den Mod-Responses — wir mappen hier
/// auf den lesbaren Namen.</summary>
public sealed record NexusCategory(int CategoryId, string Name, int ParentCategoryId);
