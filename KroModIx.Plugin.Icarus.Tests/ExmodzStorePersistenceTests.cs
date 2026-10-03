using System;
using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using KroModIx.Plugin.Contracts;
using KroModIx.Plugin.Icarus.Services.Exmodz;
using KroModIx.Plugin.TestKit;
using Xunit;

namespace KroModIx.Plugin.Icarus.Tests;

/// <summary>Die Staleness-Prüfung ist der Teil, der den Unterschied zwischen
/// „wirkt" und „wirkt stillschweigend nicht mehr" macht: nach einem
/// Icarus-Wochenupdate steht das gebaute Pak auf den Basistabellen der
/// Vorwoche und würde deren Werte zurückdrehen. Deshalb hier gegen jeden
/// einzelnen Grund geprüft, aus dem ein Neubau fällig wird.
///
/// <para>Der Pak-Container kommt seit v1.25.0 aus dem Host; hier läuft er
/// über <see cref="FakeUnrealPakService"/>. Das ist die richtige Testgrenze
/// — geprüft wird, ob die Staleness-Logik einen geänderten Index-Hash
/// erkennt, nicht ob der Hash byte-korrekt aus einem Footer
/// stammt.</para></summary>
public class ExmodzStateFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("icarus-store").FullName;
    private readonly FakeUnrealPakService _paks = new();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* Aufräumen darf scheitern */ }
    }

    [Theory]
    [InlineData("Mod 347 1.0 2026-10-01T20-47Z hash", "Mod 347 1.0 2026-10-01T20-47Z hash")]
    [InlineData("Ore/Depot", "Ore_Depot")]
    [InlineData(@"Ore\Depot", "Ore_Depot")]
    [InlineData("  ", "unbenannt")]
    [InlineData("...", "unbenannt")]
    public void KennungWirdZuEinemTauglichenDateinamen(string raw, string expected)
        => ExmodzStore.SanitizeId(raw).Should().Be(expected);

    /// <summary>Der Index-Hash der Basis-<c>data.pak</c> ist das Signal, an
    /// dem ein Spiel-Update erkannt wird. Er muss sich aus einem Pak lesen
    /// lassen und bei fehlender oder kaputter Datei <c>null</c> ergeben —
    /// nicht werfen, denn das wäre bei nicht eingehängter Spieleplatte der
    /// Normalfall.</summary>
    [Fact]
    public void BasisHashIstLesbarUndScheitertStillBeiFehlenderDatei()
    {
        ExmodzStore.TryReadBaseIndexHash(_paks, Path.Combine(_dir, "gibtsnicht.pak")).Should().BeNull();

        var kaputt = Path.Combine(_dir, "kaputt.pak");
        File.WriteAllText(kaputt, "kein Pak");
        ExmodzStore.TryReadBaseIndexHash(_paks, kaputt).Should().BeNull();

        var echt = Path.Combine(_dir, "echt.pak");
        var w = _paks.CreateBuilder();
        w.Add("Items/D_ItemsStatic.json", Encoding.UTF8.GetBytes("{\"Rows\":[]}"));
        w.Write(echt);
        ExmodzStore.TryReadBaseIndexHash(_paks, echt).Should().MatchRegex("^[0-9a-f]{40}$");
    }

    /// <summary>Ein Spiel-Update ändert die Basistabellen und damit den
    /// Index-Hash. Dass der Hash das wirklich erfasst, ist die Voraussetzung
    /// für die ganze Prüfung — ohne sie wäre „Icarus wurde aktualisiert" nie
    /// erkennbar.</summary>
    [Fact]
    public void GeaenderteBasistabellenAendernDenHash()
    {
        var a = Path.Combine(_dir, "woche251.pak");
        var b = Path.Combine(_dir, "woche252.pak");

        var w1 = _paks.CreateBuilder();
        w1.Add("Items/D_ItemsStatic.json", Encoding.UTF8.GetBytes("{\"Rows\":[{\"Name\":\"A\"}]}"));
        w1.Write(a);

        var w2 = _paks.CreateBuilder();
        w2.Add("Items/D_ItemsStatic.json", Encoding.UTF8.GetBytes("{\"Rows\":[{\"Name\":\"A\"},{\"Name\":\"B\"}]}"));
        w2.Write(b);

        ExmodzStore.TryReadBaseIndexHash(_paks, a).Should()
            .NotBe(ExmodzStore.TryReadBaseIndexHash(_paks, b));
    }
}

/// <summary>Der Compiler von der anderen Seite: was passiert mit dem
/// gebauten Pak, wenn sich die Lage ändert.</summary>
public class MergedPakLifecycleTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("icarus-lifecycle").FullName;
    private readonly FakeUnrealPakService _paks = new();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* Aufräumen darf scheitern */ }
    }

    private string BuildBasePak(string name = "data.pak")
    {
        var path = Path.Combine(_dir, name);
        var w = _paks.CreateBuilder("C:/BA/Temp/Data/");
        w.Add("Items/D_ItemsStatic.json", ExmodzFixture.BaseTable(("Eisen", 10)));
        w.Write(path);
        return path;
    }

    /// <summary>Nach einem Spiel-Update muss der Neubau gegen die
    /// <b>neuen</b> Basistabellen rechnen — und die neuen Basiszeilen müssen
    /// im Ergebnis stehen. Genau das ist der Grund, warum die
    /// <c>.EXMODZ</c>-Quelle im Plugin-Ordner bleibt und nicht ins Spiel
    /// kopiert wird.</summary>
    [Fact]
    public void NeubauNachSpielUpdateUebernimmtDieNeuenBasiszeilen()
    {
        var mod = Path.Combine(_dir, "M.EXMODZ");
        File.WriteAllBytes(mod, ExmodzFixture.Build("M", ExmodzFixture.Manifest("M", "1", new
        {
            CurrentFile = "Items-D_ItemsStatic.json",
            File_Items = new[] { new { Name = "Mod_Item", Wert = 1 } },
        })));
        var sources = new[] { new ExmodzSource("M", "M", mod) };
        var compiler = new ExmodzCompiler(_paks);

        // Woche 251
        var alt = BuildBasePak("alt.pak");
        var out1 = Path.Combine(_dir, "merged1_P.pak");
        compiler.Merge(alt, sources, out1).Ok.Should().BeTrue();

        // Woche 252: das Spiel bringt eine neue Basiszeile mit
        var neu = Path.Combine(_dir, "neu.pak");
        var w = _paks.CreateBuilder("C:/BA/Temp/Data/");
        w.Add("Items/D_ItemsStatic.json",
            ExmodzFixture.BaseTable(("Eisen", 10), ("Titan", 42)));
        w.Write(neu);

        var out2 = Path.Combine(_dir, "merged2_P.pak");
        compiler.Merge(neu, sources, out2).Ok.Should().BeTrue();

        using var r1 = _paks.OpenRead(out1);
        using var r2 = _paks.OpenRead(out2);
        var rows1 = System.Text.Json.Nodes.JsonNode
            .Parse(r1.Read("data/Items/D_ItemsStatic.json"))!["Rows"]!.AsArray();
        var rows2 = System.Text.Json.Nodes.JsonNode
            .Parse(r2.Read("data/Items/D_ItemsStatic.json"))!["Rows"]!.AsArray();

        rows1.Select(x => x!["Name"]!.GetValue<string>())
            .Should().BeEquivalentTo(["Eisen", "Mod_Item"]);
        rows2.Select(x => x!["Name"]!.GetValue<string>())
            .Should().BeEquivalentTo(["Eisen", "Titan", "Mod_Item"],
                "das alte Pak hätte Titan verschluckt — genau der Schaden, den die " +
                "Staleness-Prüfung verhindern soll");
    }

    /// <summary>Ein fehlgeschlagener Bau darf kein Pak hinterlassen.
    /// Bliebe das alte liegen, liefe der User mit einem Stand aus einer
    /// früheren Mod-Zusammenstellung weiter.</summary>
    [Fact]
    public void FehlgeschlagenerBauLaesstKeinPakZurueck()
    {
        var basePak = BuildBasePak();
        var kaputt = Path.Combine(_dir, "K.EXMODZ");
        File.WriteAllText(kaputt, "kein ZIP");

        var output = Path.Combine(_dir, "merged_P.pak");
        var result = new ExmodzCompiler(_paks).Merge(basePak,
            [new ExmodzSource("K", "K", kaputt)], output);

        result.Ok.Should().BeFalse();
        result.FailedMods.Should().ContainSingle();
        File.Exists(output).Should().BeFalse();
    }
}
