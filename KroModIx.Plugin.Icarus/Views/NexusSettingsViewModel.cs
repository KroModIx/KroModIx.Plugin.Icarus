using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KroModIx.Plugin.Contracts;
using KroModIx.Plugin.Icarus.Services.Nexus;

namespace KroModIx.Plugin.Icarus.Views;

/// <summary>VM für den Nexus-Einstellungen-Tab. API-Key eintragen +
/// verschlüsselt speichern + Verify-Button gegen die Nexus-<c>validate.json</c>.
/// Diese Config ist plugin-spezifisch (kein Cross-Cutting Concern) — daher
/// eigener Settings-Tab, nicht im Host-Settings-Fenster.</summary>
public sealed partial class NexusSettingsViewModel : ObservableObject
{
    private readonly NexusSettingsService _settings;
    private readonly NexusApiClient _api;
    private readonly IHostServices _host;

    public NexusSettingsViewModel(NexusSettingsService settings, NexusApiClient api, IHostServices host)
    {
        _settings = settings;
        _api = api;
        _host = host;
        // Wir zeigen den Key NIE im Klartext — nur Placeholder wenn schon einer da ist.
        HasKey = _settings.HasApiKey;
        GameSlug = _settings.Current.GameSlug;
        RefreshHours = _settings.Current.CatalogRefreshHours;
    }

    [ObservableProperty] private string _apiKeyInput = "";
    [ObservableProperty] private bool _hasKey;
    [ObservableProperty] private string _gameSlug = "icarus";
    [ObservableProperty] private int _refreshHours = 24;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVerifyResult))]
    private string _verifyResult = "";
    public bool HasVerifyResult => !string.IsNullOrEmpty(VerifyResult);

    [ObservableProperty] private bool _verifyBusy;

    [RelayCommand]
    private void SaveApiKey()
    {
        if (string.IsNullOrWhiteSpace(ApiKeyInput))
        {
            _host.Notifications.Notify("Bitte API-Key eintragen.", NotificationLevel.Warning);
            return;
        }
        _settings.SetApiKey(ApiKeyInput.Trim());
        ApiKeyInput = "";
        HasKey = true;
        _host.Notifications.Notify("Nexus-API-Key gespeichert (verschlüsselt).",
            NotificationLevel.Success);
    }

    [RelayCommand]
    private void ClearApiKey()
    {
        _settings.SetApiKey("");
        HasKey = false;
        VerifyResult = "";
        _host.Notifications.Notify("Nexus-API-Key gelöscht.", NotificationLevel.Info);
    }

    [RelayCommand]
    private async Task VerifyAsync()
    {
        if (!_settings.HasApiKey)
        {
            VerifyResult = "Kein API-Key gespeichert.";
            return;
        }
        VerifyBusy = true;
        try
        {
            var result = await _api.ValidateAsync();
            VerifyResult = result.Ok ? $"✔ {result.Info}" : $"✘ {result.Info}";
            if (result.Ok)
            {
                // Premium-Status persistieren — Nexus-Tab prüft das beim
                // Rendern der Download-Buttons.
                _settings.Current.IsPremium = result.IsPremium;
                _settings.Current.UserName = result.UserName;
                _settings.Current.LastVerifiedUtc = DateTime.UtcNow;
                _settings.Save();
            }
        }
        catch (Exception ex)
        {
            VerifyResult = $"✘ {ex.Message}";
        }
        finally { VerifyBusy = false; }
    }

    [RelayCommand]
    private void SaveGeneral()
    {
        _settings.Current.GameSlug = string.IsNullOrWhiteSpace(GameSlug) ? "icarus" : GameSlug.Trim();
        _settings.Current.CatalogRefreshHours = Math.Clamp(RefreshHours, 1, 168);
        _settings.Save();
        _host.Notifications.Notify("Einstellungen gespeichert.", NotificationLevel.Success);
    }

    [RelayCommand]
    private void OpenNexusAccount() =>
        _host.Shell.OpenExternalUrl("https://www.nexusmods.com/users/myaccount?tab=api%20access");
}
