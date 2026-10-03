using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KroModIx.Plugin.Icarus.Services.Exmodz;

/// <summary>Setzt die <c>File_Items</c> einer <c>.EXMOD</c>-Zeile auf eine
/// echte Icarus-Datentabelle.
///
/// <para>Die Tabellen haben die Standard-Form eines Unreal-DataTable-Exports:
/// <c>{"RowStruct": "…", "Defaults": {…}, "Rows": [{"Name": "…", …Felder}, …]}</c>
/// — bestätigt gegen eine echte installierte <c>data.pak</c>. <b>Nicht</b> die
/// flache Abbildung <c>{Name: {Felder}}</c>, die man zuerst annimmt; in der
/// Vorlage war genau das der erste Anlauf, der nie zu echten Spieldaten
/// passte und nur gegen erfundene Testdaten lief.</para>
///
/// <para><b>Jedes File_Item ist ein Upsert, kein strenger Patch.</b> Passt
/// sein <c>Name</c> auf eine bestehende Zeile, werden deren Felder flach
/// zusammengeführt (die Felder des Items gewinnen, alles andere auf der Zeile
/// bleibt unangetastet). Gibt es keine Zeile mit dem Namen, wird das Item
/// wörtlich angehängt. Das ist nicht Bequemlichkeit, sondern notwendig: die
/// meisten Mods patchen bestehende Werte, aber eine inhaltserweiternde Mod
/// bringt Zeilen mit, die das Basisspiel nicht hat — und daran zu scheitern
/// (der reine Patch-Entwurf) machte jede solche Mod unbaubar.</para>
///
/// <para>Alle anderen Schlüssel auf oberster Ebene (<c>RowStruct</c>,
/// <c>Defaults</c> und was sonst dort steht) gehen unverändert durch, weil
/// nur <c>Rows</c> angefasst wird.</para></summary>
public static class DataTablePatcher
{
    /// <summary>Wendet <paramref name="row"/> auf <paramref name="baseJson"/>
    /// an und gibt die neue Tabelle als UTF-8-Bytes zurück.</summary>
    public static byte[] ApplyRowPatch(byte[] baseJson, ExmodRow row)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(baseJson, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Basistabelle {row.CurrentFile} ist kein gültiges JSON: {ex.Message}", ex);
        }
        if (parsed is not JsonObject doc)
            throw new InvalidDataException($"Basistabelle {row.CurrentFile}: kein JSON-Objekt.");
        if (doc["Rows"] is not JsonArray rows)
            throw new InvalidDataException(
                $"Basistabelle {row.CurrentFile}: kein Array „Rows“ auf oberster Ebene.");

        // Namensindex einmal aufbauen — eine Tabelle hat bis zu einige
        // tausend Zeilen, und ein Mod setzt oft Dutzende Items.
        var byName = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i] is not JsonObject r)
                throw new InvalidDataException(
                    $"Basistabelle {row.CurrentFile}: Rows[{i}] ist kein Objekt.");
            if (r["Name"]?.GetValue<string>() is string name && name.Length > 0)
                byName[name] = r;
        }

        foreach (var item in row.FileItems)
        {
            if (byName.TryGetValue(item.Name, out var target))
            {
                foreach (var (key, value) in item.Fields)
                    target[key] = value?.DeepClone();
                continue;
            }
            var newRow = new JsonObject { ["Name"] = item.Name };
            foreach (var (key, value) in item.Fields)
                newRow[key] = value?.DeepClone();
            rows.Add(newRow);
            byName[item.Name] = newRow;
        }

        return JsonSerializer.SerializeToUtf8Bytes(doc, SerializerOptions);
    }

    /// <summary>Wie die gepatchte Tabelle ins Pak geschrieben wird.
    ///
    /// <para><b>Ohne Einrückung</b>: die Datei wird nur von der Engine
    /// gelesen, Einrückung würde <c>D_ItemsStatic.json</c> um Megabytes
    /// aufblähen.</para>
    ///
    /// <para><b>Mit entschärftem Escaping</b>, und das ist nachgemessen: mit
    /// der Voreinstellung von <c>System.Text.Json</c> wuchs
    /// <c>D_Traits/D_Itemable.json</c> von 1.863.773 auf 2.103.396 Byte
    /// (+12,9 %), weil Zeichen wie <c>+</c>, <c>&lt;</c> und alles
    /// Nicht-ASCII vorsorglich als <c>\uXXXX</c> geschrieben werden. Das ist
    /// gültiges JSON mit identischem Inhalt — der Vergleich gegen die
    /// Referenz-Implementierung lief über <c>JsonNode.DeepEquals</c> und war
    /// grün —, aber es sind Megabytes ohne Gegenwert in einer Datei, die bei
    /// jedem Spiel-Update neu entsteht. Die Entschärfung ist hier
    /// unbedenklich: das Ergebnis landet in einem Pak und nie in einer
    /// HTML-Seite, wo das aggressive Escaping sein Schutzziel hätte.</para></summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
