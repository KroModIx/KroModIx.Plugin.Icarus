using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace KroModIx.Plugin.Icarus.Services.Exmodz;

/// <summary>Ein gelesenes <c>.EXMODZ</c>: das Diff-Manifest plus die
/// vorkompilierten Asset-Dateien, die der Autor mitgeliefert hat (sie wandern
/// unverändert ins gebaute Pak und werden nie neu kompiliert).</summary>
public sealed record ExmodzBundle(ExmodDiff Diff, IReadOnlyDictionary<string, byte[]> Assets);

public static class ExmodzParser
{
    /// <summary>Obergrenze für die gemeldete entpackte Größe eines einzelnen
    /// Eintrags. Die größte echte Basistabelle hat 7,4 MB, Manifeste und
    /// mitgelieferte UE-Assets sind kleiner. 64 MiB lässt reichlich Luft und
    /// verweigert gleichzeitig, einer unbegrenzten oder gelogenen
    /// Größenangabe in einem fremden, heruntergeladenen Archiv zu
    /// glauben.</summary>
    public const long MaxEntrySize = 64L << 20;

    /// <summary>Obergrenze für die <b>Summe</b> der gemeldeten Asset-Größen.
    /// Die Einzel-Grenze allein beschränkt ein Archiv mit vielen Einträgen
    /// nicht, von denen jeder für sich darunter liegt — fünf Einträge à
    /// 60 MiB summieren sich auf 300 MiB, ohne dass einer auffällt.</summary>
    public const long MaxTotalAssetsSize = 256L << 20;

    /// <summary>Packt ein <c>.EXMODZ</c> aus: Manifest und mitgelieferte
    /// Assets.
    ///
    /// <para>Das Manifest liegt in jedem gesehenen Beispiel unter
    /// <c>Extracted Mods/&lt;Name&gt;.EXMOD</c>. Gesucht wird nach
    /// irgendeiner <c>*.EXMOD</c> unter diesem Präfix statt nach einem festen
    /// Namen — der wechselt pro Mod. Verglichen wird mit normalisierten
    /// Schrägstrichen und ohne Rücksicht auf Groß- und Kleinschreibung:
    /// manche Erzeuger sind Windows-Werkzeuge, und ZIP garantiert keine
    /// Schreibweise.</para></summary>
    public static ExmodzBundle Parse(Stream zipStream)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);

        var manifests = zip.Entries
            .Where(e => IsManifest(e.FullName))
            .ToList();
        switch (manifests.Count)
        {
            case 0:
                throw new InvalidDataException(
                    "Im .EXMODZ fehlt das Manifest (erwartet: Extracted Mods/*.EXMOD).");
            case 1:
                break;
            default:
                throw new InvalidDataException(
                    $"Das .EXMODZ hat {manifests.Count} Manifeste, das ist mehrdeutig: " +
                    string.Join(", ", manifests.Select(m => m.FullName)));
        }

        var manifestEntry = manifests[0];
        var diff = ExmodParser.Parse(ReadEntry(manifestEntry));

        // Erst die Asset-Eintraege sammeln und ihre GEMELDETEN Groessen
        // summieren, bevor einer gelesen wird. Addiert wird gegen die
        // Restluft, nicht erst summiert und dann verglichen: eine gemeldete
        // Groesse ist fremdbestimmt, und eine Summe kann ueberlaufen und
        // dabei auf einen Wert wandern, der die Pruefung faelschlich besteht.
        var assetEntries = new List<ZipArchiveEntry>();
        long declaredTotal = 0;
        foreach (var e in zip.Entries)
        {
            if (ReferenceEquals(e, manifestEntry)) continue;
            if (e.FullName.EndsWith('/') || e.Length == 0 && e.Name.Length == 0) continue;
            if (!IsAsset(e.FullName)) continue;   // README, Bilder und Sonstiges bleiben draußen
            if (e.Length > MaxTotalAssetsSize - declaredTotal)
                throw new InvalidDataException(
                    "Die mitgelieferten Assets des .EXMODZ melden zusammen mehr als " +
                    $"{MaxTotalAssetsSize} Byte entpackt.");
            assetEntries.Add(e);
            declaredTotal += e.Length;
        }

        var wrapper = ModWrapperDir(manifestEntry.FullName);
        var assets = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var e in assetEntries)
            assets[StripModWrapper(Normalize(e.FullName), wrapper)] = ReadEntry(e);

        return new ExmodzBundle(diff, assets);
    }

    public static ExmodzBundle ParseFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Parse(fs);
    }

    private static bool IsManifest(string entryName)
    {
        var lower = Normalize(entryName).ToLowerInvariant();
        return lower.StartsWith("extracted mods/", StringComparison.Ordinal)
               && lower.EndsWith(".exmod", StringComparison.Ordinal);
    }

    private static bool IsAsset(string entryName)
    {
        var lower = Normalize(entryName).ToLowerInvariant();
        return lower.EndsWith(".uasset", StringComparison.Ordinal)
               || lower.EndsWith(".uexp", StringComparison.Ordinal);
    }

    /// <summary>Der Ordner, unter dem ein <c>.EXMODZ</c> seine Assets
    /// verschachtelt: der Basisname des Manifests. In jedem geprüften Bündel
    /// genau ein Segment, und es entspricht dem Manifest-Namen.</summary>
    internal static string ModWrapperDir(string manifestPath)
    {
        var baseName = Normalize(manifestPath).Split('/')[^1];
        var dot = baseName.LastIndexOf('.');
        return dot > 0 ? baseName[..dot] : baseName;
    }

    /// <summary>Entfernt den Hüll-Ordner der Mod aus einem Asset-Pfad und
    /// liefert den Content-relativen Pfad, den das Asset einnehmen muss, um
    /// die Kopie des Basisspiels zu überschreiben.
    ///
    /// <para><b>Ohne diesen Schritt landet jedes mitgelieferte Asset ein
    /// Verzeichnis zu tief, und das Spiel ignoriert es stillschweigend</b> —
    /// eine Asset-Mod installiert sich dann sauber, prüft sich sauber und tut
    /// nichts. Belegt an OreDepot: das <c>.EXMODZ</c> legt
    /// <c>OreDepot/Assets/2DArt/UI/Items/Item_Icons/DepositoMod/ITEM_Refrigerator.uasset</c>
    /// ab, und im funktionierenden Pak liegt dasselbe Asset unter
    /// <c>Assets/2DArt/UI/Items/Item_Icons/DepositoMod/ITEM_Refrigerator.uasset</c>.</para>
    ///
    /// <para>Das Abschneiden ist an das führende Segment gebunden
    /// (Groß-/Kleinschreibung ignoriert, UE-Pfade sind unempfindlich).
    /// Unbedingt abzuschneiden würde ein Bündel beschädigen, das seine Assets
    /// schon am richtigen Platz ablegt.</para></summary>
    internal static string StripModWrapper(string assetPath, string wrapper)
    {
        if (wrapper.Length == 0) return assetPath;
        var slash = assetPath.IndexOf('/');
        if (slash <= 0) return assetPath;
        var first = assetPath[..slash];
        var rest = assetPath[(slash + 1)..];
        if (rest.Length == 0 || !string.Equals(first, wrapper, StringComparison.OrdinalIgnoreCase))
            return assetPath;
        return rest;
    }

    /// <summary>ZIP ist ein Schrägstrich-Format, manche unter Windows gebaute
    /// Archive speichern Einträge trotzdem mit Backslashes.</summary>
    internal static string Normalize(string name) => name.Replace('\\', '/');

    private static byte[] ReadEntry(ZipArchiveEntry e)
    {
        if (e.Length > MaxEntrySize)
            throw new InvalidDataException(
                $"Eintrag {e.FullName} meldet {e.Length} Byte entpackt und überschreitet damit " +
                $"die Einzel-Grenze von {MaxEntrySize} Byte.");
        using var s = e.Open();
        using var ms = new MemoryStream((int)Math.Min(e.Length, MaxEntrySize));
        // Die Grenze oben schuetzt nur gegen eine ehrliche, aber grosse
        // Angabe. Ein Kopf, der LUEGT und die echte Groesse untertreibt, darf
        // ebenfalls keinen unbegrenzten Lauf treiben — also ein Byte mehr
        // lesen als gemeldet und das als Ueberlauf erkennen.
        var buf = new byte[81920];
        long limit = e.Length + 1;
        int read;
        while (limit > 0 && (read = s.Read(buf, 0, (int)Math.Min(buf.Length, limit))) > 0)
        {
            ms.Write(buf, 0, read);
            limit -= read;
        }
        if (ms.Length > e.Length)
            throw new InvalidDataException(
                $"Eintrag {e.FullName} entpackt über die gemeldeten {e.Length} Byte hinaus.");
        return ms.ToArray();
    }
}
