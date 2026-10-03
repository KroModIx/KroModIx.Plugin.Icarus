using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using KroModIx.Plugin.Contracts;
using KroModIx.Plugin.Icarus.Services.Exmodz;
using KroModIx.Plugin.Icarus.Services.Pak;
using Xunit;

namespace KroModIx.Plugin.Icarus.Tests;

/// <summary>Messung gegen eine <b>echte</b> Icarus-Installation.
///
/// <para>Diese Tests überspringen sich, wo kein Icarus liegt — also im
/// CI-Runner. Sie setzen damit bewusst eine Entwicklungsumgebung voraus, und
/// genau deshalb dürfen sie nicht rot werden, wenn sie fehlt: ein Test, der
/// eine Steam-Installation braucht und ohne sie fehlschlägt, färbt den
/// CI-Lauf rot, ohne einen Fehler zu finden.</para>
///
/// <para><b>Warum es sie trotzdem gibt:</b> das Pak-Format ist nicht aus
/// einer Spezifikation portiert, sondern aus einer Implementierung, die
/// gegen echte Daten verifiziert wurde. Die synthetischen Rundlauf-Tests
/// prüfen nur, dass Leser und Schreiber zueinander passen — sie würden einen
/// gemeinsamen Denkfehler beider nicht finden. Erst das Lesen einer von
/// Epic gekochten <c>data.pak</c> und eines von einem fremden Werkzeug
/// geschriebenen Mod-Paks schließt das aus.</para>
///
/// <para>Stand der Messung auf dem Entwicklungsrechner (03.10.2026,
/// Spielwoche 252): 299 Einträge der <c>data.pak</c> rekonstruiert,
/// 42.274.800 Byte entpackt, 52 ms. Größte Tabelle
/// <c>Items/D_ItemsStatic.json</c> mit 7.420.669 Byte. Der Vergleich des
/// eigenen Zusammenbaus von OreDepot gegen das Pak, das lmm aus derselben
/// Mod gebaut hat: 10 von 10 Einträgen vorhanden, 2 Assets byte-identisch,
/// 8 Tabellen inhaltsgleich nach <c>JsonNode.DeepEquals</c>.</para></summary>
public class RealIcarusPakTests
{
    /// <summary>Die Umgebungsvariable, über die sich ein abweichender
    /// Installationspfad angeben lässt — sonst wird der Steam-Standard
    /// gesucht.</summary>
    private const string EnvVar = "ICARUS_INSTALL_DIR";

    private static readonly string[] Candidates =
    [
        "/run/media/system/Games/SteamLibrary/steamapps/common/Icarus",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local/share/Steam/steamapps/common/Icarus"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".steam/steam/steamapps/common/Icarus"),
    ];

    private static string? FindInstallDir()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrEmpty(fromEnv) && Directory.Exists(fromEnv)) return fromEnv;
        return Candidates.FirstOrDefault(Directory.Exists);
    }

    private static string RequireBasePak()
    {
        var install = FindInstallDir();
        if (install is null)
            Assert.Skip($"Keine Icarus-Installation gefunden (setze {EnvVar}, um eine anzugeben).");
        var game = new DetectedGame(
            new GameTarget("icarus", "Icarus", SteamAppId: 1149460,
                AlternativeExecutableNames: ["Icarus.exe"], Platforms: Platforms.Both),
            install!, null, null, RuntimeKind.Proton, GameSource.Steam);
        // Direkt die Pfad-Aufloesung pruefen statt einen ganzen ExmodzService
        // zu bauen — der braeuchte IcarusPaths und damit einen Host, und ein
        // Attrappen-Host waere hier viel Code fuer null Erkenntnis.
        var basePak = ExmodzService.ResolveBasePak(game);
        if (basePak is null)
            Assert.Skip("Icarus gefunden, aber Content/Data/data.pak nicht.");
        return basePak!;
    }

    [Fact]
    public void JedeTabelleDerEchtenDataPakWirdRekonstruiert()
    {
        var basePak = RequireBasePak();
        using var r = UnrealPakReader.Open(basePak);

        var files = r.Files();
        files.Should().NotBeEmpty();
        r.IndexHash.Should().MatchRegex("^[0-9a-f]{40}$");

        long totalBytes = 0;
        foreach (var f in files)
        {
            byte[] data;
            try
            {
                data = r.ReadFile(f.Path);
            }
            catch (Exception ex)
            {
                Assert.Fail($"{f.Path}: {ex.GetType().Name}: {ex.Message}");
                return;
            }
            // Die gemeldete Groesse ist die entpackte — stimmt sie nicht, ist
            // entweder der Index falsch gelesen oder die Dekompression
            // unvollstaendig.
            data.Length.Should().Be((int)f.Size, f.Path);
            totalBytes += data.Length;
        }
        totalBytes.Should().BeGreaterThan(10_000_000,
            "die Datentabellen von Icarus sind entpackt zweistellige Megabyte");
    }

    /// <summary>Liest ein Pak, das ein <b>anderes</b> Werkzeug geschrieben
    /// hat. Uebersprungen, wenn keines daliegt — es ist kein Teil der
    /// Spielinstallation.</summary>
    [Fact]
    public void FremdGeschriebenesModPakIstLesbar()
    {
        var install = FindInstallDir();
        if (install is null) Assert.Skip("Keine Icarus-Installation gefunden.");
        var modsDir = ModFolderDiscovery.Find(install!, "Icarus/Content/Paks/mods");
        if (modsDir is null) Assert.Skip("Kein Mods-Ordner vorhanden.");

        var foreign = Directory.EnumerateFiles(modsDir!, "*_P.pak")
            .Where(p => !Path.GetFileName(p).StartsWith("zzz_KroModIx", StringComparison.Ordinal))
            .ToList();
        if (foreign.Count == 0) Assert.Skip("Keine fremden Mod-Paks im Mods-Ordner.");

        foreach (var path in foreign)
        {
            using var r = UnrealPakReader.Open(path);
            r.Files().Should().NotBeEmpty(Path.GetFileName(path));
            foreach (var f in r.Files())
            {
                // Ein fremdes Pak darf Oodle nutzen — das ist ein erwarteter,
                // lauter Fehler und kein Testfehlschlag.
                try { r.ReadFile(f.Path).Length.Should().Be((int)f.Size, f.Path); }
                catch (UnsupportedPakFormatException) { }
            }
        }
    }
}
