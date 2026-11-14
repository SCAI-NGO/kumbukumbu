// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Calendar;

/// <summary>
/// The calendar's own keys are read ignoring case (<c>When:</c>, <c>TASK:</c>; spec §6.4). <c>due</c> and <c>column</c> are the
/// board's and <c>tags</c> the tags', and those stay exactly as written.
/// </summary>
internal static class CalendarKeys
{
    public static readonly IReadOnlyList<string> Names = ["when", "until", "task", "upcoming", "tz", "closed", "reason"];

    private static readonly HashSet<string> Known = new(Names, StringComparer.OrdinalIgnoreCase);

    /// <summary>The values with each calendar key under its lower-case name and every other key as written. <c>Twice</c> lists each
    /// calendar key written in more than one spelling; its value is then that of the first, and the note's reader must say so.</summary>
    public static (IReadOnlyDictionary<string, object?> Values, IReadOnlyList<string> Twice) Fold(IReadOnlyDictionary<string, object?> values)
    {
        // Most notes write the keys in lower case: nothing to fold, nothing to allocate.
        if (!values.Keys.Any(key => Known.TryGetValue(key, out var name) && name != key)) return (values, []);

        var folded = new Dictionary<string, object?>(values.Count, StringComparer.Ordinal);
        List<string> twice = [];
        foreach (var (key, value) in values)
        {
            var name = Known.TryGetValue(key, out var known) ? known : key;
            if (folded.TryAdd(name, value)) continue;
            if (!twice.Contains(name)) twice.Add(name);
        }

        return (folded, twice);
    }

    /// <summary>The spellings of <paramref name="name"/> in <paramref name="values"/>, in the note's order.</summary>
    public static IReadOnlyList<string> Spellings(IReadOnlyDictionary<string, object?> values, string name) =>
        [.. values.Keys.Where(key => key.Equals(name, StringComparison.OrdinalIgnoreCase))];

    /// <summary>The changes under the note's own spelling: a set writes the spelling the note has (the first, if it has several),
    /// else the lower-case one; a removal removes every spelling.</summary>
    public static IReadOnlyList<(string Key, string? Yaml)> Spelled(IReadOnlyDictionary<string, object?> values, IReadOnlyList<(string Key, string? Yaml)> changes)
    {
        List<(string Key, string? Yaml)> spelled = [];
        foreach (var (key, yaml) in changes)
        {
            var written = Known.Contains(key) ? Spellings(values, key) : [];
            if (written.Count == 0) spelled.Add((key, yaml));
            else if (yaml is null) spelled.AddRange(written.Select(k => (k, (string?)null)));
            else spelled.Add((written[0], yaml));
        }

        return spelled;
    }
}
