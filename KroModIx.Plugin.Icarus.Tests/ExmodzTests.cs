using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using KroModIx.Plugin.Icarus.Services.Exmodz;
using KroModIx.Plugin.Icarus.Services.Pak;
using Xunit;

namespace KroModIx.Plugin.Icarus.Tests;

/// <summary>Baut ein <c>.EXMODZ</c> im Speicher — nach dem Layout des
/// echten OreDepot (Nexus 347).</summary>
internal static class ExmodzFixture
{
    public static byte[] Build(string modName, object manifest,
        IEnumerable<(string Path, byte[] Data)>? assets = null,
        IEnumerable<string>? extraManifests = null)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, $"Extracted Mods/{modName}.EXMOD",
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest)));
            foreach (var extra in extraManifests ?? [])
                Write(zip, $"Extracted Mods/{extra}.EXMOD", Encoding.UTF8.GetBytes("{\"Rows\":[]}"));
            foreach (var (path, data) in assets ?? [])
                Write(zip, path, data);
            Write(zip, "README_EN.txt", Encoding.UTF8.GetBytes("lies mich"));
        }
        return ms.ToArray();
    }

    private static void Write(ZipArchive zip, string path, byte[] data)
    {
        var e = zip.CreateEntry(path);
        using var s = e.Open();
        s.Write(data);
    }

    /// <summary>Ein Manifest in der Form, die echte <c>.EXMOD</c>-Dateien
    /// haben — samt der <c>EndOfMod</c>-Abschlusszeile ohne
    /// <c>File_Items</c>.</summary>
    public static object Manifest(string name, string version, params object[] rows)
        => new
        {
            name,
            author = "Maico48",
            version,
            description = "Testmod",
            Rows = rows.Append(new { CurrentFile = "EndOfMod" }).ToArray(),
        };

    /// <summary>Eine echte Icarus-Datentabelle in der Unreal-Export-Form.</summary>
    public static byte[] BaseTable(params (string Name, int Value)[] rows)
        => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            RowStruct = "/Script/Icarus.ItemStaticData",
            Defaults = new { Wert = 0 },
            Rows = rows.Select(r => new { Name = r.Name, Wert = r.Value }).ToArray(),
        }));
}

public class ExmodzParserTests
{
    [Fact]
    public void LiestManifestUndAssetsNachDemOreDepotLayout()
    {
        var zip = ExmodzFixture.Build("OreDepot",
            ExmodzFixture.Manifest("Ore Depot", "1.0.1",
                new { CurrentFile = "Items-D_ItemsStatic.json", File_Items = new[] { new { Name = "Mod_Ore_Depot" } } }),
            assets:
            [
                ("OreDepot/Assets/2DArt/UI/Items/Item_Icons/DepositoMod/ITEM_Refrigerator.uasset", new byte[] { 1, 2, 3 }),
                ("OreDepot/Assets/2DArt/UI/Items/Item_Icons/DepositoMod/ITEM_Refrigerator.uexp", new byte[] { 4, 5 }),
            ]);

        using var ms = new MemoryStream(zip);
        var bundle = ExmodzParser.Parse(ms);

        bundle.Diff.Name.Should().Be("Ore Depot");
        bundle.Diff.Author.Should().Be("Maico48");
        bundle.Diff.Version.Should().Be("1.0.1");
        bundle.Diff.Rows.Should().HaveCount(2, "die EndOfMod-Zeile wird mitgelesen und erst beim Bau übersprungen");

        // Der Huell-Ordner „OreDepot/" ist weg — sonst landet das Asset ein
        // Verzeichnis zu tief und das Spiel ignoriert es stillschweigend.
        bundle.Assets.Keys.Should().BeEquivalentTo(
        [
            "Assets/2DArt/UI/Items/Item_Icons/DepositoMod/ITEM_Refrigerator.uasset",
            "Assets/2DArt/UI/Items/Item_Icons/DepositoMod/ITEM_Refrigerator.uexp",
        ]);
        // Die README gehoert nicht ins Pak.
        bundle.Assets.Keys.Should().NotContain(k => k.Contains("README"));
    }

    [Fact]
    public void AssetOhneHuellOrdnerBleibtUnangetastet()
        => ExmodzParser.StripModWrapper("Assets/2DArt/Icon.uasset", "OreDepot")
            .Should().Be("Assets/2DArt/Icon.uasset");

    [Fact]
    public void HuellOrdnerWirdOhneRuecksichtAufSchreibweiseAbgeschnitten()
        => ExmodzParser.StripModWrapper("oredepot/Assets/Icon.uasset", "OreDepot")
            .Should().Be("Assets/Icon.uasset");

    [Fact]
    public void MehrereManifesteSindEinFehlerStattEinerRateentscheidung()
    {
        var zip = ExmodzFixture.Build("A", ExmodzFixture.Manifest("A", "1"), extraManifests: ["B"]);
        using var ms = new MemoryStream(zip);
        var act = () => ExmodzParser.Parse(ms);
        act.Should().Throw<InvalidDataException>().WithMessage("*mehrdeutig*");
    }

    [Fact]
    public void FehlendesManifestIstEinFehler()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            zip.CreateEntry("readme.txt");
        ms.Position = 0;
        var act = () => ExmodzParser.Parse(ms);
        act.Should().Throw<InvalidDataException>().WithMessage("*Manifest*");
    }

    [Fact]
    public void BetroffeneTabellenWerdenAufgeloest()
    {
        var zip = ExmodzFixture.Build("X", ExmodzFixture.Manifest("X", "1",
            new { CurrentFile = "Audio-MusicConditions-D_MusicLocationConditions.json", File_Items = new[] { new { Name = "A" } } },
            new { CurrentFile = "Items-D_ItemsStatic.json", File_Items = new[] { new { Name = "B" } } }));
        using var ms = new MemoryStream(zip);
        var bundle = ExmodzParser.Parse(ms);

        ExmodParser.TouchedTables(bundle.Diff).Should().BeEquivalentTo(
        [
            "Audio/MusicConditions/D_MusicLocationConditions.json",
            "Items/D_ItemsStatic.json",
        ], "die EndOfMod-Zeile zählt nicht mit");
    }
}

public class DataTablePatcherTests
{
    [Fact]
    public void PatchtBestehendeZeileUndLaesstAndereFelderStehen()
    {
        var baseTable = Encoding.UTF8.GetBytes(
            "{\"RowStruct\":\"X\",\"Defaults\":{\"a\":1}," +
            "\"Rows\":[{\"Name\":\"Eisen\",\"Menge\":10,\"Farbe\":\"grau\"}]}");
        var row = new ExmodRow("Items-D_ItemsStatic.json",
        [
            new ExmodFileItem("Eisen", new Dictionary<string, JsonNode?> { ["Menge"] = JsonValue.Create(99) }),
        ]);

        var result = JsonNode.Parse(DataTablePatcher.ApplyRowPatch(baseTable, row))!;

        result["Rows"]!.AsArray().Should().HaveCount(1);
        var patched = result["Rows"]![0]!;
        patched["Menge"]!.GetValue<int>().Should().Be(99);
        patched["Farbe"]!.GetValue<string>().Should().Be("grau", "unberührte Felder müssen überleben");
        result["RowStruct"]!.GetValue<string>().Should().Be("X", "andere Schlüssel gehen unverändert durch");
        result["Defaults"]!["a"]!.GetValue<int>().Should().Be(1);
    }

    /// <summary>Eine inhaltserweiternde Mod bringt Zeilen mit, die das
    /// Basisspiel nicht hat. Daran zu scheitern — der reine Patch-Entwurf —
    /// machte jede solche Mod unbaubar.</summary>
    [Fact]
    public void FuegtUnbekannteZeileAnStattZuScheitern()
    {
        var baseTable = ExmodzFixture.BaseTable(("Eisen", 10));
        var row = new ExmodRow("Items-D_ItemsStatic.json",
        [
            new ExmodFileItem("Mod_Ore_Depot", new Dictionary<string, JsonNode?>
            {
                ["Wert"] = JsonValue.Create(60),
                ["Verschachtelt"] = new JsonObject { ["RowName"] = "Refrigerator" },
            }),
        ]);

        var result = JsonNode.Parse(DataTablePatcher.ApplyRowPatch(baseTable, row))!;
        var rows = result["Rows"]!.AsArray();
        rows.Should().HaveCount(2);
        var added = rows.Single(r => r!["Name"]!.GetValue<string>() == "Mod_Ore_Depot")!;
        added["Wert"]!.GetValue<int>().Should().Be(60);
        added["Verschachtelt"]!["RowName"]!.GetValue<string>().Should().Be("Refrigerator",
            "verschachtelte Werte müssen mitkommen");
    }

    [Fact]
    public void TabelleOhneRowsArrayIstEinFehler()
    {
        var act = () => DataTablePatcher.ApplyRowPatch(
            Encoding.UTF8.GetBytes("{\"RowStruct\":\"X\"}"),
            new ExmodRow("X.json", [new ExmodFileItem("A", new Dictionary<string, JsonNode?>())]));
        act.Should().Throw<InvalidDataException>().WithMessage("*Rows*");
    }
}

public class ExmodzCompilerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("icarus-merge").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* Aufräumen darf scheitern */ }
    }

    /// <summary>Baut eine Basis-<c>data.pak</c> mit zwei Tabellen — über den
    /// eigenen Writer, damit der Test nicht von einer Installation
    /// abhängt.</summary>
    private string BuildBasePak()
    {
        var path = Path.Combine(_dir, "data.pak");
        var w = new UnrealPakWriter("C:/BA/work/Temp/Data/");
        w.AddFile("Items/D_ItemsStatic.json", ExmodzFixture.BaseTable(("Eisen", 10), ("Holz", 5)));
        w.AddFile("Traits/D_Energy.json", ExmodzFixture.BaseTable(("Strom", 1)));
        w.Write(path);
        return path;
    }

    private string WriteExmodz(string name, byte[] content)
    {
        var p = Path.Combine(_dir, name + ".EXMODZ");
        File.WriteAllBytes(p, content);
        return p;
    }

    [Fact]
    public void BautTabellenUndAssetsInEinPak()
    {
        var basePak = BuildBasePak();
        var modPath = WriteExmodz("OreDepot", ExmodzFixture.Build("OreDepot",
            ExmodzFixture.Manifest("Ore Depot", "1.0.1",
                new
                {
                    CurrentFile = "Items-D_ItemsStatic.json",
                    File_Items = new[] { new { Name = "Mod_Ore_Depot", Wert = 60 } },
                }),
            assets: [("OreDepot/Assets/Icon.uasset", new byte[] { 9, 9, 9 })]));

        var output = Path.Combine(_dir, "merged_P.pak");
        var result = new ExmodzCompiler().Merge(basePak,
            [new ExmodzSource("OreDepot", "Ore Depot", modPath)], output);

        result.Ok.Should().BeTrue(result.Message);
        result.TableCount.Should().Be(1);
        result.AssetCount.Should().Be(1);
        result.FailedMods.Should().BeEmpty();

        using var r = UnrealPakReader.Open(output);
        r.MountPoint.Should().Be(UnrealPakFormat.IcarusContentMountPoint);
        // Tabellen bekommen das data/-Praefix, Assets nicht — mit demselben
        // Praefix kann ein Pak beide Klassen nicht adressieren.
        r.Files().Select(f => f.Path).Should().BeEquivalentTo(
            ["data/Items/D_ItemsStatic.json", "Assets/Icon.uasset"]);
        r.ReadFile("Assets/Icon.uasset").Should().Equal([9, 9, 9]);

        var table = JsonNode.Parse(r.ReadFile("data/Items/D_ItemsStatic.json"))!;
        var rows = table["Rows"]!.AsArray();
        rows.Should().HaveCount(3, "die zwei Basiszeilen plus die neue");
        rows.Single(x => x!["Name"]!.GetValue<string>() == "Mod_Ore_Depot")!["Wert"]!
            .GetValue<int>().Should().Be(60);
        // Nicht angefasste Tabellen gehoeren NICHT ins Pak — sonst
        // ueberschattet es fremde Mods ohne Grund.
        r.Contains("data/Traits/D_Energy.json").Should().BeFalse();
    }

    /// <summary>Der Kern des Merges: zwei Mods an derselben Zeile, aber an
    /// verschiedenen Feldern, überleben beide. Als getrennte Paks würde einer
    /// den anderen ganztabellig überschatten.</summary>
    [Fact]
    public void ZweiModsAnDerselbenZeileKomponierenAufFeldebene()
    {
        var basePak = BuildBasePak();
        var modA = WriteExmodz("A", ExmodzFixture.Build("A",
            ExmodzFixture.Manifest("A", "1", new
            {
                CurrentFile = "Items-D_ItemsStatic.json",
                File_Items = new[] { new { Name = "Eisen", Wert = 999 } },
            })));
        var modB = WriteExmodz("B", ExmodzFixture.Build("B",
            ExmodzFixture.Manifest("B", "1", new
            {
                CurrentFile = "Items-D_ItemsStatic.json",
                File_Items = new[] { new { Name = "Eisen", Gewicht = 7 } },
            })));

        var output = Path.Combine(_dir, "merged_P.pak");
        var result = new ExmodzCompiler().Merge(basePak,
            [new ExmodzSource("A", "A", modA), new ExmodzSource("B", "B", modB)], output);

        result.Ok.Should().BeTrue(result.Message);
        using var r = UnrealPakReader.Open(output);
        var eisen = JsonNode.Parse(r.ReadFile("data/Items/D_ItemsStatic.json"))!["Rows"]!
            .AsArray().Single(x => x!["Name"]!.GetValue<string>() == "Eisen")!;
        eisen["Wert"]!.GetValue<int>().Should().Be(999, "Mod A");
        eisen["Gewicht"]!.GetValue<int>().Should().Be(7, "Mod B");
    }

    [Fact]
    public void AssetKollisionWirdGemeldetUndIstLetzterGewinnt()
    {
        var basePak = BuildBasePak();
        var modA = WriteExmodz("A", ExmodzFixture.Build("A", ExmodzFixture.Manifest("A", "1"),
            assets: [("A/Assets/Icon.uasset", new byte[] { 1 })]));
        var modB = WriteExmodz("B", ExmodzFixture.Build("B", ExmodzFixture.Manifest("B", "1"),
            assets: [("B/Assets/Icon.uasset", new byte[] { 2 })]));

        var output = Path.Combine(_dir, "merged_P.pak");
        var result = new ExmodzCompiler().Merge(basePak,
            [new ExmodzSource("A", "A", modA), new ExmodzSource("B", "B", modB)], output);

        result.Ok.Should().BeTrue(result.Message);
        result.Warnings.Should().ContainSingle().Which.Should().Contain("Assets/Icon.uasset");
        using var r = UnrealPakReader.Open(output);
        r.ReadFile("Assets/Icon.uasset").Should().Equal([2], "die spätere Mod gewinnt");
    }

    /// <summary>Eine kaputte Mod darf die anderen nicht mitnehmen — sonst
    /// hat der User kein Mittel außer Raten, welche es war.</summary>
    [Fact]
    public void EineKaputteModNimmtDieAnderenNichtMit()
    {
        var basePak = BuildBasePak();
        var gut = WriteExmodz("Gut", ExmodzFixture.Build("Gut",
            ExmodzFixture.Manifest("Gut", "1", new
            {
                CurrentFile = "Items-D_ItemsStatic.json",
                File_Items = new[] { new { Name = "Neu", Wert = 1 } },
            })));
        var kaputt = WriteExmodz("Kaputt", ExmodzFixture.Build("Kaputt",
            ExmodzFixture.Manifest("Kaputt", "1", new
            {
                // Tabelle, die es in der Basis nicht gibt — der typische Fall
                // „Mod passt nicht zur Spielwoche".
                CurrentFile = "Gibts-Nicht.json",
                File_Items = new[] { new { Name = "X" } },
            })));

        var output = Path.Combine(_dir, "merged_P.pak");
        var result = new ExmodzCompiler().Merge(basePak,
            [new ExmodzSource("Gut", "Gut", gut), new ExmodzSource("Kaputt", "Kaputt", kaputt)], output);

        result.Ok.Should().BeTrue("die gute Mod muss gebaut werden");
        result.FailedMods.Should().ContainSingle().Which.Should().StartWith("Kaputt");
        using var r = UnrealPakReader.Open(output);
        r.Contains("data/Items/D_ItemsStatic.json").Should().BeTrue();
    }

    [Fact]
    public void OhneQuellenWirdNichtsGebaut()
    {
        var result = new ExmodzCompiler().Merge(BuildBasePak(), [], Path.Combine(_dir, "x_P.pak"));
        result.Ok.Should().BeFalse();
        File.Exists(Path.Combine(_dir, "x_P.pak")).Should().BeFalse();
    }

    [Theory]
    [InlineData("Items-D_ItemsStatic.json", "Items/D_ItemsStatic.json")]
    [InlineData("Audio-MusicConditions-D_MusicLocationConditions.json",
        "Audio/MusicConditions/D_MusicLocationConditions.json")]
    public void BindestrichPfadeWerdenAufgeloest(string currentFile, string expected)
        => ExmodzCompiler.MatchMountPath([expected, "Anderes/X.json"], currentFile)
            .Should().Be(expected);

    [Fact]
    public void UnbekannteTabelleScheitertMitHinweisAufDieSpielwoche()
    {
        var act = () => ExmodzCompiler.MatchMountPath(["Items/D_ItemsStatic.json"], "Gibts-Nicht.json");
        act.Should().Throw<InvalidDataException>().WithMessage("*Spielwoche*");
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData(@"C:\evil.uasset")]
    [InlineData("a/../../raus.uasset")]
    public void AusbruchsversucheImAssetPfadWerdenAbgelehnt(string path)
    {
        var act = () => ExmodzCompiler.SanitizeAssetPath(path);
        act.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("Assets/Icon.uasset", "Assets/Icon.uasset")]
    [InlineData(@"Assets\Icon.uasset", "Assets/Icon.uasset")]
    [InlineData("Assets/./Icon.uasset", "Assets/Icon.uasset")]
    [InlineData("Assets/sub/../Icon.uasset", "Assets/Icon.uasset")]
    public void NormalerAssetPfadWirdNormalisiert(string input, string expected)
        => ExmodzCompiler.SanitizeAssetPath(input).Should().Be(expected);
}
