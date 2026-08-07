# ModManager.Plugins.Icarus

[![CI](https://github.com/Kroste/ModManager.Plugins.Icarus/actions/workflows/ci.yml/badge.svg)](https://github.com/Kroste/ModManager.Plugins.Icarus/actions/workflows/ci.yml)

Icarus (RocketWerkz) Mod-Manager als Plugin für den [Kroste ModManager](https://github.com/Kroste/Mod-Manager).

## Ziel-Spiel

- **Icarus** (Steam App-ID 1149460)
  - PAK-Mods gehen nach `<Icarus-Install>/Icarus/Content/Paks/~mods/`

## Aktueller Umfang (v0.1.0)

- Tab **Installiert**: PAK-Mods listen, Aktiv/Inaktiv toggeln (`.pak.disabled`-
  Rename), lokale PAK installieren, deinstallieren, Mods-Ordner öffnen.

## Roadmap

- v0.2 — Katalog über `GitHubReleaseCatalog` (Kroste-hostete Icarus-Mods)
- v0.3 — modDesc-Alternativen: Manifest-Datei neben der PAK (Autor/Version/Beschreibung)

## Installation

Aus [Release](https://github.com/Kroste/ModManager.Plugins.Icarus/releases)
das ZIP entpacken nach:

- **Linux:** `~/.config/ModManager/plugins/icarus/`
- **Windows:** `%APPDATA%\ModManager\plugins\icarus\`

Ab Host v0.3 alternativ: 1-Klick-Install über die Install-Karte in der Sidebar.

## Lizenz

MIT.
