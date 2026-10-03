# KroModIx.Plugin.Icarus

## Grundlagen

- **Was:** Icarus-Mod-Manager als Plugin für KroModIx. Zielspiel:
  Icarus (Steam App-ID 1149460, RocketWerkz).
- **Stack:** .NET 10, `KroModIx.Plugin.Contracts` als PackageReference.
- **Repo:** `github.com/Kroste/KroModIx.Plugin.Icarus`.
- **Deploy-Ziel:** `~/.config/KroModIx/plugins/kroste.icarus/` bzw.
  `%APPDATA%\KroModIx\plugins\kroste.icarus\`.

## Aktueller Stand

**v1.23.0 — Mod-Archive + UE4SS-Lua-Mods:**

- **`Services/Archive/IcarusArchive`** — Typ-Erkennung, Archiv-Inspektion,
  Einordnung in PAK / `.EXMODZ` / UE4SS-Lua, Zip-Slip-Schutz. Die Erkennung
  laeuft ueber **Magic-Bytes**, und zwar in dieser Reihenfolge: erst die
  Unreal-Pak-Magic `0x5A6F12E1` im Footer (im letzten KiB gesucht, die
  genaue Position haengt an der Pak-Version), dann die drei
  Archiv-Signaturen am Dateianfang. Umgekehrt waere es angreifbar — ein PAK
  beginnt mit den Daten seiner ersten Datei, die zufaellig mit `PK` anfangen
  koennen.
- **`Services/Ue4ss/`** — vier Klassen: `Ue4ssPaths` (Win64 + Mods-Ordner
  ueber `ModFolderDiscovery`, Loader-Erkennung, `UE4SS.log` als
  Lade-Beleg), `Ue4ssBootstrapper` (GitHub-Release + Ausweich-URL, Muster
  aus DSPs `BepInExBootstrapper`), `Ue4ssLuaModService` (Liste, Install aus
  Archiv, Umschalten ueber `enabled.txt`, Deinstallieren),
  `ProtonDllOverride` (dwmapi-Umleitung in der `user.reg`).
- **`PakModSource.Ue4ssLua`** als dritte Quelle. Lua-Mods liegen in
  derselben Liste wie die PAKs, damit sie Suche, Filter, Mehrfachauswahl
  und Karten-Layout ohne eine zweite Liste bekommen. `InstalledPakMod`
  zeigt dort mit `FilePath` auf einen **Ordner**, nicht auf eine Datei.
- **`PakInstallService.InstallAny`** ist ab jetzt der Weg fuer alle
  Aufrufer; `Install` bleibt fuer den reinen PAK-Fall. Rueckgabe ist
  `ModInstallResult` (Listen statt eines Einzelwerts), weil ein Archiv
  mehrere Dinge auf einmal mitbringt. `ModInstallReporter` meldet das
  Ergebnis — eine Stelle fuer alle Tabs, damit ein Teil-Erfolg nicht je Tab
  anders beschrieben wird.
- **`DownloadPakAsync` heisst jetzt `DownloadModFileAsync`** und haengt kein
  `.pak` mehr an.
- **Testprojekt** `KroModIx.Plugin.Icarus.Tests` (66 Tests): Dateinamen-Parser
  mit echten Namen, Archiv-Einordnung am OreDepot-Layout, Typ-Erkennung,
  Zip-Slip, UE4SS-Pfade und -Dienst, DLL-Umleitung gegen einen Auszug aus
  einer echten `user.reg`.

**Drei stille Fehler, die dabei behoben wurden** — alle hatten kein Symptom
ausser Wirkungslosigkeit:

1. `DownloadPakAsync` hing jedem Download ein `.pak` an. Aus
   `OreDepot … yxOAgLyJG.zip` wurde `… .zip.pak`: der Dateiname passte nicht
   mehr aufs Nexus-Muster (kein Enrichment, kein Cover, toter
   Details-Knopf), und der Installer kopierte das ZIP unveraendert in den
   Mods-Ordner, wo Icarus es nicht lesen kann. Deshalb erkennt
   `IcarusArchive.DetectKind` den Typ am Inhalt — solche Altlasten liegen in
   bestehenden Downloads-Ordnern.
2. `NexusFileNameParser` verlangte ein abschliessendes `.pak` und kannte das
   Dash-Format nicht. Ein echter Nexus-ZIP fiel durch, `TryExtractModId` gab
   `null`, der Enricher uebersprang die Row sauber. Jetzt beide Formate und
   vier Endungen, jede optional mit angehaengtem `.pak` (Altlast).
3. Der `FileSystemWatcher` im Downloads-Tab hatte einen `"*.pak"`-Filter und
   blieb damit bei genau dem Normalfall still stehen.

**Architektur-Entscheidungen, die nicht offensichtlich sind:**

- **Die DLL-Umleitung geht in die `user.reg` des Praefix, nicht in die
  Steam-Startoptionen.** Die verbreitete Anleitung setzt
  `WINEDLLOVERRIDES="dwmapi=n,b"`. Das steht in Steams `localconfig.vdf`,
  die ein laufender Steam-Client beim Beenden aus dem Speicher
  zurueckschreibt — eine Aenderung von aussen waere verloren, solange Steam
  laeuft, und das tut es, wenn der User gerade moddet.
- **Der UE4SS-Zustand wird bei jedem Refresh neu gelesen, nicht gemerkt.**
  Steams Dateipruefung raeumt die Loader-DLLs weg, ein neu angelegtes
  Praefix nimmt die Umleitung mit. Ein gemerkter Zustand wuerde dann „alles
  in Ordnung" behaupten.
- **`UE4SS.log` ist der einzige Lade-Beleg.** Installierte Dateien beweisen
  nur, dass der Loader da ist.
- **Lua-Umschalten per Umbenennen, nicht Loeschen** (`enabled.txt` →
  `enabled.txt.disabled`) — manche Mods legen dort Inhalt ab. Eine
  **bestehende** Zeile in der `mods.txt` wird nachgezogen, weil UE4SS beide
  Quellen auswertet und ein Ausschalten sonst wirkungslos bliebe; neue
  Zeilen werden nicht angelegt.
- **`.EXMODZ` wird gezaehlt und gemeldet, nicht uebersprungen.** Sonst
  klickt der User „installieren", sieht Erfolg und wundert sich im Spiel.

**Fruehere Versionen (verdichtet):** v1.22 Auto-Discover der Mod-Ordner ueber
`ModFolderDiscovery` (Host v1.29). v1.21 Backup-Snapshot vor jedem Install.
v1.20 Review-Fixes (VM meldet sich vom `DownloadEventBus` ab, atomare Saves,
Versions-Vergleich aus den Contracts). v1.19 Description-Parser und
Rich-HTML-Rendering ueber `_host.Descriptions`. v1.18 Cover-Decode ueber
`_host.Images`. v1.17 Steam-Workshop-Tab ueber `_host.Workshop`. v1.16 DE+EN.
v1.15 Nexus wandert in den Host (`_host.Nexus`), plugin-eigener
Settings-Tab entfernt. v1.15.1 gruener Badge nur bei echten Mod-Updates.
v0.2 Bug-Fix `~mods` → `mods` (Icarus weicht von der UE4-Konvention ab).

**Tabs (Order):** Installiert (0) · Nexus (10) · Workshop (15) · Downloads (20).

## Roadmap

- **v1.24.0 — `.EXMODZ`-Unterstuetzung.** Der teure Teil, weil ein Pak
  **geschrieben** werden muss. Lesen koennte `CUE4Parse` (auf nuget.org),
  zum Schreiben gibt es dort nichts. Zwei Wege:
  - **go-unrealpak portieren** (MIT, Donovan C. Young): `pak.go` +
    `reader.go` + `writer.go` = 976 Zeilen Go ohne Tests.
    `System.IO.Compression.ZLibStream` deckt die Kompression ab; Icarus'
    `Content/Data/data.pak` ist Pak v11, 40 Tabellen unkomprimiert, 258 zlib.
  - **`UnrealPak.exe` aufrufen** — so macht es IcarusStarlink. Unter Linux
    hiesse das wine aus dem Plugin heraus. Nein.
  Dazu die Merge-Logik aus lmms `internal/source/icarus` portieren (MIT,
  ~1300 Zeilen ohne Tests). **Zwei Konstanten nicht neu herleiten, sondern
  uebernehmen:** den Mount-Point und die `CurrentFile`-Abbildung `-` → `/`.
  Beide sind dort byte-fuer-byte gegen zwei echte, laufende Mod-Paks
  verifiziert.
  **Ein Konzept fehlt dem Plugin ganz:** ein gemeinsames Merged-Pak fuer
  alle `.EXMODZ`, gebaut gegen die aktuelle `data.pak`. Nicht ein Pak je Mod
  — zwei Mods auf derselben Tabelle wuerden sich ganztabellig
  ueberschatten. Dazu eine Staleness-Pruefung (SHA256 der `data.pak` neben
  dem Merged-Pak), die den Neubau nach dem Woechentlich-Update ausloest.
- **Host-Kandidat: DLL-Umleitung nach `IHostServices`.** `ProtonDllOverride`
  liegt bewusst in einer eigenen, abhaengigkeitsfreien Klasse. Jedes Plugin
  fuer ein Unreal-Spiel mit UE4SS braucht genau diese Funktion (Satisfactory,
  Schedule I) — nach Kernprinzip 4 gehoert sie in den Host, als etwa
  `IHostServices.ProtonPrefix.SetDllOverride(...)`. Dann wird die Wanderung
  ein Verschieben und kein Neuschreiben.
- Optional: NXM-Protokoll-Handler fuer Free-User (braucht Host-Support, ein
  Plugin kann sich nicht selbst als URL-Handler registrieren).

## Referenz

- **Icarus-Mod-Ordner**: `<InstallDir>/Icarus/Content/Paks/mods/` (OHNE
  Tilde-Präfix — Icarus weicht von UE4-Konvention `~mods/` ab). Auf Linux
  liegt das oft auf einer Zusatzplatte, der Host-`DetectedGame.InstallDir`
  löst es korrekt auf via `libraryfolders.vdf`.
- **Steam-Workshop-Ordner**: `<LibraryRoot>/steamapps/workshop/content/1149460/`
  — jeder Workshop-Mod ist ein eigener Unterordner mit einer oder mehreren
  `.pak`-Dateien. Steam pflegt die Ordner selbst (Download + Update). Wir
  scannen read-only.
- **Kein XAML** — code-only Views (siehe LS25-Plugin und Skill Kernprinzip 2).
- **Kein Assembly-Reload** — nach Deploy die App komplett neu starten
  (Skill kroste-modmanager-plugin/pitfalls.md → „Assembly.LoadFrom lockt").
- **Nexus-API-Rate-Limits**: 250/h anonymous, 2500/h für Personal-Keys.
  Response-Header `X-RL-Hourly-Remaining` wird per Debug-Log ausgegeben.
- **UE4SS-Ordner**: `<InstallDir>/Icarus/Binaries/Win64/` — dort liegen
  `dwmapi.dll` und `UE4SS.dll`, die Lua-Mods in `Mods/<Name>/` (oder
  `ue4ss/Mods/<Name>/` bei den experimentellen Bauten; beide Schreibweisen
  werden gefunden).
- **Icarus laeuft auf Unreal Engine 4** — belegt an den Pfadangaben in
  `Icarus-Win64-Shipping.pdb`, die durchweg `Engine/Source/Runtime` der
  4er-Reihe nennen. Die stabile UE4SS-Reihe 3.0.x deckt das ab, es braucht
  keine spiel-spezifische Konfiguration (UE4SS sucht per Signatur).
- **`Content/Data/data.pak`** haelt die JSON-Datentabellen des Spiels (Pak
  v11, zlib, ~2,5 MB). Ein `_P.pak` in `Content/Paks/mods` mit Pfaden unter
  `Icarus/Content/data/` ueberschreibt sie — das ist der Weg, den ein
  `.EXMODZ`-Einbau gehen muss.
- **Lizenzlage der Referenz-Implementierungen**: `lmm`
  (DonovanMods/linux-mod-manager) und `go-unrealpak` sind MIT, also
  portierbar mit Attribution. **IcarusStarlink hat keine LICENSE-Datei** —
  als Verhaltensreferenz lesbar, nicht als Quelle zum Portieren. Jimk72s
  Original-Mod-Manager liegt nur als Binaer-Zip im Repo.
- **Testprojekt**: `KroModIx.Plugin.Icarus.Tests` (xunit.v3 + FluentAssertions,
  VSTest-Pfad wie in den anderen KroModIx-Plugins). `dotnet test` im
  `dotnet10`-Distrobox-Container.
