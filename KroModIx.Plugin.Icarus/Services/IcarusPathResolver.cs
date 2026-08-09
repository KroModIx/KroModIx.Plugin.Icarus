using System.IO;
using KroModIx.Plugin.Contracts;

namespace KroModIx.Plugin.Icarus.Services;

/// <summary>
/// Löst die Icarus-Mod-Ordner auf. Icarus (RocketWerkz, Unreal Engine 4)
/// nutzt zwei separate Ordner:
///
/// <list type="bullet">
/// <item><b>Manuell installierte PAKs</b> liegen unter
///   <c>&lt;InstallDir&gt;/Icarus/Content/Paks/mods/</c> (OHNE Tilde-Präfix —
///   Icarus weicht hier von der üblichen UE4-Konvention <c>~mods/</c> ab).</item>
/// <item><b>Steam-Workshop-Abos</b> landen im Workshop-Content-Ordner der
///   Library, in der Icarus installiert ist:
///   <c>&lt;LibraryRoot&gt;/steamapps/workshop/content/1149460/&lt;WorkshopId&gt;/</c>
///   — jeder Workshop-Mod ist ein eigener Unterordner mit einer oder mehreren
///   .pak-Dateien. Steam verwaltet diese Ordner selbst; Uninstall geht nur
///   über Steam (Workshop-Abo kündigen).</item>
/// </list>
///
/// <para>v0.1.0-Bug: der PathResolver zeigte auf <c>~mods/</c> — Real-Test
/// auf Bazzite zeigte, dass die zwei installierten PAKs (Ultimate Envirosuit,
/// Yeesha WeightSpeedStam) im <c>mods/</c>-Ordner liegen. Fix in v0.2.0.</para>
/// </summary>
public sealed class IcarusPathResolver
{
    private const int IcarusSteamAppId = 1149460;

    public string? GetManualModsDir(DetectedGame game)
    {
        if (string.IsNullOrEmpty(game.InstallDir) || !Directory.Exists(game.InstallDir))
            return null;
        return Path.Combine(game.InstallDir, "Icarus", "Content", "Paks", "mods");
    }

    /// <summary>Liefert den Workshop-Content-Root für Icarus in der Steam-
    /// Library dieses Spiels — oder <c>null</c> wenn wir den Library-Root
    /// nicht ableiten können. Der Ordner existiert erst, sobald der User
    /// mindestens einen Workshop-Mod abonniert hat; Nicht-Existenz ist OK.</summary>
    public string? GetWorkshopContentDir(DetectedGame game)
    {
        // Steam legt den Workshop-Content unter <libraryRoot>/steamapps/workshop/content/<appId>/
        // ab. Der InstallDir zeigt auf <libraryRoot>/steamapps/common/<GameFolder>,
        // also gehen wir zwei Ebenen hoch und dann in workshop/content/<appId>.
        if (string.IsNullOrEmpty(game.InstallDir)) return null;
        var commonDir = Directory.GetParent(game.InstallDir);        // steamapps/common
        var steamappsDir = commonDir?.Parent;                        // steamapps
        if (steamappsDir is null) return null;
        return Path.Combine(steamappsDir.FullName, "workshop", "content", IcarusSteamAppId.ToString());
    }
}
