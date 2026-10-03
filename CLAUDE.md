# KroModIx.Plugin.Icarus

## Grundlagen

- **Was:** Icarus-Mod-Manager als Plugin für KroModIx. Zielspiel:
  Icarus (Steam App-ID 1149460, RocketWerkz).
- **Stack:** .NET 10, `KroModIx.Plugin.Contracts` als PackageReference.
- **Repo:** `github.com/Kroste/KroModIx.Plugin.Icarus`.
- **Deploy-Ziel:** `~/.config/KroModIx/plugins/kroste.icarus/` bzw.
  `%APPDATA%\KroModIx\plugins\kroste.icarus\`.

## Aktueller Stand

**v1.26.0 — GitHub-Releases aus dem Host, und die feste UE4SS-URL ist weg.**
`Ue4ssBootstrapper` nutzt `IHostServices.GitHub` (Contracts v1.33.0);
`minHostVersion` steht auf **1.33.0**.

- **Die fest hinterlegte Ausweich-URL ist weg.** Sie zeigte auf `v3.0.1` und
  wäre mit jeder neuen UE4SS-Ausgabe weiter veraltet. Wer beim GitHub-Limit
  landete, bekam stillschweigend eine alte Fassung, ohne es zu erfahren. Der
  Umleitungs-Pfad des Baukastens liefert den aktuellen Tag; der Dateiname
  trägt ihn (`UE4SS_v3.0.1.zip` zu `v3.0.1`), also lässt sich die URL daraus
  bilden — kein API-Aufruf, kein Limit, nicht veraltend.
- **Ohne auffindbare Ausgabe wird gemeldet statt geraten.** Vorher lud der
  Bootstrap die feste URL und tat, als wäre alles in Ordnung.
- **Ein Ausbruchsversuch im Loader-Archiv bricht ab.** Bis v1.25.0 wurde der
  abgelehnte Eintrag nur protokolliert und trotzdem Erfolg gemeldet. Bei
  einem Release eines bekannten Projekts ist so ein Eintrag ein
  Alarmzeichen, kein Randfall.
- **Der Bootstrap war ungetestet**, weil es keine `IHostServices`-Attrappe
  gab. Seit Host v1.33.0 liegt `FakeHostServices` im TestKit. Jetzt acht
  Tests: welches der vier UE4SS-Archive genommen wird (nicht `zDEV-*`, nicht
  die Beigaben), welche URL entsteht, der Raten-Sperren-Zweig, und dass
  `UE4SS-settings.ini` und `Mods/mods.txt` beim Update erhalten bleiben,
  bei der Erstinstallation aber mitkommen.
- **Die handgeschriebenen Attrappen sind weg.** `FakeHostServices.cs` im
  Testprojekt enthielt `FakeUnrealPakService` und `FakeArchiveService`; beide
  liegen jetzt im Paket `KroModIx.Plugin.TestKit` aus demselben Host-Tag —
  samt dem Ausbruch-Schutz, der dort **nicht** nachgebaut ist, sondern
  dieselbe Funktion `ArchivePathSafety` aus den Contracts aufruft. 96 Tests.

---


**v1.25.0 — drei Baukästen in den Host gewandert:** Archiv-Behandlung,
Unreal-Pak-Leser/-Schreiber und die Proton-DLL-Umleitung liegen jetzt im
Host und kommen über `IHostServices.Archives`, `.UnrealPaks` und
`.WinePrefix` (Contracts v1.30.0). `minHostVersion` steht auf **1.30.0**.

- **`Services/Pak/` ist weg**, ebenso `Services/Ue4ss/ProtonDllOverride.cs`.
- **`IcarusArchive` ist jetzt eine Instanz** (nicht mehr statisch) und hält
  die beiden Host-Dienste. Darin bleibt nur Icarus-Wissen: die Namen der
  zwei Ordner (`Icarus Mod Manager`, `UE4SS Mods`) und die Zuordnung
  Eintrag → Mod-Art. `DetectKind` entscheidet weiter die Reihenfolge (Pak
  vor Archiv), ruft dafür aber `IUnrealPakService.IsPakFile` und
  `IArchiveService.DetectKind`.
- **Die Icarus-Konstanten liegen im Compiler**: `IcarusContentMountPoint`
  und `IcarusDataTablePrefix` stehen in `ExmodzCompiler` — der Host-Baukasten
  nimmt den Mount-Point vom Aufrufer, damit er für Satisfactory genauso
  taugt.
- **Kein SharpCompress mehr im Plugin.** `CopyLocalLockFileAssemblies` bleibt
  trotzdem an, damit eine künftige Abhängigkeit ohne weiteren Eingriff im
  Bundle landet.
- **Die Plugin-Tests nutzen Attrappen** (`FakeHostServices.cs`:
  `FakeUnrealPakService`, `FakeArchiveService`). Das ist nicht nur eine
  technische Grenze — das Testprojekt referenziert nur die Contracts —,
  sondern die richtige: das Plugin verantwortet, **welche** Tabelle gepatcht
  und **wohin** ein Eintrag sortiert wird. Ob der Container danach
  byte-korrekt auf der Platte liegt, prüft der Host
  (`HostUnrealPakServiceTests`, `RealUnrealPakTests`). Die Zip-Slip- und
  Pak-Format-Tests sind mit dem Code dorthin gewandert; 89 Tests bleiben
  hier, die Host-Suite stieg auf 258.

**Gegenprobe nach der Migration** (03.10.2026): dieselbe Messung wie vor der
Umstellung, über die neue API gefahren — 299 von 299 Einträgen der echten
`data.pak` gelesen, derselbe Zusammenbau von OreDepot ergibt dieselben 10
Pfade mit 2 byte-identischen Assets und 8 inhaltsgleichen Tabellen gegenüber
dem Ergebnis der Referenz-Implementierung. Die Umstellung ist
verhaltensgleich.

---

**v1.24.0 — Datentabellen-Mods (.EXMODZ):** damit sind alle vier
Icarus-Mod-Arten abgedeckt (PAK, Workshop, UE4SS-Lua, Datentabellen).

- **`Services/Pak/`** — Unreal-Pak-Leser und -Schreiber, portiert aus
  **go-unrealpak** (MIT, Donovan C. Young). Leser: gespeichert + Zlib,
  dreiteiliger Index, SHA1-Kette durchgesetzt. Schreiber: Pak v11,
  unkomprimiert, reproduzierbare Ausgabe.
  → **in v1.25.0 in den Host gewandert** (`IHostServices.UnrealPaks`); der
  Code liegt jetzt unter `KroModIx/Services/Pak/` im Host-Repo.
- **`Services/Exmodz/`** — Manifest- und Archiv-Parser, Zeilen-Upsert auf die
  Datentabellen, Merge, Ablage mit Staleness-Pruefung, Dienst-Fassade.
  Portiert aus **lmms `internal/source/icarus`** (MIT, derselbe Autor).
- **`PakModSource.Exmodz`** als vierte Quelle in derselben Liste.
- **114 Tests**, davon zwei, die sich ohne Icarus-Installation
  ueberspringen (`RealIcarusPakTests`) statt den CI-Lauf rot zu faerben.

**Was gemessen ist, nicht angenommen** (03.10.2026, Spielwoche 252):

1. Die echte `Content/Data/data.pak`: **299 von 299** Eintraegen
   rekonstruiert, 42.274.800 Byte entpackt, 52 ms. Groesste Tabelle
   `Items/D_ItemsStatic.json` mit 7.420.669 Byte.
2. Ein Pak der **unabhaengigen** Referenz-Implementierung (lmm): 10 von 10
   Eintraegen lesbar; ihr Mount-Point `../../../Icarus/Content/` und das
   `data/`-Praefix fuer Tabellen deckt sich mit den uebernommenen Konstanten.
3. Eigener Zusammenbau von OreDepot gegen lmms Ergebnis aus derselben Mod:
   gleiche 10 Pfade, 2 Assets byte-identisch, 8 Tabellen inhaltsgleich nach
   `JsonNode.DeepEquals`.
4. JSON-Escaping: mit der Voreinstellung von `System.Text.Json` wuchs
   `D_Traits/D_Itemable.json` von 1.863.773 auf 2.103.396 Byte (+12,9 %).
   Mit `UnsafeRelaxedJsonEscaping` sind sechs der acht Tabellen
   groessengleich zur Referenz.

**Architektur-Entscheidungen, die der Code allein nicht hergibt:**

- **EIN gemeinsames Pak fuer alle .EXMODZ, nicht eins je Mod.** Ein
  Tabellen-Override ist immer die ganze Tabelle; getrennte Paks wuerden sich
  ganztabellig ueberschatten. Im gemeinsamen Pak komponieren sie auf
  Feldebene, und zwar kostenlos: die Bytes von Mod A wieder als Basis fuer
  Mod B zu nehmen IST der Merge-Algorithmus. Assets koennen so nicht
  komponieren — Pfad-Kollision ist Letzter-gewinnt plus Warnung.
- **Die .EXMODZ bleiben im Plugin-Datenordner und gehen NICHT ins Spiel.**
  Nur deshalb ist ein Neubau nach einem Spiel-Update moeglich — die Quelle
  ist noch da.
- **Staleness ueber den Pak-Index-Hash**, nicht ueber eine SHA256 der Datei:
  der Hash steht im Footer, wird beim Oeffnen ohnehin gelesen und geprueft,
  und aendert sich bei jeder Inhalts- oder Layout-Aenderung.
- **Ein fehlgeschlagener Bau entfernt das alte Pak.** Bliebe es liegen, liefe
  der User mit einem Stand aus einer frueheren Mod-Zusammenstellung weiter.
- **Nur eine .EXMODZ je Archiv wird aufgenommen.** Bei OreDepot liegen
  `OreDepot.EXMODZ` und `OreDepot_PTBR.EXMODZ` nebeneinander — dieselbe Mod,
  anderer Item-Name. Beide wuerden dieselben Zeilen doppelt setzen. Genommen
  wird der kuerzeste Dateiname.
- **Fremde Merged-Paks werden gemeldet, nicht angefasst.**
  `zzz_LMM_Merged_P.pak` kommt nach Alphabet hinter unserem und wuerde es bei
  gemeinsamen Tabellen ueberstimmen. Es ist nicht unsere Datei.

**Reihenfolge der Konstanten, die man nicht neu herleiten darf:** der
Mount-Point `../../../Icarus/Content/`, das `data/`-Praefix fuer Tabellen (und
NICHT fuer Assets), die `CurrentFile`-Abbildung `-` → `/`, und die Pak-Magic
im Footer **vor** Version und Offsets. Alle vier sind in der Vorlage gegen
echte, laufende Mod-Paks verifiziert und hier gegengeprueft (Beleg 2 und 3
oben).

**Die Reihenfolge der Typ-Erkennung** in `IcarusArchive.DetectKind` ist
ebenfalls load-bearing: erst die Unreal-Pak-Magic im Footer, dann die
Archiv-Signaturen am Dateianfang. Umgekehrt waere es angreifbar — ein PAK
beginnt mit den Daten seiner ersten Datei, die zufaellig mit `PK` anfangen
koennen.

---

**v1.23.0 — Mod-Archive + UE4SS-Lua-Mods:**

- **`Services/Archive/IcarusArchive`** — Typ-Erkennung ueber Magic-Bytes,
  Archiv-Inspektion, Einordnung in PAK / `.EXMODZ` / UE4SS-Lua,
  Zip-Slip-Schutz (lehnt auch Laufwerksbuchstaben ab — ohne das war das
  Verhalten plattformabhaengig).
- **`Services/Ue4ss/`** — `Ue4ssPaths` (Win64 + Mods-Ordner ueber
  `ModFolderDiscovery`, Loader-Erkennung, `UE4SS.log` als Lade-Beleg),
  `Ue4ssBootstrapper` (GitHub-Release + Ausweich-URL, Muster aus DSPs
  `BepInExBootstrapper`), `Ue4ssLuaModService` (Umschalten ueber
  `enabled.txt`, `mods.txt` wird nur nachgezogen wenn die Zeile schon da
  ist), `ProtonDllOverride`.
- **`PakInstallService.InstallAny`** ist der Weg fuer alle Aufrufer;
  `Install` bleibt fuer den reinen PAK-Fall. Rueckgabe ist
  `ModInstallResult`, weil ein Archiv mehrere Dinge auf einmal mitbringt.
  `ModInstallReporter` meldet Teil-Erfolge einheitlich.
- **`DownloadPakAsync` heisst `DownloadModFileAsync`** und haengt kein `.pak`
  mehr an.

**Drei stille Fehler, die dabei behoben wurden** — alle hatten kein Symptom
ausser Wirkungslosigkeit:

1. `DownloadPakAsync` hing jedem Download ein `.pak` an. Aus
   `OreDepot … yxOAgLyJG.zip` wurde `… .zip.pak`: der Name passte nicht mehr
   aufs Nexus-Muster (kein Enrichment, kein Cover, toter Details-Knopf), und
   der Installer kopierte das ZIP unveraendert in den Mods-Ordner, wo Icarus
   es nicht lesen kann.
2. `NexusFileNameParser` verlangte ein abschliessendes `.pak` und kannte das
   Dash-Format nicht. Ein echter Nexus-ZIP fiel durch, `TryExtractModId` gab
   `null`, der Enricher uebersprang die Row sauber.
3. Der `FileSystemWatcher` im Downloads-Tab hatte einen `"*.pak"`-Filter und
   blieb damit bei genau dem Normalfall still stehen.

**UE4SS-Entscheidungen:**

- **Die DLL-Umleitung geht in die `user.reg` des Praefix, nicht in die
  Steam-Startoptionen.** Die verbreitete Anleitung setzt
  `WINEDLLOVERRIDES="dwmapi=n,b"`. Das steht in Steams `localconfig.vdf`, die
  ein laufender Steam-Client beim Beenden aus dem Speicher zurueckschreibt.
- **Der UE4SS-Zustand wird bei jedem Refresh neu gelesen, nicht gemerkt.**
  Steams Dateipruefung raeumt die Loader-DLLs weg, ein neu angelegtes Praefix
  nimmt die Umleitung mit.
- **`UE4SS.log` ist der einzige Lade-Beleg.** Installierte Dateien beweisen
  nur, dass der Loader da ist.

**Fruehere Versionen (verdichtet):** v1.22 Auto-Discover der Mod-Ordner ueber
`ModFolderDiscovery` (Host v1.29). v1.21 Backup-Snapshot vor jedem Install.
v1.20 Review-Fixes. v1.19 Description-Parser und Rich-HTML ueber
`_host.Descriptions`. v1.18 Cover-Decode ueber `_host.Images`. v1.17
Steam-Workshop-Tab ueber `_host.Workshop`. v1.16 DE+EN. v1.15 Nexus wandert in
den Host. v0.2 Bug-Fix `~mods` → `mods`.

**Tabs (Order):** Installiert (0) · Nexus (10) · Workshop (15) · Downloads (20).

## Roadmap

- **Ladereihenfolge der Datentabellen-Mods aendern.** Der Store haelt sie
  schon als geordnete Liste, und bei einem Feld-Konflikt gewinnt die untere.
  Es fehlt nur die Bedienung (Hoch/Runter in der Row).
- **Update-Discovery fuer .EXMODZ.** `InstalledExmodz.NexusModId` wird beim
  Install aus dem Archiv-Dateinamen gefuellt, der Versions-Vergleich gegen
  Nexus laeuft aber noch nicht — `InstalledUpdatesChecker` kennt nur die
  PAK-Rows.
- Optional: NXM-Protokoll-Handler fuer Free-User (braucht Host-Support).

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
- **Host-API statt eigener Kopie** (ab v1.25.0): Archive, Unreal-Paks und
  Proton-Praefixe kommen aus `IHostServices`. Bei einem neuen Bedarf dieser
  Art zuerst pruefen, ob der Host ihn schon hat — und wenn nicht, ob er
  dorthin gehoert (Kernprinzip 4). Die Entscheidung bei diesen drei fiel
  nachgemessen — und die erste Messung war falsch, weil sie vom Namen des
  Helfers statt vom Verbraucher her suchte. Richtig: **neun** Plugins oeffnen
  Archive, **sechs** trugen eine eigene Kopie des Ausbruch-Schutzes, und bei
  **vier** davon hielt diese Kopie nicht — sie prueft nur auf `..`. Der
  Ausbruch ist am Cyberpunk-Installer nachgewiesen (Datei ausserhalb des
  InstallDir, Install meldete Erfolg).
- **Lizenzlage der Referenz-Implementierungen**: `lmm`
  (DonovanMods/linux-mod-manager) und `go-unrealpak` sind MIT, also
  portierbar mit Attribution. **IcarusStarlink hat keine LICENSE-Datei** —
  als Verhaltensreferenz lesbar, nicht als Quelle zum Portieren. Jimk72s
  Original-Mod-Manager liegt nur als Binaer-Zip im Repo.
- **Testprojekt**: `KroModIx.Plugin.Icarus.Tests` (xunit.v3 + FluentAssertions,
  VSTest-Pfad wie in den anderen KroModIx-Plugins). `dotnet test` im
  `dotnet10`-Distrobox-Container. Der Pak-Leser/-Schreiber ist `internal`;
  das Testprojekt kommt per `InternalsVisibleTo` aus der csproj dran.
  `RealIcarusPakTests` misst gegen eine echte Installation und ueberspringt
  sich, wo keine liegt — Pfad per `ICARUS_INSTALL_DIR` ueberschreibbar.
- **Deutsche Anfuehrungszeichen in C#-Zeichenketten**: `„…"` mit geradem
  Schlusszeichen beendet die Zeichenkette und bricht den Build mit einer
  Kaskade aus CS1026/CS1002/CS1010, die nach einem Syntaxfehler an ganz
  anderer Stelle aussieht. In dieser Runde zweimal passiert. Immer `„…“`
  schreiben — in Kommentaren ist das gerade Zeichen harmlos, in Literalen
  nicht.
