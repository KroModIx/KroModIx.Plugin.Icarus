using System;
using System.Collections.Generic;
using System.Linq;
using KroModIx.Plugin.Contracts;
using NLog;

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

/// <summary>Die <b>Icarus-spezifische</b> Seite der Archiv-Behandlung: welcher
/// Eintrag eines Archivs welche Art von Icarus-Mod ist.
///
/// <para>Die Datei-Arbeit selbst — Archive öffnen, Einträge listen,
/// zip-slip-sicher auspacken, Formate an den Magic-Bytes erkennen — macht
/// seit v1.25.0 der Host (<see cref="IArchiveService"/> und
/// <see cref="IUnrealPakService"/>, Contracts v1.30.0). Hier bleibt nur, was
/// Icarus-Wissen ist: die Namen der beiden Ordner, in denen ein
/// Icarus-Mod-Archiv seine Teile ablegt, und die Zuordnung Eintrag →
/// Mod-Art.</para></summary>
public sealed class IcarusArchive
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>Der Unterordner, in dem ein Icarus-Mod-Archiv seine
    /// Datentabellen-Mods ablegt (Konvention des Icarus Mod Managers).</summary>
    public const string ExmodzFolder = "Icarus Mod Manager";

    /// <summary>Der Unterordner, in dem ein Icarus-Mod-Archiv seine
    /// UE4SS-Lua-Mods ablegt (Konvention aus den README-Dateien der
    /// Nexus-Mods, bestätigt an OreDepot v1.0.1 / Nexus 347).</summary>
    public const string Ue4ssFolder = "UE4SS Mods";

    private readonly IArchiveService _archives;
    private readonly IUnrealPakService _paks;

    public IcarusArchive(IArchiveService archives, IUnrealPakService paks)
    {
        _archives = archives;
        _paks = paks;
    }

    /// <summary>Endungen, die der Downloads-Ordner listet — die
    /// Archiv-Endungen des Hosts plus <c>.pak</c>. Nur ein Vorfilter beim
    /// Enumerieren; die verbindliche Einordnung macht
    /// <see cref="DetectKind"/>.</summary>
    public bool HasSupportedExtension(string path)
        => path.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)
           || _archives.HasSupportedExtension(path);

    /// <summary>Entscheidet anhand des <b>Inhalts</b>, was die Datei ist.
    ///
    /// <para><b>Die Reihenfolge ist wichtig.</b> Erst die Pak-Prüfung des
    /// Hosts (Footer-Magic), dann die Archiv-Prüfung (Signatur am
    /// Dateianfang). Umgekehrt wäre es angreifbar: ein PAK beginnt mit den
    /// Daten seiner ersten Datei, und die können zufällig mit <c>PK</c>
    /// anfangen — dann wäre ein funktionierendes Mod-PAK als „ZIP"
    /// eingeordnet und der Installer hätte versucht, es
    /// auszupacken.</para>
    ///
    /// <para>Was weder Pak noch Archiv ist, aber auf <c>.pak</c> endet, gilt
    /// als PAK — etwa ein abgebrochener Download, den der Installer dann
    /// sauber ablehnt.</para></summary>
    public IcarusFileKind DetectKind(string path)
    {
        if (_paks.IsPakFile(path)) return IcarusFileKind.Pak;
        if (_archives.DetectKind(path) != ArchiveKind.Unknown) return IcarusFileKind.Archive;
        return path.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)
            ? IcarusFileKind.Pak : IcarusFileKind.Unknown;
    }

    /// <summary>Schaut in ein Archiv, ohne etwas auszupacken, und ordnet die
    /// Einträge den drei Icarus-Mod-Arten zu.</summary>
    public IcarusArchiveContents Inspect(string archivePath)
    {
        try
        {
            return Classify(_archives.List(archivePath).Select(e => e.Path).ToList());
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

        foreach (var raw in entryKeys)
        {
            var key = raw.Replace('\\', '/');
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
