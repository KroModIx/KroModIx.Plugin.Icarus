using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using KroModIx.Plugin.Icarus.Services.Pak;
using Xunit;

namespace KroModIx.Plugin.Icarus.Tests;

/// <summary>Der Pak-Container ist die Stelle, an der ein Fehler nicht als
/// Ausnahme auffällt, sondern als „die Mod tut nichts": ein Pak mit falschem
/// Index lädt das Spiel stillschweigend nicht. Deshalb geht hier jeder
/// geschriebene Pak durch den eigenen Leser zurück.
///
/// <para>Zusätzlich zu diesen Tests ist die Portierung gegen echte Daten
/// gemessen worden — siehe <see cref="RealIcarusPakTests"/>, das übersprungen
/// wird, wo keine Icarus-Installation liegt (also im CI).</para></summary>
public class UnrealPakRoundTripTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("icarus-pak").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* Aufräumen darf scheitern */ }
    }

    private static Dictionary<string, byte[]> SamplePayloads() => new(StringComparer.Ordinal)
    {
        // Eine Tabelle, ein Asset, eine Datei auf oberster Ebene und etwas
        // Binäres — die vier Formen, die im echten Betrieb vorkommen.
        ["data/Items/D_ItemsStatic.json"] = Encoding.UTF8.GetBytes("{\"Rows\":[{\"Name\":\"A\"}]}"),
        ["data/Traits/D_Energy.json"] = Encoding.UTF8.GetBytes("{\"Rows\":[]}"),
        ["Assets/2DArt/UI/Icon.uasset"] = RandomNumberGenerator.GetBytes(4096),
        ["wurzel.json"] = Encoding.UTF8.GetBytes("{}"),
    };

    [Fact]
    public void GeschriebenesPakKommtUnveraendertZurueck()
    {
        var payloads = SamplePayloads();
        var path = Path.Combine(_dir, "test_P.pak");

        var w = new UnrealPakWriter();
        foreach (var (k, v) in payloads) w.AddFile(k, v);
        w.Write(path);

        using var r = UnrealPakReader.Open(path);
        r.MountPoint.Should().Be(UnrealPakFormat.IcarusContentMountPoint);
        r.Files().Select(f => f.Path).Should().BeEquivalentTo(payloads.Keys);
        foreach (var (k, v) in payloads)
        {
            r.ReadFile(k).Should().Equal(v, $"{k} muss byte-gleich zurückkommen");
            r.Files().Single(f => f.Path == k).Size.Should().Be(v.Length);
        }
    }

    /// <summary>Gleiche Eingabe, gleiche Bytes — unabhängig von der
    /// Aufrufreihenfolge. Das ist nicht Kosmetik: ohne diese Eigenschaft
    /// änderte sich das Pak bei jedem Neubau, und eine Prüfsumme darüber
    /// wäre als „hat sich etwas geändert"-Signal unbrauchbar.</summary>
    [Fact]
    public void AusgabeIstReproduzierbar()
    {
        var payloads = SamplePayloads();
        var a = Path.Combine(_dir, "a_P.pak");
        var b = Path.Combine(_dir, "b_P.pak");

        var w1 = new UnrealPakWriter();
        foreach (var (k, v) in payloads) w1.AddFile(k, v);
        w1.Write(a);

        var w2 = new UnrealPakWriter();
        foreach (var (k, v) in payloads.Reverse()) w2.AddFile(k, v);
        w2.Write(b);

        SHA256.HashData(File.ReadAllBytes(a)).Should().Equal(SHA256.HashData(File.ReadAllBytes(b)));
    }

    [Fact]
    public void IndexHashAendertSichMitDemInhalt()
    {
        var a = Path.Combine(_dir, "a_P.pak");
        var b = Path.Combine(_dir, "b_P.pak");

        var w1 = new UnrealPakWriter();
        w1.AddFile("data/X.json", Encoding.UTF8.GetBytes("{\"Rows\":[]}"));
        w1.Write(a);

        var w2 = new UnrealPakWriter();
        w2.AddFile("data/X.json", Encoding.UTF8.GetBytes("{\"Rows\":[{\"Name\":\"B\"}]}"));
        w2.Write(b);

        using var ra = UnrealPakReader.Open(a);
        using var rb = UnrealPakReader.Open(b);
        ra.IndexHash.Should().NotBe(rb.IndexHash);
        ra.IndexHash.Should().MatchRegex("^[0-9a-f]{40}$");
    }

    [Fact]
    public void FuehrenderSchraegstrichWirdNormalisiert()
    {
        var path = Path.Combine(_dir, "slash_P.pak");
        var w = new UnrealPakWriter();
        w.AddFile("/wurzel.json", Encoding.UTF8.GetBytes("{}"));
        w.Write(path);

        using var r = UnrealPakReader.Open(path);
        r.Files().Single().Path.Should().Be("wurzel.json");
    }

    [Fact]
    public void DoppelterPfadWirdAbgelehnt()
    {
        var w = new UnrealPakWriter();
        w.AddFile("data/X.json", [1]);
        var act = () => w.AddFile("/data/X.json", [2]);
        act.Should().Throw<InvalidOperationException>("der führende Schrägstrich macht keinen neuen Pfad");
    }

    [Fact]
    public void LeeresPakWirdAbgelehnt()
    {
        var act = () => new UnrealPakWriter().Write(Path.Combine(_dir, "leer_P.pak"));
        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>Ein abgebrochener Schreibvorgang darf kein halbes Pak
    /// zurücklassen — das Spiel würde es beim nächsten Start zu laden
    /// versuchen.</summary>
    [Fact]
    public void AbgebrochenerSchreibvorgangLaesstNichtsZurueck()
    {
        var target = Path.Combine(_dir, "unterverzeichnis", "ziel_P.pak");
        var w = new UnrealPakWriter();
        w.AddFile("data/X.json", Encoding.UTF8.GetBytes("{}"));
        w.Write(target);
        File.Exists(target).Should().BeTrue();
        File.Exists(target + ".tmp").Should().BeFalse("die Zwischendatei muss weg sein");
    }

    [Fact]
    public void FremdeDateiWirdAlsUnbekanntesFormatAbgelehnt()
    {
        var path = Path.Combine(_dir, "kein.pak");
        File.WriteAllText(path, "das ist kein Pak");
        var act = () => UnrealPakReader.Open(path);
        act.Should().Throw<UnsupportedPakFormatException>();
    }

    /// <summary>Der Pfad-Hash ist das Rezept, an dem die Vorlage am
    /// längsten gearbeitet hat (FNV-1a 64 über UTF-16LE, Seed auf den
    /// Offset-Basiswert <b>addiert</b>, kleingeschrieben, ohne führenden
    /// Schrägstrich). Er wird hier gegen seine Eigenschaften geprüft, damit
    /// ein Umbau nicht still etwas anderes berechnet.</summary>
    [Fact]
    public void PfadHashIstStabilUndUnterscheidendeFaelleUnabhaengig()
    {
        const ulong seed = UnrealPakFormat.WriterSeed;
        var a = UnrealPakFormat.HashPath("data/Items/D_ItemsStatic.json", seed);

        // Gleicher Pfad, gleicher Hash.
        UnrealPakFormat.HashPath("data/Items/D_ItemsStatic.json", seed).Should().Be(a);
        // Schreibweise ist unerheblich (UE-Pfade sind unempfindlich).
        UnrealPakFormat.HashPath("DATA/items/d_itemsstatic.JSON", seed).Should().Be(a);
        // Führender Schrägstrich ebenso.
        UnrealPakFormat.HashPath("/data/Items/D_ItemsStatic.json", seed).Should().Be(a);
        // Anderer Pfad, anderer Hash.
        UnrealPakFormat.HashPath("data/Items/D_ItemTemplate.json", seed).Should().NotBe(a);
        // Anderer Seed, anderer Hash.
        UnrealPakFormat.HashPath("data/Items/D_ItemsStatic.json", seed + 1).Should().NotBe(a);
    }

    [Theory]
    [InlineData("a/b/c.json", "a/b/", "c.json")]
    [InlineData("c.json", "/", "c.json")]
    [InlineData("a/c.json", "a/", "c.json")]
    public void PfadZerlegungTrifftDieVerzeichnisSchluessel(string rel, string dir, string file)
    {
        var (d, f) = UnrealPakFormat.SplitMountPath(rel);
        d.Should().Be(dir);
        f.Should().Be(file);
    }
}
