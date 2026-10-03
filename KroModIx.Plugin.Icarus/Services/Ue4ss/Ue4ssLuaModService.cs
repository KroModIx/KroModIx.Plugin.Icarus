using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using SharpCompress.Archives;
using KroModIx.Plugin.Icarus.Services.Archive;

namespace KroModIx.Plugin.Icarus.Services.Ue4ss;

/// <summary>Ein Lua-Mod im UE4SS-Mods-Ordner. Die Identität ist der
/// Ordnername — UE4SS lädt pro Ordner, nicht pro Datei.</summary>
public sealed record Ue4ssLuaMod(
    string Name,
    string FolderPath,
    bool IsEnabled,
    int ScriptCount,
    long SizeBytes,
    DateTime InstalledUtc);

/// <summary>Verwaltet die UE4SS-Lua-Mods einer Icarus-Installation:
/// auflisten, aus einem Archiv installieren, ein-/ausschalten,
/// deinstallieren.
///
/// <para><b>Ein-/Ausschalten läuft über <c>enabled.txt</c></b>, nicht über
/// eine Dateiendung wie bei den PAKs. Das ist die UE4SS-Konvention: liegt im
/// Mod-Ordner eine <c>enabled.txt</c>, lädt UE4SS die Mod, unabhängig von
/// der <c>mods.txt</c>. Zum Ausschalten wird sie nach
/// <c>enabled.txt.disabled</c> umbenannt statt gelöscht — manche Mods legen
/// dort Inhalt ab, und ein Umschalten darf nichts vernichten.</para>
///
/// <para><b>Die <c>mods.txt</c> wird zusätzlich nachgezogen</b>, aber nur,
/// wenn dort schon eine Zeile für genau diesen Ordnernamen steht. Grund:
/// UE4SS wertet beide Quellen aus. Steht die Mod in der <c>mods.txt</c> mit
/// <c>: 1</c>, lädt sie auch ohne <c>enabled.txt</c> — ein Ausschalten, das
/// nur die Datei umbenennt, bliebe dann wirkungslos. Neue Zeilen werden
/// nicht angelegt: die mitgelieferte <c>enabled.txt</c> reicht, und eine
/// fremde Datei umzuschreiben ist der teurere Weg.</para></summary>
public sealed class Ue4ssLuaModService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const string EnabledMarker = "enabled.txt";
    private const string DisabledMarker = "enabled.txt.disabled";

    /// <summary>Ordner im Mods-Verzeichnis, die zu UE4SS selbst gehören und
    /// keine User-Mods sind. Sie stehen bewusst nicht in der Liste — der User
    /// soll die Beispiel-Mods des Loaders nicht als eigene Installation
    /// sehen und schon gar nicht aus Versehen deinstallieren.</summary>
    private static readonly HashSet<string> BuiltInMods = new(StringComparer.OrdinalIgnoreCase)
    {
        "ActorDumperMod", "BPML_GenericFunctions", "BPModLoaderMod",
        "CheatManagerEnablerMod", "ConsoleCommandsMod", "ConsoleEnablerMod",
        "jsbLuaProfilerMod", "Keybinds", "LineTraceMod", "SplitScreenMod",
        "shared",
    };

    private readonly Ue4ssPaths _paths;

    public Ue4ssLuaModService(Ue4ssPaths paths) => _paths = paths;

    public Ue4ssPaths Paths => _paths;

    /// <summary>Die installierten Lua-Mods, ohne die Beispiel-Mods des
    /// Loaders. Leere Liste, wenn UE4SS nicht installiert ist — das ist der
    /// Normalfall und kein Fehler.</summary>
    public IReadOnlyList<Ue4ssLuaMod> ListInstalled()
    {
        var modsDir = _paths.FindModsDir();
        if (modsDir is null || !Directory.Exists(modsDir)) return [];

        var result = new List<Ue4ssLuaMod>();
        foreach (var dir in Directory.EnumerateDirectories(modsDir))
        {
            var name = Path.GetFileName(dir);
            if (BuiltInMods.Contains(name)) continue;
            try
            {
                var scripts = Directory.Exists(Path.Combine(dir, "Scripts"))
                    ? Directory.GetFiles(Path.Combine(dir, "Scripts"), "*.lua",
                        SearchOption.AllDirectories)
                    : [];
                // Ein Ordner ohne ein einziges Lua-Skript ist keine Lua-Mod
                // (Blueprint-Mods des BPModLoaders etwa liegen auch hier).
                if (scripts.Length == 0) continue;

                long size = 0;
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { size += new FileInfo(f).Length; } catch { /* einzelne Datei weg */ }
                }

                result.Add(new Ue4ssLuaMod(
                    Name: name,
                    FolderPath: dir,
                    IsEnabled: File.Exists(Path.Combine(dir, EnabledMarker)),
                    ScriptCount: scripts.Length,
                    SizeBytes: size,
                    InstalledUtc: Directory.GetLastWriteTimeUtc(dir)));
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Lua-Mod-Ordner nicht lesbar: {Dir}", dir);
            }
        }
        return result.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Installiert alle UE4SS-Lua-Mods aus einem Archiv, also jeden
    /// Ordner unterhalb von <c>UE4SS Mods/</c>. Gibt die Namen der
    /// installierten Mods zurück.</summary>
    public IReadOnlyList<string> InstallFromArchive(string archivePath, bool overwrite = true)
    {
        var modsDir = _paths.FindOrCreateModsDir();
        if (modsDir is null)
            throw new InvalidOperationException(
                "UE4SS-Mods-Ordner konnte nicht angelegt werden — Binaries/Win64 nicht gefunden " +
                "oder nicht beschreibbar.");

        using var archive = ArchiveFactory.Open(archivePath);
        var entries = archive.Entries
            .Where(e => !e.IsDirectory && !string.IsNullOrEmpty(e.Key))
            .ToList();

        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prefix = IcarusArchive.Ue4ssFolder + "/";

        foreach (var entry in entries)
        {
            var key = entry.Key!.Replace('\\', '/');
            var modName = IcarusArchive.TryGetUe4ssModName(key);
            if (modName is null) continue;

            // Pfad relativ zum "UE4SS Mods/"-Prefix — der Mod-Ordner samt
            // Unterstruktur bleibt erhalten (Scripts/, enabled.txt, …).
            var rel = key[prefix.Length..];
            if (!IcarusArchive.TryResolveSafe(modsDir, rel, out var dst))
            {
                Log.Warn("Zip-Slip im UE4SS-Eintrag uebersprungen: {Key}", key);
                continue;
            }
            if (File.Exists(dst) && !overwrite)
                throw new IOException($"Lua-Mod ist bereits installiert: {modName}");

            IcarusArchive.ExtractOne(entry, dst);
            installed.Add(modName);
        }

        foreach (var name in installed)
            Log.Info("UE4SS-Lua-Mod installiert: {Name} → {Dir}", name, Path.Combine(modsDir, name));
        return installed.ToList();
    }

    /// <summary>Schaltet eine Lua-Mod ein oder aus. Rückgabe ist der neue
    /// Zustand der Mod.</summary>
    public Ue4ssLuaMod SetEnabled(Ue4ssLuaMod mod, bool enabled)
    {
        if (mod.IsEnabled == enabled) return mod;
        var enabledPath = Path.Combine(mod.FolderPath, EnabledMarker);
        var disabledPath = Path.Combine(mod.FolderPath, DisabledMarker);

        if (enabled)
        {
            if (File.Exists(disabledPath)) File.Move(disabledPath, enabledPath, overwrite: true);
            else File.WriteAllText(enabledPath, "");
        }
        else if (File.Exists(enabledPath))
        {
            File.Move(enabledPath, disabledPath, overwrite: true);
        }

        TrySyncModsTxt(mod.Name, enabled);
        Log.Info("UE4SS-Lua-Mod {State}: {Name}", enabled ? "aktiviert" : "deaktiviert", mod.Name);
        return mod with { IsEnabled = enabled };
    }

    public void Uninstall(Ue4ssLuaMod mod)
    {
        if (!Directory.Exists(mod.FolderPath))
        {
            Log.Warn("UE4SS-Uninstall: Ordner bereits weg: {Dir}", mod.FolderPath);
            return;
        }
        var modsDir = _paths.FindModsDir();
        // Sicherheitsnetz gegen einen falsch gesetzten FolderPath: nur
        // loeschen, was wirklich unterhalb des Mods-Ordners liegt.
        if (modsDir is null
            || !Path.GetFullPath(mod.FolderPath).StartsWith(
                   Path.GetFullPath(modsDir) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Ordner liegt nicht im UE4SS-Mods-Verzeichnis: {mod.FolderPath}");

        Directory.Delete(mod.FolderPath, recursive: true);
        TrySyncModsTxt(mod.Name, enabled: false);
        Log.Info("UE4SS-Lua-Mod deinstalliert: {Name}", mod.Name);
    }

    /// <summary>Zieht eine <b>bestehende</b> Zeile in der <c>mods.txt</c>
    /// nach. Fehlt die Datei oder die Zeile, passiert nichts — die
    /// <c>enabled.txt</c> allein genügt UE4SS, und eine fremde Datei um
    /// Einträge zu erweitern wäre ein Eingriff ohne Gegenwert.
    ///
    /// <para>Fehler werden geloggt, aber nicht geworfen: der Umschalt-Vorgang
    /// selbst war schon erfolgreich, und ein nicht beschreibbares
    /// <c>mods.txt</c> darf ihn nicht nachträglich als gescheitert
    /// darstellen.</para></summary>
    private void TrySyncModsTxt(string modName, bool enabled)
    {
        try
        {
            var modsDir = _paths.FindModsDir();
            if (modsDir is null) return;
            var path = Path.Combine(modsDir, "mods.txt");
            if (!File.Exists(path)) return;

            var lines = File.ReadAllLines(path);
            var changed = false;
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var sep = line.IndexOf(':');
                if (sep <= 0) continue;
                var name = line[..sep].Trim();
                // Das BOM, das UE4SS in seine mods.txt schreibt, haengt an
                // der ersten Zeile und wuerde den Namensvergleich brechen.
                name = name.TrimStart('﻿');
                if (!string.Equals(name, modName, StringComparison.OrdinalIgnoreCase)) continue;
                var want = $"{name} : {(enabled ? 1 : 0)}";
                if (lines[i] == want) break;
                lines[i] = want;
                changed = true;
                break;
            }
            if (!changed) return;

            var tmp = path + ".tmp";
            File.WriteAllLines(tmp, lines);
            File.Move(tmp, path, overwrite: true);
            Log.Info("mods.txt nachgezogen: {Name} = {State}", modName, enabled ? 1 : 0);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "mods.txt konnte nicht nachgezogen werden: {Name}", modName);
        }
    }
}
