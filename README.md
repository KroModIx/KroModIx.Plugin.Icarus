# KroModIx.Plugin.Icarus

[![CI](https://github.com/KroModIx/KroModIx.Plugin.Icarus/actions/workflows/ci.yml/badge.svg)](https://github.com/KroModIx/KroModIx.Plugin.Icarus/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/KroModIx/KroModIx.Plugin.Icarus)](https://github.com/KroModIx/KroModIx.Plugin.Icarus/releases)

**Icarus** (RocketWerkz) Mod-Manager als Plugin für den
[KroModIx](https://github.com/KroModIx/KroModIx). Nexus-Mods-Katalog mit
Direct-Download (Premium) oder Browser-Weg (Free), Manual-PAKs und
Steam-Workshop-Abos gemeinsam gelistet, Update-Discovery für installierte
Mods, KI-Zusammenfassung im Detail-Dialog.

## Ziel-Spiel

**Icarus** — Steam AppId 1149460.

- Manuelle PAK-Mods: `<Icarus-Install>/Icarus/Content/Paks/mods/`
- Steam-Workshop-Abos: `<Library>/steamapps/workshop/content/1149460/`

## Features (v1.14.0)

### Installiert-Tab
- Manuelle PAKs + Steam-Workshop-Abos gemeinsam gelistet, Workshop-Rows
  farblich markiert (Steam verwaltet sie, wir nur lesen)
- Cover-Enrichment via Nexus für Manual-PAKs mit erkennbarer Nexus-Mod-Id
  im Filename
- **🔄 Updates prüfen** — vergleicht installierte Version (aus Filename)
  mit `nexus/mods/{id}.json.version`
- **⬆ Alle updaten** — Bulk-Update aller Manual-PAKs sequenziell
  (Nexus-Rate-Limit-Rücksicht)
- **🔍 Details** per Doppelklick oder Button
- **Multi-Select** mit Bulk-Aktivieren/Deaktivieren/Deinstallieren
- Filter (Suche + Manual/Workshop-Toggles), F5/Ctrl+F/Del-Shortcuts,
  Drag&Drop von .pak-Files
- Backup + Restore mit Enabled-State-Manifest

### Nexus-Tab (Katalog)
- Aggregation aus `latest_added` + `latest_updated` + `trending` + extended
  via `updated.json?period=1m` (Auto-Trigger bei < 100 Basis-Einträgen)
- Cover, Autor, Version, Endorsements, Summary
- Doppelklick öffnet Detail-Dialog mit voller Beschreibung + KI
- **⬇ Download** für Premium-User (Direct-URL)
- Free-User: „↗ Nexus öffnen" → Browser mit Slow-Wall

### Downloads-Tab
- Alle heruntergeladenen `.pak`-Files mit Nexus-Enrichment (Cover, Autor,
  Version, Summary — via `mod_id` aus Filename)
- **📥 Alle installieren** — Bulk-Install
- Pro Row: Installieren + 🔍 Details + Löschen
- Auto-Refresh via FileSystemWatcher

### Nexus-Einstellungen
- Personal-API-Key eintragen (verschlüsselt via DPAPI/AES gespeichert,
  niemals im Log)
- Verify-Button (setzt Premium-Flag), Game-Slug, Cache-Refresh-Intervall

### IUpdateNotifier
Grüner ↑-Badge auf der Icarus-Kachel bei neuen Nexus-Katalog-Einträgen
UND bei Updates für installierte Manual-PAKs.

## Nexus-API-Key beschaffen

1. Bei [nexusmods.com](https://www.nexusmods.com) anmelden (kostenlos)
2. [Account → API Access](https://www.nexusmods.com/users/myaccount?tab=api%20access)
   öffnen
3. „Personal API Key" generieren, kopieren
4. Im Plugin-Tab „Nexus-Einstellungen" einfügen, „Speichern", „Verify" klicken

**Rate-Limit:** 250 Requests/h für Free-User, 2500/h für Premium.
Der Key liegt lokal DPAPI/AES-verschlüsselt und wird nie geloggt.

**Direct-Download** (`⬇ Download` in Katalog + `⬆ Alle updaten` im
Installiert-Tab) funktioniert nur mit **Nexus-Premium** — Nexus liefert
Direct-URLs nur für zahlende User. Free-User klicken „↗ Nexus öffnen",
laden im Browser via Slow-Wall, das Plugin picks die Datei aus dem
Downloads-Ordner auf.

## Installation

Aus [Release](https://github.com/KroModIx/KroModIx.Plugin.Icarus/releases)
das ZIP entpacken nach:

- **Linux:** `~/.config/KroModIx/plugins/kroste.icarus/`
- **Windows:** `%APPDATA%\KroModIx\plugins\kroste.icarus\`

Alternativ: 1-Klick-Install über die Install-Karte in der KroModIx-Sidebar.

## Lizenz

MIT — siehe [LICENSE](LICENSE).

---

☕ [buymeacoffee.com/kroste](https://buymeacoffee.com/kroste)
