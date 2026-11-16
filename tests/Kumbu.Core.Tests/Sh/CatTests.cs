// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Sh;
using Kumbu.Core.Sh.Commands;
using Kumbu.Core.Tests.Fakes;

namespace Kumbu.Core.Tests.Sh;

public class CatTests
{
    private const string Content = "# Title\r\n\n  trailing  \n";

    private static readonly InMemoryNoteStore Reader = new(("/Work/a.md", Content), ("/Work/projects/x.md", "X"));

    private static Task<ShellResult> Run(params string[] args) =>
        new Cat(Reader).RunAsync(args, null, CancellationToken.None);

    [Fact]
    public void NameIsCat() => Assert.Equal("cat", new Cat(Reader).Name);

    [Fact]
    public async Task ReturnsContentVerbatimWithItsVersion() => Assert.Equal(
        ShellResult.Ok(Content) with { Version = new NoteVersion("/Work/a.md", VersionToken.Of(Content)) },
        await Run("/Work/a.md"));

    [Fact]
    public async Task MissingOperand() => Assert.Equal(ShellResult.Error("cat: missing operand"), await Run());

    private static Task<ShellResult> RunOn(InMemoryNoteStore store, params string[] args) =>
        new Cat(store).RunAsync(args, null, CancellationToken.None);

    [Fact]
    public async Task Several_notes_print_under_headers_with_a_version_each()
    {
        var store = new InMemoryNoteStore(("/Demo/a.md", "one\n"), ("/Demo/b.md", "two\n"));

        var result = await RunOn(store, "/Demo/a.md", "/Demo/b.md");

        Assert.False(result.IsError);
        Assert.Equal("==> /Demo/a.md <==\none\n\n==> /Demo/b.md <==\ntwo\n", result.Output);
        Assert.Null(result.Version);
        Assert.Equal(
            [new NoteVersion("/Demo/a.md", VersionToken.Of("one\n")), new NoteVersion("/Demo/b.md", VersionToken.Of("two\n"))],
            result.Versions);
    }

    [Fact]
    public async Task A_missing_path_is_reported_in_place_and_the_rest_printed()
    {
        var store = new InMemoryNoteStore(("/Demo/a.md", "one\n"));

        var result = await RunOn(store, "/Demo/a.md", "/Demo/nope.md");

        Assert.False(result.IsError);
        Assert.Contains("cat: /Demo/nope.md: No such file or directory", result.Output, StringComparison.Ordinal);
        Assert.Contains("one", result.Output, StringComparison.Ordinal);
        Assert.Single(result.Versions!);
    }

    [Fact]
    public async Task All_paths_missing_is_an_error()
    {
        var store = new InMemoryNoteStore(("/Demo/a.md", "one\n"));

        var result = await RunOn(store, "/Demo/x.md", "/Demo/y.md");

        Assert.True(result.IsError);
        Assert.Contains("cat: /Demo/x.md: No such file or directory", result.Output, StringComparison.Ordinal);
        Assert.Contains("cat: /Demo/y.md: No such file or directory", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_note_past_the_budget_prints_its_outline()
    {
        var big = "# Big\n\n" + new string('x', 15_000) + "\n\n## Part\n\ntext\n";
        var store = new InMemoryNoteStore(("/Demo/a.md", big), ("/Demo/b.md", big));

        var result = await RunOn(store, "/Demo/a.md", "/Demo/b.md");

        Assert.False(result.IsError);
        Assert.Equal(1, Occurrences(result.Output, "[outline only: too long for this call; read it alone or by section]"));
        Assert.True(result.Output.IndexOf("[outline only:", StringComparison.Ordinal) > result.Output.IndexOf("==> /Demo/b.md <==", StringComparison.Ordinal));
        Assert.Contains("## Part", result.Output, StringComparison.Ordinal);
        Assert.True(result.Output.Length <= OutputLimit.MaxChars);
        Assert.Equal(2, result.Versions!.Count);
    }

    private const int Budget = OutputLimit.MaxChars - 200;

    [Fact]
    public async Task A_second_note_that_leaves_almost_no_room_prints_a_marker_without_crashing()
    {
        // The first note leaves ~50 chars: no room for the second's outline.
        var first = "# A\n\n" + new string('x', Budget - 150) + "\n";
        var store = new InMemoryNoteStore(("/Demo/a.md", first), ("/Demo/b.md", "# B\n\n" + new string('y', 5000) + "\n"));

        var result = await RunOn(store, "/Demo/a.md", "/Demo/b.md");

        Assert.False(result.IsError);
        Assert.True(result.Output.Length <= Budget);
        Assert.Contains("==> /Demo/b.md <==", result.Output, StringComparison.Ordinal);
        Assert.Contains("[too long for this call; read it alone: cat /Demo/b.md]", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("yyyy", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_section_too_long_for_the_budget_prints_a_marker_not_text_or_an_outline()
    {
        var store = new InMemoryNoteStore(
            ("/Demo/a.md", "# A\n\n" + new string('x', Budget - 400) + "\n"),
            ("/Demo/b.md", "# B\n\n## Long\n\n" + new string('z', 3000) + "\n\n## Other\n\nshort\n"));

        var result = await RunOn(store, "/Demo/a.md", "/Demo/b.md#Long");

        Assert.Contains("==> /Demo/b.md#Long <==", result.Output, StringComparison.Ordinal);
        Assert.Contains("[too long for this call; read it alone: cat \"/Demo/b.md#Long\"]", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("zzzz", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("[outline only", result.Output, StringComparison.Ordinal);
        Assert.True(result.Output.Length <= Budget);
    }

    [Fact]
    public async Task A_text_file_too_long_for_the_budget_prints_a_marker()
    {
        var store = new InMemoryNoteStore(("/Demo/a.md", "# A\n\n" + new string('x', Budget - 400) + "\n"));
        store.AddTextFile("/Skills/s/run.py", new string('p', 3000) + "\n");

        var result = await RunOn(store, "/Demo/a.md", "/Skills/s/run.py");

        Assert.Contains("[too long for this call; read it alone: cat /Skills/s/run.py]", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("pppp", result.Output, StringComparison.Ordinal);
        Assert.True(result.Output.Length <= Budget);
    }

    [Fact]
    public async Task Many_paths_fill_the_budget_and_the_rest_is_counted()
    {
        var store = new InMemoryNoteStore(("/Demo/a.md", "one\n"));
        var args = new[] { "/Demo/a.md" }.Concat(Enumerable.Range(0, 800).Select(i => $"/Demo/missing{i}.md")).ToArray();

        var result = await RunOn(store, args);

        Assert.False(result.IsError);
        Assert.Matches(@"\[\d+ more paths not read: this call is full; read them in another call\]", result.Output);
        Assert.True(result.Output.Length <= Budget);
    }

    [Fact]
    public async Task One_path_left_out_is_counted_in_the_singular()
    {
        // a.md fills the call up to the room kept for the count; b.md, with its long name, fits not even as a marker.
        var header = "==> /Demo/a.md <==\n";
        var b = "/Demo/" + new string('b', 120) + ".md";
        var store = new InMemoryNoteStore(("/Demo/a.md", new string('x', Budget - 100 - header.Length - 1) + "\n"), (b, new string('y', 5000) + "\n"));

        var result = await RunOn(store, "/Demo/a.md", b);

        Assert.False(result.IsError);
        Assert.EndsWith("\n[1 more path not read: this call is full; read it in another call]\n", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_sections_of_one_note_get_distinct_headers()
    {
        var store = new InMemoryNoteStore(("/Demo/a.md", "# T\n\n## X\n\nxx\n\n## Y\n\nyy\n"));

        var result = await RunOn(store, "/Demo/a.md#X", "/Demo/a.md#Y");

        Assert.Contains("==> /Demo/a.md#X <==", result.Output, StringComparison.Ordinal);
        Assert.Contains("==> /Demo/a.md#Y <==", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task All_paths_failing_on_a_split_path_carries_the_quote_hint()
    {
        var result = await Run("/Work/My", "Note.md");

        Assert.True(result.IsError);
        Assert.EndsWith(CommandArgs.QuoteHint, result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Version_takes_one_path() =>
        Assert.Equal(ShellResult.Error("cat: --version takes one path"), await Run("--version", "abc", "/Work/a.md", "/Work/projects/x.md"));

    private static int Occurrences(string text, string part) =>
        (text.Length - text.Replace(part, "", StringComparison.Ordinal).Length) / part.Length;

    [Fact]
    public async Task NotFound() =>
        Assert.Equal(ShellResult.Error("cat: /Work/nope.md: No such file or directory"), await Run("/Work/nope.md"));

    [Theory]
    [InlineData("/Work/projects", "/Work/projects")]
    [InlineData("/Work", "/Work")]
    [InlineData("/", "/")]
    [InlineData("/Work/projects/", "/Work/projects")]
    public async Task IsADirectory(string arg, string shown) =>
        Assert.Equal(ShellResult.Error($"cat: {shown}: Is a directory"), await Run(arg));

    [Fact]
    public async Task RelativePath() =>
        Assert.Equal(ShellResult.Error("cat: a.md: paths must be absolute (there is no working directory)"), await Run("a.md"));

    [Fact]
    public async Task InvalidOption() =>
        Assert.Equal(ShellResult.Error("cat: invalid option -- 'n'"), await Run("-n", "/Work/a.md"));

    [Fact]
    public async Task A_note_last_written_by_another_user_is_marked()
    {
        // Alice's own shell: Bob's write (an empty label, a member's reference) is marked.
        var alice = TestShell.Of(SharedVaults.SharedGarten(asMember: false));

        var result = await alice.RunAsync("cat /Garten/plan.md", CancellationToken.None);

        Assert.Equal("[/Garten/plan.md: last written by another user]\nBeete planen\n", result.Output);
        // In a pipeline the mark stays out of what the pipe passes on.
        Assert.Equal("[/Garten/plan.md: last written by another user]\n1", (await alice.RunAsync("cat /Garten/plan.md | wc -l", CancellationToken.None)).Output);
        // As Bob, the owner's agent's write is another user's.
        var bob = SharedVaults.AsBob(SharedVaults.SharedGarten(asMember: true));
        Assert.StartsWith("[/Garten/beet.md: last written by another user]\n", (await bob.RunAsync("cat /Garten/beet.md", CancellationToken.None)).Output);
        Assert.StartsWith("[/Garten/plan.md: last written by another user]\n", (await bob.RunAsync("cat /Garten/plan.md", CancellationToken.None)).Output);
    }

    [Fact]
    public async Task The_callers_own_agents_writes_are_not_marked()
    {
        // Alice's agent wrote beet.md under its own label: hers.
        Assert.Equal("Beete gießen\n", (await TestShell.Of(SharedVaults.SharedGarten(asMember: false)).RunAsync("cat /Garten/beet.md", CancellationToken.None)).Output);
        // Bob's agent's write stores the empty label, as every member's: his by the writer id alone.
        Assert.Equal("Beete jäten\n", (await SharedVaults.AsBob(SharedVaults.SharedGarten(asMember: true)).RunAsync("cat /Garten/own.md", CancellationToken.None)).Output);
        // A vault nobody else reaches marks nothing.
        var alone = SharedVaults.SharedGarten(asMember: false);
        alone.Infos.Clear();
        Assert.Equal("Beete planen\n", (await TestShell.Of(alone).RunAsync("cat /Garten/plan.md", CancellationToken.None)).Output);
    }

    private const string Plan = "# Plan\n\nThe pump needs a new controller. ^controller\n\n## Next\n";

    private static Task<ShellResult> RunPlan(string arg) =>
        new Cat(new InMemoryNoteStore(("/Work/Pump/Plan.md", Plan))).RunAsync([arg], null, CancellationToken.None);

    [Fact]
    public async Task A_block_prints_with_its_marker() => Assert.Equal(
        ShellResult.Ok("The pump needs a new controller. ^controller") with { Version = new NoteVersion("/Work/Pump/Plan.md", VersionToken.Of(Plan)) },
        await RunPlan("/Work/Pump/Plan.md#^controller"));

    [Fact]
    public async Task An_unknown_block_is_an_error() => Assert.Equal(
        ShellResult.Error("cat: /Work/Pump/Plan.md#^contoller: no such block (a block id is written as ^contoller at the end of a paragraph, item or heading)"),
        await RunPlan("/Work/Pump/Plan.md#^contoller"));

    [Fact]
    public async Task A_percent_encoded_caret_names_a_block() => Assert.Equal(
        ShellResult.Ok("The pump needs a new controller. ^controller") with { Version = new NoteVersion("/Work/Pump/Plan.md", VersionToken.Of(Plan)) },
        await RunPlan("/Work/Pump/Plan.md#%5Econtroller"));

    [Fact]
    public async Task A_section_still_prints_as_before() => Assert.Equal(
        ShellResult.Ok("## Next") with { Version = new NoteVersion("/Work/Pump/Plan.md", VersionToken.Of(Plan)) },
        await RunPlan("/Work/Pump/Plan.md#Next"));
}
