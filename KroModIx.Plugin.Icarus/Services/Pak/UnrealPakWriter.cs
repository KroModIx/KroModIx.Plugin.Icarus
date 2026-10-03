using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NLog;

namespace KroModIx.Plugin.Icarus.Services.Pak;

/// <summary>Schreibt ein unkomprimiertes, unverschlüsseltes Pak der Version
/// 11 mit vollständigem dreiteiligem Index (Primärindex, Pfad-Hash-Index,
/// vollständiger Verzeichnisindex) und dem 221-Byte-Footer.
///
/// <para><see cref="AddFile"/> puffert im Speicher, <see cref="Write"/> gibt
/// alles nach Pfad sortiert aus — gleiche Eingabe ergibt also byte-identische
/// Ausgabe, unabhängig von der Aufrufreihenfolge. Mod-Paks sind klein (die
/// komplette <c>data.pak</c> von Icarus hat 2,4 MB), das Puffern kostet also
/// kaum etwas, und reproduzierbare Ausgabe ist mehr wert: nur so bleibt ein
/// gebautes Pak über Neubauten hinweg stabil, und nur so lässt sich im Test
/// auf Bytes prüfen.</para>
///
/// <para>Portiert aus go-unrealpak (MIT, Donovan C. Young).</para></summary>
internal sealed class UnrealPakWriter
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly List<(string Path, byte[] Data)> _files = [];
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly string _mountPoint;

    public UnrealPakWriter(string mountPoint = UnrealPakFormat.IcarusContentMountPoint)
        => _mountPoint = mountPoint;

    public int FileCount => _files.Count;

    /// <summary>Nimmt einen Eintrag auf. Nichts erreicht die Platte, bis
    /// <see cref="Write"/> läuft.</summary>
    public void AddFile(string mountPath, byte[] data)
    {
        // Dateien auf oberster Ebene werden im Verzeichnisindex unter „/"
        // geschluesselt, der kanonische Pfad traegt aber keinen fuehrenden
        // Schraegstrich — und HashPath verlangt ihn ohne.
        var rel = mountPath.Replace('\\', '/').TrimStart('/');
        if (rel.Length == 0)
            throw new ArgumentException("Leerer Mount-Pfad.", nameof(mountPath));
        if (!_seen.Add(rel))
            throw new InvalidOperationException($"Pfad doppelt im Pak: {rel}");
        _files.Add((rel, data));
    }

    public bool Contains(string mountPath)
        => _seen.Contains(mountPath.Replace('\\', '/').TrimStart('/'));

    /// <summary>Baut den Datenbereich und alle drei Index-Strukturen und
    /// schreibt sie samt Footer nach <paramref name="targetPath"/>.
    ///
    /// <para>Geschrieben wird über <c>.tmp</c> und <c>File.Move</c>: ein
    /// abgebrochener Lauf darf kein halbes Pak im Mods-Ordner hinterlassen,
    /// das das Spiel beim nächsten Start zu laden versucht.</para></summary>
    public void Write(string targetPath)
    {
        if (_files.Count == 0)
            throw new InvalidOperationException("Ein Pak ohne Einträge wäre sinnlos.");

        var files = _files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();

        // Datenbereich und kodierte Index-Saetze in einem Durchlauf. Jeder
        // Nutzdatenblock hat seinen 53-Byte-Kopf davor, die Eintraege liegen
        // ohne Fuellbytes hintereinander.
        using var data = new MemoryStream();
        using var encoded = new MemoryStream();
        var locations = new Dictionary<string, int>(files.Count, StringComparer.Ordinal);

        foreach (var (path, content) in files)
        {
            long offset = data.Length;
            long size = content.Length;
            if (offset > uint.MaxValue || size > uint.MaxValue)
                throw new InvalidOperationException(
                    $"{path}: Offset oder Größe überschreitet die 32-Bit-Form, die dieser Writer ausgibt.");

            var header = UnrealPakFormat.StoredEntryHeader(size, content);
            data.Write(header, 0, header.Length);
            data.Write(content, 0, content.Length);

            if (encoded.Length > int.MaxValue)
                throw new InvalidOperationException(
                    $"{path}: der kodierte Index überschreitet das 32-Bit-Positionsfeld.");
            locations[path] = (int)encoded.Length;

            // Der 12-Byte-Satz fuer einen gespeicherten Eintrag: Bits 31/30/29
            // melden Offset, UncompressedSize und Size als 32-Bit, Methode 0,
            // keine Kompressionsbloecke. Size selbst wird bei Methode 0 nicht
            // serialisiert (es ist gleich UncompressedSize), deshalb folgen
            // nur zwei uint32.
            WriteUInt32(encoded, 0xE0000000);
            WriteUInt32(encoded, (uint)offset);
            WriteUInt32(encoded, (uint)size);
        }

        var fdi = BuildFullDirectoryIndex(files, locations);
        var phi = BuildPathHashIndex(files, locations);

        var phiHash = SHA1.HashData(phi);
        var fdiHash = SHA1.HashData(fdi);
        long indexOffset = data.Length;

        // Der Primaerindex haelt die absoluten Offsets der beiden Unterindizes,
        // die ihm folgen; seine eigene Laenge haengt nicht von deren Werten ab.
        // Also einmal mit Nullen messen, dann mit den echten Offsets bauen.
        var sizing = UnrealPakFormat.BuildPrimaryIndex(_mountPoint, files.Count,
            UnrealPakFormat.WriterSeed, 0, 0, phiHash, 0, 0, fdiHash, encoded.ToArray());
        long phiOffset = indexOffset + sizing.Length;
        long fdiOffset = phiOffset + phi.Length;
        var index = UnrealPakFormat.BuildPrimaryIndex(_mountPoint, files.Count,
            UnrealPakFormat.WriterSeed, phiOffset, phi.Length, phiHash,
            fdiOffset, fdi.Length, fdiHash, encoded.ToArray());
        var indexHash = SHA1.HashData(index);

        var tmp = targetPath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            using (var fs = File.Create(tmp))
            {
                // Die Bereiche kacheln die Datei luecken- und ueberlappungsfrei,
                // genau wie in jedem echten Pak: Daten | Primaerindex |
                // Pfad-Hash-Index | Verzeichnisindex | Footer.
                data.Position = 0;
                data.CopyTo(fs);
                fs.Write(index);
                fs.Write(phi);
                fs.Write(fdi);
                fs.Write(UnrealPakFormat.BuildFooter(
                    UnrealPakFormat.WriteVersion, indexOffset, index.Length, indexHash));
            }
            File.Move(tmp, targetPath, overwrite: true);
            Log.Info("Pak geschrieben: {Path} ({Count} Einträge)", targetPath, files.Count);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* Temp-Rest */ }
            throw;
        }
    }

    /// <summary>Vollständiger Verzeichnisindex: Verzeichnis → Datei →
    /// Position des kodierten Satzes.</summary>
    private static byte[] BuildFullDirectoryIndex(
        List<(string Path, byte[] Data)> files, Dictionary<string, int> locations)
    {
        var byDir = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (path, _) in files)
        {
            var (dir, name) = UnrealPakFormat.SplitMountPath(path);
            if (!byDir.TryGetValue(dir, out var list))
            {
                list = [];
                byDir[dir] = list;
            }
            list.Add(name);
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        w.Write(byDir.Count);
        foreach (var (dir, names) in byDir)
        {
            UnrealPakFormat.WriteFString(w, dir);
            names.Sort(StringComparer.Ordinal);
            w.Write(names.Count);
            foreach (var name in names)
            {
                UnrealPakFormat.WriteFString(w, name);
                w.Write(locations[(dir + name).TrimStart('/')]);
            }
        }
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Pfad-Hash-Index, danach ein leerer beschnittener
    /// Verzeichnisindex.</summary>
    private static byte[] BuildPathHashIndex(
        List<(string Path, byte[] Data)> files, Dictionary<string, int> locations)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        w.Write(files.Count);
        foreach (var (path, _) in files)
        {
            w.Write(UnrealPakFormat.HashPath(path, UnrealPakFormat.WriterSeed));
            w.Write(locations[path]);
        }
        w.Write(0); // beschnittener Index: 0 Verzeichnisse
        w.Flush();
        return ms.ToArray();
    }

    private static void WriteUInt32(Stream s, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        s.Write(b);
    }
}
