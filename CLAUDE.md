# ModManager.Plugins.Icarus

## Grundlagen

- **Was:** Icarus-Mod-Manager als Plugin für Kroste ModManager. Zielspiel:
  Icarus (Steam App-ID 1149460, RocketWerkz).
- **Stack:** .NET 10, `Kroste.ModManager.PluginContracts` als PackageReference.
- **Repo:** `github.com/Kroste/ModManager.Plugins.Icarus`.
- **Deploy-Ziel:** `~/.config/ModManager/plugins/kroste.icarus/` bzw.
  `%APPDATA%\ModManager\plugins\kroste.icarus\`.

## Aktueller Stand

**v0.2.0 (M5.2 — Workshop-Support + Nexus-Katalog + Backup + Kroste-Look):**
- **Bug-Fix `~mods` → `mods`**: v0.1.0 zeigte auf `Content/Paks/~mods/`
  (UE4-Konvention), Icarus nutzt aber `Content/Paks/mods/` (ohne Tilde) —
  User-Real-Test bestätigt, v0.1.0 hätte immer „keine Mods" gezeigt.
- **Steam-Workshop-Erkennung**: `IcarusPathResolver.GetWorkshopContentDir`
  leitet aus `<Library>/steamapps/workshop/content/1149460/` alle abonnierten
  Mods ab. Workshop-Rows sind read-only (Toggle/Uninstall wirft — Steam
  verwaltet), visuell mit `⚙ WORKSHOP`-Badge markiert.
- **PakInstallService** erweitert: `ListInstalled()` liefert Manual + Workshop
  mit `PakModSource`-Kennzeichnung. Downloads-Ordner kommt vom `IcarusPaths`.
- **Nexus-Mods-Katalog** (`Services/Nexus/`): `NexusApiClient` mit `apikey`-
  Header, aggregiert die drei „latest"-Endpunkte (latest_added/latest_updated/
  trending) — bis zu 60 unique Mods pro Load. `NexusCatalogService` mit
  Snapshot-Cache im `PluginCacheDir` (Alter über `NexusSettings.CatalogRefreshHours`,
  default 24 h, Stale-Fallback bei Netzfehler).
- **API-Key-Storage**: `NexusSettings.ApiKeyProtected` als `v1:<base64>`-Blob
  via `IHostServices.Secrets.Protect/Unprotect` (DPAPI/AES) — nie im Klartext.
- **Nexus-Settings-Tab**: Key eintragen (PasswordChar) + Verify gegen
  `/users/validate.json` + Game-Slug + Cache-Refresh-Intervall.
- **Nexus-Tab**: Katalog-Karten mit Cover (aus API-`picture_url` gecached
  in `PluginCacheDir/covers/`), Autor, Version, Endorsements, Summary.
  Kein In-App-Download — Klick auf „Nexus öffnen" führt zum Browser.
- **Downloads-Tab**: watched `PluginDataDir/downloads/` mit
  `FileSystemWatcher` (500 ms debounced), listet PAKs, bietet Install +
  Delete pro Row. Auto-Refresh via `DownloadEventBus.DownloadsChanged`.
- **Backup/Restore** (`PakBackupService`): ZIP mit allen Manual-PAKs +
  `manifest.json` (Enabled-State). Workshop-Mods werden nicht gebackupt
  (Steam macht das). Analog LS25.
- **DownloadEventBus** mit zwei Events (`DownloadsChanged`, `ModInstalled`) —
  Konsistenz-Regel aus LS25-v0.11.1: JEDER Mutation-Weg feuert das passende
  Event, damit „User muss immer aktualisieren drücken" nicht passiert.
- **FileSystemWatcher** auf Manual-Ordner + Workshop-Ordner + Downloads-
  Ordner → Auto-Refresh bei Steam-Workshop-Updates ohne Plugin-Restart.
- **Kroste-Card-Look** (Skill Kernprinzip 2): alle Views nutzen `DynamicResource`
  (KrosteCardBrush, KrosteSurfaceBrush, KrosteGoldBrush, KrosteSuccessBrush).
  Keine hardcoded `Color.FromRgb`.

**Tabs (Order):** Installiert (0) · Nexus (10) · Downloads (20) · Nexus-Einstellungen (30).

**v0.1.0 (M5.1 — obsolet, siehe Bug-Fix oben):** Skelett + Installiert-Tab
mit falschem `~mods`-Path. Ersetzt durch v0.2.0.

## Roadmap

- **v0.3** — Optional: KI-Zusammenfassung im Nexus-Detail via `_host.Ai`
  (analog LS25-ModDetail).
- **v0.4** — Optional: GitHubReleaseCatalog als zweite Katalog-Quelle, sobald
  wir eigene Kroste-Icarus-Mods bauen. Contract kommt vermutlich als
  `PluginContracts.GitHub`-Helper aus dem Host-Repo.
- **v0.5** — Optional: Nexus-Premium-Download via API (spart Slow-Wall) —
  nur wenn ein Premium-User uns danach fragt.

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
