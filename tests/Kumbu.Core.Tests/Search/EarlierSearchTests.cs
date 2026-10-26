// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Search;

namespace Kumbu.Core.Tests.Search;

/// <summary>Search in earlier versions: words only, over a note's history, never repeating today's text.</summary>
public class EarlierSearchTests
{
    private static readonly DateTime T0 = new(2026, 10, 26, 9, 0, 0, DateTimeKind.Utc);

    private static VaultPath P(string s) => VaultPath.TryParse(s, out var p) ? p : throw new ArgumentException(s);

    private static StoredNote Note(string path, string content, params HistoryEntry[] history) =>
        new(P(path), content, history.Length + 1, "ada", T0, history);

    private static HistoryEntry H(string content, int hoursAgo, string by = "liz") => new(content, by, T0.AddHours(-hoursAgo));

    [Fact]
    public void Finds_a_word_that_is_only_in_an_earlier_version()
    {
        var old = "# Plan\n\nZebras first.\n";
        var notes = new[] { Note("/W/plan.md", "# Plan\n\nLions now.\n", H(old, 1)) };

        var hit = Assert.Single(EarlierSearch.Find(notes, "zebras", 10));

        Assert.Equal((P("/W/plan.md"), VersionToken.Of(old), T0.AddHours(-1), "liz"), (hit.Path, hit.Version, hit.WrittenAt, hit.Writer));
        Assert.Contains("Zebras first.", hit.Snippet);
        Assert.Equal((0, 3), (hit.StartLine, hit.EndLine));
    }

    [Fact]
    public void A_section_unchanged_today_is_no_earlier_hit()
    {
        var notes = new[]
        {
            Note("/W/plan.md", "# Plan\n\n## Animals\nZebras stay.\n\n## Weather\nSunny.\n", H("# Plan\n\n## Animals\nZebras stay.\n\n## Weather\nRainy.\n", 1)),
        };

        Assert.Empty(EarlierSearch.Find(notes, "zebras", 10));
        Assert.Single(EarlierSearch.Find(notes, "rainy", 10));
    }

    [Fact]
    public void Every_word_must_be_in_the_section()
    {
        var notes = new[] { Note("/W/plan.md", "now\n", H("# A\nZebras here.\n# B\nLions there.\n", 1)) };

        Assert.Empty(EarlierSearch.Find(notes, "zebras lions", 10));
        Assert.Single(EarlierSearch.Find(notes, "ZEBRAS here", 10));
    }

    [Fact]
    public void Newest_version_wins_for_the_same_section()
    {
        var text = "# Plan\nZebras first.\n";
        var notes = new[]
        {
            Note("/W/plan.md", "# Plan\nLions now.\n", H(text, 1, "liz"), H("# Plan\nLions before.\n", 2), H(text, 3, "ada")),
            Note("/W/other.md", "x\n", H("Zebras elsewhere.\n", 5)),
        };

        var hits = EarlierSearch.Find(notes, "zebras", 10);

        Assert.Equal([("/W/plan.md", "liz"), ("/W/other.md", "liz")], hits.Select(h => (h.Path.Value, h.Writer)));
        Assert.Equal(T0.AddHours(-1), hits[0].WrittenAt);
        Assert.Equal(VersionToken.Of("# Plan\nLions before.\n"), hits[0].Before);
        Assert.Null(hits[1].Before);
        Assert.False(hits[1].BeforeLost);
        Assert.Single(EarlierSearch.Find(notes, "zebras", 1));
    }
}
