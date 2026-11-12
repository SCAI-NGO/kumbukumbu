// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Kumbu.Core.Links;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Tests.Links;

public class NoteAliasesTests
{
    private static VaultPath P(string s) => VaultPath.TryParse(s, out var p) ? p : throw new ArgumentException(s);

    [Theory]
    [InlineData("---\naliases: [Kumbu, K²]\n---\n# K\n", new[] { "Kumbu", "K²" })]
    [InlineData("---\naliases:\n  - Kumbu\n  - K²\n---\n", new[] { "Kumbu", "K²" })]
    [InlineData("---\naliases: Kumbu\n---\n", new[] { "Kumbu" })]
    [InlineData("---\naliases: [2026, true]\n---\n", new[] { "2026", "true" })]
    [InlineData("---\naliases: [a b, {x: 1}, [y], ~, '']\n---\n", new[] { "a b" })]
    [InlineData("---\naliases: [' Kumbu ', kumbu, KUMBU]\n---\n", new[] { "Kumbu" })]
    [InlineData("---\naliases: [a/b, 'a#b', 'a|b', 'a[b', 'a]b', 'a^b', \"a\\nb\", ok]\n---\n", new[] { "ok" })]
    [InlineData("---\naliases:\n---\n", new string[0])]
    [InlineData("---\nalias: Kumbu\n---\n", new string[0])]
    [InlineData("---\nAliases: Kumbu\n---\n", new string[0])]
    [InlineData("---\naliases: [unclosed\n---\n", new string[0])]
    [InlineData("---\ntags: [x]\n---\naliases: [Kumbu]\n", new string[0])]
    [InlineData("aliases: [Kumbu]\n", new string[0])]
    [InlineData("---\nnested:\n  aliases: [Kumbu]\n---\n", new string[0])]
    public void Reads_the_aliases(string content, string[] expected) => Assert.Equal(expected, NoteAliases.Of(content));

    [Fact]
    public void An_alias_is_normalised_to_nfc()
    {
        var nfd = "Überlegung".Normalize(NormalizationForm.FormD);
        Assert.Equal(["Überlegung".Normalize(NormalizationForm.FormC)], NoteAliases.Of($"---\naliases: [{nfd}]\n---\n"));
    }

    [Fact]
    public void Of_notes_keeps_only_notes_with_aliases()
    {
        var map = NoteAliases.Of([new Note(P("/W/a.md"), "---\naliases: [x]\n---\n"), new Note(P("/W/b.md"), "# b\n")]);
        Assert.Equal(["x"], map[P("/W/a.md")]);
        Assert.False(map.ContainsKey(P("/W/b.md")));
    }
}
