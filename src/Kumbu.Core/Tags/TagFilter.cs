// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Tags;

/// <summary>A tag of a listing: the spelling used most often, and how many notes carry it.</summary>
public sealed record TagCount(string Tag, int Count);

/// <summary>Tags over a set of notes, the same for <c>tags</c>, the <c>--tag</c> filters and the REST endpoints, so a
/// checkpoint's notes work as today's do.</summary>
public static class TagFilter
{
    /// <summary>The uses of <paramref name="asked"/> (normalised) in a note, nested tags included.</summary>
    public static IReadOnlyList<TagUse> Uses(Note note, string asked) => [.. TagReader.Of(note.Content).Where(u => TagMatch.Covers(asked, u.Tag))];

    public static bool Carries(Note note, string asked) => TagReader.Of(note.Content).Any(u => TagMatch.Covers(asked, u.Tag));

    /// <summary>Every tag of the notes, ignoring case, with the number of notes that carry it; most notes first, then by
    /// name. A tag shows in the spelling used most often, a tie going to the one met first in path order.</summary>
    public static IReadOnlyList<TagCount> Count(IEnumerable<Note> notes)
    {
        var tags = new Dictionary<string, (int Notes, Dictionary<string, (int Uses, int First)> Spellings)>(StringComparer.OrdinalIgnoreCase);
        var order = 0;
        foreach (var note in notes.OrderBy(n => n.Path.Value, TextOrder.Default))
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var use in TagReader.Of(note.Content))
            {
                if (!tags.TryGetValue(use.Tag, out var tag)) tags[use.Tag] = tag = (0, new Dictionary<string, (int, int)>(StringComparer.Ordinal));
                if (seen.Add(use.Tag)) tags[use.Tag] = tag = (tag.Notes + 1, tag.Spellings);
                tag.Spellings[use.Tag] = tag.Spellings.TryGetValue(use.Tag, out var spelling) ? (spelling.Uses + 1, spelling.First) : (1, order++);
            }
        }

        return [.. tags.Values
            .Select(t => new TagCount(t.Spellings.OrderByDescending(s => s.Value.Uses).ThenBy(s => s.Value.First).First().Key, t.Notes))
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.Tag, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Tag, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The notes that carry every one of <paramref name="asked"/> (with or without '#'). <c>Error</c>, a sentence for
    /// the person or agent who asked, when one can't be a tag or no note below <paramref name="scope"/> carries it: a
    /// misspelt tag says so instead of finding nothing.
    /// </summary>
    public static (IReadOnlyList<Note>? Notes, string? Error) Narrow(IReadOnlyList<Note> notes, IReadOnlyList<string> asked, VaultPath scope)
    {
        IReadOnlyList<Note> result = notes;
        foreach (var text in asked)
        {
            if (Parse(text) is not { } tag) return (null, NotATag(text));
            if (!notes.Any(n => Carries(n, tag))) return (null, Unknown(tag, scope));
            result = [.. result.Where(n => Carries(n, tag))];
        }

        return (result, null);
    }

    /// <summary>The tag as asked for, normalised; null if it can't be one.</summary>
    public static string? Parse(string asked) => TagMatch.Normalise(asked);

    public static string NotATag(string asked) =>
        $"'{asked}' can't be a tag (a tag is letters, digits, _, - and / without spaces, and not only digits)";

    public static string Unknown(string tag, VaultPath scope) => scope.IsRoot
        ? $"no note carries #{tag}; 'tags' lists the tags"
        : $"no note below {scope.Value} carries #{tag}; 'tags {Quote(scope)}' lists the tags";

    private static string Quote(VaultPath path) => path.Value.Contains(' ') ? $"\"{path.Value}\"" : path.Value;
}
