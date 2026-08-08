using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace ModManager.Plugins.Icarus.Services.Nexus;

/// <summary>Lädt die Kategorien für ein Spiel einmal pro Session und cached
/// sie in-memory. Wird vom Detail-Dialog benutzt um <c>category_id</c> auf
/// einen lesbaren Namen zu mappen.</summary>
public sealed class NexusCategoryService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private readonly NexusApiClient _api;
    private readonly NexusSettingsService _settings;
    private Dictionary<int, string>? _cache;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public NexusCategoryService(NexusApiClient api, NexusSettingsService settings)
    {
        _api = api;
        _settings = settings;
    }

    public async Task<string> GetCategoryNameAsync(int categoryId, CancellationToken ct = default)
    {
        if (categoryId <= 0) return "";
        if (_cache is null) await LoadAsync(ct);
        return _cache is not null && _cache.TryGetValue(categoryId, out var name) ? name : "";
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_cache is not null) return;
            var list = await _api.GetCategoriesAsync(_settings.Current.GameSlug, ct);
            var dict = new Dictionary<int, string>(list.Count);
            foreach (var c in list) dict[c.CategoryId] = c.Name;
            _cache = dict;
            Log.Info("Nexus categories geladen: {N} für slug={Slug}",
                dict.Count, _settings.Current.GameSlug);
        }
        catch (System.Exception ex)
        {
            Log.Warn(ex, "Nexus categories konnten nicht geladen werden");
            _cache = new Dictionary<int, string>(); // negativer Cache — nicht endlos retryen
        }
        finally { _lock.Release(); }
    }
}
