// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using Kumbu.Core.Board;
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Kumbu.Core.Queries;

/// <summary>A note's properties, typed; <see cref="Problem"/> is set (and <see cref="Values"/> empty) when the frontmatter does not parse.</summary>
public sealed record NoteProperties(IReadOnlyDictionary<string, Value> Values, string? Problem);

/// <summary>Types a note's frontmatter. A declared type wins over how a value is written; without one, a plain scalar
/// reads as what it looks like and a quoted one stays text; a value that doesn't fit its type stays the text it is.
/// The cases are in <c>tests/query-conformance/properties</c>.</summary>
public static partial class Properties
{
    private static readonly IReadOnlyDictionary<string, Value> NoValues = new Dictionary<string, Value>();

    // Lists of text in every vault, declared or not.
    private static readonly Dictionary<string, PropertyType> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tags"] = PropertyType.Multitext,
        ["aliases"] = PropertyType.Multitext,
        ["cssclasses"] = PropertyType.Multitext,
    };

    public static NoteProperties Read(string content, IReadOnlyDictionary<string, PropertyType> types)
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

        public Dictionary<string, Value> Top(YamlMappingNode map, IReadOnlyDictionary<string, PropertyType> types) =>
            Map(map, 0, key => types.TryGetValue(key, out var declared) || BuiltIn.TryGetValue(key, out declared) ? declared : null);

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

        private Value Convert(YamlNode node, PropertyType? type, int depth)
        {
            if (++values > Frontmatter.MaxValues || depth > Frontmatter.MaxDepth) throw new TooLarge();
            return node switch
            {
                YamlMappingNode map => new ObjectValue(Map(map, depth, _ => null)),
                YamlSequenceNode list => new ListValue(list.Children
                    .Select(item => Convert(item, type == PropertyType.Multitext ? PropertyType.Text : null, depth + 1)).ToList()),
                YamlScalarNode scalar => Scalar(scalar.Value ?? "", scalar.Style is ScalarStyle.Plain or ScalarStyle.Any, type),
                _ => NullValue.Instance,
            };
        }
    }

    private static Value Scalar(string raw, bool plain, PropertyType? type)
    {
        if (plain && IsNull(raw)) return NullValue.Instance;
        return type switch
        {
            null => plain ? Inferred(raw) : new TextValue(raw),
            PropertyType.Text => new TextValue(raw),
            PropertyType.Multitext => new ListValue([new TextValue(raw)]),
            PropertyType.Number => Number(raw) ?? new TextValue(raw),
            PropertyType.Checkbox => Boolean(raw) ?? new TextValue(raw),
            PropertyType.Date => Date(raw) ?? (DateTime(raw) is DateTimeValue t ? new DateValue(DateOnly.FromDateTime(t.DateTime)) : new TextValue(raw)),
            PropertyType.Datetime => DateTime(raw) ?? (Date(raw) is DateValue d ? new DateTimeValue(d.Date.ToDateTime(TimeOnly.MinValue)) : new TextValue(raw)),
            _ => new TextValue(raw),
        };
    }

    /// <summary>What a plain scalar looks like: a checkbox, a number, a date, a datetime, or else text.</summary>
    internal static Value Inferred(string raw) => Boolean(raw) ?? Number(raw) ?? Date(raw) ?? DateTime(raw) ?? new TextValue(raw);

    // YAML 1.2's core schema, which Obsidian's parser follows; hex, octal and infinities are left as text, as a person meant them.
    private static bool IsNull(string raw) => raw is "" or "~" or "null" or "Null" or "NULL";

    private static Value? Boolean(string raw) => raw switch
    {
        "true" or "True" or "TRUE" => new BooleanValue(true),
        "false" or "False" or "FALSE" => new BooleanValue(false),
        _ => null,
    };

    private static Value? Number(string raw) =>
        NumberPattern().IsMatch(raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? new NumberValue(n) : null;

    private static Value? Date(string raw) =>
        DatePattern().IsMatch(raw) && DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? new DateValue(d) : null;

    private static Value? DateTime(string raw)
    {
        var match = DateTimePattern().Match(raw);
        if (!match.Success) return null;
        var text = $"{match.Groups["date"].Value}T{match.Groups["time"].Value}" + (match.Groups["seconds"].Success ? "" : ":00");
        return System.DateTime.TryParseExact(text, ["yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? new DateTimeValue(t)
            : null;
    }

    [GeneratedRegex(@"^[-+]?(\d+(\.\d*)?|\.\d+)([eE][-+]?\d+)?$")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"^(?<date>\d{4}-\d{2}-\d{2})[T ](?<time>\d{2}:\d{2}(?<seconds>:\d{2}(\.\d{1,7})?)?)$")]
    private static partial Regex DateTimePattern();
}
