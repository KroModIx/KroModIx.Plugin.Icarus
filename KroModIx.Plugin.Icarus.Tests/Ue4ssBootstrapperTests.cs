using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using KroModIx.Plugin.Contracts;
using KroModIx.Plugin.Icarus.Services.Ue4ss;
using KroModIx.Plugin.TestKit;
using Xunit;

namespace KroModIx.Plugin.Icarus.Tests;

/// <summary>Der UE4SS-Bootstrap war bis v1.26.0 <b>ungetestet</b> — und trug
/// eine fest hinterlegte Ausweich-URL auf <c>v3.0.1</c>, die mit jeder neuen
/// UE4SS-Ausgabe weiter veraltet wäre.
///
/// <para>Geprüft wird, <b>welches</b> der vier Archive eines UE4SS-Releases
/// genommen wird und <b>welche URL</b> dabei entsteht. Der Download läuft
/// über eine Attrappe, die immer dasselbe ZIP ausliefert und die Adresse
/// mitschreibt.</para></summary>
public sealed class Ue4ssBootstrapperTests : IDisposable
{
    private const string Repo = "UE4SS-RE/RE-UE4SS";

    private readonly string _tmp;
    private readonly string _win64;
    private readonly Ue4ssPaths _paths;
    private readonly FakeGitHubService _gh = new();
    private readonly FakeArchiveService _archives = new();
    private readonly AuslieferHandler _handler;
    private readonly Ue4ssBootstrapper _sut;

    public Ue4ssBootstrapperTests()
    {
        _tmp = Directory.CreateTempSubdirectory("kromodix-icarus-ue4ss").FullName;
        var installDir = Path.Combine(_tmp, "Icarus");
        _win64 = Path.Combine(installDir, "Icarus", "Binaries", "Win64");
        Directory.CreateDirectory(_win64);

        var game = new DetectedGame(
            Target: new GameTarget("icarus", "Icarus", 1149460,
                Array.Empty<string>(), Platforms.Both),
            InstallDir: installDir,
            UserDataDir: null,
            ProtonPrefix: null,
            Runtime: RuntimeKind.Proton,
            Source: GameSource.Steam);
        _paths = new Ue4ssPaths(game);

        _handler = new AuslieferHandler(BaueZip("dwmapi.dll", "UE4SS.dll",
            "UE4SS-settings.ini", "Mods/mods.txt"));
        _sut = new Ue4ssBootstrapper(new HttpClient(_handler), _gh, _archives);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* Aufräumen darf scheitern */ }
    }

    private static byte[] BaueZip(params string[] pfade)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var pfad in pfade)
            {
                using var s = zip.CreateEntry(pfad).Open();
                s.Write([1, 2, 3]);
            }
        return ms.ToArray();
    }

    /// <summary>Ein UE4SS-Release liefert vier ZIPs. Gebraucht wird das
    /// Laufzeit-Archiv — <c>zDEV-*</c> ist der Entwickler-Bau mit 24 MB
    /// Symbolen, <c>zCustomGameConfigs</c> und <c>zMapGenBP</c> sind
    /// Beigaben, die Icarus nicht braucht.</summary>
    [Fact]
    public async Task Nimmt_das_Laufzeit_Archiv_und_nicht_den_Entwickler_Bau()
    {
        _gh.AddRelease(Repo, "v3.0.1", "zDEV-UE4SS_v3.0.1.zip",
            "zCustomGameConfigs_v3.0.1.zip", "UE4SS_v3.0.1.zip", "zMapGenBP_v3.0.1.zip");

        var r = await _sut.InstallAsync(_paths, ct: TestContext.Current.CancellationToken);

        r.Ok.Should().BeTrue(r.Message);
        r.Version.Should().Be("v3.0.1");
        _handler.Angefragt.Should().ContainSingle().Which.Should().Be(
            $"https://github.com/{Repo}/releases/download/v3.0.1/UE4SS_v3.0.1.zip");
        File.Exists(Path.Combine(_win64, "dwmapi.dll")).Should().BeTrue();
        File.Exists(Path.Combine(_win64, "UE4SS.dll")).Should().BeTrue();
    }

    /// <summary>Der eigentliche Grund für die Migration. Greift die
    /// Raten-Sperre, kennt der Baukasten nur den Tag — der Dateiname trägt
    /// ihn, also lässt sich die URL daraus bilden. Vorher kam hier eine fest
    /// hinterlegte <c>v3.0.1</c>, die mit jeder neuen Ausgabe weiter
    /// veraltet wäre.</summary>
    [Fact]
    public async Task Bei_Raten_Sperre_kommt_der_aktuelle_Tag()
    {
        _gh.AddRelease(Repo, "v3.1.0", "UE4SS_v3.1.0.zip");
        _gh.RateLimited = true;

        var r = await _sut.InstallAsync(_paths, ct: TestContext.Current.CancellationToken);

        r.Ok.Should().BeTrue(r.Message);
        r.Version.Should().Be("v3.1.0");
        _handler.Angefragt.Should().ContainSingle().Which.Should().Be(
            $"https://github.com/{Repo}/releases/download/v3.1.0/UE4SS_v3.1.0.zip");
        _handler.Angefragt[0].Should().NotContain("v3.0.1",
            "die fest hinterlegte Fassung ist mit v1.26.0 entfallen");
    }

    [Fact]
    public async Task Ohne_Ausgabe_wird_gemeldet_statt_geraten()
    {
        var r = await _sut.InstallAsync(_paths, ct: TestContext.Current.CancellationToken);

        r.Ok.Should().BeFalse();
        r.Message.Should().Contain("Kein UE4SS-Release gefunden");
        _handler.Angefragt.Should().BeEmpty();
        Directory.GetFiles(_win64).Should().BeEmpty();
    }

    /// <summary>Ein Loader-Update darf die Einstellungen des Nutzers nicht
    /// zurücksetzen: <c>UE4SS-settings.ini</c> und <c>Mods/mods.txt</c>
    /// bleiben, wenn sie schon da sind. Alles andere wird
    /// überschrieben — das ist der Update-Weg.</summary>
    [Fact]
    public async Task Nutzer_Dateien_bleiben_beim_Update_unangetastet()
    {
        _gh.AddRelease(Repo, "v3.0.1", "UE4SS_v3.0.1.zip");
        Directory.CreateDirectory(Path.Combine(_win64, "Mods"));
        File.WriteAllText(Path.Combine(_win64, "UE4SS-settings.ini"), "meine Einstellung");
        File.WriteAllText(Path.Combine(_win64, "Mods", "mods.txt"), "MeinMod : 1");
        File.WriteAllText(Path.Combine(_win64, "UE4SS.dll"), "alte Fassung");

        var r = await _sut.InstallAsync(_paths, ct: TestContext.Current.CancellationToken);

        r.Ok.Should().BeTrue(r.Message);
        File.ReadAllText(Path.Combine(_win64, "UE4SS-settings.ini"))
            .Should().Be("meine Einstellung");
        File.ReadAllText(Path.Combine(_win64, "Mods", "mods.txt")).Should().Be("MeinMod : 1");
        File.ReadAllText(Path.Combine(_win64, "UE4SS.dll"))
            .Should().NotBe("alte Fassung", "der Loader selbst wird ersetzt");
    }

    /// <summary>Fehlen die Nutzer-Dateien, kommen sie aus dem Archiv — sonst
    /// hätte eine Erstinstallation keine Voreinstellungen.</summary>
    [Fact]
    public async Task Bei_der_Erstinstallation_kommen_die_Vorlagen_mit()
    {
        _gh.AddRelease(Repo, "v3.0.1", "UE4SS_v3.0.1.zip");

        var r = await _sut.InstallAsync(_paths, ct: TestContext.Current.CancellationToken);

        r.Ok.Should().BeTrue(r.Message);
        File.Exists(Path.Combine(_win64, "UE4SS-settings.ini")).Should().BeTrue();
        File.Exists(Path.Combine(_win64, "Mods", "mods.txt")).Should().BeTrue();
    }

    /// <summary>Bis v1.25.0 wurde ein abgelehnter Eintrag hier nur
    /// protokolliert und trotzdem Erfolg gemeldet. Bei einem offiziellen
    /// Release ist so ein Eintrag ein Alarmzeichen — jetzt bricht es ab.</summary>
    [Fact]
    public async Task Ausbruchsversuch_im_Release_bricht_ab()
    {
        _gh.AddRelease(Repo, "v3.0.1", "UE4SS_v3.0.1.zip");
        var opfer = Path.Combine(_tmp, "ausserhalb.dll");
        _handler.Inhalt = BaueZip("dwmapi.dll", opfer);

        var r = await _sut.InstallAsync(_paths, ct: TestContext.Current.CancellationToken);

        File.Exists(opfer).Should().BeFalse();
        r.Ok.Should().BeFalse("vorher meldete derselbe Fall Erfolg");
        r.Message.Should().Contain("sollte bei einem offiziellen Release nicht vorkommen");
    }

    [Fact]
    public async Task Ohne_Win64_Verzeichnis_wird_gar_nicht_gefragt()
    {
        var leer = new DetectedGame(
            Target: new GameTarget("icarus", "Icarus", 1149460,
                Array.Empty<string>(), Platforms.Both),
            InstallDir: Path.Combine(_tmp, "kein-icarus"),
            UserDataDir: null, ProtonPrefix: null,
            Runtime: RuntimeKind.Proton, Source: GameSource.Steam);

        var r = await _sut.InstallAsync(new Ue4ssPaths(leer),
            ct: TestContext.Current.CancellationToken);

        r.Ok.Should().BeFalse();
        r.Message.Should().Contain("nicht gefunden");
        _gh.Queries.Should().BeEmpty("ohne Ziel wird GitHub nicht belästigt");
    }

    private sealed class AuslieferHandler(byte[] inhalt) : HttpMessageHandler
    {
        public List<string> Angefragt { get; } = [];
        public byte[] Inhalt { get; set; } = inhalt;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Angefragt.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Inhalt),
            });
        }
    }
}
