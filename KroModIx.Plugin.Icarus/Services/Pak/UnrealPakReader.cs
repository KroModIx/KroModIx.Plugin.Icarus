using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace KroModIx.Plugin.Icarus.Services.Pak;

/// <summary>Ein Eintrag im Pak, wie ihn <see cref="UnrealPakReader.Files"/>
/// meldet. <see cref="Path"/> ist mount-relativ, etwa
/// <c>Items/D_ItemsStatic.json</c>.</summary>
public sealed record PakFileEntry(string Path, long Size);

/// <summary>Liest ein unverschlüsseltes Pak der UE4-Reihe. Gespeicherte und
/// Zlib-komprimierte Einträge sind lesbar; jedes andere Verfahren (Oodle)
/// ist ein harter Fehler statt einer stillen Teilausgabe.
///
/// <para>Portiert aus go-unrealpak (MIT, Donovan C. Young). Für Icarus' eigene
/// <c>Content/Data/data.pak</c> relevant: sie legt 40 Tabellen unkomprimiert
/// ab und komprimiert die anderen 258 mit Zlib — beides also mit der
/// Standardbibliothek erreichbar, ohne Oodle.</para>
///
/// <para><b>Die Kompressionstabelle muss aus dem eigenen Footer kommen</b>
/// und darf nicht angenommen werden: Icarus' <c>data.pak</c> deklariert
/// <c>["Zlib"]</c> (Index 1 heißt dort Zlib), ihre <c>pakchunk</c>-Dateien
/// dagegen <c>["Oodle","Zlib"]</c> (Index 1 heißt dort Oodle). Die Tabelle
/// eines Paks auf ein anderes anzuwenden ist genau die Verwechslung, die in
/// der Vorlage einmal zu der falschen Schlussfolgerung geführt hat, Icarus'
/// Datentabellen seien Oodle-komprimiert und damit unerreichbar.</para></summary>
internal sealed class UnrealPakReader : IDisposable
{
    private readonly FileStream _f;
    private readonly long _fileSize;
    private readonly List<ReaderEntry> _entries;
    private readonly string[] _methods;

    private sealed record ReaderEntry(
        string Path, long UncompressedSize, long Offset, int Method, long Size, int Blocks);

    private UnrealPakReader(FileStream f, long fileSize, List<ReaderEntry> entries,
        string[] methods, string mountPoint, byte[] indexHash)
    {
        _f = f;
        _fileSize = fileSize;
        _entries = entries;
        _methods = methods;
        MountPoint = mountPoint;
        IndexHash = Convert.ToHexStringLower(indexHash);
    }

    /// <summary>Der Mount-Point aus dem Primärindex dieses Paks.</summary>
    public string MountPoint { get; }

    /// <summary>Die im Footer vermerkte SHA1 des Primärindex als
    /// Kleinbuchstaben-Hex — ein billiger, stabiler Fingerabdruck des
    /// Pak-Inhalts. Jede Änderung an Inhalt oder Layout ändert den
    /// Primärindex und damit diesen Wert, ohne dass die (oft riesige) Datei
    /// selbst gehasht werden muss.</summary>
    public string IndexHash { get; }

    public static UnrealPakReader Open(string path)
    {
        var f = File.OpenRead(path);
        try
        {
            var fileSize = f.Length;
            var footer = ReadFooter(f, fileSize);
            if (footer.EncryptedIndex)
                throw new UnsupportedPakFormatException($"{path}: verschlüsselter Index.");

            var indexBuf = ReadRegion(f, footer.IndexOffset, footer.IndexSize, fileSize, footer.IndexHash);
            var (mountPoint, entries) = ParseIndex(f, indexBuf, fileSize);
            return new UnrealPakReader(f, fileSize, entries, footer.Methods, mountPoint, footer.IndexHash);
        }
        catch
        {
            f.Dispose();
            throw;
        }
    }

    public void Dispose() => _f.Dispose();

    public IReadOnlyList<PakFileEntry> Files()
        => _entries.Select(e => new PakFileEntry(e.Path, e.UncompressedSize)).ToList();

    public bool Contains(string path) => _entries.Any(e => e.Path == path);

    /// <summary>Liefert die Bytes des Eintrags am mount-relativen Pfad.
    ///
    /// <para>Der Index zeigt auf den FPakEntry-Kopf, nicht auf die Nutzdaten.
    /// Der Kopf wird neu gelesen und <b>gegengeprüft</b> statt geglaubt:
    /// Methode und Größen müssen mit dem Index übereinstimmen, und sein
    /// Hash muss zur SHA1 der Nutzdaten passen. Echte Paks erfüllen das
    /// durchweg — eine Abweichung heißt also Beschädigung oder ein Layout,
    /// das hier falsch gelesen wurde.</para></summary>
    public byte[] ReadFile(string path)
    {
        var e = _entries.FirstOrDefault(x => x.Path == path)
            ?? throw new FileNotFoundException($"Im Pak nicht enthalten: {path}");
        if (e.Method == 0) return ReadStored(e);
        var name = MethodName(e.Method);
        if (string.Equals(name, UnrealPakFormat.ZlibMethodName, StringComparison.OrdinalIgnoreCase))
            return ReadZlib(e);
        // Oodle und alles andere bleibt ein harter Fehler — und zwar hier,
        // nicht beim Index-Lesen: so bleibt Files() fuer ein Pak nutzbar,
        // dessen Eintraege nicht alle lesbar sind.
        throw new UnsupportedPakFormatException(
            $"{path}: Kompressionsverfahren „{name}“ (Index {e.Method}) wird nicht unterstützt.");
    }

    /// <summary>Löst einen 1-basierten Methoden-Index gegen die eigene
    /// Footer-Tabelle auf. Ein Index außerhalb des Bereichs ergibt einen
    /// leeren Namen, auf den kein unterstütztes Verfahren passt — er fällt
    /// also in den Fehlerfall und wird nicht stillschweigend als
    /// „gespeichert" behandelt.</summary>
    private string MethodName(int method)
    {
        if (method < 1 || method > _methods.Length) return "";
        var name = _methods[method - 1];
        return name.Length > 0 ? name : $"unbenanntes Verfahren {method}";
    }

    private byte[] ReadStored(ReaderEntry e)
    {
        var hdr = ReadAt(e.Offset, UnrealPakFormat.StoredHeaderSize);
        var m = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(24, 4));
        if (m != 0)
            throw new UnsupportedPakFormatException(
                $"{e.Path}: komprimierte Nutzdaten (Verfahren {m}), obwohl der Index „gespeichert“ meldet.");
        var size = BinaryPrimitives.ReadInt64LittleEndian(hdr.AsSpan(8, 8));
        if (size != e.UncompressedSize)
            throw new InvalidDataException(
                $"{e.Path}: Kopf meldet {size} Byte, der Index {e.UncompressedSize}.");

        var n = ValidateAllocSize(e.UncompressedSize, _fileSize);
        var buf = ReadAt(e.Offset + UnrealPakFormat.StoredHeaderSize, n);
        if (!SHA1.HashData(buf).AsSpan().SequenceEqual(hdr.AsSpan(28, 20)))
            throw new InvalidDataException($"{e.Path}: Inhalts-Hash passt nicht.");
        return buf;
    }

    private static long CompressedHeaderSize(int blocks)
        => UnrealPakFormat.StoredHeaderSize + 4 + 16L * blocks;

    /// <summary>Liest einen Zlib-komprimierten Eintrag und setzt ihn zusammen.
    ///
    /// <para>Die Nutzdaten sind in unabhängig komprimierte Blöcke zerlegt. Die
    /// verbindliche Blocktabelle steht im Kopf des Eintrags selbst als Paare
    /// (CompressedStart, CompressedEnd), gemessen ab dem Eintrags-Offset — die
    /// optionale Blockgrößen-Liste im Index fehlt bei einem einzelnen
    /// unverschlüsselten Block und ist deshalb nicht verlässlich. Der
    /// Kopf-Hash deckt die komprimierten Bytes auf der Platte ab, nicht das
    /// Ergebnis.</para></summary>
    private byte[] ReadZlib(ReaderEntry e)
    {
        if (e.Blocks <= 0)
            throw new UnsupportedPakFormatException(
                $"{e.Path}: komprimierter Eintrag meldet {e.Blocks} Blöcke.");

        var hdrSize = CompressedHeaderSize(e.Blocks);
        var hdr = ReadAt(e.Offset, ValidateAllocSize(hdrSize, _fileSize));

        var m = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(24, 4));
        if (m != e.Method)
            throw new InvalidDataException($"{e.Path}: Kopf-Verfahren {m}, Index-Verfahren {e.Method}.");
        var size = BinaryPrimitives.ReadInt64LittleEndian(hdr.AsSpan(8, 8));
        if (size != e.Size)
            throw new InvalidDataException($"{e.Path}: Kopf-Größe {size}, Index-Größe {e.Size}.");
        var usize = BinaryPrimitives.ReadInt64LittleEndian(hdr.AsSpan(16, 8));
        if (usize != e.UncompressedSize)
            throw new InvalidDataException(
                $"{e.Path}: Kopf meldet {usize} Byte entpackt, der Index {e.UncompressedSize}.");
        var nb = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(48, 4));
        if (nb != e.Blocks)
            throw new InvalidDataException($"{e.Path}: Kopf meldet {nb} Blöcke, der Index {e.Blocks}.");

        var payload = ReadAt(e.Offset + hdrSize, ValidateAllocSize(e.Size, _fileSize));
        if (!SHA1.HashData(payload).AsSpan().SequenceEqual(hdr.AsSpan(28, 20)))
            throw new InvalidDataException($"{e.Path}: Inhalts-Hash passt nicht.");

        if (e.UncompressedSize < 0 || e.UncompressedSize > UnrealPakFormat.MaxUncompressedEntrySize)
            throw new UnsupportedPakFormatException(
                $"{e.Path}: entpackte Größe {e.UncompressedSize} überschreitet die Decke von " +
                $"{UnrealPakFormat.MaxUncompressedEntrySize} Byte.");

        using var outMs = new MemoryStream((int)e.UncompressedSize);
        for (var i = 0; i < e.Blocks; i++)
        {
            var start = BinaryPrimitives.ReadInt64LittleEndian(hdr.AsSpan(52 + i * 16, 8));
            var end = BinaryPrimitives.ReadInt64LittleEndian(hdr.AsSpan(60 + i * 16, 8));
            // Blockgrenzen sind relativ zum Eintrags-Offset und muessen
            // innerhalb des Nutzdatenbereichs hinter dem Kopf liegen.
            if (start < hdrSize || end < start || end > hdrSize + e.Size)
                throw new InvalidDataException(
                    $"{e.Path}: Block {i} spannt [{start},{end}) und liegt außerhalb der Nutzdaten.");

            var remaining = e.UncompressedSize - outMs.Length;
            using var src = new MemoryStream(payload, (int)(start - hdrSize), (int)(end - start));
            using var zs = new ZLibStream(src, CompressionMode.Decompress);
            // Hoechstens ein Byte mehr lesen als noch erlaubt ist — eine
            // gelogene UncompressedSize darf keinen unbegrenzten Lauf treiben.
            var before = outMs.Length;
            CopyAtMost(zs, outMs, remaining + 1);
            if (outMs.Length - before > remaining)
                throw new InvalidDataException(
                    $"{e.Path}: entpackte Ausgabe überschreitet die gemeldeten {e.UncompressedSize} Byte.");
        }
        if (outMs.Length != e.UncompressedSize)
            throw new InvalidDataException(
                $"{e.Path}: {outMs.Length} Byte entpackt, der Kopf meldet {e.UncompressedSize}.");
        return outMs.ToArray();
    }

    private static void CopyAtMost(Stream from, Stream to, long limit)
    {
        var buf = new byte[81920];
        while (limit > 0)
        {
            var want = (int)Math.Min(buf.Length, limit);
            var read = from.Read(buf, 0, want);
            if (read <= 0) return;
            to.Write(buf, 0, read);
            limit -= read;
        }
    }

    private byte[] ReadAt(long offset, int count)
    {
        var buf = new byte[count];
        _f.Position = offset;
        _f.ReadExactly(buf, 0, count);
        return buf;
    }

    /// <summary>Prüft ein Längenfeld aus Pak-Daten, bevor daraus eine
    /// Allokation wird: nicht negativ, nicht größer als die Pak-Datei selbst
    /// (kein echter Bereich kann größer sein als die Datei, die ihn enthält)
    /// und innerhalb von <see cref="int"/>. Ein Feld außerhalb dieses
    /// Bereichs ist Beschädigung oder ein unverstandenes Layout, nie etwas,
    /// wofür man Speicher anfordert.</summary>
    private static int ValidateAllocSize(long size, long fileSize)
    {
        if (size < 0 || size > fileSize || size > int.MaxValue)
            throw new UnsupportedPakFormatException(
                $"Größenfeld {size} ist für ein {fileSize}-Byte-Pak ungültig.");
        return (int)size;
    }

    /// <summary>Liest <paramref name="size"/> Byte an
    /// <paramref name="offset"/> und prüft sie gegen
    /// <paramref name="want"/>. Jeder Index-Bereich eines Paks der Version 11
    /// ist SHA1-gesichert: der Footer deckt den Primärindex ab, der
    /// Primärindex jeden Unterindex. Alle drei Prüfungen werden durchgesetzt
    /// — eine Abweichung ist Beschädigung, nie etwas, worüber man
    /// hinwegliest.</summary>
    private static byte[] ReadRegion(FileStream f, long offset, long size, long fileSize, byte[] want)
    {
        if (offset < 0) throw new UnsupportedPakFormatException("Negatives Bereichs-Offset.");
        var n = ValidateAllocSize(size, fileSize);
        var buf = new byte[n];
        f.Position = offset;
        f.ReadExactly(buf, 0, n);
        if (!SHA1.HashData(buf).AsSpan().SequenceEqual(want))
            throw new InvalidDataException(
                $"Hash-Abweichung im Index-Bereich bei {offset} (beschädigt oder unbekanntes Format).");
        return buf;
    }

    private sealed record Footer(int Version, long IndexOffset, long IndexSize,
        byte[] IndexHash, bool EncryptedIndex, string[] Methods);

    /// <summary>Liest die einzige Footer-Form, die hier unterstützt wird. Der
    /// Footer hat feste Größe und liegt dicht am Dateiende — es gibt also
    /// nichts zu suchen und keine andere Breite zu probieren: steht die Magic
    /// nicht dort, wo sie stehen muss, ist das kein Pak, das hier behandelt
    /// wird.</summary>
    private static Footer ReadFooter(FileStream f, long fileSize)
    {
        if (fileSize < UnrealPakFormat.FooterSize)
            throw new UnsupportedPakFormatException(
                $"Datei mit {fileSize} Byte ist kleiner als ein {UnrealPakFormat.FooterSize}-Byte-Footer.");
        var buf = new byte[UnrealPakFormat.FooterSize];
        f.Position = fileSize - UnrealPakFormat.FooterSize;
        f.ReadExactly(buf, 0, buf.Length);

        // Layout: EncryptionKeyGuid(0:16) bEncryptedIndex(16) Magic(17:21)
        // Version(21:25) IndexOffset(25:33) IndexSize(33:41) IndexHash(41:61)
        // CompressionMethods(61:221).
        if (BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(17, 4)) != UnrealPakFormat.Magic)
            throw new UnsupportedPakFormatException("Keine Pak-Magic an der erwarteten Footer-Position.");

        var version = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(21, 4));
        if (version < UnrealPakFormat.MinVersion)
            throw new UnsupportedPakFormatException(
                $"Pak-Version {version} — mindestens {UnrealPakFormat.MinVersion} nötig.");

        var methods = new string[UnrealPakFormat.MaxCompressionMethods];
        for (var i = 0; i < methods.Length; i++)
        {
            var slot = buf.AsSpan(
                UnrealPakFormat.CompressionMethodsOffset + i * UnrealPakFormat.CompressionMethodNameSize,
                UnrealPakFormat.CompressionMethodNameSize);
            methods[i] = Encoding.ASCII.GetString(slot).TrimEnd('\0');
        }

        return new Footer(
            Version: version,
            IndexOffset: BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(25, 8)),
            IndexSize: BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(33, 8)),
            IndexHash: buf.AsSpan(41, 20).ToArray(),
            EncryptedIndex: buf[16] != 0,
            Methods: methods);
    }

    /// <summary>Liest den Primärindex und darüber den vollständigen
    /// Verzeichnisindex, und löst jeden Pfad auf seinen bitgepackten Satz auf.
    ///
    /// <para>Paks der Version 11 haben kein flaches Eintrags-Array. Der
    /// Primärindex hält einen Block bitgepackter Sätze plus SHA1-gesicherte
    /// Offsets auf zwei Unterindizes. Die Aufzählung läuft über den
    /// Verzeichnisindex — er ist der einzige, der echte Pfad-Zeichenketten
    /// trägt.</para></summary>
    private static (string MountPoint, List<ReaderEntry> Entries) ParseIndex(
        FileStream f, byte[] index, long fileSize)
    {
        var c = new Cursor(index);
        var mountPoint = c.FString();
        var numEntries = c.I32();
        c.U64(); // PathHashSeed — nur der Writer braucht ihn

        var pathHash = ReadSubIndexRef(c, "Pfad-Hash-Index");
        var fullDir = ReadSubIndexRef(c, "vollständiger Verzeichnisindex");
        var encoded = c.Bytes(c.I32());
        var nonEncoded = c.I32();
        if (nonEncoded != 0)
            throw new UnsupportedPakFormatException(
                $"{nonEncoded} nicht kodierte Index-Einträge werden nicht unterstützt.");

        // Den Hash des Pfad-Hash-Index pruefen, obwohl die Aufzaehlung ihn
        // nicht braucht: er ist Teil der Integritaetskette des Formats, und
        // ein Pak, dessen Unterindex-Hashes nicht halten, ist keines, dem man
        // traut.
        ReadRegion(f, pathHash.Offset, pathHash.Size, fileSize, pathHash.Hash);
        var dirBuf = ReadRegion(f, fullDir.Offset, fullDir.Size, fileSize, fullDir.Hash);

        var entries = ParseDirectoryIndex(dirBuf, encoded);
        if (entries.Count != numEntries)
            throw new InvalidDataException(
                $"Verzeichnisindex listet {entries.Count} Dateien, der Index-Kopf meldet {numEntries}.");
        entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return (mountPoint, entries);
    }

    private sealed record SubIndexRef(long Offset, long Size, byte[] Hash);

    private static SubIndexRef ReadSubIndexRef(Cursor c, string name)
    {
        if (c.I32() == 0)
            throw new UnsupportedPakFormatException($"Pak hat keinen {name}.");
        var offset = c.I64();
        var size = c.I64();
        return new SubIndexRef(offset, size, c.Bytes(20));
    }

    private static List<ReaderEntry> ParseDirectoryIndex(byte[] dir, byte[] encoded)
    {
        var c = new Cursor(dir);
        var dirCount = c.I32();
        var entries = new List<ReaderEntry>();
        for (var i = 0; i < dirCount; i++)
        {
            var dirName = c.FString();
            var fileCount = c.I32();
            for (var j = 0; j < fileCount; j++)
            {
                var fileName = c.FString();
                var loc = c.I32();
                // Dateien auf oberster Ebene liegen unter dem Schluessel „/",
                // der naive Zusammenbau ergibt also einen fuehrenden
                // Schraegstrich; der kanonische mount-relative Pfad hat keinen.
                var full = (dirName + fileName).TrimStart('/');
                if (loc < 0)
                    throw new UnsupportedPakFormatException(
                        $"{full}: nicht kodierte Eintrags-Position wird nicht unterstützt.");
                entries.Add(DecodeEntry(encoded, loc) with { Path = full });
            }
        }
        return entries;
    }

    /// <summary>Dekodiert einen bitgepackten FPakEntry aus dem kodierten Block.
    ///
    /// <para>Das führende uint32 packt: Bit 31 Offset ist 32-Bit, Bit 30
    /// UncompressedSize ist 32-Bit, Bit 29 Size ist 32-Bit, Bits 28–23
    /// Methoden-Index, Bit 22 verschlüsselt, Bits 21–6 Blockzahl, Bits 5–0
    /// Blockgröße ≫ 11 (0x3F ist der Ausweichwert, dann folgt ein
    /// ausdrückliches uint32). Danach folgen die Felder in dieser Reihenfolge:
    /// [Blockgröße] Offset, UncompressedSize, [Size], [Blockgrößen]. Size
    /// fehlt bei gespeicherten Einträgen (es ist gleich UncompressedSize),
    /// und die Blockgrößen-Tabelle fehlt bei einem einzelnen
    /// unverschlüsselten Block.</para>
    ///
    /// <para><b>Die Reihenfolge „Blockgröße vor Offset" ist leicht falsch zu
    /// raten.</b> Sie ist in der Vorlage empirisch festgenagelt und
    /// reproduziert alle 173.078 Sätze einer echten Installation
    /// genau.</para></summary>
    private static ReaderEntry DecodeEntry(byte[] b, int at)
    {
        var c = new Cursor(b) { Position = at };
        var flags = c.U32();
        var method = (int)((flags >> 23) & 0x3F);
        var blockCount = (int)((flags >> 6) & 0xFFFF);
        var encrypted = (flags & (1u << 22)) != 0;

        if ((flags & 0x3F) == 0x3F) c.U32(); // ausdrueckliche CompressionBlockSize

        long Read(bool is32) => is32 ? c.U32() : (long)c.U64();

        var offset = Read((flags & (1u << 31)) != 0);
        var uncompressed = Read((flags & (1u << 30)) != 0);
        var size = uncompressed; // gespeicherter Eintrag serialisiert Size nicht
        if (method != 0) size = Read((flags & (1u << 29)) != 0);
        if (blockCount > 0 && (blockCount > 1 || encrypted)) c.Bytes(4 * blockCount);

        if (encrypted)
            throw new UnsupportedPakFormatException("Verschlüsselter Eintrag.");

        return new ReaderEntry("", uncompressed, offset, method, size, blockCount);
    }

    /// <summary>Begrenzungsgeprüfter Little-Endian-Lesezeiger über einen
    /// Index-Bereich im Speicher.</summary>
    private sealed class Cursor(byte[] b)
    {
        public int Position { get; set; }

        private ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || Position + n > b.Length)
                throw new InvalidDataException("Index-Bereich endet unerwartet.");
            var s = b.AsSpan(Position, n);
            Position += n;
            return s;
        }

        public byte[] Bytes(int n) => Take(n).ToArray();
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
        public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

        /// <summary>Liest einen längenpräfixierten FString. Eine negative
        /// Länge meldet UTF-16, was kein Pak einer echten Icarus-Installation
        /// nutzt und hier nicht dekodiert wird.</summary>
        public string FString()
        {
            var n = I32();
            if (n == 0) return "";
            if (n < 0) throw new UnsupportedPakFormatException("UTF-16-FString wird nicht unterstützt.");
            return Encoding.ASCII.GetString(Take(n)).TrimEnd('\0');
        }
    }
}
