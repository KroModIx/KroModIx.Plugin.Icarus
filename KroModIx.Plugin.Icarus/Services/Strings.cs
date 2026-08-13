using System.Collections.Generic;
using KroModIx.Plugin.Contracts;

namespace KroModIx.Plugin.Icarus.Services;

/// <summary>Uebersetzungs-Tabelle fuer alle User-facing Strings im
/// Icarus-Plugin. Sprachen: <c>de</c> (Fallback) + <c>en</c>.
///
/// <para>Nutzung: <c>Strings.Init(host.Localization)</c> beim Plugin-Init,
/// dann ueberall <c>Strings.T("key")</c>. Bei fehlendem Key wird der Key
/// selbst zurueckgegeben (macht Missing-Translations sofort sichtbar).</para>
///
/// <para><b>Kein Live-Refresh bei Sprachwechsel:</b> die Strings werden
/// zum View-Constructor-Zeitpunkt gelesen. Bei Sprachwechsel im Host muss
/// der User die Icarus-Kachel neu waehlen (Host-Tab-Cache erzeugt dann
/// neue View-Instanzen mit den frischen Uebersetzungen) oder die App neu
/// starten.</para></summary>
public static class Strings
{
    private static ILocalization? _loc;

    public static void Init(ILocalization loc) => _loc = loc;

    public static string T(string key)
    {
        var iso = _loc?.CurrentIso ?? "de";
        if (iso.StartsWith("en") && En.TryGetValue(key, out var en)) return en;
        if (De.TryGetValue(key, out var de)) return de;
        return key;
    }

    private static readonly Dictionary<string, string> De = new()
    {
        // Tab-Labels
        ["tab.installed"] = "Installiert",
        ["tab.nexus"] = "Nexus",
        ["tab.downloads"] = "Downloads",

        // Installiert-Tab: Toolbar-Buttons
        ["btn.update_all"] = "⬆  Alle updaten",
        ["btn.check_updates"] = "🔄  Updates prüfen",
        ["btn.install_pak"] = "📁  PAK installieren…",
        ["btn.refresh"] = "↺  Aktualisieren",
        ["btn.toggle_bulk"] = "🔀  Aktiv/Inaktiv",
        ["btn.uninstall_selection"] = "🗑  Auswahl deinstallieren",
        ["btn.open_mods_folder"] = "📂  Mod-Ordner",
        ["btn.open_workshop_folder"] = "⚙  Workshop-Ordner",
        ["btn.backup"] = "💾  Backup",
        ["btn.restore"] = "♻  Restore",

        // Filter-Row
        ["toggle.manual"] = "📁  Manuell",
        ["toggle.workshop"] = "⚙  Workshop",

        // Row-Aktionen
        ["btn.update"] = "⬆  Update",
        ["btn.toggle_enabled"] = "⏻  (De-)Aktivieren",
        ["btn.details"] = "🔍  Details",
        ["btn.uninstall"] = "🗑  Deinstallieren",

        // Badges + Row-Meta
        ["badge.active"] = "aktiv",
        ["badge.workshop"] = "⚙ WORKSHOP",
        ["row.state.active"] = "aktiv",
        ["row.state.inactive"] = "inaktiv",
        ["row.state.workshop"] = "Workshop",
        ["row.steam_managed"] = "Steam verwaltet",
        ["row.update_badge_prefix"] = "⬆ Update v",

        // Placeholders + Tooltips
        ["placeholder.filter_paks"] = "PAK-Mods filtern (Dateiname) …",
        ["placeholder.filter_nexus"] = "Nexus-Katalog filtern …",
        ["tooltip.update_all"] = "Installiert alle Updates sequenziell (Nexus-Rate-Limit-Rücksicht). Braucht Nexus-Premium.",
        ["tooltip.check_updates"] = "Prüft für jeden Manual-Mod mit erkennbarer Nexus-Mod-Id ob dort eine neuere Version steht (throttled 250ms). Workshop-Mods updated Steam automatisch.",
        ["tooltip.details"] = "Nexus-Mod-Detail öffnen (nur bei Nexus-Downloads mit erkennbarer Mod-Id)",
        ["tooltip.details_downloads"] = "Nexus-Mod-Detail öffnen (nur bei Downloads mit erkennbarer Nexus-Mod-Id)",
        ["tooltip.load_extended"] = "Erweitert den Katalog um alle Mods aus updated.json?period=1m (Detail-für-Detail, throttled). Kostet API-Rate-Limit-Kontingent — Premium-User 2500/h, Free-User 250/h.",
        ["tooltip.premium_download"] = "Direct-Download in den Downloads-Ordner (Nexus-Premium nötig)",
        ["tooltip.premium_download_detail"] = "Direct-Download in den Downloads-Ordner (Nexus-Premium nötig — sonst \"Auf Nexus öffnen\" für Browser)",
        ["tooltip.install_all"] = "Installiert alle PAK-Downloads (überschreibt bestehende Versionen). Ideal nach einem Update-Batch.",

        // Path-Labels
        ["label.manual_path_prefix"] = "Manual: {0}",
        ["label.downloads_folder_prefix"] = "Ordner: {0}",
        ["label.workshop_none"] = "(kein Workshop-Ordner erkannt)",

        // Status-Zeilen (Installiert)
        ["status.no_mods"] = "Keine Mods gefunden.",
        ["status.mod_summary"] = "{0} aktiv · {1} manuell · {2} Workshop",
        ["status.mods_load_error"] = "Fehler beim Lesen der Mod-Ordner.",
        ["status.updates_found"] = "Updates gefunden: {0} Mod(s).",
        ["status.no_updates"] = "Keine Updates.",
        ["status.selection_count"] = "{0} ausgewählt",

        // Status-Zeilen (Downloads)
        ["status.no_downloads"] = "Keine PAK-Dateien im Downloads-Ordner.",
        ["status.downloads_summary"] = "{0} PAKs · {1:F1} MB gesamt",
        ["status.downloads_load_error"] = "Fehler beim Lesen des Downloads-Ordners.",

        // Status-Zeilen (Nexus)
        ["status.catalog_loading"] = "Nexus-Katalog wird geladen …",
        ["status.no_api_key_status"] = "Kein Nexus-API-Key konfiguriert — bitte im Nexus-Settings-Tab eintragen.",
        ["status.catalog_summary"] = "{0} Mods (Cache-Alter: {1} h) — Extended-Load läuft im Hintergrund …",
        ["status.catalog_full"] = "{0} Mods im Katalog (vollständig).",
        ["status.catalog_load_error"] = "Fehler beim Laden: {0}",
        ["status.extended_baseline"] = "Extended-Katalog: Baseline via updated.json?period=1m …",
        ["status.extended_progress"] = "Detail {0}/{1} …",
        ["status.extended_load_error"] = "Extended-Load fehlgeschlagen: {0}",

        // Notifications (Installiert)
        ["notify.mod_enabled_prefix"] = "Mod aktiviert: ",
        ["notify.mod_disabled_prefix"] = "Mod deaktiviert: ",
        ["notify.error_prefix"] = "Fehler: ",
        ["notify.bulk_only_workshop"] = "Nur Workshop-Mods ausgewählt — die kann Steam nur.",
        ["notify.bulk_enable_result"] = "{0} Mod(s) aktiviert.",
        ["notify.bulk_disable_result"] = "{0} Mod(s) deaktiviert.",
        ["notify.workshop_readonly"] = "Workshop-Mod: Abo in Steam kündigen, dann verschwindet er hier automatisch.",
        ["notify.uninstalled_prefix"] = "Deinstalliert: ",
        ["notify.bulk_uninstall_result"] = "{0} Mod(s) deinstalliert.",
        ["notify.installed_prefix"] = "Installiert: ",
        ["notify.installed_drop_prefix"] = "Installiert (Drop): ",
        ["notify.drop_install_fail"] = "Drop-Install fehlgeschlagen ({0}): {1}",
        ["notify.no_workshop_folder"] = "Kein Workshop-Ordner — noch keine Workshop-Mods abonniert.",
        ["notify.nexus_api_unavailable"] = "Nexus-API nicht verfügbar.",
        ["notify.no_nexus_key_check"] = "Kein Nexus-API-Key konfiguriert — Updates prüfen im Nexus-Settings-Tab.",
        ["notify.no_backup_manual"] = "Keine manuellen Mods zum Sichern (Workshop-Mods sichert Steam).",
        ["notify.backup_summary"] = "Backup: {0} Mods · {1} → {2}",
        ["notify.backup_error"] = "Backup-Fehler: ",
        ["notify.backup_invalid"] = "Backup ungültig: ",
        ["notify.restore_summary"] = "Restore: {0} wiederhergestellt, {1} übersprungen.",
        ["notify.restore_error"] = "Restore-Fehler: ",
        ["notify.nexus_detail_unavailable"] = "Nexus-Detail nicht verfügbar (Nexus-Client fehlt in dieser Session).",
        ["notify.workshop_no_nexus"] = "Workshop-Mods haben keine Nexus-Details.",
        ["notify.no_nexus_id"] = "Keine Nexus-Mod-Id im Dateinamen erkennbar: {0}",

        // Notifications (Update-Flow)
        ["notify.no_updates_hint"] = "Keine offenen Updates. Erst 🔄 Updates prüfen klicken.",
        ["notify.updates_installed"] = "{0} PAK-Update(s) installiert.",
        ["notify.updates_result"] = "{0} installiert, {1} Fehler.",
        ["notify.no_mod_id_update"] = "Keine Nexus-Mod-Id — Update nicht auflösbar.",
        ["notify.update_needs_premium"] = "Update braucht Nexus-Premium für Direct-Download. Browser-Weg via Nexus-Katalog.",
        ["notify.no_main_file"] = "Keine Main-Datei bei Nexus gefunden.",
        ["notify.nexus_deny_url_settings"] = "Nexus verweigert Direct-URL — Premium-Status im Nexus-Settings-Tab prüfen.",
        ["notify.update_installed_prefix"] = "Update installiert: ",
        ["notify.update_error_prefix"] = "Update-Fehler: ",

        // Notifications (Downloads-Tab)
        ["notify.no_downloads_install"] = "Keine Downloads zu installieren.",
        ["notify.bulk_install_ok"] = "{0} PAKs installiert.",
        ["notify.bulk_install_partial"] = "{0} installiert, {1} Fehler (siehe Log).",
        ["notify.deleted_prefix"] = "Gelöscht: ",

        // Notifications (Nexus-Tab)
        ["notify.premium_required"] = "Direct-Download braucht Nexus-Premium. Klick \"Nexus öffnen\" für den Browser-Weg.",
        ["notify.premium_required_detail"] = "Direct-Download braucht Nexus-Premium. Klick \"Auf Nexus öffnen\" für den Browser-Weg.",
        ["notify.no_main_file_generic"] = "Keine Main-Datei gefunden.",
        ["notify.nexus_deny_url_verify"] = "Nexus verweigert Download-URL — Premium-Status prüfen (Verify im Settings-Tab).",
        ["notify.download_ok_prefix"] = "Heruntergeladen: ",
        ["notify.download_error_prefix"] = "Download-Fehler: ",

        // Notifications (Detail-Dialog)
        ["notify.detail_wait"] = "Bitte warten bis Detail geladen ist.",
        ["notify.ai_unavailable"] = "KI-Provider nicht erreichbar — bitte in den KroModIx-Einstellungen konfigurieren.",

        // Nexus-Tab: No-API-Key-Panel
        ["nokey.title"] = "Nexus-Mods braucht einen API-Key",
        ["nokey.body"] = "Öffne den Tab \"Nexus-Einstellungen\", trag deinen persönlichen API-Key ein (kostenlos nach Nexus-Registrierung auf nexusmods.com → Account → API Keys) und komm dann hierher zurück.",

        // Nexus-Tab: Buttons
        ["btn.load_extended"] = "📚  Vollen Katalog laden",
        ["btn.open_downloads"] = "📂  Downloads-Ordner",
        ["btn.download"] = "⬇  Download",
        ["btn.download_long"] = "⬇  Herunterladen",
        ["btn.open_nexus"] = "↗  Nexus öffnen",
        ["btn.open_nexus_long"] = "↗  Auf Nexus öffnen",
        ["btn.close"] = "Schließen",
        ["btn.ai_summary"] = "🤖  KI-Zusammenfassung",

        // Downloads-Tab: Buttons
        ["btn.install_all"] = "📥  Alle installieren",
        ["btn.open_downloads_folder"] = "📂  Downloads-Ordner öffnen",
        ["btn.install"] = "📥  Installieren",
        ["btn.delete"] = "🗑  Löschen",

        // Nexus-Detail-Dialog
        ["detail.window_title"] = "Nexus-Mod-Detail",
        ["detail.meta.author"] = "Autor",
        ["detail.meta.version"] = "Version",
        ["detail.meta.category"] = "Kategorie",
        ["detail.meta.updated"] = "Aktualisiert",
        ["detail.meta.endorsements"] = "Endorsements",
        ["detail.section.description"] = "Beschreibung",
        ["detail.section.ai_summary"] = "🤖 KI-Zusammenfassung",
        ["detail.status.loading"] = "Detail wird geladen …",
        ["detail.desc_placeholder"] = "Detail-Beschreibung wird geladen …",
        ["detail.desc_load_error"] = "Detail konnte nicht geladen werden (API-Fehler oder Rate-Limit).",
        ["detail.desc_no_content"] = "Keine Beschreibung im Detail-Endpoint.",
        ["detail.status.load_error"] = "Fehler beim Laden.",
        ["detail.status.available"] = "verfügbar",
        ["detail.status.unavailable"] = "nicht verfügbar",
        ["detail.busy_download"] = "…Download läuft…",
        ["detail.busy_ai"] = "…KI läuft…",
        ["detail.badge.adult"] = "🔞 ADULT",
        ["detail.ai.starting"] = "KI-Zusammenfassung via {0} …",
        ["detail.ai.no_answer"] = "KI hat keine Antwort geliefert.",
        ["detail.error_prefix"] = "Fehler: ",

        // Dialogs
        ["dialog.uninstall_title"] = "PAK-Mod deinstallieren",
        ["dialog.uninstall_msg"] = "„{0}“ wirklich löschen?",
        ["dialog.uninstall_bulk_title"] = "Mods deinstallieren",
        ["dialog.uninstall_bulk_msg"] = "{0} manuelle Mod(s) wirklich löschen?",
        ["dialog.uninstall_bulk_more"] = "… und {0} weitere",
        ["dialog.delete_download_title"] = "Download löschen",
        ["dialog.delete_download_msg"] = "„{0}“ aus dem Downloads-Ordner löschen?",
        ["dialog.pick_pak_title"] = "PAK-Mod wählen",
        ["dialog.pick_pak_filter"] = "Icarus PAK-Mod (.pak)",
        ["dialog.pick_backup_title"] = "Backup-ZIP wählen",
        ["dialog.pick_backup_filter"] = "Icarus-Backup (.zip)",
        ["dialog.restore_title"] = "Backup wiederherstellen",
        ["dialog.restore_msg"] = "Backup vom {0} · {1} Mods.\nVorhandene PAKs mit gleichem Namen werden überschrieben.\nFortfahren?",
        ["dialog.btn.delete"] = "Löschen",
        ["dialog.btn.cancel"] = "Abbrechen",
        ["dialog.btn.restore"] = "Wiederherstellen",

        // Progress
        ["progress.backup"] = "Backup erstellen …",
        ["progress.restore"] = "Backup wiederherstellen …",
        ["progress.backup_row"] = "{0}/{1} · {2}",
        ["progress.updates"] = "{0} PAK-Updates …",
        ["progress.update_row"] = "Update {0}/{1}: {2}",
        ["progress.update_scope"] = "Update: {0}",
        ["progress.update_files_load"] = "Datei-Liste laden …",
        ["progress.update_url"] = "Download-URL holen ({0}) …",
        ["progress.download_percent"] = "{0} · {1}%",
        ["progress.nexus_scope"] = "Nexus: {0}",
        ["progress.install_downloads"] = "Installiere {0} PAK-Downloads …",
        ["progress.install_row"] = "Installiere {0}/{1}: {2}",
    };

    private static readonly Dictionary<string, string> En = new()
    {
        // Tab-Labels
        ["tab.installed"] = "Installed",
        ["tab.nexus"] = "Nexus",
        ["tab.downloads"] = "Downloads",

        // Installed tab: toolbar buttons
        ["btn.update_all"] = "⬆  Update all",
        ["btn.check_updates"] = "🔄  Check updates",
        ["btn.install_pak"] = "📁  Install PAK…",
        ["btn.refresh"] = "↺  Refresh",
        ["btn.toggle_bulk"] = "🔀  Enable/disable",
        ["btn.uninstall_selection"] = "🗑  Uninstall selection",
        ["btn.open_mods_folder"] = "📂  Mods folder",
        ["btn.open_workshop_folder"] = "⚙  Workshop folder",
        ["btn.backup"] = "💾  Backup",
        ["btn.restore"] = "♻  Restore",

        // Filter row
        ["toggle.manual"] = "📁  Manual",
        ["toggle.workshop"] = "⚙  Workshop",

        // Row actions
        ["btn.update"] = "⬆  Update",
        ["btn.toggle_enabled"] = "⏻  Enable/disable",
        ["btn.details"] = "🔍  Details",
        ["btn.uninstall"] = "🗑  Uninstall",

        // Badges + row meta
        ["badge.active"] = "active",
        ["badge.workshop"] = "⚙ WORKSHOP",
        ["row.state.active"] = "active",
        ["row.state.inactive"] = "disabled",
        ["row.state.workshop"] = "Workshop",
        ["row.steam_managed"] = "Steam managed",
        ["row.update_badge_prefix"] = "⬆ Update v",

        // Placeholders + tooltips
        ["placeholder.filter_paks"] = "Filter PAK mods (filename) …",
        ["placeholder.filter_nexus"] = "Filter Nexus catalog …",
        ["tooltip.update_all"] = "Installs all updates sequentially (respecting Nexus rate limits). Requires Nexus Premium.",
        ["tooltip.check_updates"] = "Checks each manual mod with a recognizable Nexus mod-id for a newer version (throttled 250ms). Workshop mods are updated by Steam automatically.",
        ["tooltip.details"] = "Open Nexus mod details (only for Nexus downloads with a recognizable mod-id)",
        ["tooltip.details_downloads"] = "Open Nexus mod details (only for downloads with a recognizable Nexus mod-id)",
        ["tooltip.load_extended"] = "Extends the catalog by all mods from updated.json?period=1m (detail-by-detail, throttled). Costs API rate limit quota — Premium 2500/h, Free 250/h.",
        ["tooltip.premium_download"] = "Direct download to downloads folder (Nexus Premium required)",
        ["tooltip.premium_download_detail"] = "Direct download to downloads folder (Nexus Premium required — otherwise \"Open on Nexus\" for browser)",
        ["tooltip.install_all"] = "Installs all PAK downloads (overwrites existing versions). Ideal after an update batch.",

        // Path labels
        ["label.manual_path_prefix"] = "Manual: {0}",
        ["label.downloads_folder_prefix"] = "Folder: {0}",
        ["label.workshop_none"] = "(no workshop folder detected)",

        // Status lines (Installed)
        ["status.no_mods"] = "No mods found.",
        ["status.mod_summary"] = "{0} active · {1} manual · {2} workshop",
        ["status.mods_load_error"] = "Error reading mod folders.",
        ["status.updates_found"] = "Updates found: {0} mod(s).",
        ["status.no_updates"] = "No updates.",
        ["status.selection_count"] = "{0} selected",

        // Status lines (Downloads)
        ["status.no_downloads"] = "No PAK files in downloads folder.",
        ["status.downloads_summary"] = "{0} PAKs · {1:F1} MB total",
        ["status.downloads_load_error"] = "Error reading downloads folder.",

        // Status lines (Nexus)
        ["status.catalog_loading"] = "Loading Nexus catalog …",
        ["status.no_api_key_status"] = "No Nexus API key configured — set it in the Nexus settings tab.",
        ["status.catalog_summary"] = "{0} mods (cache age: {1} h) — extended load running in background …",
        ["status.catalog_full"] = "{0} mods in catalog (complete).",
        ["status.catalog_load_error"] = "Load error: {0}",
        ["status.extended_baseline"] = "Extended catalog: baseline via updated.json?period=1m …",
        ["status.extended_progress"] = "Detail {0}/{1} …",
        ["status.extended_load_error"] = "Extended load failed: {0}",

        // Notifications (Installed)
        ["notify.mod_enabled_prefix"] = "Mod enabled: ",
        ["notify.mod_disabled_prefix"] = "Mod disabled: ",
        ["notify.error_prefix"] = "Error: ",
        ["notify.bulk_only_workshop"] = "Only workshop mods selected — those are managed by Steam.",
        ["notify.bulk_enable_result"] = "{0} mod(s) enabled.",
        ["notify.bulk_disable_result"] = "{0} mod(s) disabled.",
        ["notify.workshop_readonly"] = "Workshop mod: unsubscribe in Steam and it will disappear here automatically.",
        ["notify.uninstalled_prefix"] = "Uninstalled: ",
        ["notify.bulk_uninstall_result"] = "{0} mod(s) uninstalled.",
        ["notify.installed_prefix"] = "Installed: ",
        ["notify.installed_drop_prefix"] = "Installed (drop): ",
        ["notify.drop_install_fail"] = "Drop install failed ({0}): {1}",
        ["notify.no_workshop_folder"] = "No workshop folder — no workshop mods subscribed yet.",
        ["notify.nexus_api_unavailable"] = "Nexus API not available.",
        ["notify.no_nexus_key_check"] = "No Nexus API key configured — configure it in the Nexus settings tab.",
        ["notify.no_backup_manual"] = "No manual mods to back up (workshop mods are backed up by Steam).",
        ["notify.backup_summary"] = "Backup: {0} mods · {1} → {2}",
        ["notify.backup_error"] = "Backup error: ",
        ["notify.backup_invalid"] = "Backup invalid: ",
        ["notify.restore_summary"] = "Restore: {0} restored, {1} skipped.",
        ["notify.restore_error"] = "Restore error: ",
        ["notify.nexus_detail_unavailable"] = "Nexus details not available (Nexus client missing in this session).",
        ["notify.workshop_no_nexus"] = "Workshop mods have no Nexus details.",
        ["notify.no_nexus_id"] = "No Nexus mod-id recognizable in filename: {0}",

        // Notifications (update flow)
        ["notify.no_updates_hint"] = "No pending updates. Click 🔄 Check updates first.",
        ["notify.updates_installed"] = "{0} PAK update(s) installed.",
        ["notify.updates_result"] = "{0} installed, {1} error(s).",
        ["notify.no_mod_id_update"] = "No Nexus mod-id — update cannot be resolved.",
        ["notify.update_needs_premium"] = "Update requires Nexus Premium for direct download. Browser path via Nexus catalog.",
        ["notify.no_main_file"] = "No main file found on Nexus.",
        ["notify.nexus_deny_url_settings"] = "Nexus denied direct URL — check Premium status in the Nexus settings tab.",
        ["notify.update_installed_prefix"] = "Update installed: ",
        ["notify.update_error_prefix"] = "Update error: ",

        // Notifications (Downloads tab)
        ["notify.no_downloads_install"] = "No downloads to install.",
        ["notify.bulk_install_ok"] = "{0} PAKs installed.",
        ["notify.bulk_install_partial"] = "{0} installed, {1} error(s) (see log).",
        ["notify.deleted_prefix"] = "Deleted: ",

        // Notifications (Nexus tab)
        ["notify.premium_required"] = "Direct download requires Nexus Premium. Click \"Open on Nexus\" for the browser flow.",
        ["notify.premium_required_detail"] = "Direct download requires Nexus Premium. Click \"Open on Nexus\" for the browser flow.",
        ["notify.no_main_file_generic"] = "No main file found.",
        ["notify.nexus_deny_url_verify"] = "Nexus denied download URL — check Premium status (Verify in the settings tab).",
        ["notify.download_ok_prefix"] = "Downloaded: ",
        ["notify.download_error_prefix"] = "Download error: ",

        // Notifications (Detail dialog)
        ["notify.detail_wait"] = "Please wait until details are loaded.",
        ["notify.ai_unavailable"] = "AI provider not reachable — configure it in KroModIx settings.",

        // Nexus tab: no-API-key panel
        ["nokey.title"] = "Nexus mods require an API key",
        ["nokey.body"] = "Open the \"Nexus settings\" tab, enter your personal API key (free after Nexus registration on nexusmods.com → Account → API Keys) and come back here.",

        // Nexus tab: buttons
        ["btn.load_extended"] = "📚  Load full catalog",
        ["btn.open_downloads"] = "📂  Downloads folder",
        ["btn.download"] = "⬇  Download",
        ["btn.download_long"] = "⬇  Download",
        ["btn.open_nexus"] = "↗  Open on Nexus",
        ["btn.open_nexus_long"] = "↗  Open on Nexus",
        ["btn.close"] = "Close",
        ["btn.ai_summary"] = "🤖  AI summary",

        // Downloads tab: buttons
        ["btn.install_all"] = "📥  Install all",
        ["btn.open_downloads_folder"] = "📂  Open downloads folder",
        ["btn.install"] = "📥  Install",
        ["btn.delete"] = "🗑  Delete",

        // Nexus detail dialog
        ["detail.window_title"] = "Nexus mod details",
        ["detail.meta.author"] = "Author",
        ["detail.meta.version"] = "Version",
        ["detail.meta.category"] = "Category",
        ["detail.meta.updated"] = "Updated",
        ["detail.meta.endorsements"] = "Endorsements",
        ["detail.section.description"] = "Description",
        ["detail.section.ai_summary"] = "🤖 AI summary",
        ["detail.status.loading"] = "Loading details …",
        ["detail.desc_placeholder"] = "Loading description …",
        ["detail.desc_load_error"] = "Details could not be loaded (API error or rate limit).",
        ["detail.desc_no_content"] = "No description in detail endpoint.",
        ["detail.status.load_error"] = "Load error.",
        ["detail.status.available"] = "available",
        ["detail.status.unavailable"] = "unavailable",
        ["detail.busy_download"] = "…download running…",
        ["detail.busy_ai"] = "…AI running…",
        ["detail.badge.adult"] = "🔞 ADULT",
        ["detail.ai.starting"] = "AI summary via {0} …",
        ["detail.ai.no_answer"] = "AI returned no answer.",
        ["detail.error_prefix"] = "Error: ",

        // Dialogs
        ["dialog.uninstall_title"] = "Uninstall PAK mod",
        ["dialog.uninstall_msg"] = "Really delete \"{0}\"?",
        ["dialog.uninstall_bulk_title"] = "Uninstall mods",
        ["dialog.uninstall_bulk_msg"] = "Really delete {0} manual mod(s)?",
        ["dialog.uninstall_bulk_more"] = "… and {0} more",
        ["dialog.delete_download_title"] = "Delete download",
        ["dialog.delete_download_msg"] = "Delete \"{0}\" from the downloads folder?",
        ["dialog.pick_pak_title"] = "Pick PAK mod",
        ["dialog.pick_pak_filter"] = "Icarus PAK mod (.pak)",
        ["dialog.pick_backup_title"] = "Pick backup ZIP",
        ["dialog.pick_backup_filter"] = "Icarus backup (.zip)",
        ["dialog.restore_title"] = "Restore backup",
        ["dialog.restore_msg"] = "Backup from {0} · {1} mods.\nExisting PAKs with the same name will be overwritten.\nContinue?",
        ["dialog.btn.delete"] = "Delete",
        ["dialog.btn.cancel"] = "Cancel",
        ["dialog.btn.restore"] = "Restore",

        // Progress
        ["progress.backup"] = "Creating backup …",
        ["progress.restore"] = "Restoring backup …",
        ["progress.backup_row"] = "{0}/{1} · {2}",
        ["progress.updates"] = "{0} PAK updates …",
        ["progress.update_row"] = "Update {0}/{1}: {2}",
        ["progress.update_scope"] = "Update: {0}",
        ["progress.update_files_load"] = "Loading file list …",
        ["progress.update_url"] = "Getting download URL ({0}) …",
        ["progress.download_percent"] = "{0} · {1}%",
        ["progress.nexus_scope"] = "Nexus: {0}",
        ["progress.install_downloads"] = "Installing {0} PAK downloads …",
        ["progress.install_row"] = "Installing {0}/{1}: {2}",
    };
}
