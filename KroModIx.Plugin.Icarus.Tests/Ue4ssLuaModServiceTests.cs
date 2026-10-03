using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using FluentAssertions;
using KroModIx.Plugin.Contracts;
using KroModIx.Plugin.Icarus.Services.Ue4ss;
using Xunit;

namespace KroModIx.Plugin.Icarus.Tests;

/// <summary>Baut eine Icarus-Installation im Temp-Verzeichnis nach, damit
/// die UE4SS-Logik ohne echtes Spiel prüfbar ist.</summary>
internal sealed class FakeIcarusInstall : IDisposable
{
    public FakeIcarusInstall(string win64Relative = "Icarus/Binaries/Win64")
    {
        Root = Directory.CreateTempSubdirectory("icarus-ue4ss").FullName;
        Win64 = Path.Combine(Root, win64Relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Win64);
        Game = new DetectedGame(
            new GameTarget("icarus", "Icarus", SteamAppId: 1149460,
                AlternativeExecutableNames: new[] { "Icarus.exe" }, Platforms: Platforms.Both),
            InstallDir: Root, UserDataDir: null, ProtonPrefix: null,
            Runtime: RuntimeKind.Proton, Source: GameSource.Steam);
    }

    public string Root { get; }
    public string Win64 { get; }
    public DetectedGame Game { get; }

    public Ue4ssPaths Paths => new(Game);
    public Ue4ssLuaModService Service => new(Paths, Archives);

    /// <summary>Der Archiv-Baukasten kommt seit v1.25.0 aus dem Host;
    /// hier die Attrappe.</summary>
    public FakeArchiveService Archives { get; } = new();

    /// <summary>Legt die Dateien an, die einen installierten Loader
    /// ausmachen.</summary>
    public void InstallLoader()
    {
        File.WriteAllText(Path.Combine(Win64, "dwmapi.dll"), "x");
        File.WriteAllText(Path.Combine(Win64, "UE4SS.dll"), "x");
    }

    public string AddLuaMod(string name, bool enabled = true, int scripts = 1)
    {
        var dir = Path.Combine(Win64, "Mods", name);
        Directory.CreateDirectory(Path.Combine(dir, "Scripts"));
        for (var i = 0; i < scripts; i++)
            File.WriteAllText(Path.Combine(dir, "Scripts", i == 0 ? "main.lua" : $"part{i}.lua"), "-- lua");
        if (enabled) File.WriteAllText(Path.Combine(dir, "enabled.txt"), "");
        return dir;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* Aufräumen darf scheitern */ }
    }
}

public class Ue4ssPathsTests
{
    [Fact]
    public void FindetWin64UndMeldetLoaderZustand()
    {
        using var fake = new FakeIcarusInstall();
        var paths = fake.Paths;

        paths.IsGameLayoutKnown.Should().BeTrue();
        paths.IsLoaderInstalled().Should().BeFalse("ohne DLLs ist nichts installiert");

        fake.InstallLoader();
        fake.Paths.IsLoaderInstalled().Should().BeTrue();
    }

    /// <summary>Eine Proxy-DLL ohne UE4SS.dll ist kein installierter Loader —
    /// sie könnte von einem anderen Werkzeug stammen. Und eine UE4SS.dll
    /// ohne Proxy wird nie geladen.</summary>
    [Fact]
    public void HalberLoaderZaehltNicht()
    {
        using var fake = new FakeIcarusInstall();
        File.WriteAllText(Path.Combine(fake.Win64, "dwmapi.dll"), "x");
        fake.Paths.IsLoaderInstalled().Should().BeFalse();

        File.Delete(Path.Combine(fake.Win64, "dwmapi.dll"));
        File.WriteAllText(Path.Combine(fake.Win64, "UE4SS.dll"), "x");
        fake.Paths.IsLoaderInstalled().Should().BeFalse();
    }

    /// <summary>Unter Linux ist die Schreibweise entscheidend: ein von Hand
    /// angelegtes <c>win64/</c> wird vom Spiel geladen, und das Plugin muss
    /// es finden.</summary>
    [Fact]
    public void FindetAbweichendeSchreibweise()
    {
        using var fake = new FakeIcarusInstall("Icarus/Binaries/win64");
        fake.Paths.IsGameLayoutKnown.Should().BeTrue();
    }

    [Fact]
    public void OhneWin64IstNichtsBekannt()
    {
        var root = Directory.CreateTempSubdirectory("icarus-empty").FullName;
        try
        {
            var game = new DetectedGame(
                new GameTarget("icarus", "Icarus", SteamAppId: 1149460,
                AlternativeExecutableNames: new[] { "Icarus.exe" }, Platforms: Platforms.Both),
                root, null, null, RuntimeKind.Proton, GameSource.Steam);
            var paths = new Ue4ssPaths(game);
            paths.IsGameLayoutKnown.Should().BeFalse();
            paths.FindModsDir().Should().BeNull();
            paths.FindOrCreateModsDir().Should().BeNull();
            paths.IsLoaderInstalled().Should().BeFalse();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Installierte Dateien beweisen nur, dass der Loader da ist.
    /// Dass er beim letzten Spielstart auch geladen wurde, belegt allein
    /// seine Logdatei — unter Proton ist das der Unterschied zwischen
    /// „funktioniert" und „tut stillschweigend nichts".</summary>
    [Fact]
    public void LoaderLogIstDerEinzigeLadeBeleg()
    {
        using var fake = new FakeIcarusInstall();
        fake.InstallLoader();
        fake.Paths.LoaderLastLoadedUtc().Should().BeNull();

        File.WriteAllText(Path.Combine(fake.Win64, "UE4SS.log"), "geladen");
        fake.Paths.LoaderLastLoadedUtc().Should().NotBeNull();
    }
}

public class Ue4ssLuaModServiceTests
{
    [Fact]
    public void ListetLuaModsMitSkriptzahlUndZustand()
    {
        using var fake = new FakeIcarusInstall();
        fake.AddLuaMod("DepositoMinerios", enabled: true, scripts: 2);
        fake.AddLuaMod("Stiller", enabled: false);

        var mods = fake.Service.ListInstalled();

        mods.Should().HaveCount(2);
        var depot = mods.Single(m => m.Name == "DepositoMinerios");
        depot.IsEnabled.Should().BeTrue();
        depot.ScriptCount.Should().Be(2);
        mods.Single(m => m.Name == "Stiller").IsEnabled.Should().BeFalse();
    }

    /// <summary>Die mitgelieferten Beispiel-Mods des Loaders sind keine
    /// Installation des Users. Sie in der Liste zu zeigen würde dazu
    /// einladen, sie zu deinstallieren.</summary>
    [Fact]
    public void BeispielModsDesLoadersBleibenAussen()
    {
        using var fake = new FakeIcarusInstall();
        fake.AddLuaMod("ConsoleEnablerMod");
        fake.AddLuaMod("Keybinds");
        fake.AddLuaMod("MeineMod");

        fake.Service.ListInstalled().Select(m => m.Name)
            .Should().BeEquivalentTo(["MeineMod"]);
    }

    /// <summary>Ein Ordner ohne Lua-Skript ist keine Lua-Mod — dort liegen
    /// etwa Blueprint-Mods des BPModLoaders.</summary>
    [Fact]
    public void OrdnerOhneSkriptIstKeineMod()
    {
        using var fake = new FakeIcarusInstall();
        Directory.CreateDirectory(Path.Combine(fake.Win64, "Mods", "NurBlueprints"));
        File.WriteAllText(Path.Combine(fake.Win64, "Mods", "NurBlueprints", "x.pak"), "x");

        fake.Service.ListInstalled().Should().BeEmpty();
    }

    [Fact]
    public void OhneUe4ssIstDieListeLeerUndKeinFehler()
    {
        using var fake = new FakeIcarusInstall();
        fake.Service.ListInstalled().Should().BeEmpty();
    }

    /// <summary>Umschalten läuft über die <c>enabled.txt</c>, und zwar per
    /// Umbenennen statt Löschen — manche Mods legen dort Inhalt ab.</summary>
    [Fact]
    public void UmschaltenBenenntDenMarkerUmUndVerliertNichts()
    {
        using var fake = new FakeIcarusInstall();
        var dir = fake.AddLuaMod("X");
        File.WriteAllText(Path.Combine(dir, "enabled.txt"), "wichtiger Inhalt");
        var svc = fake.Service;

        var mod = svc.ListInstalled().Single();
        var off = svc.SetEnabled(mod, false);

        off.IsEnabled.Should().BeFalse();
        File.Exists(Path.Combine(dir, "enabled.txt")).Should().BeFalse();
        File.ReadAllText(Path.Combine(dir, "enabled.txt.disabled"))
            .Should().Be("wichtiger Inhalt");

        var on = svc.SetEnabled(off, true);
        on.IsEnabled.Should().BeTrue();
        File.ReadAllText(Path.Combine(dir, "enabled.txt")).Should().Be("wichtiger Inhalt");
    }

    /// <summary>UE4SS wertet <c>mods.txt</c> UND <c>enabled.txt</c> aus.
    /// Steht die Mod dort mit <c>: 1</c>, lädt sie auch ohne Marker — ein
    /// Ausschalten, das nur den Marker umbenennt, bliebe wirkungslos.</summary>
    [Fact]
    public void BestehendeZeileInModsTxtWirdNachgezogen()
    {
        using var fake = new FakeIcarusInstall();
        fake.AddLuaMod("X");
        var modsTxt = Path.Combine(fake.Win64, "Mods", "mods.txt");
        File.WriteAllText(modsTxt, "﻿CheatManagerEnablerMod : 1\nX : 1\nKeybinds : 1\n");
        var svc = fake.Service;

        svc.SetEnabled(svc.ListInstalled().Single(), false);

        var lines = File.ReadAllLines(modsTxt);
        lines.Should().Contain("X : 0");
        lines.Should().Contain("Keybinds : 1", "fremde Zeilen bleiben unberührt");
        lines[0].Should().Contain("CheatManagerEnablerMod",
            "das BOM der ersten Zeile darf den Namensvergleich nicht brechen");
    }

    /// <summary>Neue Zeilen werden nicht angelegt: die mitgelieferte
    /// <c>enabled.txt</c> genügt UE4SS, und eine fremde Datei zu erweitern
    /// ist ein Eingriff ohne Gegenwert.</summary>
    [Fact]
    public void FehlendeZeileWirdNichtAngelegt()
    {
        using var fake = new FakeIcarusInstall();
        fake.AddLuaMod("X");
        var modsTxt = Path.Combine(fake.Win64, "Mods", "mods.txt");
        File.WriteAllText(modsTxt, "Keybinds : 1\n");
        var svc = fake.Service;

        svc.SetEnabled(svc.ListInstalled().Single(), false);

        File.ReadAllText(modsTxt).Should().Be("Keybinds : 1\n");
    }

    [Fact]
    public void InstalliertLuaModsAusEinemArchiv()
    {
        using var fake = new FakeIcarusInstall();
        var zip = Path.Combine(fake.Root, "OreDepot.zip");
        using (var a = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            WriteEntry(a, "Icarus Mod Manager/OreDepot.EXMODZ", "{}");
            WriteEntry(a, "UE4SS Mods/DepositoMinerios/Scripts/main.lua", "-- loader");
            WriteEntry(a, "UE4SS Mods/DepositoMinerios/Scripts/deposito.lua", "-- logik");
            WriteEntry(a, "UE4SS Mods/DepositoMinerios/enabled.txt", "");
            WriteEntry(a, "README_EN.txt", "lies mich");
        }

        var installed = fake.Service.InstallFromArchive(zip);

        installed.Should().BeEquivalentTo(["DepositoMinerios"]);
        var modDir = Path.Combine(fake.Win64, "Mods", "DepositoMinerios");
        File.Exists(Path.Combine(modDir, "Scripts", "main.lua")).Should().BeTrue();
        File.Exists(Path.Combine(modDir, "enabled.txt")).Should().BeTrue();
        // Die EXMODZ und die README gehoeren nicht in den Lua-Ordner.
        File.Exists(Path.Combine(modDir, "README_EN.txt")).Should().BeFalse();
        Directory.Exists(Path.Combine(fake.Win64, "Mods", "Icarus Mod Manager")).Should().BeFalse();

        var mod = fake.Service.ListInstalled().Single();
        mod.Name.Should().Be("DepositoMinerios");
        mod.ScriptCount.Should().Be(2);
        mod.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void DeinstallierenLoeschtNurImLuaOrdner()
    {
        using var fake = new FakeIcarusInstall();
        fake.AddLuaMod("X");
        var svc = fake.Service;
        var mod = svc.ListInstalled().Single();

        svc.Uninstall(mod);
        Directory.Exists(mod.FolderPath).Should().BeFalse();

        // Ein Pfad ausserhalb des Mods-Ordners wird abgelehnt, auch wenn er
        // existiert — Sicherheitsnetz gegen einen falsch gesetzten FolderPath.
        var fremd = Directory.CreateTempSubdirectory("icarus-fremd").FullName;
        try
        {
            var act = () => svc.Uninstall(mod with { FolderPath = fremd });
            act.Should().Throw<InvalidOperationException>();
            Directory.Exists(fremd).Should().BeTrue();
        }
        finally { Directory.Delete(fremd, recursive: true); }
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var e = archive.CreateEntry(path);
        using var s = e.Open();
        using var w = new StreamWriter(s);
        w.Write(content);
    }
}
