// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Links;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Tests.Links;

/// <summary>A wikilink's bare name that no note or attachment carries leads to the notes whose front matter lists it
/// among their <c>aliases</c>.</summary>
public class AliasResolutionTests
{
    private static VaultPath P(string s) => VaultPath.TryParse(s, out var p) ? p : throw new ArgumentException(s);

    private static readonly string[] Kumbukumbu = ["Kumbu", "K²"];

    /// <summary>A resolver over /W/Projects/Kumbukumbu.md (aliases Kumbu and K²), /W/Diary/day.md and
    /// <paramref name="more"/>.</summary>
    private static LinkResolver Resolver(
        (string Path, string[] Aliases)[]? more = null, string[]? attachments = null)
    {
        (string Path, string[] Aliases)[] notes = [("/W/Projects/Kumbukumbu.md", Kumbukumbu), ("/W/Diary/day.md", []), .. more ?? []];
        var aliases = notes.Where(n => n.Aliases.Length > 0)
            .ToDictionary(n => P(n.Path), n => (IReadOnlyList<string>)n.Aliases);
        return new LinkResolver("W", notes.Select(n => P(n.Path)), attachments?.Select(P), aliases);
    }

    private static string Resolve(LinkResolver resolver, string source, string target)
    {
        var r = resolver.Resolve(P(source), target);
        var answer = r.Kind switch
        {
            LinkKind.Note => r.Target!.Value,
            LinkKind.Attachment => r.Target is { } t ? "attachment " + t.Value : "attachment",
            LinkKind.Ambiguous => "ambiguous: " + string.Join(", ", r.Candidates.Select(c => c.Value).Order()),
            _ => r.Kind.ToString(),
        };
        return r.ViaAlias ? answer + " (alias)" : answer;
    }

    [Theory]
    [InlineData("/W/Diary/day.md", "Kumbu", "/W/Projects/Kumbukumbu.md (alias)")]
    [InlineData("/W/Diary/day.md", "kumbu", "/W/Projects/Kumbukumbu.md (alias)")]
    [InlineData("/W/Diary/day.md", "K²", "/W/Projects/Kumbukumbu.md (alias)")]
    [InlineData("/W/Diary/day.md", "Kumbukumbu", "/W/Projects/Kumbukumbu.md")]
    [InlineData("/W/Diary/day.md", "Kumbu.md", "NotFound")]
    [InlineData("/W/Diary/day.md", "/Kumbu", "NotFound")]
    [InlineData("/W/Diary/day.md", "./Kumbu", "NotFound")]
    [InlineData("/W/Diary/day.md", "Projects/Kumbu", "NotFound")]
    [InlineData("/W/Projects/Kumbukumbu.md", "Kumbu", "/W/Projects/Kumbukumbu.md (alias)")]
    public void Resolves_by_alias_only_for_a_bare_name(string source, string target, string expected) =>
        Assert.Equal(expected, Resolve(Resolver(), source, target));

    [Fact]
    public void A_note_name_beats_an_alias() =>
        Assert.Equal("/W/Ideas/Kumbu.md", Resolve(Resolver([("/W/Ideas/Kumbu.md", [])]), "/W/Diary/day.md", "Kumbu"));

    [Fact]
    public void An_attachment_name_beats_an_alias()
    {
        var resolver = Resolver([("/W/Notes/Screens.md", ["shot.png"])], ["/W/shot.png"]);
        Assert.Equal("attachment /W/shot.png", Resolve(resolver, "/W/Diary/day.md", "shot.png"));
    }

    [Fact]
    public void A_name_ambiguity_stays_one()
    {
        var resolver = Resolver([("/W/a/Kumbu.md", []), ("/W/b/Kumbu.md", [])]);
        Assert.Equal("ambiguous: /W/a/Kumbu.md, /W/b/Kumbu.md", Resolve(resolver, "/W/Diary/day.md", "Kumbu"));
    }

    [Fact]
    public void Two_notes_sharing_an_alias_are_ambiguous()
    {
        var resolver = Resolver([("/W/People/Kumbu Team.md", ["K²"])]);
        Assert.Equal("ambiguous: /W/People/Kumbu Team.md, /W/Projects/Kumbukumbu.md (alias)",
            Resolve(resolver, "/W/Diary/day.md", "K²"));
    }

    [Fact]
    public void The_source_folder_settles_a_shared_alias()
    {
        var resolver = Resolver([("/W/People/Kumbu Team.md", ["K²"])]);
        Assert.Equal("/W/People/Kumbu Team.md (alias)", Resolve(resolver, "/W/People/x.md", "K²"));
    }

    [Fact]
    public void A_living_note_beats_an_archived_one_by_alias()
    {
        var resolver = Resolver([("/W/_archive/Old.md", ["K²"])]);
        Assert.Equal("/W/Projects/Kumbukumbu.md (alias)", Resolve(resolver, "/W/Diary/day.md", "K²"));
        Assert.Equal("ambiguous: /W/_archive/Old.md, /W/Projects/Kumbukumbu.md (alias)",
            Resolve(resolver, "/W/_archive/Sub/x.md", "K²"));
    }

    [Fact]
    public void An_alias_that_looks_like_a_file_name_resolves() =>
        Assert.Equal("/W/Notes/Release.md (alias)",
            Resolve(Resolver([("/W/Notes/Release.md", ["v1.0"])]), "/W/Diary/day.md", "v1.0"));

    [Fact]
    public void Aliases_of_notes_not_given_or_in_another_vault_do_not_count()
    {
        var aliases = new Dictionary<VaultPath, IReadOnlyList<string>>
        {
            [P("/W/Gone.md")] = ["Bob"],
            [P("/Other/x.md")] = ["Carol"],
        };
        var resolver = new LinkResolver("W", [P("/W/Diary/day.md"), P("/Other/x.md")], null, aliases);
        Assert.Equal("NotFound", Resolve(resolver, "/W/Diary/day.md", "Bob"));
        Assert.Equal("NotFound", Resolve(resolver, "/W/Diary/day.md", "Carol"));
    }

    [Fact]
    public void An_alias_equal_to_the_own_name_changes_nothing()
    {
        var aliases = new Dictionary<VaultPath, IReadOnlyList<string>> { [P("/W/Projects/Kumbukumbu.md")] = ["kumbukumbu"] };
        var resolver = new LinkResolver("W", [P("/W/Projects/Kumbukumbu.md"), P("/W/b/Kumbukumbu.md")], null, aliases);
        Assert.Equal("ambiguous: /W/b/Kumbukumbu.md, /W/Projects/Kumbukumbu.md", Resolve(resolver, "/W/Diary/day.md", "Kumbukumbu"));
    }

    private static readonly VaultPath[] Notes =
    [
        P("/W/projects/kumbu.md"), P("/W/inbox.md"), P("/W/a/plan.md"), P("/W/b/plan.md"),
        P("/W/Überlegungen.md"), P("/Other/inbox.md"), P("/W/v1.2 notes.md"),
    ];

    // LinkResolverTests.Resolves, case for case.
    [Theory]
    [InlineData("/W/inbox.md", "kumbu", "/W/projects/kumbu.md")]
    [InlineData("/W/inbox.md", "projects/kumbu", "/W/projects/kumbu.md")]
    [InlineData("/W/inbox.md", "projects/kumbu.md", "/W/projects/kumbu.md")]
    [InlineData("/W/inbox.md", "/projects/kumbu", "/W/projects/kumbu.md")]
    [InlineData("/W/inbox.md", "KUMBU", "/W/projects/kumbu.md")]
    [InlineData("/W/inbox.md", "überlegungen", "/W/Überlegungen.md")]
    [InlineData("/W/inbox.md", "Überlegungen", "/W/Überlegungen.md")]
    [InlineData("/W/inbox.md", "v1.2 notes", "/W/v1.2 notes.md")]
    [InlineData("/W/a/plan.md", "plan", "/W/a/plan.md")]
    [InlineData("/W/b/x.md", "plan", "/W/b/plan.md")]
    [InlineData("/W/inbox.md", "a/plan", "/W/a/plan.md")]
    [InlineData("/W/a/plan.md", "../b/plan", "/W/b/plan.md")]
    [InlineData("/W/projects/kumbu.md", "./kumbu", "/W/projects/kumbu.md")]
    [InlineData("/W/inbox.md", "plan", "ambiguous: /W/a/plan.md, /W/b/plan.md")]
    [InlineData("/W/inbox.md", "nope", "NotFound")]
    [InlineData("/W/inbox.md", "./kumbu", "NotFound")]
    [InlineData("/W/inbox.md", "/kumbu", "NotFound")]
    [InlineData("/W/inbox.md", "../../../etc/passwd", "NotFound")]
    [InlineData("/W/inbox.md", "jects/kumbu", "NotFound")]
    [InlineData("/W/inbox.md", "diagram.png", "Attachment")]
    [InlineData("/W/inbox.md", "", "Self")]
    public void Without_aliases_nothing_changes(string source, string target, string expected)
    {
        var without = new LinkResolver("W", Notes).Resolve(P(source), target);
        var empty = new LinkResolver("W", Notes, null, new Dictionary<VaultPath, IReadOnlyList<string>>());
        var with = empty.Resolve(P(source), target);
        Assert.Equal(expected, with.Kind switch
        {
            LinkKind.Note => with.Target!.Value,
            LinkKind.Ambiguous => "ambiguous: " + string.Join(", ", with.Candidates.Select(c => c.Value).Order()),
            _ => with.Kind.ToString(),
        });
        Assert.Equal((without.Kind, without.Target, without.ViaAlias), (with.Kind, with.Target, with.ViaAlias));
        Assert.Equal(without.Candidates, with.Candidates);
    }

    [Fact]
    public void An_alias_never_takes_a_name_of_a_kind_the_vault_keeps_as_attachments()
    {
        // Without the attachment list (rm, replace, write) [[shot.png]] may name a real file: it stays an attachment.
        var resolver = Resolver([("/W/Shots.md", ["shot.png"])]);
        Assert.Equal("attachment", Resolve(resolver, "/W/Diary/day.md", "shot.png"));
    }

    [Fact]
    public void Many_notes_sharing_many_aliases_index_in_linear_time()
    {
        string[] shared = [.. Enumerable.Range(0, 100).Select(i => $"a{i}")];
        var notes = Enumerable.Range(0, 5000).Select(i => P($"/W/n{i}.md")).ToList();
        var aliases = notes.ToDictionary(n => n, _ => (IReadOnlyList<string>)shared);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var resolver = new LinkResolver("W", notes, null, aliases);
        Assert.True(watch.ElapsedMilliseconds < 3000, $"{watch.ElapsedMilliseconds} ms");
        Assert.Equal(LinkKind.Ambiguous, resolver.Resolve(P("/W/x/y.md"), "a1").Kind);
    }
}
