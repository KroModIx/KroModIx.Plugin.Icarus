using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KroModIx.Plugin.Icarus.Services.Exmodz;

/// <summary>Das gelesene <c>.EXMOD</c>-Manifest — ein Diff gegen die
/// JSON-Datentabellen des Basisspiels, nicht gegen kompilierte Assets.</summary>
public sealed record ExmodDiff(
    string Name,
    string Author,
    string Version,
    string Description,
    IReadOnlyList<ExmodRow> Rows);

/// <summary>Zielt auf eine Basistabelle, etwa <c>AI-D_AIGrowth.json</c>.</summary>
public sealed record ExmodRow(string CurrentFile, IReadOnlyList<ExmodFileItem> FileItems);

/// <summary>Setzt Felder auf der Basiszeile mit dem Namen
/// <see cref="Name"/> — patcht sie, wenn sie existiert, und fügt sie sonst
/// als neue Zeile an.
///
/// <para><see cref="Fields"/> hält jeden Schlüssel außer <c>Name</c> selbst,
/// und zwar generisch: das echte Schema verschachtelt hier beliebige
/// Spieldaten-Formen, eine Aufzählung der Felder wäre also zum Scheitern
/// verurteilt.</para></summary>
public sealed record ExmodFileItem(string Name, IReadOnlyDictionary<string, JsonNode?> Fields);

public static class ExmodParser
{
    /// <summary>Eine bekannte Abschlusszeile des <c>.EXMOD</c>-Ökosystems:
    /// echte Manifeste beenden ihr <c>Rows</c>-Array mit
    /// <c>{"CurrentFile":"EndOfMod"}</c> und ohne <c>File_Items</c>. Die
    /// Zeile zielt auf keine Tabelle und trägt keinen Patch.</summary>
    public const string EndOfModSentinel = "EndOfMod";

    public static ExmodDiff Parse(byte[] data)
    {
        JsonNode? root;
        try
        {
            // AllowTrailingCommas + Kommentare: .EXMOD-Dateien entstehen in
            // einem Windows-Editor und sind nicht garantiert streng.
            root = JsonNode.Parse(data, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($".EXMOD ist kein gültiges JSON: {ex.Message}", ex);
        }
        if (root is not JsonObject obj)
            throw new InvalidDataException(".EXMOD enthält kein JSON-Objekt.");

        var rows = new List<ExmodRow>();
        if (obj["Rows"] is JsonArray rowArray)
        {
            foreach (var rawRow in rowArray)
            {
                if (rawRow is not JsonObject r) continue;
                var currentFile = r["CurrentFile"]?.GetValue<string>() ?? "";
                var items = new List<ExmodFileItem>();
                if (r["File_Items"] is JsonArray itemArray)
                {
                    foreach (var rawItem in itemArray)
                    {
                        if (rawItem is not JsonObject item) continue;
                        var name = item["Name"]?.GetValue<string>();
                        if (string.IsNullOrEmpty(name))
                            throw new InvalidDataException(
                                $".EXMOD-Zeile {currentFile}: ein File_Items-Eintrag hat keinen Name.");
                        var fields = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
                        foreach (var (key, value) in item)
                        {
                            if (key == "Name") continue;
                            // DeepClone ist Pflicht: ein JsonNode hat genau
                            // einen Elternknoten, und der Wert wird spaeter in
                            // ein anderes Dokument gehaengt.
                            fields[key] = value?.DeepClone();
                        }
                        items.Add(new ExmodFileItem(name, fields));
                    }
                }
                rows.Add(new ExmodRow(currentFile, items));
            }
        }

        return new ExmodDiff(
            Name: obj["name"]?.GetValue<string>() ?? "",
            Author: obj["author"]?.GetValue<string>() ?? "",
            Version: obj["version"]?.GetValue<string>() ?? "",
            Description: obj["description"]?.GetValue<string>() ?? "",
            Rows: rows);
    }

    /// <summary>Die Tabellen, die dieses Manifest anfasst — als
    /// mount-relative Pfade, also mit <c>-</c> zu <c>/</c> aufgelöst. Für die
    /// Konflikt-Anzeige im Installiert-Tab, ohne dafür ein Pak öffnen zu
    /// müssen.</summary>
    public static IReadOnlyList<string> TouchedTables(ExmodDiff diff)
        => diff.Rows
            .Where(r => r.CurrentFile != EndOfModSentinel && r.CurrentFile.Length > 0)
            .Select(r => r.CurrentFile.Replace('-', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
