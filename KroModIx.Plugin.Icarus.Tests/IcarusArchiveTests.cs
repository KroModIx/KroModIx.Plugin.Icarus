using System;
using System.IO;
using System.IO.Compression;
using FluentAssertions;
using KroModIx.Plugin.Icarus.Services.Archive;
using Xunit;

namespace KroModIx.Plugin.Icarus.Tests;

public class IcarusArchiveClassifyTests
{
    /// <summary>Die Eintragsliste von OreDepot v1.0.1 (Nexus 347), wörtlich
    /// aus <c>unzip -l</c> übernommen. Das ist das Referenz-Layout eines
    /// Icarus-Mod-Archivs: Datentabellen-Mod und Lua-Mod in einem Paket.</summary>
    private static readonly string[] OreDepotEntries =
    [
        "Icarus Mod Manager/OreDepot.EXMODZ",
        "Icarus Mod Manager/OreDepot_PTBR.EXMODZ",
        "UE4SS Mods/DepositoMinerios/Scripts/main.lua",
        "UE4SS Mods/DepositoMinerios/Scripts/deposito.lua",
        "UE4SS Mods/DepositoMinerios/enabled.txt",
        "README_EN.txt",
        "LEIAME_PT.txt",
    ];

    [Fact]
    public void OreDepot_WirdVollstaendigEingeordnet()
    {
        var c = IcarusArchive.Classify(OreDepotEntries);

        c.IsReadable.Should().BeTrue();
        c.ExmodzEntries.Should().HaveCount(2);
        c.Ue4ssModNames.Should().ContainSingle().Which.Should().Be("DepositoMinerios");
        c.PakEntries.Should().BeEmpty();
        c.HasInstallable.Should().BeTrue();
        c.TotalEntries.Should().Be(7);
    }

    [Fact]
    public void MehrereLuaMods_WerdenEinzelnGezaehlt()
    {
        var c = IcarusArchive.Classify([
            "UE4SS Mods/ModA/Scripts/main.lua",
            "UE4SS Mods/ModA/enabled.txt",
            "UE4SS Mods/ModB/Scripts/main.lua",
        ]);
        c.Ue4ssModNames.Should().BeEquivalentTo(["ModA", "ModB"]);
    }

    /// <summary>Eine lose Datei direkt in <c>UE4SS Mods/</c> ist keine Mod —
    /// UE4SS lädt pro Ordner. Ohne diese Grenze würde eine beigelegte
    /// <c>mods.txt</c> als Mod namens „mods.txt" in der Liste stehen.</summary>
    [Fact]
    public void LoseDateiImUe4ssOrdner_IstKeineMod()
    {
        var c = IcarusArchive.Classify(["UE4SS Mods/mods.txt"]);
        c.Ue4ssModNames.Should().BeEmpty();
    }

    [Fact]
    public void ReinesPakArchiv_WirdAlsPakErkannt()
    {
        var c = IcarusArchive.Classify(["Ultimate Envirosuit_P.pak", "README.md"]);
        c.PakEntries.Should().ContainSingle();
        c.HasUe4ssMods.Should().BeFalse();
        c.HasExmodz.Should().BeFalse();
    }

    [Fact]
    public void ArchivOhneModInhalt_HatNichtsInstallierbares()
    {
        var c = IcarusArchive.Classify(["README.txt", "screenshots/bild.png"]);
        c.HasInstallable.Should().BeFalse();
        c.IsReadable.Should().BeTrue("lesbar und leer ist nicht dasselbe wie unlesbar");
    }

    [Fact]
    public void BackslashPfade_WerdenNormalisiert()
    {
        var c = IcarusArchive.Classify([@"UE4SS Mods\DepositoMinerios\Scripts\main.lua"]);
        c.Ue4ssModNames.Should().ContainSingle().Which.Should().Be("DepositoMinerios");
    }

    [Fact]
    public void UnlesbaresArchiv_MeldetFehlerStattLeere()
    {
        var c = IcarusArchiveContents.Unreadable("kaputt");
        c.IsReadable.Should().BeFalse();
        c.HasInstallable.Should().BeFalse();
    }
}

/// <summary>Die Typ-Erkennung ist der Reihenfolge wegen interessant: erst
/// Pak, dann Archiv. Die Byte-Arbeit selbst machen seit v1.25.0 die
/// Host-Baukästen, hier über Attrappen — geprüft wird die
/// Entscheidungslogik des Plugins, nicht ob eine Signatur stimmt. Dafür gibt
/// es im Host <c>HostArchiveServiceTests</c> und
/// <c>HostUnrealPakServiceTests</c>.</summary>
public class IcarusArchiveDetectKindTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("icarus-detect").FullName;
    private readonly FakeUnrealPakService _paks = new();
    private readonly FakeArchiveService _archives = new();
    private readonly IcarusArchive _sut;

    public IcarusArchiveDetectKindTests() => _sut = new IcarusArchive(_archives, _paks);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* Aufräumen darf scheitern */ }
    }

    private string MakeZip(string name)
    {
        var path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        zip.CreateEntry("UE4SS Mods/X/enabled.txt");
        return path;
    }

    private string MakePak(string name)
    {
        var path = Path.Combine(_dir, name);
        var b = _paks.CreateBuilder();
        b.Add("Items/D_ItemsStatic.json", System.Text.Encoding.UTF8.GetBytes("{\"Rows\":[]}"));
        b.Write(path);
        return path;
    }

    [Fact]
    public void EchtesZipIstArchiv()
        => _sut.DetectKind(MakeZip("mod.zip")).Should().Be(IcarusFileKind.Archive);

    /// <summary>Der eigentliche Grund für die Inhaltsprüfung: bis v1.22
    /// hängte der Downloader jedem Download ein <c>.pak</c> an. Nach der
    /// Endung behandelt wäre das hier ein PAK und würde unverändert in den
    /// Mods-Ordner kopiert, wo Icarus es nicht lesen kann — still, ohne
    /// Fehlermeldung.</summary>
    [Fact]
    public void ZipMitPakEndungIstTrotzdemArchiv()
        => _sut.DetectKind(MakeZip("Mod 347 1.0 2026-10-01T20-47Z hash.zip.pak"))
            .Should().Be(IcarusFileKind.Archive);

    [Fact]
    public void EchtesPakIstPak()
        => _sut.DetectKind(MakePak("Mod_P.pak")).Should().Be(IcarusFileKind.Pak);

    /// <summary>Die Pak-Prüfung läuft <b>vor</b> der Archiv-Prüfung. Sonst
    /// wäre ein Pak, dessen erste Bytes zufällig wie ein ZIP aussehen, als
    /// Archiv eingeordnet und der Installer hätte versucht, es
    /// auszupacken.</summary>
    [Fact]
    public void PakGewinntGegenEineZipSignatur()
    {
        var path = Path.Combine(_dir, "zwitter.pak");
        // Die Attrappe erkennt ihr Pak an der Kennung am Dateianfang; fuer
        // diesen Test zaehlt, dass die Reihenfolge im Plugin stimmt — ein
        // echtes Pak wird vom Host an der Footer-Magic erkannt, unabhaengig
        // davon, womit es anfaengt.
        MakePak("zwitter.pak");
        _paks.IsPakFile(path).Should().BeTrue();
        _sut.DetectKind(path).Should().Be(IcarusFileKind.Pak);
    }

    [Fact]
    public void FremdeDateiIstUnbekannt()
    {
        var path = Path.Combine(_dir, "notizen.txt");
        File.WriteAllText(path, "Hallo!");
        _sut.DetectKind(path).Should().Be(IcarusFileKind.Unknown);
    }

    /// <summary>Was weder Pak noch Archiv ist, aber auf <c>.pak</c> endet,
    /// gilt als PAK — etwa ein abgebrochener Download, den der Installer
    /// dann sauber ablehnt.</summary>
    [Fact]
    public void UnlesbaresMitPakEndungGiltAlsPak()
    {
        var path = Path.Combine(_dir, "abgebrochen.pak");
        File.WriteAllBytes(path, [0x00, 0x01]);
        _sut.DetectKind(path).Should().Be(IcarusFileKind.Pak);
    }

    [Fact]
    public void FehlendeDateiIstUnbekannt()
        => _sut.DetectKind(Path.Combine(_dir, "gibtsnicht.zip")).Should().Be(IcarusFileKind.Unknown);

    [Theory]
    [InlineData("mod.pak", true)]
    [InlineData("mod.zip", true)]
    [InlineData("mod.RAR", true)]
    [InlineData("mod.7z", true)]
    [InlineData("mod.txt", false)]
    public void EndungsVorfilter(string name, bool expected)
        => _sut.HasSupportedExtension(name).Should().Be(expected);

    [Fact]
    public void InspiziertEinArchivUeberDenHostBaukasten()
    {
        var path = Path.Combine(_dir, "oredepot.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            zip.CreateEntry("Icarus Mod Manager/OreDepot.EXMODZ");
            zip.CreateEntry("UE4SS Mods/DepositoMinerios/Scripts/main.lua");
            zip.CreateEntry("README_EN.txt");
        }

        var c = _sut.Inspect(path);
        c.IsReadable.Should().BeTrue();
        c.ExmodzEntries.Should().ContainSingle();
        c.Ue4ssModNames.Should().BeEquivalentTo(["DepositoMinerios"]);
        c.HasInstallable.Should().BeTrue();
    }

    [Fact]
    public void UnlesbaresArchivMeldetFehlerStattLeere()
    {
        var path = Path.Combine(_dir, "kaputt.zip");
        File.WriteAllText(path, "kein ZIP");
        var c = _sut.Inspect(path);
        c.IsReadable.Should().BeFalse();
        c.HasInstallable.Should().BeFalse();
    }
}
