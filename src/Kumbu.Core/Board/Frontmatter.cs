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
                IDictionary<object, object?> map => new FrontmatterValues(ToMap(map), null),
                _ => new FrontmatterValues(NoValues, "The frontmatter is not a map of keys and values."),
            };
        }
        catch (YamlException e)
        {
            return new FrontmatterValues(NoValues, e.Message);
        }
    }

    /// <summary>Replaces, adds or (Yaml null) removes the given top-level keys; every other line stays as it was.
    /// A key's extent is its line plus the lines indented under it or starting with "- ".</summary>
    public static string Set(string content, IReadOnlyList<(string Key, string? Yaml)> changes)
    {
        var lines = Lines.Split(content).ToList();
        var end = Outline.FrontmatterEnd(lines);
        if (end == 0)
        {
            var created = changes.Where(c => c.Yaml is not null).Select(c => $"{c.Key}: {c.Yaml}").ToList();
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
                lines.Insert(close++, $"{key}: {yaml}");
                continue;
            }

            var stop = start + 1;
            while (stop < close && IsContinuation(lines[stop])) stop++;
            lines.RemoveRange(start, stop - start);
            close -= stop - start;
            if (yaml is null) continue;
            lines.Insert(start, $"{key}: {yaml}");
            close++;
        }

        return Lines.Join(lines) + (content.EndsWith('\n') ? "\n" : "");
    }

    private static bool IsKeyLine(string line, string key) =>
        line.StartsWith(key, StringComparison.Ordinal) && line[key.Length..].TrimStart(' ', '\t').StartsWith(':');

    private static bool IsContinuation(string line) =>
        line.StartsWith(' ') || line.StartsWith('\t') || line.StartsWith("- ", StringComparison.Ordinal) || line.TrimEnd() == "-";

    private static Dictionary<string, object?> ToMap(IDictionary<object, object?> map) =>
        map.ToDictionary(p => p.Key.ToString() ?? "", p => Convert(p.Value));

    private static object? Convert(object? value) => value switch
    {
        IDictionary<object, object?> map => ToMap(map),
        IList<object?> list => list.Select(Convert).ToList(),
        null => null,
        _ => value.ToString(),
    };
}
