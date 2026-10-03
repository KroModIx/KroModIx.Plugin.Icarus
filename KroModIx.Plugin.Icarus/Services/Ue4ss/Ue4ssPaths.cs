using System;
using System.IO;
using System.Linq;
using KroModIx.Plugin.Contracts;

namespace KroModIx.Plugin.Icarus.Services.Ue4ss;

/// <summary>Löst die UE4SS-Pfade einer Icarus-Installation auf.
///
/// <para>UE4SS ist der Lua-Mod-Loader für Unreal-Engine-Spiele. Er liegt
/// neben der Spiel-Exe in <c>Icarus/Binaries/Win64/</c> und lädt seine Mods
/// aus einem <c>Mods/</c>-Ordner daneben. Je nach UE4SS-Ausgabe heißt der
/// entweder <c>Mods/</c> (3.0.x, die stabile Reihe) oder <c>ue4ss/Mods/</c>
/// (die experimentellen Bauten) — beide Schreibweisen werden gefunden, denn
/// welche vorliegt, entscheidet der User mit seinem Download, nicht wir.</para>
///
/// <para>Die Auflösung läuft über <see cref="ModFolderDiscovery"/>, also
/// ohne Rücksicht auf Groß- und Kleinschreibung. Unter Linux ist das keine
/// Kosmetik: ein von Hand angelegtes <c>mods/</c> findet
/// <c>Directory.Exists(".../Mods")</c> nicht, das Spiel lädt die Mods aber
/// trotzdem.</para></summary>
public sealed class Ue4ssPaths
{
    /// <summary>Relativ zum InstallDir — die Kandidaten für das
    /// Verzeichnis der Spiel-Exe.</summary>
    private static readonly string[] Win64Candidates =
        ["Icarus/Binaries/Win64", "Icarus/Binaries/win64"];

    /// <summary>Relativ zum Win64-Verzeichnis — die Kandidaten für den
    /// Lua-Mod-Ordner, in der Reihenfolge ihrer Verbreitung.</summary>
    private static readonly string[] ModsCandidates = ["Mods", "ue4ss/Mods"];

    /// <summary>Dateien, deren Vorhandensein einen installierten Loader
    /// belegt. <c>dwmapi.dll</c> ist der Einhängepunkt der 3.x-Reihe,
    /// <c>xinput1_3.dll</c> der der 2.x-Reihe.</summary>
    private static readonly string[] LoaderProxyDlls = ["dwmapi.dll", "xinput1_3.dll"];

    public Ue4ssPaths(DetectedGame game)
    {
        InstallDir = game.InstallDir ?? "";
        Win64Dir = string.IsNullOrEmpty(InstallDir)
            ? null
            : ModFolderDiscovery.Find(InstallDir, Win64Candidates);
    }

    public string InstallDir { get; }

    /// <summary>Das Verzeichnis der Spiel-Exe, oder null wenn es nicht
    /// gefunden wurde (kaputte Installation, falscher InstallDir).</summary>
    public string? Win64Dir { get; }

    public bool IsGameLayoutKnown => Win64Dir is not null;

    /// <summary>Der vorhandene Lua-Mod-Ordner, ohne etwas anzulegen. Null
    /// bedeutet: UE4SS ist (noch) nicht installiert.</summary>
    public string? FindModsDir()
        => Win64Dir is null ? null : ModFolderDiscovery.Find(Win64Dir, ModsCandidates);

    /// <summary>Wie <see cref="FindModsDir"/>, legt aber <c>Mods/</c> an,
    /// wenn keiner da ist — für den Install-Weg. Null nur, wenn das
    /// Win64-Verzeichnis fehlt oder nicht beschreibbar ist.</summary>
    public string? FindOrCreateModsDir()
        => Win64Dir is null ? null : ModFolderDiscovery.FindOrCreate(Win64Dir, ModsCandidates);

    /// <summary>Ob der Loader selbst installiert ist: eine der
    /// Proxy-DLLs UND die <c>UE4SS.dll</c>. Beides einzeln ist kein Beleg —
    /// eine Proxy-DLL allein könnte von einem anderen Werkzeug stammen, und
    /// eine UE4SS.dll ohne Proxy wird nie geladen.</summary>
    public bool IsLoaderInstalled()
    {
        if (Win64Dir is null) return false;
        var hasProxy = LoaderProxyDlls.Any(d => File.Exists(Path.Combine(Win64Dir, d)));
        if (!hasProxy) return false;
        return File.Exists(Path.Combine(Win64Dir, "UE4SS.dll"))
               || File.Exists(Path.Combine(Win64Dir, "ue4ss", "UE4SS.dll"));
    }

    /// <summary>Die Logdatei, die UE4SS beim Start schreibt. Ihr
    /// Vorhandensein ist der einzige verlässliche Beleg dafür, dass der
    /// Loader beim letzten Spielstart wirklich eingehängt <b>wurde</b> —
    /// installierte Dateien beweisen nur, dass er da ist. Unter Proton ist
    /// genau das die Stelle, an der es ohne DLL-Umleitung stillschweigend
    /// scheitert.</summary>
    public string? FindLoaderLog()
    {
        if (Win64Dir is null) return null;
        foreach (var rel in new[] { "UE4SS.log", Path.Combine("ue4ss", "UE4SS.log") })
        {
            var p = Path.Combine(Win64Dir, rel);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Wann der Loader zuletzt geladen wurde (Zeitstempel der
    /// Logdatei), oder null wenn es keine gibt.</summary>
    public DateTime? LoaderLastLoadedUtc()
    {
        var log = FindLoaderLog();
        if (log is null) return null;
        try { return File.GetLastWriteTimeUtc(log); }
        catch { return null; }
    }
}
