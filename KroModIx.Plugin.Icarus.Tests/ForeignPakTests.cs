using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using KroModIx.Plugin.Icarus.Services;
using Xunit;

namespace KroModIx.Plugin.Icarus.Tests;

/// <summary>Paks, die einem anderen Mod-Manager gehören. Der Anlass ist
/// gemessen: am 03.10.2026 lag <c>zzz_LMM_Merged_P.pak</c> im Mods-Ordner,
/// das Plugin listete es als gewöhnliche manuelle Mod, und ein Klick auf
/// Deinstallieren nahm damit alle Datentabellen-Mods aus dem Spiel, die lmm
/// dort zusammengeführt hatte — lautlos, während die Quellen unversehrt in
/// lmms Zwischenspeicher lagen.</summary>
public sealed class ForeignPakDetectorTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("icarus-fremd").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* Aufräumen darf scheitern */ }
    }

    private string Datei(string name, string inhalt = "x")
    {
        var p = Path.Combine(_tmp, name);
        File.WriteAllText(p, inhalt);
        return p;
    }

    /// <summary>Der Fall von gestern, am Namen erkannt — greift auch dann,
    /// wenn ein Manager kopiert statt zu verweisen.</summary>
    [Fact]
    public void Lmm_Merged_Pak_wird_am_Namen_erkannt()
    {
        var p = Datei("zzz_LMM_Merged_P.pak");

        ForeignPakDetector.IsForeignManaged(p, out var wer).Should().BeTrue();
        wer.Should().Be("lmm");
    }

    /// <summary>Das verlässlichere Merkmal: lmm legt einen Verweis in seinen
    /// eigenen Zwischenspeicher. Das trägt auch für Manager, deren
    /// Namensschema wir nicht kennen.</summary>
    [Fact]
    public void Verweis_in_den_lmm_Zwischenspeicher_wird_erkannt()
    {
        var ziel = Datei("echte-datei.pak");
        var cache = Path.Combine(_tmp, "share", "lmm", "cache");
        Directory.CreateDirectory(cache);
        var imCache = Path.Combine(cache, "irgendwas.pak");
        File.Move(ziel, imCache);

        var link = Path.Combine(_tmp, "HarmlosBenannt_P.pak");
        File.CreateSymbolicLink(link, imCache);

        ForeignPakDetector.IsForeignManaged(link, out var wer).Should().BeTrue();
        wer.Should().Be("lmm");
    }

    /// <summary>Ein Verweis irgendwohin sonst ist auch fremd — nur wissen
    /// wir dann nicht, wem. Dann bleibt die Angabe neutral statt zu raten.</summary>
    [Fact]
    public void Fremder_Verweis_ohne_bekannten_Besitzer_bleibt_neutral()
    {
        var ziel = Datei("woanders.pak");
        var link = Path.Combine(_tmp, "verweis_P.pak");
        File.CreateSymbolicLink(link, ziel);

        ForeignPakDetector.IsForeignManaged(link, out var wer).Should().BeTrue();
        wer.Should().Be("ein anderer Mod-Manager");
    }

    /// <summary>Eine von Hand hineinkopierte Mod ist unsere und muss sich
    /// weiter umschalten und deinstallieren lassen — sonst hätte die
    /// Absicherung den Normalfall mitgenommen.</summary>
    [Theory]
    [InlineData("Ultimate Envirosuit_P.pak")]
    [InlineData("levelcap_252_500_P.pak")]
    [InlineData("Yeesha_WeightSpeedStam_P.pak")]
    [InlineData("endofquarrites_252_P.pak")]
    public void Gewoehnliche_Datei_ist_nicht_fremd(string name)
    {
        var p = Datei(name);

        ForeignPakDetector.IsForeignManaged(p, out var wer).Should().BeFalse();
        wer.Should().BeEmpty();
    }

    /// <summary>Eine fehlende Datei darf die ganze Liste nicht
    /// mitreißen — dann gilt sie als gewöhnlich und verhält sich wie
    /// bisher.</summary>
    [Fact]
    public void Fehlende_Datei_gilt_als_gewoehnlich()
        => ForeignPakDetector.IsForeignManaged(
            Path.Combine(_tmp, "gibtsnicht.pak"), out _).Should().BeFalse();
}

/// <summary>Was das Plugin mit einem fremdverwalteten Pak tun darf: listen,
/// und sonst nichts.</summary>
public sealed class FremdverwaltetesPakVerhaltenTests
{
    private static InstalledPakMod Fremd(string name = "zzz_LMM_Merged_P.pak", string? wer = "lmm")
        => new(FilePath: "/spiel/Content/Paks/mods/" + name, FileName: name,
            FileSizeBytes: 6_708_382, InstalledUtc: DateTime.UtcNow, IsEnabled: true,
            Source: PakModSource.ForeignManaged, ManagedBy: wer);

    /// <summary>Die Meldung muss drei Dinge sagen: wer es verwaltet, was
    /// verloren ginge, und wohin der Nutzer stattdessen greift. Das Fehlen
    /// genau dieser Angaben hat am 03.10.2026 Stunden gekostet.</summary>
    [Fact]
    public void Die_Meldung_nennt_Verwalter_Folge_und_Ausweg()
    {
        var text = PakInstallService.FremdverwaltetMeldung(Fremd(), "deinstallieren");

        text.Should().Contain("zzz_LMM_Merged_P.pak");
        text.Should().Contain("lmm");
        text.Should().Contain("deinstallieren");
        text.Should().Contain("verschwänden", "die Folge muss benannt sein, nicht nur das Verbot");
        text.Should().Contain("Quellen", "und dass die Quellen liegen bleiben");
    }

    [Fact]
    public void Ohne_bekannten_Verwalter_bleibt_die_Meldung_lesbar()
    {
        var text = PakInstallService.FremdverwaltetMeldung(Fremd(wer: null), "umschalten");

        text.Should().Contain("ein anderer Mod-Manager");
        text.Should().NotContain("  ", "keine Luecke, wo der Name fehlt");
    }
}

/// <summary>Der Dienst selbst — hier zählt, dass er den Scan richtig
/// einordnet und dass die Sperre wirklich greift. Die Oberfläche fängt es
/// vorher ab, aber eine Absicherung, die nur in der Ansicht sitzt, ist
/// keine: der Dienst ist öffentlich und wird auch aus den Bulk-Pfaden und
/// aus dem Installer gerufen.</summary>
public sealed class FremdverwaltetesPakDienstTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("icarus-fremd-dienst").FullName;
    private readonly string _mods;
    private readonly PakInstallService _sut;

    public FremdverwaltetesPakDienstTests()
    {
        _mods = Path.Combine(_tmp, "mods");
        Directory.CreateDirectory(_mods);
        _sut = new PakInstallService(_mods, null, Path.Combine(_tmp, "downloads"),
            new KroModIx.Plugin.TestKit.FakeArchiveService(),
            new KroModIx.Plugin.TestKit.FakeUnrealPakService());
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* Aufräumen darf scheitern */ }
    }

    /// <summary>Der echte Ordnerinhalt von Lars' Installation am
    /// 03.10.2026: vier eigene Paks und das Merged-Pak von lmm.</summary>
    [Fact]
    public void Der_Scan_trennt_eigene_von_fremden_Paks()
    {
        foreach (var n in new[] { "Ultimate Envirosuit_P.pak", "levelcap_252_500_P.pak",
                                  "Yeesha_WeightSpeedStam_P.pak", "endofquarrites_252_P.pak" })
            File.WriteAllText(Path.Combine(_mods, n), "pak");
        File.WriteAllText(Path.Combine(_mods, "zzz_LMM_Merged_P.pak"), "pak");

        var liste = _sut.ListInstalled();

        liste.Should().HaveCount(5);
        liste.Where(m => m.Source == PakModSource.Manual).Should().HaveCount(4);
        var fremd = liste.Should().ContainSingle(m => m.Source == PakModSource.ForeignManaged).Subject;
        fremd.FileName.Should().Be("zzz_LMM_Merged_P.pak");
        fremd.ManagedBy.Should().Be("lmm");
    }

    [Fact]
    public void Deinstallieren_eines_fremden_Paks_wird_verweigert_und_die_Datei_bleibt()
    {
        var pfad = Path.Combine(_mods, "zzz_LMM_Merged_P.pak");
        File.WriteAllText(pfad, "pak");
        var mod = _sut.ListInstalled().Single();

        ((Action)(() => _sut.Uninstall(mod))).Should()
            .Throw<InvalidOperationException>().WithMessage("*lmm*");

        File.Exists(pfad).Should().BeTrue("genau das ist gestern passiert");
    }

    [Fact]
    public void Umschalten_eines_fremden_Paks_wird_verweigert()
    {
        var pfad = Path.Combine(_mods, "zzz_LMM_Merged_P.pak");
        File.WriteAllText(pfad, "pak");
        var mod = _sut.ListInstalled().Single();

        ((Action)(() => _sut.SetEnabled(mod, false))).Should()
            .Throw<InvalidOperationException>().WithMessage("*lmm*");

        File.Exists(pfad).Should().BeTrue();
        File.Exists(pfad + ".disabled").Should().BeFalse();
    }

    /// <summary>Gegenprobe: eine eigene Mod muss sich weiter deinstallieren
    /// lassen. Eine Sperre, die den Normalfall mitnimmt, wäre schlimmer als
    /// das Problem.</summary>
    [Fact]
    public void Eine_eigene_Mod_laesst_sich_weiter_deinstallieren()
    {
        var pfad = Path.Combine(_mods, "MeineMod_P.pak");
        File.WriteAllText(pfad, "pak");
        var mod = _sut.ListInstalled().Single();
        mod.Source.Should().Be(PakModSource.Manual);

        _sut.Uninstall(mod);

        File.Exists(pfad).Should().BeFalse();
    }
}
