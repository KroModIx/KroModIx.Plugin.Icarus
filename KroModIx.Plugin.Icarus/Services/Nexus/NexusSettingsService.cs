using System;
using System.IO;
using System.Text.Json;
using KroModIx.Plugin.Contracts;
using NLog;

namespace KroModIx.Plugin.Icarus.Services.Nexus;

/// <summary>Ab Icarus v1.15.0 nur noch schmale Fassade auf
/// <see cref="INexusService"/> (Host-zentraler Nexus-Baukasten). API-Key +
/// Persistenz + Verschlüsselung sind komplett im Host — der User pflegt
/// den Key im Host-Settings-Fenster (Tab „🌐 Nexus"), alle Nexus-basierten
/// Plugins (Icarus, Cyberpunk 2077, …) teilen ihn.
///
/// <para>Migration: die alte <c>plugin-data/kroste.icarus/nexus.json</c>
/// wird von <see cref="IcarusPlugin.InitializeAsync"/> gelesen — wenn der
/// Host noch keinen Key hat aber Icarus einen alten hatte, zeigt das
/// Plugin eine Toast-Notification die den User zum Host-Settings-Tab
/// führt (Migration muss der User einmalig manuell durchklicken —
/// direktes Key-Übernehmen erfordert eine Contract-Erweiterung, die für
/// einen einmaligen Migrations-Schritt overkill wäre).</para></summary>
public sealed class NexusSettingsService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly IcarusPaths _paths;
    private readonly ISecretProtection _secrets;
    private readonly INexusService _hostNexus;

    /// <summary>Legacy: die alten Icarus-Nexus-Settings (Genres/Filter/Baseline)
    /// die NICHT den API-Key betreffen. API-Key ist ab v1.15 im Host — der
    /// Wert hier bleibt fuer Migration-Detection erhalten.</summary>
    public NexusSettings Current { get; private set; } = new();

    public NexusSettingsService(IcarusPaths paths, ISecretProtection secrets, INexusService hostNexus)
    {
        _paths = paths;
        _secrets = secrets;
        _hostNexus = hostNexus;
        Load();
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(_paths.NexusSettingsPath))
            {
                Current = new NexusSettings();
                return;
            }
            var json = File.ReadAllText(_paths.NexusSettingsPath);
            Current = JsonSerializer.Deserialize<NexusSettings>(json) ?? new NexusSettings();
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "NexusSettings-Load fehlgeschlagen — Datei nach .broken sichern");
            try { File.Move(_paths.NexusSettingsPath, _paths.NexusSettingsPath + ".broken", overwrite: true); }
            catch { /* egal */ }
            Current = new NexusSettings();
        }
    }

    public void Save()
    {
        try
        {
            var tmp = _paths.NexusSettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOpts));
            File.Move(tmp, _paths.NexusSettingsPath, overwrite: true);
        }
        catch (Exception ex) { Log.Warn(ex, "NexusSettings-Save fehlgeschlagen"); }
    }

    /// <summary>Delegiert an den Host — Icarus verwaltet den Key nicht mehr
    /// selbst. Wird noch von altem Code aufgerufen (Views); wirft eine
    /// klare NotSupportedException damit die Aufrufer migriert werden.
    /// Nach der v1.15-Migration alle Callsites entfernen.</summary>
    [Obsolete("Ab Icarus v1.15: API-Key wird im Host-Settings-Tab '🌐 Nexus' gesetzt.")]
    public void SetApiKey(string apiKey) => throw new NotSupportedException(
        "Nexus-API-Key wird ab KroModIx v1.14 zentral im Host-Settings-Fenster " +
        "(Tab '🌐 Nexus') verwaltet — nicht mehr im Plugin.");

    /// <summary>Legacy-Reader für die Migration (siehe IcarusPlugin.
    /// TryMigrateLegacyApiKey). Sonst nicht mehr benutzen — das Plugin
    /// braucht den Key nie direkt, es geht ueber _host.Nexus.</summary>
    public string GetLegacyApiKey()
    {
        if (string.IsNullOrEmpty(Current.ApiKeyProtected)) return "";
        try { return _secrets.Unprotect(Current.ApiKeyProtected) ?? ""; }
        catch (Exception ex)
        {
            Log.Warn(ex, "Legacy-Nexus-API-Key konnte nicht entschlüsselt werden");
            return "";
        }
    }

    /// <summary>Delegiert an <see cref="INexusService.HasApiKey"/>. Alle
    /// Callsites die früher hier fragten funktionieren unveraendert weiter,
    /// bekommen aber den Host-Status statt Plugin-Status.</summary>
    public bool HasApiKey => _hostNexus.HasApiKey;
}
