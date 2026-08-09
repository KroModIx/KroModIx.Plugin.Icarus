# KroModIx.Plugin.Icarus

[![CI](https://github.com/Kroste/KroModIx.Plugin.Icarus/actions/workflows/ci.yml/badge.svg)](https://github.com/Kroste/KroModIx.Plugin.Icarus/actions/workflows/ci.yml)

Icarus (RocketWerkz) Mod-Manager als Plugin für den [KroModIx](https://github.com/KroModIx/KroModIx).

## Ziel-Spiel

- **Icarus** (Steam App-ID 1149460)
  - Manuelle PAK-Mods: `<Icarus-Install>/Icarus/Content/Paks/mods/`
  - Steam-Workshop-Abos: `<Library>/steamapps/workshop/content/1149460/`

## Features (v0.4.0)

- **Tab „Installiert"** — Manuelle Mods + Steam-Workshop-Abos gemeinsam,
  Workshop-Rows sind read-only (Steam verwaltet), farbliches Badge zur
  Unterscheidung. Filter (Suche + Manual/Workshop-Toggles), Multi-Select
  mit Bulk-Aktivieren/Deaktivieren/Deinstallieren, Kontextmenü, F5/Ctrl+F/Del-
  Shortcuts, Drag&Drop von .pak-Files.
- **Tab „Nexus"** — Katalog der drei Nexus-Listen (latest_added,
  latest_updated, trending) als Kroste-Cards mit Cover, Autor, Version,
  Update-Datum, Endorsements, Summary. **Doppelklick oder 🔍-Button öffnet
  Detail-Dialog** mit vollständiger Beschreibung, Kategorie und KI-
  Zusammenfassung (via Host-KI-Provider). **⬇ Download-Button für Nexus-
  Premium-User**: PAK landet direkt im Downloads-Ordner mit Progress in
  der Statusbar. Free-User: „Nexus öffnen" führt zum Browser (Slow-Wall
  klicken, Datei landet im Downloads-Ordner).
- **Tab „Downloads"** — Watch-Ordner für Browser-Downloads. Auto-Refresh
  via FileSystemWatcher, Install- und Delete-Button pro Datei.
- **Tab „Nexus-Einstellungen"** — Personal-API-Key eintragen (verschlüsselt
  via DPAPI/AES gespeichert), Verify-Button, Game-Slug, Cache-Refresh-Intervall.
- **Backup/Restore** manueller Mods als ZIP mit Enabled-State-Manifest.
- **Auto-Refresh**: FileSystemWatcher auf Manual/Workshop/Downloads +
  Plugin-interner Event-Bus — kein „Aktualisieren"-Klick nötig.

## Nexus-API-Key beschaffen

1. Bei nexusmods.com anmelden (kostenlos)
2. https://www.nexusmods.com/users/myaccount?tab=api%20access öffnen
3. „Personal API Key" generieren, kopieren
4. Im Plugin-Tab „Nexus-Einstellungen" einfügen, „Speichern", „Verify" klicken

Rate-Limit: 2500 Requests pro Stunde mit Personal-Key (Nexus-Standard).
Der Key wird lokal mit DPAPI (Windows) bzw. AES + Machine-Bindung (Linux)
verschlüsselt gespeichert und niemals im Log ausgegeben.

## Installation

Aus [Release](https://github.com/Kroste/KroModIx.Plugin.Icarus/releases)
das ZIP entpacken nach:

- **Linux:** `~/.config/KroModIx/plugins/kroste.icarus/`
- **Windows:** `%APPDATA%\KroModIx\plugins\kroste.icarus\`

Ab Host v0.3 alternativ: 1-Klick-Install über die Install-Karte in der Sidebar.

## Lizenz

MIT.
