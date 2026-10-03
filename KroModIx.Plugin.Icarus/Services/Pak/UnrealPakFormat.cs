using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace KroModIx.Plugin.Icarus.Services.Pak;

/// <summary>Wird geworfen, wenn ein Pak eine Eigenschaft nutzt, die dieser
/// Code bewusst nicht unterstützt (Verschlüsselung, Oodle-Kompression, eine
/// zu alte Pak-Version) — im Unterschied zu einem echten Lesefehler.
/// Aufrufer sollen daran laut scheitern und nicht stillschweigend etwas
/// Halbes tun.</summary>
public sealed class UnsupportedPakFormatException(string message) : Exception(message);

/// <summary>Die Format-Konstanten und gemeinsamen Bausteine des
/// Unreal-Pak-Containers.
///
/// <para><b>Herkunft:</b> portiert aus
/// <see href="https://github.com/DonovanMods/go-unrealpak">go-unrealpak</see>
/// (MIT, Donovan C. Young). Die dortigen Werte sind nicht aus einer
/// Spezifikation abgeleitet, sondern gegen eine echte Icarus-Installation
/// verifiziert — alle 34 Paks, 173.078 Index-Einträge, und die 298 Tabellen
/// der <c>data.pak</c> byte-für-byte rekonstruiert. Deshalb werden sie hier
/// übernommen und nicht neu hergeleitet.</para>
///
/// <para><b>Eine Falle aus der Vorlage, die man sonst wiederholt:</b> im
/// Footer stehen <c>EncryptionKeyGuid</c> und <c>bEncryptedIndex</c>
/// <b>vor</b> der Magic — nicht hinter dem Index-Hash, wie manche
/// öffentliche Beschreibung behauptet.</para></summary>
internal static class UnrealPakFormat
{
    /// <summary>Die Pak-Magic, wie sie als uint32 little-endian im Footer
    /// steht.</summary>
    public const uint Magic = 0x5A6F12E1;

    /// <summary>Die einzige Footer-Form, die hier unterstützt wird (Version
    /// ≥ 8): EncryptionKeyGuid(16) + bEncryptedIndex(1) + Magic(4) +
    /// Version(4) + IndexOffset(8) + IndexSize(8) + IndexHash(20) +
    /// CompressionMethods(5×32) = 221.</summary>
    public const int FooterSize = 221;

    /// <summary>Älteste Pak-Version, die hier gelesen wird. Version 10
    /// brachte den dreiteiligen Index (Primärindex + Pfad-Hash-Index +
    /// vollständiger Verzeichnisindex), den dieser Code auswertet. Ältere
    /// Paks haben einen flachen Index mit ganz anderer Form — statt einen
    /// zweiten Parser für ein Layout zu pflegen, das Icarus nicht
    /// ausliefert, ist das ein harter Fehler.</summary>
    public const int MinVersion = 10;

    /// <summary>Was der Writer ausgibt: dieselbe Version, die Icarus' eigene
    /// Paks nutzen — damit die Engine unsere Ausgabe über denselben Pfad
    /// lädt wie ihre eigenen Dateien.</summary>
    public const int WriteVersion = 11;

    /// <summary>Größe des FPakEntry-Kopfs, der jedem unkomprimiert
    /// gespeicherten Nutzdatenblock vorangeht: Offset(8) + Size(8) +
    /// UncompressedSize(8) + CompressionMethodIndex(4) + Hash(20) +
    /// Flags(1) + CompressionBlockSize(4) = 53. Komprimierte Einträge
    /// schieben zwischen Hash und Flags noch BlockCount(4) und 16 Byte je
    /// Block ein.</summary>
    public const int StoredHeaderSize = 53;

    public const int MaxCompressionMethods = 5;
    public const int CompressionMethodNameSize = 32;
    public const int CompressionMethodsOffset = 61;

    /// <summary>Das einzige Kompressionsverfahren, das hier entpackt wird.
    /// Groß-/Kleinschreibung wird ignoriert: der Name ist freier Text, den
    /// das jeweilige Cook-Werkzeug hineinschreibt.</summary>
    public const string ZlibMethodName = "Zlib";

    /// <summary>Obergrenze für die entpackte Größe eines einzelnen Eintrags.
    ///
    /// <para>Bewusst <b>keine</b> Ableitung aus der Pak-Dateigröße: die gilt
    /// für Bereiche auf der Platte, aber nicht für entpackte Ausgabe.
    /// Icarus' <c>Items/D_ItemsStatic.json</c> wächst auf 7.304.687 Byte in
    /// einem 2.458.743 Byte großen Pak. Eine feste Decke ist hier die
    /// richtige Form der Schranke — sie stoppt eine gelogene
    /// <c>UncompressedSize</c>, ohne echte Kompression abzulehnen.</para></summary>
    public const long MaxUncompressedEntrySize = 512L << 20;

    /// <summary>Der PathHashSeed, den der Writer einsetzt. Jeder Wert geht —
    /// Leser nehmen den Seed aus dem Index, und echte Paks nutzen pro Chunk
    /// einen anderen — aber ein fester Wert macht die Ausgabe
    /// reproduzierbar: gleiche Eingabe, gleiche Bytes.</summary>
    public const ulong WriterSeed = 0x9E3779B97F4A7C15;

    /// <summary>Der Mount-Point, den ein gebautes <c>_P.pak</c> deklarieren
    /// muss, damit Icarus' Datentabellen-Lader es findet.
    ///
    /// <para><c>../../../</c> führt vom Verzeichnis der Spiel-Exe zum äußeren
    /// Spielordner; von dort steigen echte Icarus-Mods mit einem wörtlichen
    /// <c>Icarus/Content/</c> wieder ab — der UProject-Ordner heißt selbst
    /// „Icarus". Belegt sowohl durch die Mount-Strings echter Mod-Paks als
    /// auch durch die Verschachtelung der <c>data.pak</c> selbst
    /// (<c>…/Icarus/Icarus/Content/Data/data.pak</c>), und in der Vorlage
    /// gegen zwei unabhängige, laufende Mod-Paks geprüft.</para></summary>
    public const string IcarusContentMountPoint = "../../../Icarus/Content/";

    /// <summary>Wird dem mount-relativen Pfad einer gepatchten Basistabelle
    /// vorangestellt, bevor sie ins gebaute Pak wandert. Echte Mods legen
    /// ihre Tabellen-Überschreibungen unter
    /// <c>Icarus/Content/data/&lt;derselbe Pfad&gt;</c> ab — in der Vorlage
    /// byte-für-byte gegen zwei laufende Mod-Paks bestätigt.
    ///
    /// <para><b>Nicht</b> auf mitgelieferte Assets anwenden: die sind
    /// Content-Pakete, keine Tabellen-Überschreibungen. Ein Pak kann beide
    /// Klassen nicht mit demselben Präfix adressieren.</para></summary>
    public const string IcarusDataTablePrefix = "data/";

    /// <summary>Berechnet den Schlüssel eines Pfads im Pfad-Hash-Index:
    /// FNV-1a 64 über die UTF-16LE-Bytes des kleingeschriebenen,
    /// mount-relativen Pfads (ohne abschließende NUL), wobei der
    /// <paramref name="seed"/> zum FNV-Offset-Basiswert <b>addiert</b> wird.
    /// Ein führender <c>/</c> fällt vorher weg.
    ///
    /// <para>Dieses Rezept ist in der Vorlage nicht geraten, sondern durch
    /// Durchprobieren von Seed-, Kodierungs-, Schreibweise- und
    /// Präfix-Varianten wiederhergestellt und dann gegen alle 173.078
    /// Einträge einer echten Installation geprüft worden.</para></summary>
    public static ulong HashPath(string mountRelative, ulong seed)
    {
        const ulong offsetBasis = 0xCBF29CE484222325;
        const ulong prime = 0x00000100000001B3;

        var path = mountRelative.StartsWith('/') ? mountRelative[1..] : mountRelative;
        // ToLowerInvariant statt ToLower: das Ergebnis darf nicht von der
        // Systemsprache abhaengen (tuerkisches I).
        path = path.ToLowerInvariant();

        var h = offsetBasis + seed;
        foreach (var ch in path)
        {
            // UTF-16LE: erst das niedrige, dann das hohe Byte.
            h ^= (byte)ch;
            h *= prime;
            h ^= (byte)(ch >> 8);
            h *= prime;
        }
        return h;
    }

    /// <summary>Schreibt einen längenpräfixierten ANSI-FString (die Länge
    /// zählt die abschließende NUL mit).</summary>
    public static void WriteFString(BinaryWriter w, string s)
    {
        var bytes = Encoding.ASCII.GetBytes(s);
        w.Write(bytes.Length + 1);
        w.Write(bytes);
        w.Write((byte)0);
    }

    /// <summary>Zerlegt einen mount-relativen Pfad in den Schlüssel des
    /// Verzeichnisindex (mit abschließendem <c>/</c>, oder genau <c>/</c>
    /// für eine Datei auf oberster Ebene) und den Dateinamen — so, wie echte
    /// Paks ihre Verzeichnisindizes schlüsseln.</summary>
    public static (string Dir, string File) SplitMountPath(string rel)
    {
        var i = rel.LastIndexOf('/');
        return i >= 0 ? (rel[..(i + 1)], rel[(i + 1)..]) : ("/", rel);
    }

    /// <summary>Baut den 53-Byte-FPakEntry-Kopf, der den Nutzdaten eines
    /// gespeicherten Eintrags vorangeht. Das Offset-Feld bleibt in dieser
    /// lokalen Kopie immer 0 — echte Paks schreiben dort auch 0, das
    /// verbindliche Offset steht im Index. <c>Hash</c> ist die SHA1 der
    /// Nutzdaten auf der Platte.</summary>
    public static byte[] StoredEntryHeader(long size, ReadOnlySpan<byte> content)
    {
        var b = new byte[StoredHeaderSize];
        var span = b.AsSpan();
        BinaryPrimitives.WriteInt64LittleEndian(span[0..8], 0);      // Offset
        BinaryPrimitives.WriteInt64LittleEndian(span[8..16], size);  // Size
        BinaryPrimitives.WriteInt64LittleEndian(span[16..24], size); // UncompressedSize
        BinaryPrimitives.WriteInt32LittleEndian(span[24..28], 0);    // Methode: gespeichert
        System.Security.Cryptography.SHA1.HashData(content, span[28..48]);
        span[48] = 0;                                                // Flags
        BinaryPrimitives.WriteUInt32LittleEndian(span[49..53], 0);   // CompressionBlockSize
        return b;
    }

    /// <summary>Serialisiert den Primärindex.
    ///
    /// <para>Aufrufer bauen ihn zweimal: die Offsets der beiden
    /// Unterindizes, die er festhält, zeigen hinter sein eigenes Ende — seine
    /// Länge hängt aber nicht von deren Werten ab (alle Felder sind
    /// feste int64). Ein erster Durchlauf mit Nullen misst ihn, der zweite
    /// schreibt die echten Offsets.</para></summary>
    public static byte[] BuildPrimaryIndex(string mountPoint, int numEntries, ulong seed,
        long phiOffset, long phiSize, byte[] phiHash,
        long fdiOffset, long fdiSize, byte[] fdiHash, byte[] encoded)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        WriteFString(w, mountPoint);
        w.Write(numEntries);
        w.Write(seed);              // PathHashSeed
        w.Write(1);                 // bHasPathHashIndex
        w.Write(phiOffset);
        w.Write(phiSize);
        w.Write(phiHash);
        w.Write(1);                 // bHasFullDirectoryIndex
        w.Write(fdiOffset);
        w.Write(fdiSize);
        w.Write(fdiHash);
        w.Write(encoded.Length);    // EncodedPakEntriesSize
        w.Write(encoded);
        w.Write(0);                 // NumNonEncodedFiles: keine
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Serialisiert den 221-Byte-Footer.</summary>
    public static byte[] BuildFooter(int version, long indexOffset, long indexSize, byte[] indexHash)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        w.Write(new byte[16]);      // EncryptionKeyGuid: null
        w.Write((byte)0);           // bEncryptedIndex: nein
        w.Write(Magic);
        w.Write(version);
        w.Write(indexOffset);
        w.Write(indexSize);
        w.Write(indexHash);
        // CompressionMethods: fünf feste 32-Byte-Namensplätze, alle leer —
        // dieser Writer schreibt nur gespeicherte Einträge. Echte Paks
        // nennen hier „Oodle" und „Zlib"; eine Nulltabelle ist die richtige
        // Form für Methode 0.
        w.Write(new byte[MaxCompressionMethods * CompressionMethodNameSize]);
        w.Flush();
        return ms.ToArray();
    }
}
