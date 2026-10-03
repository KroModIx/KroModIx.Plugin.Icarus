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

public class IcarusArchiveDetectKindTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("icarus-detect").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* Aufräumen darf scheitern */ }
    }

    private string Write(string name, byte[] content)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, content);
        return p;
    }

    [Fact]
    public void EchtesZip_IstArchiv()
    {
        var path = Path.Combine(_dir, "mod.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            zip.CreateEntry("UE4SS Mods/X/enabled.txt");
        IcarusArchive.DetectKind(path).Should().Be(IcarusFileKind.Archive);
    }

    /// <summary>Der eigentliche Grund für die Magic-Byte-Erkennung: bis v1.22
    /// hängte <c>DownloadPakAsync</c> jedem Download ein <c>.pak</c> an. Nach
    /// der Endung behandelt wäre das hier ein PAK und würde unverändert in
    /// den Mods-Ordner kopiert, wo Icarus es nicht lesen kann — still, ohne
    /// Fehlermeldung.</summary>
    [Fact]
    public void ZipMitPakEndung_IstTrotzdemArchiv()
    {
        var tmp = Path.Combine(_dir, "tmp.zip");
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            zip.CreateEntry("egal.txt");
        var bytes = File.ReadAllBytes(tmp);
        var path = Write("Mod 347 1.0 2026-10-01T20-47Z hash.zip.pak", bytes);
        IcarusArchive.DetectKind(path).Should().Be(IcarusFileKind.Archive);
    }

    [Fact]
    public void RarSignatur_IstArchiv()
        => IcarusArchive.DetectKind(Write("mod.rar",
            [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00]))
            .Should().Be(IcarusFileKind.Archive);

    [Fact]
    public void SevenZipSignatur_IstArchiv()
        => IcarusArchive.DetectKind(Write("mod.7z",
            [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00]))
            .Should().Be(IcarusFileKind.Archive);

    /// <summary>Ein echtes Unreal-PAK hat seine Magic (0x5A6F12E1) im Footer,
    /// dessen Position von der Pak-Version abhängt. Deshalb ist PAK der
    /// Ausweichfall: keine der drei Archiv-Signaturen, aber auf .pak endend.</summary>
    [Fact]
    public void PakOhneArchivSignatur_IstPak()
        => IcarusArchive.DetectKind(Write("Mod_P.pak",
            [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06]))
            .Should().Be(IcarusFileKind.Pak);

    [Fact]
    public void FremdeDatei_IstUnbekannt()
        => IcarusArchive.DetectKind(Write("notizen.txt",
            [0x48, 0x61, 0x6C, 0x6C, 0x6F, 0x21, 0x0A]))
            .Should().Be(IcarusFileKind.Unknown);

    [Fact]
    public void FehlendeDatei_IstUnbekannt()
        => IcarusArchive.DetectKind(Path.Combine(_dir, "gibtsnicht.zip"))
            .Should().Be(IcarusFileKind.Unknown);

    /// <summary>Eine zu kurze Datei darf nicht crashen. Endet sie auf .pak,
    /// gilt sie als PAK — ein abgebrochener Download, den der Installer
    /// später sauber ablehnt.</summary>
    [Fact]
    public void ZuKurzeDatei_FaelltAufEndungZurueck()
    {
        IcarusArchive.DetectKind(Write("kurz.pak", [0x00, 0x01])).Should().Be(IcarusFileKind.Pak);
        IcarusArchive.DetectKind(Write("kurz.txt", [0x00, 0x01])).Should().Be(IcarusFileKind.Unknown);
    }
}

public class IcarusArchiveZipSlipTests
{
    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData(@"..\..\windows\system32\evil.dll")]
    [InlineData("/etc/passwd")]
    [InlineData(@"C:\windows\evil.dll")]
    [InlineData("harmlos/../../../raus.txt")]
    [InlineData("")]
    [InlineData("   ")]
    public void AusbruchsversuchWirdAbgelehnt(string relative)
    {
        var root = Path.Combine(Path.GetTempPath(), "icarus-zipslip-root");
        IcarusArchive.TryResolveSafe(root, relative, out var dst).Should().BeFalse();
        dst.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Mod_P.pak")]
    [InlineData("UE4SS Mods/DepositoMinerios/Scripts/main.lua")]
    [InlineData(@"UE4SS Mods\DepositoMinerios\enabled.txt")]
    public void NormalerPfadWirdAngenommen(string relative)
    {
        var root = Path.Combine(Path.GetTempPath(), "icarus-zipslip-root");
        IcarusArchive.TryResolveSafe(root, relative, out var dst).Should().BeTrue();
        dst.Should().StartWith(Path.GetFullPath(root));
    }
}
