// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Graph;
using Kumbu.Core.Notes;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;

namespace Kumbu.Core.Tests.Sh;

public class GraphCommandTests
{
    // The spec's example (§7.1) in small: Plan links Budget and embeds a picture, Budget links a name nobody has, two
    // notes link to Plan, and a hub of five linking notes is capped to three of them.
    private static Shell Spec()
    {
        var store = new InMemoryNoteStore(
            ("/Work/Plan.md", "[[Budget]]\n![[diagram.png]]\n"),
            ("/Work/Budget.md", "[[Anna]]\n"),
            ("/Work/Daily/2026-11-12.md", "[[Plan]]\n"),
            ("/Work/Daily.md", "[[Plan]]\n"),
            ("/Work/Log1.md", "[[Daily]]\n"), ("/Work/Log2.md", "[[Daily]]\n"), ("/Work/Log3.md", "[[Daily]]\n"),
            ("/Work/Log4.md", "[[Daily]]\n"), ("/Work/Log5.md", "[[Daily]]\n"));
        store.AddAttachment("/Work/Pump/diagram.png");
        return TestShell.Of(store);
    }

    private static Task<ShellResult> Run(Shell sh, string line) => sh.RunAsync(line, CancellationToken.None);

    [Fact]
    public async Task Local_graph_prints_as_the_spec_shows()
    {
        var result = await Run(Spec(), "graph /Work/Plan.md --depth 2 --cap 4");
        Assert.False(result.IsError);
        Assert.Equal(
            """
            /Work/Plan.md, depth 2, both directions: 7 notes, 1 attachment, 1 without a note
            1  /Work/Plan.md -> /Work/Budget.md
            1  /Work/Plan.md -> /Work/Pump/diagram.png (embed)
            1  /Work/Daily.md -> /Work/Plan.md
            1  /Work/Daily/2026-11-12.md -> /Work/Plan.md
            2  /Work/Budget.md -> Anna (no note)
            2  /Work/Log1.md -> /Work/Daily.md
            2  /Work/Log2.md -> /Work/Daily.md
            2  /Work/Log3.md -> /Work/Daily.md
               +2 more linking to /Work/Daily.md: graph /Work/Daily.md --in
            """.ReplaceLineEndings("\n"),
            result.Output);
    }

    [Fact]
    public async Task Path_prints_arrows_in_link_direction()
    {
        var sh = TestShell.Of(("/W/A.md", "[[C]]\n"), ("/W/B.md", "[[C]]\n"), ("/W/C.md", "text\n"), ("/W/D.md", "[[A]]\n"));

        Assert.Equal(
            "/W/A.md to /W/B.md, 2 steps, in either direction: 1 path\n/W/A.md -> /W/C.md <- /W/B.md",
            (await Run(sh, "graph --path /W/A.md /W/B.md")).Output);
        Assert.Equal(
            "/W/B.md to /W/A.md, 2 steps, in either direction: 1 path\n/W/B.md -> /W/C.md <- /W/A.md",
            (await Run(sh, "graph --path /W/B.md /W/A.md")).Output);
        Assert.Equal(
            "/W/D.md to /W/C.md, 2 steps, following links as written: 1 path\n/W/D.md -> /W/A.md -> /W/C.md",
            (await Run(sh, "graph --path /W/D.md /W/C.md --out")).Output);
    }

    [Fact]
    public async Task Not_connected_and_farther_than_six_are_told_apart()
    {
        // A chain of eight notes, and one note on its own.
        var notes = Enumerable.Range(1, 8).Select(i => ($"/W/n{i}.md", i < 8 ? $"[[n{i + 1}]]\n" : "end\n")).Append(("/W/island.md", "x\n")).ToArray();
        var sh = TestShell.Of(notes);

        Assert.Equal("/W/n1.md and /W/island.md are not connected at all (no path of any length)", (await Run(sh, "graph --path /W/n1.md /W/island.md")).Output);
        Assert.Equal("/W/n1.md and /W/n8.md are farther than 6 steps apart", (await Run(sh, "graph --path /W/n1.md /W/n8.md")).Output);
        Assert.StartsWith("/W/n1.md to /W/n7.md, 6 steps,", (await Run(sh, "graph --path /W/n1.md /W/n7.md")).Output);

        // Under --out "not connected" would claim too much: the other way round there is a path.
        Assert.Equal("no path from /W/n8.md to /W/n1.md following links as written", (await Run(sh, "graph --path /W/n8.md /W/n1.md --out")).Output);
        Assert.Equal("/W/n1.md and /W/n8.md are farther than 6 steps apart following links as written", (await Run(sh, "graph --path /W/n1.md /W/n8.md --out")).Output);
    }

    [Fact]
    public async Task Orphans_hubs_unresolved_list()
    {
        var sh = TestShell.Of(
            ("/W/hub.md", "[[a]] [[b]] [[ghost one]]\n"), ("/W/a.md", "[[hub]]\n"), ("/W/b.md", "[[hub]] [[ghost one]]\nline two [[Other ghost]]\n"),
            ("/W/alone.md", "x\n"), ("/W/sub/alone2.md", "x\n"), ("/V/other.md", "x\n"));

        Assert.Equal("/V/other.md\n/W/alone.md\n/W/sub/alone2.md", (await Run(sh, "graph --orphans")).Output);
        Assert.Equal("/W/sub/alone2.md", (await Run(sh, "graph --orphans /W/sub")).Output);
        Assert.Equal("(no notes without links)", (await Run(sh, "graph --orphans /W/a.md")).Output);
        Assert.Equal(
            "5  /W/hub.md  (2 in, 3 out)\n4  /W/b.md  (1 in, 3 out)\n2  /W/a.md  (1 in, 1 out)",
            (await Run(sh, "graph --hubs /W -n 3")).Output);
        Assert.Equal(
            """
            ghost one, linked from 2 notes:
              /W/b.md:1
              /W/hub.md:1
            Other ghost, linked from 1 note:
              /W/b.md:2
            """.ReplaceLineEndings("\n"),
            (await Run(sh, "graph --unresolved /W")).Output);
        Assert.Equal("(no unresolved links)", (await Run(sh, "graph --unresolved /W --no-ghosts")).Output);
    }

    [Fact]
    public async Task Hub_at_depth_three_stays_in_budget()
    {
        var pad = new string('x', 90);
        var notes = new List<(string, string)> { ("/Hub/Hub.md", "hub\n") };
        for (var i = 0; i < 400; i++)
        {
            var next = string.Concat(Enumerable.Range(1, 3).Select(k => $"[[{pad}{(i + k) % 400:D3}]] "));
            notes.Add(($"/Hub/{pad}{i:D3}.md", $"[[Hub]] {next}\n"));
        }

        var result = await Run(TestShell.Of([.. notes]), "graph /Hub/Hub.md --depth 3 --cap 100");
        Assert.False(result.IsError);
        Assert.True(result.Output.Length <= OutputLimit.MaxChars, $"{result.Output.Length} chars");
        Assert.DoesNotContain("[truncated", result.Output);
        var last = result.Output.Split('\n')[^1];
        Assert.StartsWith("(", last);
        Assert.Contains("--depth", last);
        Assert.Contains("--tag", last);
        Assert.Contains("go on from /Hub/", last);
        Assert.DoesNotContain("go on from /Hub/Hub.md", last);
        Assert.Equal(result.Output, OutputLimit.Apply(result).Output);
    }

    [Fact]
    public async Task Path_takes_the_filters()
    {
        var sh = TestShell.Of(("/W/A.md", "[[2026-11-12]]\n"), ("/W/2026-11-12.md", "[[B]]\n"), ("/W/B.md", "x\n"));
        Assert.Equal(
            "/W/A.md to /W/B.md, 2 steps, in either direction: 1 path\n/W/A.md -> /W/2026-11-12.md -> /W/B.md",
            (await Run(sh, "graph --path /W/A.md /W/B.md")).Output);
        Assert.Equal("/W/A.md and /W/B.md are not connected at all (no path of any length)", (await Run(sh, "graph --path /W/A.md /W/B.md --no-days")).Output);
    }

    // A links one node of each kind the flags hide: a tagged note, a day note, an archived note, an attachment and a
    // name nobody has.
    private static Shell Kinds()
    {
        var store = new InMemoryNoteStore(
            ("/W/A.md", "#t\n[[B]] [[2026-11-12]] [[Old]] ![[pic.png]] [[Nobody]]\n"),
            ("/W/B.md", "#t\n"), ("/W/2026-11-12.md", "x\n"), ("/W/_archive/Old.md", "x\n"));
        store.AddAttachment("/W/pic.png");
        return TestShell.Of(store);
    }

    [Theory]
    [InlineData("", "/W/2026-11-12.md|/W/B.md|/W/pic.png|Nobody")]
    [InlineData("--no-days", "/W/B.md|/W/pic.png|Nobody")]
    [InlineData("--no-attachments", "/W/2026-11-12.md|/W/B.md|Nobody")]
    [InlineData("--no-ghosts", "/W/2026-11-12.md|/W/B.md|/W/pic.png")]
    [InlineData("--archive", "/W/2026-11-12.md|/W/B.md|/W/_archive/Old.md|/W/pic.png|Nobody")]
    [InlineData("--tag t", "/W/B.md|/W/pic.png|Nobody")]
    public async Task Local_graph_takes_each_filter(string flags, string targets)
    {
        var result = await Run(Kinds(), $"graph /W/A.md --out {flags}");

        Assert.False(result.IsError, result.Output);
        var shown = result.Output.Split('\n').Skip(1)
            .Select(l => l["1  /W/A.md -> ".Length..].Replace(" (embed)", "").Replace(" (no note)", ""))
            .Order(StringComparer.Ordinal);
        Assert.Equal(targets.Split('|'), shown);
    }

    // From A to Z by four routes of two steps: through a day note, an archived note, a tagged note, and (never walked) an
    // attachment and a name nobody has.
    [Theory]
    [InlineData("", "/W/A.md -> /W/2026-11-12.md -> /W/Z.md|/W/A.md -> /W/T.md -> /W/Z.md")]
    [InlineData("--no-days", "/W/A.md -> /W/T.md -> /W/Z.md")]
    [InlineData("--no-attachments", "/W/A.md -> /W/2026-11-12.md -> /W/Z.md|/W/A.md -> /W/T.md -> /W/Z.md")]
    [InlineData("--no-ghosts", "/W/A.md -> /W/2026-11-12.md -> /W/Z.md|/W/A.md -> /W/T.md -> /W/Z.md")]
    [InlineData("--archive", "/W/A.md -> /W/2026-11-12.md -> /W/Z.md|/W/A.md -> /W/T.md -> /W/Z.md|/W/A.md -> /W/_archive/Old.md -> /W/Z.md")]
    [InlineData("--tag t", "/W/A.md -> /W/T.md -> /W/Z.md")]
    public async Task Path_takes_each_filter(string flags, string paths)
    {
        var store = new InMemoryNoteStore(
            ("/W/A.md", "[[2026-11-12]] [[Old]] [[T]] ![[pic.png]] [[Nobody]]\n"),
            ("/W/2026-11-12.md", "[[Z]]\n"), ("/W/_archive/Old.md", "[[Z]]\n"), ("/W/T.md", "#t [[Z]]\n"), ("/W/Z.md", "[[pic.png]] [[Nobody]]\n"));
        store.AddAttachment("/W/pic.png");

        var result = await Run(TestShell.Of(store), $"graph --path /W/A.md /W/Z.md {flags}");

        Assert.False(result.IsError, result.Output);
        Assert.Equal(paths.Split('|'), result.Output.Split('\n').Skip(1).Order(StringComparer.Ordinal));
    }

    // Four notes without links (one tagged, a day note, an archived one, a plain one), two notes that link a name nobody has
    // (one tagged), and a day note linking both.
    [Theory]
    [InlineData("--orphans", "/W/2026-11-12.md|/W/alone.md|/W/plain.md")]
    [InlineData("--orphans --no-days", "/W/alone.md|/W/plain.md")]
    [InlineData("--orphans --archive", "/W/_archive/Old.md|/W/2026-11-12.md|/W/alone.md|/W/plain.md")]
    [InlineData("--orphans --tag t", "/W/alone.md")]
    [InlineData("--hubs -n 2", "2  /W/2026-11-14.md  (0 in, 2 out)|2  /W/t.md  (1 in, 1 out)")]
    [InlineData("--hubs -n 2 --no-days", "2  /W/t.md  (1 in, 1 out)|2  /W/u.md  (1 in, 1 out)")]
    [InlineData("--hubs -n 2 --tag t", "2  /W/t.md  (1 in, 1 out)|0  /W/alone.md  (0 in, 0 out)")]
    [InlineData("--unresolved", "Ghost T, linked from 1 note:|  /W/t.md:2|Ghost U, linked from 1 note:|  /W/u.md:1")]
    [InlineData("--unresolved --tag t", "Ghost T, linked from 1 note:|  /W/t.md:2")]
    public async Task Lists_take_the_filters(string flags, string lines)
    {
        var sh = TestShell.Of(
            ("/W/alone.md", "#t\n"), ("/W/plain.md", "x\n"), ("/W/2026-11-12.md", "x\n"), ("/W/_archive/Old.md", "x\n"),
            ("/W/t.md", "#t\n[[Ghost T]]\n"), ("/W/u.md", "[[Ghost U]]\n"), ("/W/2026-11-14.md", "[[t]] [[u]]\n"));

        var result = await Run(sh, $"graph {flags}");

        Assert.False(result.IsError, result.Output);
        Assert.Equal(lines.Replace('|', '\n'), result.Output);
    }

    [Fact]
    public async Task Hubs_and_unresolved_name_their_own_way_to_narrow()
    {
        var pad = new string('x', 90);
        var notes = Enumerable.Range(0, 400).Select(i => ($"/L/{pad}{i:D3}.md", $"[[{pad}ghost{i:D3}]]\n")).ToArray();
        var sh = TestShell.Of(notes);

        var hubs = (await Run(sh, "graph --hubs -n 500")).Output.Split('\n')[^1];
        Assert.Contains("a smaller -n (now 500)", hubs);
        Assert.Contains("graph --hubs /Work", hubs);
        var unresolved = (await Run(sh, "graph --unresolved")).Output.Split('\n')[^1];
        Assert.Contains("graph --unresolved /Work", unresolved);
        Assert.DoesNotContain("--orphans", unresolved);
    }

    [Theory]
    [InlineData("graph /W/a.md --depth 0", "graph: --depth must be 1 to 3")]
    [InlineData("graph /W/a.md --depth 4", "graph: --depth must be 1 to 3")]
    [InlineData("graph /W/a.md --depth x", "graph: --depth must be 1 to 3")]
    [InlineData("graph /W/a.md --cap 0", "graph: --cap must be 1 to 100")]
    [InlineData("graph /W/a.md --cap 101", "graph: --cap must be 1 to 100")]
    [InlineData("graph /W/a.md --out --in", "graph: --out and --in are exclusive: give one")]
    [InlineData("graph --orphans --hubs", "graph: --path, --orphans, --hubs and --unresolved are exclusive: give one")]
    [InlineData("graph --path /W/a.md", "graph: --path needs two notes")]
    [InlineData("graph --orphans --depth 2", "graph: --depth applies to a local graph only")]
    [InlineData("graph --hubs -n 0", "graph: -n must be 1 or more")]
    [InlineData("graph", "graph: missing operand")]
    [InlineData("graph /W/nope.md", "graph: /W/nope.md: No such file or directory")]
    [InlineData("graph a.md", "graph: a.md: paths must be absolute (there is no working directory)")]
    [InlineData("graph /W/a.md /W/b.md", "graph: too many arguments")]
    [InlineData("graph --orphans /Nowhere", "graph: /Nowhere: No such file or directory")]
    [InlineData("graph --path /W/a.md /X/b.md", "graph: --path: /W/a.md and /X/b.md are in different vaults, and links never cross vaults")]
    public async Task Bad_depth_and_two_modes_are_errors(string line, string message)
    {
        var sh = TestShell.Of(("/W/a.md", "x\n"), ("/W/b.md", "x\n"), ("/X/b.md", "x\n"));
        var result = await Run(sh, line);
        Assert.True(result.IsError);
        Assert.StartsWith(message, result.Output);
    }

    [Theory]
    [InlineData("graph /W/a.md")]
    [InlineData("graph --orphans /W")]
    [InlineData("graph --hubs")]
    [InlineData("graph --unresolved")]
    [InlineData("graph --path /W/a.md /W/b.md")]
    public async Task Tags_are_taken_as_often_as_the_REST_filter_takes_them(string line)
    {
        var sh = TestShell.Of(("/W/a.md", "x\n"), ("/W/b.md", "x\n"));
        var tags = (int n) => string.Concat(Enumerable.Repeat(" --tag t", n));

        Assert.False((await Run(sh, line + tags(GraphFilter.MaxFilters))).IsError);
        var refused = await Run(sh, line + tags(GraphFilter.MaxFilters + 1));
        Assert.True(refused.IsError);
        Assert.Equal($"graph: --tag can be given at most {GraphFilter.MaxFilters} times", refused.Output);
    }

    [Fact]
    public async Task Help_names_every_mode()
    {
        var help = (await Run(TestShell.Of(), "help graph")).Output;
        Assert.StartsWith("graph <note> [--depth N]", help);
        foreach (var mode in new[] { "--path", "--orphans", "--hubs", "--unresolved", "--tag", "--no-ghosts" }) Assert.Contains(mode, help);
        Assert.Contains("graph <note>", Manual.ToolDescription);
    }

    [Fact]
    public async Task Help_says_archive_takes_in_notes_and_attachments()
    {
        var help = System.Text.RegularExpressions.Regex.Replace((await Run(TestShell.Of(), "help graph")).Output, @"\s+", " ");
        Assert.Contains("take in the notes and attachments of the archive (_archive/)", help);
    }
}
