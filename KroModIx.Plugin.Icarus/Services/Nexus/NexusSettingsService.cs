using System;
using System.IO;
using System.Text.Json;
using KroModIx.Plugin.Contracts;
using NLog;

namespace KroModIx.Plugin.Icarus.Services.Nexus;

/// <summary>Load/Save von <see cref="NexusSettings"/> als JSON. Atomar
/// (tmp+move), defensiv bei kaputter Datei (nach .broken sichern, neu
/// starten). API-Key läuft über <c>IHostServices.Secrets</c> — hier nur
/// das verschlüsselte Blob.</summary>
public sealed class NexusSettingsService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly IcarusPaths _paths;
    private readonly ISecretProtection _secrets;

    public NexusSettings Current { get; private set; } = new();

    public NexusSettingsService(IcarusPaths paths, ISecretProtection secrets)
    {
        _paths = paths;
        _secrets = secrets;
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

    /// <summary>Verschlüsselt den API-Key und persistiert ihn. Leerer Key
    /// löscht die gespeicherte Verschlüsselung.</summary>
    public void SetApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            Current.ApiKeyProtected = "";
        else
            Current.ApiKeyProtected = _secrets.Protect(apiKey) ?? "";
        Save();
    }

    /// <summary>Liefert den entschlüsselten API-Key oder leeren String wenn
    /// nichts gesetzt/entschlüsselbar.</summary>
    public string GetApiKey()
    {
        if (string.IsNullOrEmpty(Current.ApiKeyProtected)) return "";
        try { return _secrets.Unprotect(Current.ApiKeyProtected) ?? ""; }
        catch (Exception ex)
        {
            Log.Warn(ex, "Nexus-API-Key konnte nicht entschlüsselt werden");
            return "";
        }
    }

    public bool HasApiKey => !string.IsNullOrEmpty(Current.ApiKeyProtected);
}
