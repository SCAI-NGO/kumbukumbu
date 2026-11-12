// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Board;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Sh.Commands;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Sh;

/// <summary>A standing-alone <c>cat</c> appends the notes a note embeds, below a marked line (spec §3).</summary>
public class CatEmbedsTests
{
    private const string Plan = "# Plan\n\nThe pump needs a new controller. ^controller\n\n```mermaid\nflowchart LR\n  Sensor --> Controller --> Pump\n```\n\n![[Ruling#Decision]]\n";
    private const string Ruling = "# Ruling\n\nIntro\n\n## Decision\n\nWe buy the controller.\n\n## Later\n\nx\n";
    private const string Header = "--- embedded notes: shown for reading, not part of this note's text (which ends above) ---";

    private static readonly CancellationToken Ct = CancellationToken.None;

    private static InMemoryNoteStore Store(string plan, params (string Path, string Content)[] more) =>
        new([("/Work/Pump/Plan.md", plan), ("/Work/Pump/Ruling.md", Ruling), .. more]);

    private static Task<ShellResult> Cat(string plan, string line = "cat /Work/Pump/Plan.md", params (string Path, string Content)[] more) =>
        TestShell.Of(Store(plan, more)).RunAsync(line, Ct);

    private static string Appendix(ShellResult result, string content)
    {
        Assert.False(result.IsError, result.Output);
        Assert.StartsWith(content, result.Output, StringComparison.Ordinal);
        return result.Output[content.Length..];
    }

    [Fact]
    public async Task Cat_appends_the_embedded_section()
    {
        var result = await Cat(Plan);

        Assert.Equal(
            Plan + "\n" + Header + "\n[line 10: ![[Ruling#Decision]] → /Work/Pump/Ruling.md#Decision]\n## Decision\n\nWe buy the controller.",
            result.Output);
        Assert.Equal(new NoteVersion("/Work/Pump/Plan.md", VersionToken.Of(Plan)), result.Version);
    }

    [Fact]
    public async Task The_note_text_is_byte_for_byte()
    {
        const string content = "# Plan\r\n\n  trailing  \n![[Ruling]]";
        var result = await Cat(content);

        Assert.StartsWith(content, result.Output, StringComparison.Ordinal);
        // No final line break: a blank line still parts the note from the block.
        Assert.Equal("\n\n" + Header + "\n[line 4: ![[Ruling]] → /Work/Pump/Ruling.md]\n" + Ruling[..^1], Appendix(result, content));
    }

    [Fact]
    public async Task A_missing_embed_says_not_found()
    {
        const string content = "# Plan\n\n![[Missing]]\n";
        Assert.Equal("\n" + Header + "\n[line 3: ![[Missing]] → not found]", Appendix(await Cat(content), content));
    }

    [Fact]
    public async Task An_ambiguous_embed_names_the_candidates()
    {
        const string content = "# Plan\n\n![[Dup]]\n";
        var result = await Cat(content, "cat /Work/Pump/Plan.md", ("/Work/A/Dup.md", "a\n"), ("/Work/B/Dup.md", "b\n"));
        Assert.Equal("\n" + Header + "\n[line 3: ![[Dup]] → ambiguous: /Work/A/Dup.md, /Work/B/Dup.md]", Appendix(result, content));
    }

    [Fact]
    public async Task A_block_embed_shows_the_block()
    {
        const string content = "# Tasks\n\n![[Plan#^controller]]\n![[Plan#^contoller]]\n";
        var result = await Cat(Plan, "cat /Work/Pump/Tasks.md", ("/Work/Pump/Tasks.md", content));
        Assert.Equal(
            "\n" + Header
            + "\n[line 3: ![[Plan#^controller]] → /Work/Pump/Plan.md#^controller]\nThe pump needs a new controller. ^controller"
            + "\n[line 4: ![[Plan#^contoller]] → not found: no block ^contoller in /Work/Pump/Plan.md]",
            Appendix(result, content));
    }

    [Fact]
    public async Task A_missing_section_says_so()
    {
        const string content = "# Plan\n\n![[Ruling#Nope]]\n";
        Assert.Equal("\n" + Header + "\n[line 3: ![[Ruling#Nope]] → not found: no section #Nope in /Work/Pump/Ruling.md]", Appendix(await Cat(content), content));
    }

    [Fact]
    public async Task An_attachment_embed_gets_its_description()
    {
        const string content = "# Plan\n\n![[shot.png]]\n";
        var store = Store(content);
        store.AddAttachment("/Work/Pump/shot.png");

        var result = await TestShell.Of(store).RunAsync("cat /Work/Pump/Plan.md", Ct);

        Assert.Equal("\n" + Header + "\n[line 3: ![[shot.png]] → /Work/Pump/shot.png]\nshot.png: image (PNG), 8 B", Appendix(result, content));
    }

    [Fact]
    public async Task Each_target_once_and_never_the_note_itself()
    {
        const string content = "# Plan\n\n![[Plan]]\n![[Ruling]]\n![[#Plan]]\n\n![[Ruling]] ![[Ruling#Decision]]\n";
        var appendix = Appendix(await Cat(content), content);

        Assert.Equal(
            "\n" + Header + "\n[line 4: ![[Ruling]] → /Work/Pump/Ruling.md]\n" + Ruling[..^1]
            + "\n[line 7: ![[Ruling#Decision]] → /Work/Pump/Ruling.md#Decision]\n## Decision\n\nWe buy the controller.",
            appendix);
    }

    [Fact]
    public async Task One_level_deep()
    {
        const string content = "# Plan\n\n![[Inner]]\n";
        var result = await Cat(content, "cat /Work/Pump/Plan.md", ("/Work/Pump/Inner.md", "inner\n\n![[Other]]\n"), ("/Work/Pump/Other.md", "other text\n"));

        Assert.Equal("\n" + Header + "\n[line 3: ![[Inner]] → /Work/Pump/Inner.md]\ninner\n\n![[Other]]", Appendix(result, content));
    }

    [Fact]
    public async Task An_embed_within_a_sentence_is_not_appended()
    {
        const string content = "# Plan\n\nsee ![[Ruling]] here\n![[Ruling]] and [[Ruling]]\n";
        Assert.Equal(content, (await Cat(content)).Output);
    }

    [Fact]
    public async Task An_embed_in_code_is_not_appended()
    {
        const string content = "# Plan\n\n```\n![[Ruling]]\n```\n\n~~~md\n![[Ruling#Decision]]\n~~~\n";
        Assert.Equal(content, (await Cat(content)).Output);
    }

    [Fact]
    public async Task A_section_appends_only_its_embeds()
    {
        const string content = "# Plan\n\n![[Ruling#Decision]]\n\n# Other\n\n![[Missing]]\n";

        var plan = await Cat(content, "cat \"/Work/Pump/Plan.md#Plan\"");
        Assert.Equal(
            "# Plan\n\n![[Ruling#Decision]]\n\n" + Header + "\n[line 3: ![[Ruling#Decision]] → /Work/Pump/Ruling.md#Decision]\n## Decision\n\nWe buy the controller.",
            plan.Output);

        // Lines count in the whole note, as 'links' prints them.
        var other = await Cat(content, "cat \"/Work/Pump/Plan.md#Other\"");
        Assert.Equal("# Other\n\n![[Missing]]\n\n" + Header + "\n[line 7: ![[Missing]] → not found]", other.Output);
        Assert.Equal(new NoteVersion("/Work/Pump/Plan.md", VersionToken.Of(content)), other.Version);
    }

    [Fact]
    public async Task No_appendix_in_a_pipeline() =>
        Assert.Equal("10", (await Cat(Plan, "cat /Work/Pump/Plan.md | wc -l")).Output);

    [Fact]
    public async Task No_appendix_with_version()
    {
        var result = await Cat(Plan, $"cat --version {VersionToken.Of(Plan)} /Work/Pump/Plan.md");
        Assert.Equal(ShellResult.Ok(Plan), result);
    }

    [Fact]
    public async Task No_appendix_with_checkpoint()
    {
        var store = Store(Plan);
        var time = new FakeTimeProvider(InMemoryNoteStore.Seeded.AddDays(1));
        var writer = new NoteWriter(store, time, new FixedCaller(Writers.Dev));
        var sh = new Shell(store, writer, new NoteMover(store, time, new FixedCaller(Writers.Dev)), new Checkpoints(store, writer, new Plans(), limit: 3));
        Assert.False((await sh.RunAsync("mkcp /Work before", Ct)).IsError);

        Assert.Equal(ShellResult.Ok(Plan), await sh.RunAsync("cat --checkpoint before /Work/Pump/Plan.md", Ct));
    }

    [Fact]
    public async Task No_appendix_for_a_note_without_embeds()
    {
        const string content = "# Plan\n\nsee [[Ruling]]\n";
        Assert.Equal(
            ShellResult.Ok(content) with { Version = new NoteVersion("/Work/Pump/Plan.md", VersionToken.Of(content)) },
            await Cat(content));
    }

    [Fact]
    public async Task A_long_appendix_is_cut_at_an_embed()
    {
        // The note's 5,000 characters and Big's 20,000 pass the output limit together.
        var content = "# Plan\n\n" + new string('p', 5_000) + "\n\n![[Small]]\n![[Big]]\n![[Ruling]]\n";
        var big = "# Big\n" + new string('x', 20_000) + "\n";
        var result = await Cat(content, "cat /Work/Pump/Plan.md", ("/Work/Pump/Small.md", "small\n"), ("/Work/Pump/Big.md", big));

        Assert.Equal(
            "\n" + Header + "\n[line 5: ![[Small]] → /Work/Pump/Small.md]\nsmall"
            + "\n[embedded notes cut here; read the rest with cat \"/Work/Pump/Big.md\"]",
            Appendix(result, content));
    }

    private static string NoteOf(int length, string embed)
    {
        var head = $"# Plan\n\n{embed}\n\n";
        return head + new string('p', length - head.Length - 1) + "\n";
    }

    [Fact]
    public async Task A_note_just_under_the_limit_gets_no_block_rather_than_a_cut_one()
    {
        var content = NoteOf(OutputLimit.MaxChars - 100, "![[Ruling]]");
        var result = await Cat(content);

        Assert.Equal(content, result.Output);
        Assert.DoesNotContain("[truncated", result.Output);
    }

    [Fact]
    public async Task A_cut_line_with_a_long_target_is_measured_whole()
    {
        var name = new string('n', 100);
        var target = $"/Work/{new string('d', 120)}/{name}.md";
        var cut = $"\n[embedded notes cut here; read the rest with cat \"{target}\"]";
        var more = (target, "# Long\n" + new string('x', 5_000) + "\n");
        // Room for the header and the cut line, to the character, below the limit less the notice's room.
        var length = OutputLimit.MaxChars - 200 - 1 - Header.Length - cut.Length;

        var content = NoteOf(length, $"![[{name}]]");
        var result = await Cat(content, "cat /Work/Pump/Plan.md", more);
        Assert.Equal(content + "\n" + Header + cut, result.Output);
        Assert.DoesNotContain("[truncated", result.Output);

        // One character more and the cut line would not fit whole: no block.
        var longer = NoteOf(length + 1, $"![[{name}]]");
        Assert.Equal(longer, (await Cat(longer, "cat /Work/Pump/Plan.md", more)).Output);
    }

    [Fact]
    public async Task An_embed_is_added_only_with_room_for_the_next_cut_line()
    {
        var name = new string('n', 100);
        var target = $"/Work/{new string('d', 120)}/{name}.md";
        var small = new string('s', 100);
        var entry = $"\n[line 3: ![[Small]] → /Work/Pump/Small.md]\n{small}";
        // Small fits, but the cut line naming the long target would not fit after it: the cut comes before Small.
        var content = NoteOf(OutputLimit.MaxChars - 200 - 1 - Header.Length - entry.Length - 10, $"![[Small]]\n![[{name}]]");
        var result = await Cat(content, "cat /Work/Pump/Plan.md", ("/Work/Pump/Small.md", small + "\n"), (target, "long\n"));

        Assert.Equal("\n" + Header + "\n[embedded notes cut here; read the rest with cat \"/Work/Pump/Small.md\"]", Appendix(result, content));
    }

    [Fact]
    public async Task Embeds_as_list_items_and_in_quotes_are_appended()
    {
        const string content = "# Plan\n\n- ![[Small]]\n> ![[Ruling#Decision]]\n> > 1. [x] ![[Other]]\n- see ![[Missing]]\n-![[Missing]]\n";
        var result = await Cat(content, "cat /Work/Pump/Plan.md", ("/Work/Pump/Small.md", "small\n"), ("/Work/Pump/Other.md", "other\n"));

        Assert.Equal(
            "\n" + Header + "\n[line 3: ![[Small]] → /Work/Pump/Small.md]\nsmall"
            + "\n[line 4: ![[Ruling#Decision]] → /Work/Pump/Ruling.md#Decision]\n## Decision\n\nWe buy the controller."
            + "\n[line 5: ![[Other]] → /Work/Pump/Other.md]\nother",
            Appendix(result, content));
    }

    [Fact]
    public async Task A_note_changed_between_the_reads_gets_no_block()
    {
        const string content = "# Plan\n\n![[Ruling]]\n";
        var cat = new Cat(Store(content));
        // A section read from an earlier state of the note: its token is not the stored note's.
        var read = ShellResult.Ok("# Plan\n\n![[Ruling]]") with { Version = new NoteVersion("/Work/Pump/Plan.md", VersionToken.Of("# Plan\n")) };

        Assert.Equal(read, await cat.AppendEmbedsAsync("/Work/Pump/Plan.md#Plan", read, read, Ct));
        var current = read with { Version = new NoteVersion("/Work/Pump/Plan.md", VersionToken.Of(content)) };
        Assert.Contains(Header, (await cat.AppendEmbedsAsync("/Work/Pump/Plan.md#Plan", current, current, Ct)).Output);
    }

    [Fact]
    public async Task A_note_too_long_alone_lists_only_the_embed_lines()
    {
        var content = "# Plan\n\n![[Ruling#Decision]]\n\n## Body\n\n" + string.Join('\n', Enumerable.Repeat(new string('y', 100), 300)) + "\n\n![[Missing]]\n";
        var result = await Cat(content);

        Assert.False(result.IsError, result.Output);
        var footer = result.Output.IndexOf("[note too long for one call", StringComparison.Ordinal);
        Assert.True(footer > 0, result.Output);
        Assert.EndsWith(
            "its outline is above. Read a part: cat \"/Work/Pump/Plan.md#Section\", head -n 40 /Work/Pump/Plan.md, rg -n … /Work/Pump/Plan.md]\n\n" + Header
            + "\n[line 3: ![[Ruling#Decision]] → /Work/Pump/Ruling.md#Decision]\n[line 308: ![[Missing]] → not found]",
            result.Output);
        Assert.DoesNotContain("We buy the controller.", result.Output);
    }

    [Fact]
    public async Task An_embedded_note_written_by_another_user_is_marked()
    {
        // Alice's own shell: plan.md is Bob's write, beet.md her agent's, index.md too.
        var store = SharedVaults.SharedGarten(asMember: false);
        const string index = "# Index\n\n![[plan]]\n![[beet]]\n";
        store.Put(new StoredNote(P("/Garten/index.md"), index, 1, "claude", InMemoryNoteStore.Seeded, [], index, Ref: WriterRef.ForOwner(WriterRef.Agent, "k1")));

        var result = await TestShell.Of(store).RunAsync("cat /Garten/index.md", Ct);

        Assert.Equal(
            "[/Garten/plan.md: last written by another user]\n" + index + "\n" + Header
            + "\n[line 3: ![[plan]] → /Garten/plan.md]\nBeete planen\n[line 4: ![[beet]] → /Garten/beet.md]\nBeete gießen",
            result.Output);
    }

    [Fact]
    public async Task Skills_cat_gets_no_appendix()
    {
        const string skill = "---\nname: pump\ndescription: Plans pumps.\n---\n# Pump\n\n![[ref]]\n";
        var store = new InMemoryNoteStore(("/Skills/pump/SKILL.md", skill), ("/Skills/pump/ref.md", "ref\n")) { SkillsVault = "Skills" };

        var result = await TestShell.Of(store).RunAsync("skills cat pump", Ct);

        Assert.Equal(skill, result.Output);
        // cat itself, standing alone, appends it.
        Assert.Contains(Header, (await TestShell.Of(store).RunAsync("cat /Skills/pump/SKILL.md", Ct)).Output);
    }

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var p) ? p : throw new ArgumentException(path);
}
