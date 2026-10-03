using FluentAssertions;
using KroModIx.Plugin.Icarus.Services.Nexus;
using Xunit;

namespace KroModIx.Plugin.Icarus.Tests;

/// <summary>Der Parser ist die Stelle, an der das Downloads- und
/// Installiert-Enrichment hängt. Schlägt er fehl, passiert nichts
/// Sichtbares — kein Fehler, kein Log, nur Rows ohne Cover und mit totem
/// Details-Knopf. Deshalb echte Dateinamen als Testfälle, keine erfundenen.
///
/// <para>Die Namen in <see cref="RealSpaceFormatNames"/> stammen aus einem
/// echten Downloads-Ordner (Bazzite, 03.10.2026) und aus der Nexus-CDN-URL
/// von OreDepot (Nexus 347).</para></summary>
public class NexusFileNameParserTests
{
    // ---- Space-Format (ISO-Zeitstempel), echte Dateinamen ----

    [Theory]
    [InlineData("OreDepot V1.0.1 347 2 2026-10-01T20-47Z yxOAgLyJG.zip", 347)]
    [InlineData("Level500cap-Week-252.zip 213 252 2026-10-02T15-31Z sVKlcSs5B.zip", 213)]
    [InlineData("IcarusStutterFix v0.2.0 294 0.2.0 2026-08-09T08-33Z KeUvDtKhb.zip", 294)]
    [InlineData("Balanced Armor Protection X2.5 250 1.0.5 2026-08-10T01-15Z AHkozCAwW.rar", 250)]
    [InlineData("Rada-CheatMenu 289 1.9 2026-08-09T21-35Z 6MsNEx6U7.7z", 289)]
    public void SpaceFormat_LiefertModId(string fileName, int expected)
        => NexusFileNameParser.TryExtractModId(fileName).Should().Be(expected);

    /// <summary>Die Altlast: bis v1.22 hängte der Downloader jedem Download
    /// ein <c>.pak</c> an. Solche Dateien liegen in bestehenden Ordnern und
    /// müssen weiter erkannt werden — sonst verlieren Bestandsnutzer beim
    /// Update auf v1.23 ihr Enrichment.</summary>
    [Theory]
    [InlineData("OreDepot V1.0.1 347 2 2026-10-01T20-47Z yxOAgLyJG.zip.pak", 347)]
    [InlineData("IcarusStutterFix v0.2.0 294 0.2.0 2026-08-09T08-33Z KeUvDtKhb.zip.pak", 294)]
    [InlineData("Rada-CheatMenu 289 1.9 2026-08-09T21-35Z 6MsNEx6U7.rar.pak", 289)]
    public void SpaceFormat_MitAngehaengtemPak_LiefertModId(string fileName, int expected)
        => NexusFileNameParser.TryExtractModId(fileName).Should().Be(expected);

    [Fact]
    public void SpaceFormat_LiefertNameUndVersion()
    {
        const string f = "OreDepot V1.0.1 347 2 2026-10-01T20-47Z yxOAgLyJG.zip";
        NexusFileNameParser.TryExtractModName(f).Should().Be("OreDepot V1.0.1");
        NexusFileNameParser.TryExtractVersion(f).Should().Be("2");
    }

    // ---- Dash-Format (Unix-Zeitstempel) ----

    [Theory]
    [InlineData("IcarusStutterFix-294-0-2-0-1703155833.zip", 294)]
    [InlineData("Locale-15-1-0-1703155833.7z", 15)]
    [InlineData("Rada-CheatMenu-289-1-9-1703155833.rar", 289)]
    public void DashFormat_LiefertModId(string fileName, int expected)
        => NexusFileNameParser.TryExtractModId(fileName).Should().Be(expected);

    [Fact]
    public void DashFormat_NormalisiertVersionZuPunkten()
        => NexusFileNameParser.TryExtractVersion("IcarusStutterFix-294-0-2-0-1703155833.zip")
            .Should().Be("0.2.0");

    [Fact]
    public void DashFormat_NameMitBindestrich_BleibtGanz()
        => NexusFileNameParser.TryExtractModName("Rada-CheatMenu-289-1-9-1703155833.rar")
            .Should().Be("Rada-CheatMenu");

    // ---- Was NICHT matchen darf ----

    [Theory]
    [InlineData("meine-eigene-mod.pak")]
    [InlineData("README.txt")]
    [InlineData("Ultimate Envirosuit_P.pak")]
    [InlineData("zzz_LMM_Merged_P.pak")]
    [InlineData("")]
    public void FremdeDateinamen_LiefernNull(string fileName)
    {
        NexusFileNameParser.TryExtractModId(fileName).Should().BeNull();
        NexusFileNameParser.TryExtractModName(fileName).Should().BeNull();
        NexusFileNameParser.TryExtractVersion(fileName).Should().BeNull();
    }

    /// <summary>Gegenprobe zur Endungs-Liste: eine Endung, die Nexus für
    /// Icarus nicht liefert, darf nicht durchrutschen — sonst steht der
    /// Parser für jede beliebige Datei gut und der Details-Knopf verspricht
    /// Metadaten, die es nicht gibt.</summary>
    [Fact]
    public void UnbekannteEndung_LiefertNull()
        => NexusFileNameParser.TryExtractModId(
            "OreDepot V1.0.1 347 2 2026-10-01T20-47Z yxOAgLyJG.tar.gz").Should().BeNull();
}
