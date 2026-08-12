using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KroModIx.Plugin.Contracts;
using NLog;

namespace KroModIx.Plugin.Icarus.Services.Nexus;

/// <summary>Adapter um <see cref="INexusService"/> aus dem Host (Contracts
/// v1.14.0+). Icarus behält seine plugin-lokalen Records (weil <c>long</c>-
/// Feld-Typen und <see cref="NexusCatalogEntry.DetailUrl"/>-Extension), aber
/// der HTTP-Layer + API-Key-Management ist jetzt zentral im Host.
///
/// <para>Migration von Pre-v1.15: bis dahin hatte Icarus einen eigenen
/// HttpClient + eigene API-Key-Persistenz in <c>plugin-data/kroste.icarus/
/// nexus.json</c>. Beim ersten Start ab v1.15.0 zeigt <see cref="IcarusPlugin"/>
/// eine Toast-Notification wenn der Host-Key fehlt aber der alte Plugin-Key
/// da war — der User traegt ihn dann einmalig neu im Host-Settings-Tab
/// „🌐 Nexus" ein.</para></summary>
public sealed class NexusApiClient
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private readonly INexusService _hostNexus;

    public NexusApiClient(INexusService hostNexus)
    {
        _hostNexus = hostNexus;
    }

    /// <summary>True wenn der Host-Nexus einen API-Key hat.</summary>
    public bool HasApiKey => _hostNexus.HasApiKey;

    public async Task<NexusValidateResult> ValidateAsync(CancellationToken ct = default)
    {
        var r = await _hostNexus.ValidateAsync(ct);
        return new NexusValidateResult(r.Valid, r.UserName, r.IsPremium, r.Message);
    }

    public async Task<IReadOnlyList<NexusCatalogEntry>> GetCatalogAsync(
        string gameSlug, string endpoint, CancellationToken ct = default)
    {
        var list = await _hostNexus.GetLatestModsAsync(gameSlug, endpoint, ct);
        var result = new List<NexusCatalogEntry>(list.Count);
        foreach (var e in list)
        {
            result.Add(new NexusCatalogEntry(
                ModId: e.ModId,
                Name: e.Name,
                Author: e.Author,
                Summary: e.Summary,
                Category: e.Category,
                Version: e.Version,
                PictureUrl: e.PictureUrl,
                UpdatedUtc: e.UpdatedUtc,
                Downloads: e.Downloads,
                Endorsements: e.Endorsements,
                Available: e.Available));
        }
        return result;
    }

    public async Task<IReadOnlyList<int>> GetUpdatedModIdsAsync(
        string gameSlug, string period, CancellationToken ct = default)
        => await _hostNexus.GetUpdatedModIdsAsync(gameSlug, period, ct);

    public async Task<NexusModDetail?> GetModDetailAsync(
        string gameSlug, int modId, CancellationToken ct = default)
    {
        var d = await _hostNexus.GetModDetailAsync(gameSlug, modId, ct);
        if (d is null) return null;
        return new NexusModDetail(
            ModId: d.ModId,
            Name: d.Name,
            Author: d.Author,
            Summary: d.Summary,
            DescriptionHtml: d.DescriptionHtml,
            Version: d.Version,
            PictureUrl: d.PictureUrl,
            CategoryId: d.CategoryId,
            CreatedUtc: d.CreatedUtc,
            UpdatedUtc: d.UpdatedUtc,
            EndorsementCount: d.EndorsementCount,
            ContainsAdultContent: d.ContainsAdultContent,
            Available: d.Available,
            DomainName: d.DomainName);
    }

    public async Task<IReadOnlyList<NexusFileEntry>> GetFilesAsync(
        string gameSlug, int modId, CancellationToken ct = default)
    {
        var list = await _hostNexus.GetFilesAsync(gameSlug, modId, ct);
        var result = new List<NexusFileEntry>(list.Count);
        foreach (var f in list)
        {
            result.Add(new NexusFileEntry(
                FileId: f.FileId,
                Name: f.Name,
                FileName: f.FileName,
                Version: f.Version,
                Description: f.Description,
                CategoryId: f.CategoryId,
                CategoryName: f.CategoryName,
                IsPrimary: f.IsPrimary,
                SizeInBytes: f.SizeInBytes,
                UploadedUtc: f.UploadedUtc));
        }
        return result;
    }

    public async Task<string?> GetDownloadLinkAsync(string gameSlug, int modId, long fileId,
        CancellationToken ct = default)
        => await _hostNexus.GetDownloadLinkAsync(gameSlug, modId, fileId, ct);

    public async Task<IReadOnlyList<NexusCategory>> GetCategoriesAsync(string gameSlug,
        CancellationToken ct = default)
    {
        var list = await _hostNexus.GetCategoriesAsync(gameSlug, ct);
        var result = new List<NexusCategory>(list.Count);
        foreach (var c in list)
            result.Add(new NexusCategory(c.CategoryId, c.Name,
                c.ParentCategoryId ?? 0)); // Icarus-Convention: 0 = Root
        return result;
    }
}

// Records (NexusValidateResult, NexusModDetail, NexusCategory, NexusFileEntry,
// NexusCatalogEntry) leben unveraendert in ihren eigenen Dateien in diesem
// Namespace — die Signaturen sind identisch zu den Contract-Records, nur
// die "long"-Felder machen den Contract-Cast noetig (im Adapter oben).
