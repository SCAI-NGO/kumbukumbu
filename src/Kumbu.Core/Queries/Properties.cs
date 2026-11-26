// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Kumbu.Core.Board;
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Kumbu.Core.Queries;

/// <summary>A note's properties, typed; <see cref="Problem"/> is set (and <see cref="Values"/> empty) when the frontmatter does not parse.</summary>
public sealed record NoteProperties(IReadOnlyDictionary<string, Value> Values, string? Problem);

/// <summary>Types a note's frontmatter: a plain scalar reads as what it looks like (YAML 1.2's core schema, and dates)
/// and a quoted one stays text; for a run, a declared type converts a value that fits it cleanly, and any other keeps
/// its own (bases-without-obsidian-bugs spec §2 item 4). The tags property gives its tags with their '#'. The cases are
/// in <c>tests/query-conformance/properties</c>.</summary>
public static partial class Properties
{
    private static readonly IReadOnlyDictionary<string, Value> NoValues = new Dictionary<string, Value>();

    // Parsed properties by the content they were parsed from (ADR 0027): a note's content is the same string for as long
    // as the working set holds it unchanged, so each is parsed once, not once per query; the entry goes with the string.
    private static readonly ConditionalWeakTable<string, NoteProperties> ByContent = [];
    private static readonly ConditionalWeakTable<string, TypedProperties> TypedByContent = [];

    private sealed record TypedProperties(IReadOnlyDictionary<string, PropertyType> Types, NoteProperties Properties);

    /// <summary>The properties as the YAML alone types them, as <c>query properties</c> reports them.</summary>
    public static NoteProperties Read(string content) => ByContent.GetValue(content, c => Parse(c, null));

    /// <summary>The properties as a run reads them, with the vault's declared types.</summary>
    public static NoteProperties Read(string content, IReadOnlyDictionary<string, PropertyType> types)
    {
        if (TypedByContent.TryGetValue(content, out var known) && SameTypes(known.Types, types)) return known.Properties;
        var properties = Parse(content, types);
        TypedByContent.AddOrUpdate(content, new TypedProperties(types, properties));
        return properties;
    }

    // The vault's types.json is read for each query, so the same declarations come as another dictionary.
    private static bool SameTypes(IReadOnlyDictionary<string, PropertyType> a, IReadOnlyDictionary<string, PropertyType> b) =>
        ReferenceEquals(a, b) || (a.Count == b.Count && a.All(t => b.TryGetValue(t.Key, out var other) && other == t.Value));

    private static NoteProperties Parse(string content, IReadOnlyDictionary<string, PropertyType>? types)
    {
        var lines = Lines.Split(content);
        var end = Outline.FrontmatterEnd(lines);
        if (end == 0) return new NoteProperties(NoValues, null);

        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(string.Join('\n', lines.Skip(1).Take(end - 2))));
            return stream.Documents.FirstOrDefault()?.RootNode switch
            {
                null => new NoteProperties(NoValues, null),
                YamlScalarNode { Value: "" or "~" or null, Style: ScalarStyle.Plain } => new NoteProperties(NoValues, null),
                YamlMappingNode map => new NoteProperties(new Budget().Top(map, types), null),
                _ => new NoteProperties(NoValues, "The frontmatter is not a map of keys and values."),
            };
        }
        catch (YamlException e)
        {
            return new NoteProperties(NoValues, e.Message);
        }
        catch (TooLarge)
        {
            return new NoteProperties(NoValues, Frontmatter.TooLargeProblem);
        }
        catch (KeyTwice e)
        {
            return new NoteProperties(NoValues, $"The frontmatter has the key '{e.Key}' twice.");
        }
    }

    private sealed class TooLarge : Exception;

    private sealed class KeyTwice(string key) : Exception
    {
        public string Key { get; } = key;
    }

    /// <summary>Walks the YAML under the frontmatter's budget: anchors turn a few lines into a tenfold expansion per level.</summary>
    private sealed class Budget
    {
        private int values;

        public Dictionary<string, Value> Top(YamlMappingNode map, IReadOnlyDictionary<string, PropertyType>? types)
        {
            var values = Map(map, 0, types is null ? _ => null : name => PropertyTypes.Of(name, types));
            // Obsidian gives the tags property's tags with their '#', as file.tags has them (finding 6).
            if (values.TryGetValue("tags", out var tags)) values["tags"] = Tagged(tags);
            return values;
        }

        private Dictionary<string, Value> Map(YamlMappingNode map, int depth, Func<string, PropertyType?> typeOf)
        {
            var result = new Dictionary<string, Value>();
            foreach (var (key, value) in map.Children)
            {
                var name = key is YamlScalarNode scalar ? scalar.Value ?? "" : key.NodeType.ToString();
                if (!result.TryAdd(name, Convert(value, typeOf(name), depth + 1))) throw new KeyTwice(name);
            }

            return result;
        }

        // Only a property's own value is declared: what a list or a map holds reads as written.
        private Value Convert(YamlNode node, PropertyType? type, int depth)
        {
            if (++values > Frontmatter.MaxValues || depth > Frontmatter.MaxDepth) throw new TooLarge();
            return node switch
            {
                YamlMappingNode map => new ObjectValue(Map(map, depth, _ => null)),
                YamlSequenceNode list => new ListValue(list.Children.Select(item => Convert(item, null, depth + 1)).ToList()),
                YamlScalarNode scalar => Scalar(scalar.Value ?? "", scalar.Style is ScalarStyle.Plain or ScalarStyle.Any, type),
                _ => NullValue.Instance,
            };
        }

        private static Value Tagged(Value value) => value switch
        {
            TextValue { Text.Length: > 0 } t => t.Text.StartsWith('#') ? t : new TextValue("#" + t.Text),
            ListValue l => new ListValue(l.Items.Select(i => i is TextValue ? Tagged(i) : i).ToList()),
            _ => value,
        };
    }

    private static Value Scalar(string raw, bool plain, PropertyType? type)
    {
        if (plain && IsNull(raw)) return NullValue.Instance;
        var converted = type switch
        {
            null => null,
            // A number or a date declared a text is the text it is written as (01010 keeps its zero).
            PropertyType.Text => new TextValue(raw),
            PropertyType.Multitext => new ListValue([new TextValue(raw)]),
            // A plain decimal only: "4,50" and "1e3" may mean something else than the number they'd read as.
            PropertyType.Number => PlainDecimalPattern().IsMatch(raw) ? Decimal(raw) : null,
            PropertyType.Checkbox => raw.Equals("true", StringComparison.OrdinalIgnoreCase) ? new BooleanValue(true)
                : raw.Equals("false", StringComparison.OrdinalIgnoreCase) ? new BooleanValue(false) : null,
            // One date type: a date, with a time or without, is a date of either declared type, as it is written.
            _ => Date(raw) ?? DateTime(raw),
        };
        return converted ?? (plain ? Inferred(raw) : new TextValue(raw));
    }

    /// <summary>Whether a property's value, as a run reads it, is of its declared type; an empty value is of any.</summary>
    public static bool Fits(Value value, PropertyType type) => value is NullValue || type switch
    {
        PropertyType.Text => value is TextValue,
        PropertyType.Multitext => value is ListValue,
        PropertyType.Number => value is NumberValue,
        PropertyType.Checkbox => value is BooleanValue,
        _ => value is DateValue,
    };

    /// <summary>A text as a value of <paramref name="type"/>, as a parameter of that type reads it; null when it doesn't fit.</summary>
    internal static Value? Typed(string raw, PropertyType type) => type switch
    {
        // An argument is anyone's input: a plain decimal number only, no NaN, infinity, hex or octal.
        PropertyType.Number => Decimal(raw),
        PropertyType.Checkbox => Boolean(raw),
        PropertyType.Date => Date(raw) ?? (DateTime(raw) is DateValue t ? new DateValue(t.Date) : null),
        PropertyType.Datetime => DateTime(raw) ?? (Date(raw) is DateValue d ? d with { HasTime = true } : null),
        _ => new TextValue(raw),
    };

    /// <summary>What a plain scalar looks like: a checkbox, a number, a date with or without a time, or else text.</summary>
    internal static Value Inferred(string raw) => Boolean(raw) ?? Number(raw) ?? Date(raw) ?? DateTime(raw) ?? new TextValue(raw);

    // YAML 1.2's core schema, which Obsidian's parser follows (finding 11).
    private static bool IsNull(string raw) => raw is "" or "~" or "null" or "Null" or "NULL";

    private static Value? Boolean(string raw) => raw switch
    {
        "true" or "True" or "TRUE" => new BooleanValue(true),
        "false" or "False" or "FALSE" => new BooleanValue(false),
        _ => null,
    };

    private static Value? Number(string raw) => raw switch
    {
        ".inf" or ".Inf" or ".INF" or "+.inf" or "+.Inf" or "+.INF" => new NumberValue(double.PositiveInfinity),
        "-.inf" or "-.Inf" or "-.INF" => new NumberValue(double.NegativeInfinity),
        ".nan" or ".NaN" or ".NAN" => new NumberValue(double.NaN),
        _ when HexPattern().IsMatch(raw) => new NumberValue((double)BigInteger.Parse("0" + raw[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)),
        _ when OctalPattern().IsMatch(raw) => new NumberValue((double)raw[2..].Aggregate(BigInteger.Zero, (value, digit) => value * 8 + (digit - '0'))),
        _ => Decimal(raw),
    };

    private static Value? Decimal(string raw) =>
        NumberPattern().IsMatch(raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? new NumberValue(n) : null;

    private static Value? Date(string raw) =>
        DatePattern().IsMatch(raw) && DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? new DateValue(d) : null;

    private static Value? DateTime(string raw)
    {
        var match = DateTimePattern().Match(raw);
        if (!match.Success) return null;
        var text = $"{match.Groups["date"].Value}T{match.Groups["time"].Value}" + (match.Groups["seconds"].Success ? "" : ":00");
        return System.DateTime.TryParseExact(text, ["yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? new DateValue(t, true)
            : null;
    }

    [GeneratedRegex(@"^[-+]?(\d+(\.\d*)?|\.\d+)([eE][-+]?\d+)?$")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"^[-+]?[0-9]+(\.[0-9]+)?\z")]
    private static partial Regex PlainDecimalPattern();

    [GeneratedRegex(@"^0x[0-9a-fA-F]+\z")]
    private static partial Regex HexPattern();

    [GeneratedRegex(@"^0o[0-7]+\z")]
    private static partial Regex OctalPattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"^(?<date>\d{4}-\d{2}-\d{2})[T ](?<time>\d{2}:\d{2}(?<seconds>:\d{2}(\.\d{1,7})?)?)$")]
    private static partial Regex DateTimePattern();
}
