using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using SharpCompress.Archives;

namespace KroModIx.Plugin.Icarus.Services.Archive;

/// <summary>Was eine Datei im Downloads-Ordner wirklich ist.</summary>
public enum IcarusFileKind
{
    /// <summary>Ein Unreal-PAK — wandert direkt in den Mods-Ordner.</summary>
    Pak,
    /// <summary>ZIP, RAR oder 7z — muss ausgepackt und einsortiert werden.</summary>
    Archive,
    /// <summary>Weder noch (oder nicht lesbar).</summary>
    Unknown,
}

/// <summary>Archiv-Grundlage für das Icarus-Plugin: erkennt Dateitypen,
/// inspiziert Archive und packt sie sicher aus.
///
/// <para><b>Erkennung läuft über Magic-Bytes, nicht über die Endung.</b> Das
/// ist keine Vorsichtsmaßnahme, sondern Pflicht, weil im Downloads-Ordner
/// Altlasten liegen: bis v1.22 hängte <c>PakInstallService.DownloadPakAsync</c>
/// jedem Download ein <c>.pak</c> an, auch einem ZIP. Dateien wie
/// <c>Mod 347 1.0 2026-10-01T20-47Z hash.zip.pak</c> sind also ZIPs mit
/// PAK-Endung — nach Endung behandelt landeten sie als „PAK" im Mods-Ordner,
/// wo Icarus sie nicht lesen kann. Still: kein Fehler, kein Log, die Mod
/// wirkte einfach nicht.</para></summary>
public static class IcarusArchive
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>Endungen, die der Downloads-Ordner listet. Nur für den
    /// Vorfilter beim Enumerieren — die echte Einordnung macht
    /// <see cref="DetectKind"/>.</summary>
    public static readonly string[] SupportedExtensions = [".pak", ".zip", ".rar", ".7z"];

    /// <summary>Der Unterordner, in dem ein Icarus-Mod-Archiv seine
    /// Datentabellen-Mods ablegt (Konvention des Icarus Mod Managers).</summary>
    public const string ExmodzFolder = "Icarus Mod Manager";

    /// <summary>Der Unterordner, in dem ein Icarus-Mod-Archiv seine
    /// UE4SS-Lua-Mods ablegt (Konvention aus den README-Dateien der
    /// Nexus-Mods, bestätigt an OreDepot v1.0.1 / Nexus 347).</summary>
    public const string Ue4ssFolder = "UE4SS Mods";

    public static bool HasSupportedExtension(string path)
        => SupportedExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>Liest die ersten Bytes und entscheidet daraus, was die Datei
    /// ist. ZIP/RAR/7z haben eindeutige Signaturen am Dateianfang; ein
    /// Unreal-PAK hat seine Magic (<c>0x5A6F12E1</c>) im Footer, dessen
    /// Position von der Pak-Version abhängt. Deshalb die Logik umgekehrt:
    /// erst die drei Archiv-Signaturen prüfen, und was keine ist, aber auf
    /// <c>.pak</c> endet, gilt als PAK.</summary>
    public static IcarusFileKind DetectKind(string path)
    {
        try
        {
            if (!File.Exists(path)) return IcarusFileKind.Unknown;
            Span<byte> head = stackalloc byte[6];
            using (var fs = File.OpenRead(path))
            {
                if (fs.Read(head) < 6)
                    return path.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)
                        ? IcarusFileKind.Pak : IcarusFileKind.Unknown;
            }
            // ZIP: "PK" + 03 04 / 05 06 (leer) / 07 08 (gespannt)
            if (head[0] == 0x50 && head[1] == 0x4B) return IcarusFileKind.Archive;
            // RAR: "Rar!" 1A 07
            if (head[0] == 0x52 && head[1] == 0x61 && head[2] == 0x72 && head[3] == 0x21)
                return IcarusFileKind.Archive;
            // 7z: "7z" BC AF 27 1C
            if (head[0] == 0x37 && head[1] == 0x7A && head[2] == 0xBC && head[3] == 0xAF)
                return IcarusFileKind.Archive;

            return path.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)
                ? IcarusFileKind.Pak : IcarusFileKind.Unknown;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Dateityp nicht bestimmbar: {Path}", path);
            return IcarusFileKind.Unknown;
        }
    }

    /// <summary>Schaut in ein Archiv, ohne etwas auszupacken, und ordnet die
    /// Einträge den drei Icarus-Mod-Arten zu.</summary>
    public static IcarusArchiveContents Inspect(string archivePath)
    {
        try
        {
            using var archive = ArchiveFactory.Open(archivePath);
            var keys = archive.Entries
                .Where(e => !e.IsDirectory && !string.IsNullOrEmpty(e.Key))
                .Select(e => e.Key!.Replace('\\', '/'))
                .ToList();
            return Classify(keys);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Archiv nicht lesbar: {Path}", archivePath);
            return IcarusArchiveContents.Unreadable(ex.Message);
        }
    }

    /// <summary>Die Einordnung selbst — getrennt von der IO, damit sie ohne
    /// echtes Archiv testbar ist.</summary>
    public static IcarusArchiveContents Classify(IReadOnlyList<string> entryKeys)
    {
        var paks = new List<string>();
        var exmodz = new List<string>();
        var luaFolders = new List<string>();
        var seenLua = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in entryKeys)
        {
            if (key.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
            {
                paks.Add(key);
                continue;
            }
            if (key.EndsWith(".exmodz", StringComparison.OrdinalIgnoreCase))
            {
                exmodz.Add(key);
                continue;
            }
            // UE4SS-Lua: alles unterhalb von "UE4SS Mods/<ModName>/…". Der
            // Ordnername direkt darunter IST die Mod-Identität (UE4SS lädt
            // pro Ordner), deshalb wird er und nicht die Einzeldatei gelistet.
            var luaName = TryGetUe4ssModName(key);
            if (luaName is not null && seenLua.Add(luaName)) luaFolders.Add(luaName);
        }

        return new IcarusArchiveContents(paks, exmodz, luaFolders, entryKeys.Count, null);
    }

    /// <summary>Aus <c>UE4SS Mods/DepositoMinerios/Scripts/main.lua</c> wird
    /// <c>DepositoMinerios</c>. Null, wenn der Eintrag nicht unter dem
    /// UE4SS-Ordner liegt oder direkt darin (ohne Mod-Unterordner) steht —
    /// eine lose Datei neben den Mod-Ordnern ist keine Mod.</summary>
    public static string? TryGetUe4ssModName(string entryKey)
    {
        var normalized = entryKey.Replace('\\', '/');
        var prefix = Ue4ssFolder + "/";
        if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = normalized[prefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0) return null;
        return rest[..slash];
    }

    /// <summary>Zip-Slip-Schutz: löst einen archiv-relativen Pfad gegen
    /// <paramref name="root"/> auf und nimmt ihn nur an, wenn das Ergebnis
    /// wirklich unterhalb von root landet. Ein <c>Contains("..")</c>-Test
    /// reicht nicht — absolute Schlüssel (<c>/etc/…</c>, <c>C:\…</c>)
    /// rutschen daran vorbei. Portiert aus <c>DspZipInstaller</c>.
    ///
    /// <para><b>Laufwerksbuchstaben werden ausdrücklich abgelehnt</b>, nicht
    /// nur über <see cref="Path.IsPathRooted(string)"/>. Der Grund ist eine
    /// Plattform-Asymmetrie, die beim Testen aufgefallen ist: auf Windows
    /// gilt <c>C:\windows\evil.dll</c> als absolut und fliegt raus, auf Linux
    /// nicht — dort wird daraus der relative Pfad <c>C:/windows/evil.dll</c>,
    /// der brav unterhalb von root landet. Ausgebrochen wäre also nichts, der
    /// Eintrag hätte aber einen Ordner namens <c>C:</c> im Mods-Verzeichnis
    /// angelegt. Ein Archiv mit Laufwerksbuchstaben im Schlüssel ist ohnehin
    /// kaputt oder böswillig; beide Plattformen sollen es gleich
    /// behandeln.</para></summary>
    public static bool TryResolveSafe(string root, string relative, out string destination)
    {
        destination = "";
        if (string.IsNullOrWhiteSpace(relative)) return false;
        if (HasDriveLetter(relative)) return false;
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

    /// <summary><c>C:\…</c> oder <c>C:/…</c> am Anfang — unabhängig davon,
    /// was die laufende Plattform für absolut hält.</summary>
    private static bool HasDriveLetter(string path)
        => path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':'
           && (path[2] == '\\' || path[2] == '/');

    public static void ExtractOne(IArchiveEntry entry, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var input = entry.OpenEntryStream();
        using var output = File.Create(destination);
        input.CopyTo(output);
    }
}

/// <summary>Was in einem Icarus-Mod-Archiv steckt. <see cref="Error"/> ist
/// gesetzt, wenn das Archiv nicht lesbar war — dann sind alle Listen leer
/// und der Aufrufer darf den Inhalt nicht als „nichts drin" deuten.</summary>
public sealed record IcarusArchiveContents(
    IReadOnlyList<string> PakEntries,
    IReadOnlyList<string> ExmodzEntries,
    IReadOnlyList<string> Ue4ssModNames,
    int TotalEntries,
    string? Error)
{
    public static IcarusArchiveContents Unreadable(string error)
        => new([], [], [], 0, error);

    public bool IsReadable => Error is null;
    public bool HasPaks => PakEntries.Count > 0;
    public bool HasExmodz => ExmodzEntries.Count > 0;
    public bool HasUe4ssMods => Ue4ssModNames.Count > 0;

    /// <summary>Ob das Archiv überhaupt etwas enthält, das das Plugin
    /// installieren kann.</summary>
    public bool HasInstallable => HasPaks || HasUe4ssMods || HasExmodz;
}
