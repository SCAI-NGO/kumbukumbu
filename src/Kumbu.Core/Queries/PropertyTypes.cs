// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kumbu.Core.Queries;

/// <summary>The types a property can be declared with, as Obsidian names them in <c>.obsidian/types.json</c>.</summary>
public enum PropertyType
{
    Text,
    Multitext,
    Number,
    Checkbox,
    Date,
    Datetime,
}

/// <summary>Reads a vault's declared property types. Obsidian declares them for the whole vault, not per note,
/// so a note's <c>birthday</c> is a date because the vault says so, not because of how it is written.</summary>
public static class PropertyTypes
{
    public static readonly IReadOnlyDictionary<string, PropertyType> None = new Dictionary<string, PropertyType>(StringComparer.OrdinalIgnoreCase);

    // Obsidian writes "aliases" and "tags" as types of their own; both are lists of text.
    private static readonly Dictionary<string, PropertyType> Names = new(StringComparer.Ordinal)
    {
        ["text"] = PropertyType.Text,
        ["multitext"] = PropertyType.Multitext,
        ["aliases"] = PropertyType.Multitext,
        ["tags"] = PropertyType.Multitext,
        ["number"] = PropertyType.Number,
        ["checkbox"] = PropertyType.Checkbox,
        ["date"] = PropertyType.Date,
        ["datetime"] = PropertyType.Datetime,
    };

    // Lists of text in every vault, declared or not.
    private static readonly Dictionary<string, PropertyType> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tags"] = PropertyType.Multitext,
        ["aliases"] = PropertyType.Multitext,
        ["cssclasses"] = PropertyType.Multitext,
    };

    /// <summary>The type a property has in a vault with these declarations: its own, else tags, aliases and cssclasses
    /// as lists; null for one that is typed by how its values are written.</summary>
    public static PropertyType? Of(string name, IReadOnlyDictionary<string, PropertyType> types) =>
        types.TryGetValue(name, out var declared) || BuiltIn.TryGetValue(name, out declared) ? declared : null;

    /// <summary>The type of an Obsidian type name ("date"), matched exactly as types.json writes it; null for another name.</summary>
    public static PropertyType? Named(string name) => Names.TryGetValue(name, out var type) ? type : null;

    // The declarations by the SHA-256 of the text they were read from, so each query of an unchanged file gets the same
    // dictionary and a note's typed properties are found by reference, not by comparing every declaration. Bounded: a
    // full cache starts over.
    private const int Remembered = 64;
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, PropertyType>> ByText = new(StringComparer.Ordinal);

    /// <summary>The <c>types</c> of a <c>types.json</c>; a file that isn't one, or a type name Obsidian doesn't have, declares nothing.</summary>
    public static IReadOnlyDictionary<string, PropertyType> Read(string json)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        if (ByText.TryGetValue(key, out var known)) return known;
        if (ByText.Count >= Remembered) ByText.Clear();
        return ByText.GetOrAdd(key, Parse(json));
    }

    private static IReadOnlyDictionary<string, PropertyType> Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("types", out var types)
                || types.ValueKind != JsonValueKind.Object) return None;

            var declared = new Dictionary<string, PropertyType>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in types.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String && Names.TryGetValue(property.Value.GetString()!, out var type))
                    declared[property.Name] = type;
            }

            return declared;
        }
        catch (JsonException)
        {
            return None;
        }
    }
}
