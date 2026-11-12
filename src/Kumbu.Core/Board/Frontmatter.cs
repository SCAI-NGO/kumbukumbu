// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Kumbu.Core.Board;

/// <summary>A note's frontmatter as plain values; <see cref="Problem"/> is set (and <see cref="Values"/> empty) when the YAML does not parse.</summary>
public sealed record FrontmatterValues(IReadOnlyDictionary<string, object?> Values, string? Problem);

/// <summary>Reads a note's YAML frontmatter and changes single keys without touching any other line.</summary>
public static class Frontmatter
{
    private static readonly IReadOnlyDictionary<string, object?> NoValues = new Dictionary<string, object?>();

    public static FrontmatterValues Read(string content)
    {
        var lines = Lines.Split(content);
        var end = Outline.FrontmatterEnd(lines);
        if (end == 0) return new FrontmatterValues(NoValues, null);

        var yaml = string.Join('\n', lines.Skip(1).Take(end - 2));
        try
        {
            var root = new DeserializerBuilder().WithDuplicateKeyChecking().Build().Deserialize<object?>(yaml);
            return root switch
            {
                null => new FrontmatterValues(NoValues, null),
                IDictionary<object, object?> map => new FrontmatterValues(new Budget().ToMap(map, 0), null),
                _ => new FrontmatterValues(NoValues, "The frontmatter is not a map of keys and values."),
            };
        }
        catch (YamlException e)
        {
            return new FrontmatterValues(NoValues, e.Message);
        }
        catch (TooLarge)
        {
            return new FrontmatterValues(NoValues, "The frontmatter is too large or nested too deeply.");
        }
        catch (KeyTwice e)
        {
            return new FrontmatterValues(NoValues, $"The frontmatter has the key '{e.Key}' twice.");
        }
    }

    /// <summary>Replaces, adds or (Yaml null) removes the given top-level keys; every other line stays as it was.
    /// A key's extent is its line plus the lines indented under it or starting with "- ", and the blank lines between those;
    /// a key written quoted (<c>"column":</c>, <c>'column':</c>) is the same key.</summary>
    public static string Set(string content, IReadOnlyList<(string Key, string? Yaml)> changes)
    {
        var lines = Lines.Split(content).ToList();
        var end = Outline.FrontmatterEnd(lines);
        if (end == 0)
        {
            var created = changes.Where(c => c.Yaml is not null).Select(c => Line(c.Key, c.Yaml!)).ToList();
            if (created.Count == 0) return content;
            return string.Join('\n', ["---", .. created, "---"]) + "\n" + content;
        }

        var close = end - 1;
        foreach (var (key, yaml) in changes)
        {
            var start = lines.FindIndex(1, close - 1, l => IsKeyLine(l, key));
            if (start < 0)
            {
                if (yaml is null) continue;
                lines.Insert(close++, Line(key, yaml));
                continue;
            }

            var stop = start + 1;
            for (var next = stop; next < close; next++)
            {
                if (IsContinuation(lines[next])) stop = next + 1;
                else if (lines[next].Trim().Length > 0) break;
            }

            lines.RemoveRange(start, stop - start);
            close -= stop - start;
            if (yaml is null) continue;
            lines.Insert(start, Line(key, yaml));
            close++;
        }

        return Lines.Join(lines) + (content.EndsWith('\n') ? "\n" : "");
    }

    // A block value starting on the next line ("\n  a: b") leaves no trailing space after the key.
    private static string Line(string key, string yaml) => yaml.StartsWith('\n') ? $"{key}:{yaml}" : $"{key}: {yaml}";

    private static bool IsKeyLine(string line, string key) =>
        new[] { key, $"\"{key}\"", $"'{key}'" }.Any(written =>
            line.StartsWith(written, StringComparison.Ordinal) && line[written.Length..].TrimStart(' ', '\t').StartsWith(':'));

    /// <summary>A non-blank line that belongs to the key above; <see cref="Set"/> takes blank lines between such lines along.</summary>
    private static bool IsContinuation(string line) =>
        line.Trim().Length > 0
        && (line.StartsWith(' ') || line.StartsWith('\t') || line.StartsWith("- ", StringComparison.Ordinal) || line.TrimEnd() == "-");

    private const int MaxValues = 10_000;
    private const int MaxDepth = 64;

    private sealed class TooLarge : Exception;

    private sealed class KeyTwice(string key) : Exception
    {
        public string Key { get; } = key;
    }

    /// <summary>Converts the parsed YAML into plain values under a budget: anchors turn a few lines into a cycle
    /// (<c>a: &amp;a [*a]</c>) or a tenfold expansion per level, and link resolution reads front matter that anyone
    /// with write access can plant. A cycle runs into <see cref="MaxDepth"/>, an expansion into <see cref="MaxValues"/>.</summary>
    private sealed class Budget
    {
        private int values;

        public Dictionary<string, object?> ToMap(IDictionary<object, object?> map, int depth)
        {
            var result = new Dictionary<string, object?>();
            foreach (var (key, value) in map)
            {
                // A key that is a list or a map reads as its type's name, so two of them read alike.
                var name = key.ToString() ?? "";
                if (!result.TryAdd(name, Convert(value, depth + 1))) throw new KeyTwice(name);
            }

            return result;
        }

        private object? Convert(object? value, int depth)
        {
            if (++values > MaxValues || depth > MaxDepth) throw new TooLarge();
            return value switch
            {
                IDictionary<object, object?> map => ToMap(map, depth),
                IList<object?> list => list.Select(item => Convert(item, depth + 1)).ToList(),
                null => null,
                _ => value.ToString(),
            };
        }
    }
}
