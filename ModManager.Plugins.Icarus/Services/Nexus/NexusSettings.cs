namespace ModManager.Plugins.Icarus.Services.Nexus;

/// <summary>Plugin-lokale Nexus-Konfiguration. API-Key wird via
/// <c>IHostServices.Secrets</c> verschlüsselt gespeichert (DPAPI/AES),
/// nicht als Klartext in der JSON.</summary>
public sealed class NexusSettings
{
    /// <summary>Verschlüsseltes API-Key-Blob (Format <c>v1:&lt;base64&gt;</c>
    /// aus <see cref="ModManager.PluginContracts.ISecretProtection"/>).
    /// Empty = kein Key konfiguriert.</summary>
    public string ApiKeyProtected { get; set; } = "";

    /// <summary>Nexus-Slug für Icarus. Default <c>icarus</c> — kann in
    /// Settings überschrieben werden falls Nexus den Slug umbenennt.</summary>
    public string GameSlug { get; set; } = "icarus";

    /// <summary>Cache-Alter (Stunden), nach dem der Katalog vom API neu
    /// geladen wird. Nexus-Rate-Limit: 2500 requests/h für personal-keys —
    /// 24 h reichen locker.</summary>
    public int CatalogRefreshHours { get; set; } = 24;

    /// <summary>Welcher Katalog-Endpunkt beim ersten Load geöffnet wird
    /// (latest_added / latest_updated / trending).</summary>
    public string DefaultCatalog { get; set; } = "latest_updated";

    /// <summary>Cached Premium-Status aus dem letzten <c>Verify</c>-Call.
    /// Bestimmt ob Download-Buttons im Nexus-Tab enabled sind (Nexus liefert
    /// direkte Download-URLs nur für Premium-Konten — für Free-User → 403).</summary>
    public bool IsPremium { get; set; }

    /// <summary>Wann der Premium-Status zuletzt geprüft wurde. Wenn älter
    /// als ~7 Tage, im UI einen „bitte neu verifizieren"-Hint zeigen.</summary>
    public System.DateTime? LastVerifiedUtc { get; set; }

    /// <summary>Anzeige-Name des Nexus-Accounts (aus /users/validate.json).</summary>
    public string UserName { get; set; } = "";
}
