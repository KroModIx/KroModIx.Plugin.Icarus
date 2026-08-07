# ModManager.Plugins.Icarus

## Grundlagen

- **Was:** Icarus-Mod-Manager als Plugin für Kroste ModManager. Zielspiel:
  Icarus (Steam App-ID 1149460, RocketWerkz).
- **Stack:** .NET 10, `Kroste.ModManager.PluginContracts` als PackageReference.
- **Repo:** `github.com/Kroste/ModManager.Plugins.Icarus`.
- **Deploy-Ziel:** `~/.config/ModManager/plugins/icarus/` bzw.
  `%APPDATA%\ModManager\plugins\icarus\`.

## Aktueller Stand

**v0.1.0 (M5.1 — Skelett + Installiert-Tab):**
- `IcarusPlugin` mit einem Target (Icarus/1149460).
- `IcarusPathResolver`: baut `<InstallDir>/Icarus/Content/Paks/~mods/`
  (Unreal-Standard). Nutzt die vom Host aufgelöste `DetectedGame.InstallDir`.
- `PakInstallService`: List (.pak + .pak.disabled), Install (nur .pak), Uninstall,
  SetEnabled via .pak.disabled-Rename.
- `InstalledPaksView` + VM (code-only Avalonia): Toolbar (Install-PAK, Refresh,
  Ordner öffnen, Toggle, Uninstall mit Confirm). Zeigt FileName, Größe, State.

## Roadmap

- **v0.2** — GitHubReleaseCatalog: Katalog-Tab mit unseren Kroste-hostete
  Icarus-Mods (Release-Assets pro Mod-Repo, `mod.json`-Metadaten).
- **v0.3** — Optional: Mod-Metadaten-Datei (Autor/Version/Beschreibung/Bild)
  als Sidecar neben der PAK.

## Referenz

- **Kein XAML** — code-only Views (siehe LS25-Plugin-Rationale).
- **Icarus-Mods** sind Unreal-Engine-PAK-Pakete. Enable/Disable macht der
  Manager per Datei-Rename (~mods/-Ordner mit `.pak` vs `.pak.disabled`) —
  keine game-side Konfiguration nötig.
- **Warum überhaupt Icarus?** Es gibt bisher keinen etablierten Icarus-Mod-
  Manager unter Linux. Wir definieren die Kroste-Konvention selbst.
