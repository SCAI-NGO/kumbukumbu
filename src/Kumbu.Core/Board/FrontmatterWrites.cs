// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Board;

/// <summary>Writing the server's own frontmatter keys, for the board and the calendar alike: changed through
/// <see cref="Frontmatter.Set"/>, checked before anything is written.</summary>
internal static class FrontmatterWrites
{
    /// <summary>The note's content with the server's own keys changed, when that leaves frontmatter that parses, reads each of
    /// those keys back as written and fits the note limit; otherwise why not (prefixed with <paramref name="tool"/>), and
    /// nothing is to be written.</summary>
    public static bool TryOwnedKeys(
        string tool, VaultPath path, string content, IReadOnlyList<(string Key, string? Yaml)> changes, out string written, out ShellResult error)
    {
        error = ShellResult.Ok("");
        written = Frontmatter.Set(content, changes);
        var read = Frontmatter.Read(written);
        if (read.Problem is not null)
        {
            error = Error(tool, $"{path.Value}: changing {string.Join(", ", changes.Select(c => c.Key))} would leave frontmatter that does not parse, "
                + $"so nothing was written; fix the note with edit: {read.Problem}", ShellErrorKind.Conflict);
            return false;
        }

        foreach (var (key, yaml) in changes)
        {
            var wanted = yaml is null ? null : Frontmatter.Read($"---\n{key}: {yaml}\n---\n").Values.GetValueOrDefault(key);
            var found = read.Values.TryGetValue(key, out var value);
            if (yaml is null ? !found : found && Same(wanted, value)) continue;
            error = Error(tool, $"{path.Value}: {key} would not read back as written, so nothing was written; fix the note's frontmatter with edit",
                ShellErrorKind.Conflict);
            return false;
        }

        var bytes = Encoding.UTF8.GetByteCount(written);
        if (bytes <= NoteWriter.MaxBytes) return true;
        error = Error(tool, $"{path.Value} would be {bytes} bytes; the limit is {NoteWriter.MaxBytes} (1 MiB)", ShellErrorKind.Invalid);
        return false;

        static bool Same(object? a, object? b) => (a, b) switch
        {
            (IReadOnlyList<object?> x, IReadOnlyList<object?> y) => x.Count == y.Count && x.Zip(y).All(p => Same(p.First, p.Second)),
            (IReadOnlyDictionary<string, object?> x, IReadOnlyDictionary<string, object?> y) =>
                x.Count == y.Count && x.All(p => y.TryGetValue(p.Key, out var v) && Same(p.Value, v)),
            _ => Equals(a, b),
        };
    }

    private static readonly HashSet<string> YamlWords =
        new(["null", "~", "true", "false", "yes", "no", "on", "off", "y", "n"], StringComparer.OrdinalIgnoreCase);

    /// <summary>A one-line YAML scalar: plain when it reads back as the same string, otherwise double-quoted. A value that
    /// starts with a digit is quoted unless it is a day, so that no reader (Obsidian's) takes a rank or tag for a number.</summary>
    public static string Scalar(string value)
    {
        var plain = value.Length > 0 && char.IsLetterOrDigit(value[0]) && value[^1] != ' ' && !YamlWords.Contains(value)
            && value.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.')
            && (!char.IsAsciiDigit(value[0]) || DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
        if (plain) return value;

        var quoted = new StringBuilder("\"");
        foreach (var c in value)
        {
            quoted.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\t' => "\\t",
                < ' ' => $"\\x{(int)c:X2}",
                _ => c.ToString(),
            });
        }

        return quoted.Append('"').ToString();
    }

    private static ShellResult Error(string tool, string message, ShellErrorKind kind) =>
        ShellResult.Error($"{tool}: {message}") with { Kind = kind };
}
