using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using KroModIx.Plugin.Contracts;

namespace KroModIx.Plugin.Icarus.Tests;

/// <summary>Attrappen der Host-Baukästen für die Plugin-Tests.
///
/// <para><b>Warum Attrappen und nicht die echten.</b> Seit v1.25.0 liegen
/// Archiv- und Pak-Arbeit im Host; das Plugin-Testprojekt referenziert nur
/// die Contracts, nicht die Host-App. Das ist nicht nur eine technische
/// Grenze, sondern die richtige Testgrenze: das Plugin verantwortet
/// <b>welche</b> Tabelle gepatcht und <b>wohin</b> ein Eintrag sortiert
/// wird. Ob der Pak-Container danach byte-korrekt auf der Platte liegt,
/// prüft der Host (<c>HostUnrealPakServiceTests</c>,
/// <c>RealUnrealPakTests</c>) — hier noch einmal mitzuprüfen würde die
/// Tests nur an ein Dateiformat binden, das das Plugin nicht mehr
/// verantwortet.</para></summary>
internal sealed class FakeUnrealPakService : IUnrealPakService
{
    /// <summary>Kennung am Dateianfang, damit <see cref="IsPakFile"/> ein
    /// Attrappen-Pak von allem anderen unterscheiden kann.</summary>
    private const string Marker = "FAKEPAK1";

    private sealed record Payload(string MountPoint, Dictionary<string, string> Entries);

    public bool IsPakFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var r = new StreamReader(path);
            var head = new char[Marker.Length];
            return r.Read(head, 0, head.Length) == head.Length
                   && new string(head) == Marker;
        }
        catch { return false; }
    }

    public IUnrealPakReader OpenRead(string path)
    {
        if (!IsPakFile(path))
            throw new UnsupportedPakFormatException($"Kein Attrappen-Pak: {path}");
        var json = File.ReadAllText(path)[Marker.Length..];
        var p = JsonSerializer.Deserialize<Payload>(json)
                ?? throw new UnsupportedPakFormatException($"Attrappen-Pak leer: {path}");
        return new Reader(p);
    }

    public IUnrealPakBuilder CreateBuilder(string? mountPoint = null)
        => new Builder(mountPoint ?? "../../../");

    private sealed class Reader(Payload p) : IUnrealPakReader
    {
        public string MountPoint => p.MountPoint;

        /// <summary>Ein stabiler Fingerabdruck über den Inhalt — dieselbe
        /// Rolle wie der echte Index-Hash, nur billiger gerechnet.</summary>
        public string IndexHash => Convert.ToHexStringLower(
            System.Security.Cryptography.SHA1.HashData(
                Encoding.UTF8.GetBytes(string.Join('\n',
                    p.Entries.OrderBy(e => e.Key, StringComparer.Ordinal)
                             .Select(e => e.Key + ":" + e.Value)))));

        public IReadOnlyList<UnrealPakEntry> Entries => p.Entries
            .Select(e => new UnrealPakEntry(e.Key, Convert.FromBase64String(e.Value).Length))
            .ToList();

        public bool Contains(string mountRelativePath) => p.Entries.ContainsKey(mountRelativePath);

        public byte[] Read(string mountRelativePath)
            => p.Entries.TryGetValue(mountRelativePath, out var b64)
                ? Convert.FromBase64String(b64)
                : throw new FileNotFoundException($"Im Pak nicht enthalten: {mountRelativePath}");

        public void Dispose() { }
    }

    private sealed class Builder(string mountPoint) : IUnrealPakBuilder
    {
        private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);

        public int Count => _entries.Count;

        public void Add(string mountPath, byte[] data)
        {
            var rel = mountPath.Replace('\\', '/').TrimStart('/');
            if (rel.Length == 0) throw new ArgumentException("Leerer Mount-Pfad.", nameof(mountPath));
            if (!_entries.TryAdd(rel, Convert.ToBase64String(data)))
                throw new InvalidOperationException($"Pfad doppelt im Pak: {rel}");
        }

        public bool Contains(string mountPath)
            => _entries.ContainsKey(mountPath.Replace('\\', '/').TrimStart('/'));

        public void Write(string targetPath)
        {
            if (_entries.Count == 0)
                throw new InvalidOperationException("Ein Pak ohne Einträge wäre sinnlos.");
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.WriteAllText(targetPath,
                Marker + JsonSerializer.Serialize(new Payload(mountPoint, _entries)));
        }
    }
}

/// <summary>Attrappe des Archiv-Baukastens. Nur ZIP — die Plugin-Tests
/// erzeugen ihre Archive selbst, und für die Frage „liegt das Richtige am
/// richtigen Platz" ist das Format gleichgültig.</summary>
internal sealed class FakeArchiveService : IArchiveService
{
    private static readonly string[] Exts = [".zip", ".rar", ".7z"];

    public IReadOnlyList<string> SupportedExtensions => Exts;

    public bool HasSupportedExtension(string path)
        => Exts.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    public ArchiveKind DetectKind(string path)
    {
        try
        {
            if (!File.Exists(path)) return ArchiveKind.Unknown;
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[2];
            return fs.Read(head) == 2 && head[0] == 0x50 && head[1] == 0x4B
                ? ArchiveKind.Zip : ArchiveKind.Unknown;
        }
        catch { return ArchiveKind.Unknown; }
    }

    public IReadOnlyList<ArchiveEntry> List(string archivePath)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        return zip.Entries
            .Where(e => e.Name.Length > 0)
            .Select(e => new ArchiveEntry(e.FullName.Replace('\\', '/'), e.Length))
            .ToList();
    }

    public ArchiveExtractResult Extract(string archivePath, string targetDir,
        ArchiveExtractOptions? options = null)
    {
        var opts = options ?? new ArchiveExtractOptions();
        var prefix = string.IsNullOrEmpty(opts.StripPrefix)
            ? null : opts.StripPrefix!.Replace('\\', '/').Trim('/') + "/";
        var extracted = new List<string>();
        var skipped = new List<string>();

        Directory.CreateDirectory(targetDir);
        using var zip = ZipFile.OpenRead(archivePath);
        foreach (var e in zip.Entries)
        {
            if (e.Name.Length == 0) continue;
            var key = e.FullName.Replace('\\', '/');
            if (opts.Filter is not null && !opts.Filter(key)) continue;

            var rel = key;
            if (prefix is not null)
            {
                if (!rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                rel = rel[prefix.Length..];
            }
            if (opts.Flatten) rel = rel.Split('/')[^1];
            if (rel.Length == 0) continue;

            if (!TryResolveSafe(targetDir, rel, out var dst))
            {
                skipped.Add(key);
                continue;
            }
            if (File.Exists(dst) && !opts.Overwrite)
                throw new IOException($"Datei existiert bereits: {dst}");
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            e.ExtractToFile(dst, overwrite: true);
            extracted.Add(dst);
        }
        return new ArchiveExtractResult(extracted, skipped);
    }

    public void ExtractEntry(string archivePath, string entryPath, string destinationFile)
    {
        var wanted = entryPath.Replace('\\', '/');
        using var zip = ZipFile.OpenRead(archivePath);
        var e = zip.Entries.FirstOrDefault(x => x.FullName.Replace('\\', '/') == wanted)
                ?? throw new FileNotFoundException($"Im Archiv nicht enthalten: {entryPath}", archivePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
        e.ExtractToFile(destinationFile, overwrite: true);
    }

    public bool TryResolveSafe(string root, string relative, out string destination)
    {
        destination = "";
        if (string.IsNullOrWhiteSpace(relative)) return false;
        if (relative.Length >= 3 && char.IsAsciiLetter(relative[0]) && relative[1] == ':')
            return false;
        var rel = relative.Replace('\\', Path.DirectorySeparatorChar)
                          .Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(rel)) return false;
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
        string full;
        try { full = Path.GetFullPath(Path.Combine(rootFull, rel)); }
        catch { return false; }
        if (!full.StartsWith(rootFull, StringComparison.Ordinal)) return false;
        destination = full;
        return true;
    }
}
