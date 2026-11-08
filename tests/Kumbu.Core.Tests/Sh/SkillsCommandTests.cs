// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Skills;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Sh;

/// <summary><c>skills ls|cat|check|copy</c>: it reads and checks skills, and copies a built-in one; nothing else writes.</summary>
public class SkillsCommandTests
{
    private const string WriteAsAlice = "---\nname: write-as-alice\ndescription: Writes posts in Alice's voice.\n---\n# Write\n";

    private readonly InMemoryNoteStore _store = new();
    private readonly HashSet<VaultPath> _updates = [];

    public SkillsCommandTests()
    {
        _store.SkillsVault = "Skills";
        Put("/Skills/write-as-alice/SKILL.md", WriteAsAlice);
        Put("/Skills/write-as-alice/examples/post.md", "# A post\n");
        Put("/Work/_skills/release-notes/SKILL.md", "---\nname: release-notes\ndescription: Drafts release notes.\n---\n# Notes\n");
        Put("/Work/plan.md", "# Plan\n");
    }

    private sealed class Updates(IReadOnlySet<VaultPath> folders) : ISkillUpdates
    {
        public Task<IReadOnlySet<VaultPath>> AvailableAsync(CancellationToken ct) => Task.FromResult(folders);
    }

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var p) ? p : throw new ArgumentException(path);

    private void Put(string path, string content) => Put(P(path), content);

    private void Put(VaultPath path, string content) =>
        _store.Put(new StoredNote(path, content, 1, "claude", InMemoryNoteStore.Seeded, [], content, Ref: WriterRef.ForOwner(WriterRef.Agent, "k1")));

    private Task<ShellResult> Run(string line, ICaller? caller = null)
    {
        var time = new FakeTimeProvider(InMemoryNoteStore.Seeded.AddDays(1));
        caller ??= new FixedCaller(Writers.Dev);
        var shell = new Shell(_store, new NoteWriter(_store, time, caller), new NoteMover(_store, time, caller), updates: new Updates(_updates));
        return shell.RunAsync(line, CancellationToken.None);
    }

    [Fact]
    public async Task Ls_lists_every_visible_skill()
    {
        var result = await Run("skills ls");

        Assert.False(result.IsError, result.Output);
        var lines = result.Output.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("create-skill (system): ", lines[0]);
        Assert.Equal("write-as-alice (/Skills/write-as-alice, last changed by claude): Writes posts in Alice's voice.", lines[1]);
        Assert.Equal("release-notes (/Work/_skills/release-notes, last changed by claude): Drafts release notes.", lines[2]);
    }

    [Fact]
    public async Task Ls_of_a_vault_lists_its_skills()
    {
        Assert.Equal("release-notes (/Work/_skills/release-notes, last changed by claude): Drafts release notes.", (await Run("skills ls /Work")).Output);
        Assert.Equal("no skills yet; 'skills copy create-skill' adds the one that writes skills", (await Run("skills ls /Dave")).Output);
    }

    [Fact]
    public async Task Ls_says_when_an_update_is_known()
    {
        _updates.Add(P("/Work/_skills/release-notes"));

        var lines = (await Run("skills ls")).Output.Split('\n');

        Assert.EndsWith("Drafts release notes. [update available: Skills screen]", lines[2]);
        Assert.DoesNotContain("update available", lines[1]);
    }

    [Fact]
    public async Task Cat_by_name_prints_SKILL_md_with_a_version()
    {
        var result = await Run("skills cat write-as-alice");

        Assert.False(result.IsError, result.Output);
        Assert.Equal(WriteAsAlice, result.Output);
        Assert.Equal("/Skills/write-as-alice/SKILL.md", result.Version?.Path);
    }

    [Fact]
    public async Task Cat_of_a_file_prints_it()
    {
        var time = new FakeTimeProvider(InMemoryNoteStore.Seeded);
        Assert.False((await new NoteWriter(_store, time, new FixedCaller(Writers.Dev)).WriteAsync("/Skills/write-as-alice/run.py", "print(1)\n", null, CancellationToken.None)).IsError);

        Assert.Equal("# A post\n", (await Run("skills cat write-as-alice/examples/post.md")).Output);
        Assert.Equal("print(1)\n", (await Run("skills cat write-as-alice/run.py")).Output);
        Assert.Equal("# A post\n", (await Run("skills cat /Skills/write-as-alice/examples/post.md")).Output);
        Assert.Equal(WriteAsAlice, (await Run("skills cat /Skills/write-as-alice")).Output);
        var missing = await Run("skills cat write-as-alice/nothing.md");
        Assert.True(missing.IsError);
        Assert.StartsWith("skills cat: ", missing.Output);
    }

    [Fact]
    public async Task Cat_of_a_system_skill_prints_it()
    {
        var result = await Run("skills cat create-skill");

        Assert.Equal(SystemSkills.Find("create-skill")!.Files[0].Content, result.Output);
        Assert.Null(result.Version);
    }

    [Fact]
    public async Task Cat_of_an_ambiguous_name_lists_the_paths()
    {
        Put("/Skills/pdf/SKILL.md", "---\nname: pdf\ndescription: Mine.\n---\n");
        Put("/Work/_skills/pdf/SKILL.md", "---\nname: pdf\ndescription: Theirs.\n---\n");

        var result = await Run("skills cat pdf");

        Assert.True(result.IsError);
        Assert.Equal("skills cat: 'pdf' names 2 skills: /Skills/pdf, /Work/_skills/pdf; give the path", result.Output);
        Assert.Equal("---\nname: pdf\ndescription: Theirs.\n---\n", (await Run("skills cat /Work/_skills/pdf")).Output);
    }

    [Fact]
    public async Task Cat_and_check_take_a_path_as_given()
    {
        var folder = VaultPath.Root.Child("Skills").Child("evil‮name");
        Put(folder.Child("SKILL.md"), "---\nname: evil\ndescription: Tricky.\n---\n");

        Assert.Equal("---\nname: evil\ndescription: Tricky.\n---\n", (await Run($"skills cat \"{folder.Value}\"")).Output);
        var check = await Run($"skills check \"{folder.Value}\"");
        Assert.True(check.IsError);
        Assert.StartsWith($"{folder.Value}: name 'evil' must be the folder's name", check.Output);
    }

    [Fact]
    public async Task Cat_outside_the_skills_a_caller_sees_finds_nothing()
    {
        var result = await Run("skills cat /Dave/_skills/x");

        Assert.True(result.IsError);
        Assert.Equal(ShellErrorKind.NotFound, result.Kind);
        Assert.True((await Run("skills cat /Work/plan.md")).IsError);
    }

    [Fact]
    public async Task Check_names_each_problem()
    {
        Put("/Work/_skills/broken/SKILL.md", "---\nname: Broken\n---\n");
        Put("/Work/_skills/bare/notes.md", "# Notes\n");

        var result = await Run("skills check /Work");

        Assert.True(result.IsError);
        Assert.Equal(ShellErrorKind.Invalid, result.Kind);
        Assert.Equal(
            "/Work/_skills/bare: SKILL.md is missing\n"
            + "/Work/_skills/broken: name 'Broken' must be lowercase letters, digits and hyphens, at most 64 characters; description is missing\n"
            + "/Work/_skills/release-notes: ok",
            result.Output);
    }

    [Fact]
    public async Task Check_of_a_good_skill_says_ok()
    {
        Assert.Equal("/Skills/write-as-alice: ok", (await Run("skills check /Skills/write-as-alice")).Output);
        var all = await Run("skills check");
        Assert.False(all.IsError, all.Output);
        Assert.Equal("create-skill: ok\n/Skills/write-as-alice: ok\n/Work/_skills/release-notes: ok", all.Output);
    }

    [Fact]
    public async Task Copy_writes_create_skill_into_Skills()
    {
        var result = await Run("skills copy create-skill");

        Assert.False(result.IsError, result.Output);
        Assert.Equal("copied create-skill to /Skills/create-skill/ (1 file); it is yours now, and changes to the built-in one don't reach it", result.Output);
        Assert.Equal(SystemSkills.Find("create-skill")!.Files[0].Content, await _store.ReadAsync(P("/Skills/create-skill/SKILL.md"), CancellationToken.None));
    }

    [Fact]
    public async Task Copy_into_a_vault_uses__skills()
    {
        var result = await Run("skills copy create-skill /Work");

        Assert.False(result.IsError, result.Output);
        Assert.StartsWith("copied create-skill to /Work/_skills/create-skill/ (1 file)", result.Output);
        Assert.NotNull(await _store.ReadAsync(P("/Work/_skills/create-skill/SKILL.md"), CancellationToken.None));
    }

    [Fact]
    public async Task Copy_without_a_vault_Skills_asks_for_one()
    {
        _store.SkillsVault = null;

        var result = await Run("skills copy create-skill");

        Assert.Equal("skills copy: there is no vault Skills here; name a vault: skills copy create-skill /Work", result.Output);
        Assert.Equal(ShellErrorKind.Invalid, result.Kind);
    }

    [Fact]
    public async Task Copy_refuses_an_existing_target()
    {
        Assert.False((await Run("skills copy create-skill")).IsError);

        var again = await Run("skills copy create-skill");

        Assert.True(again.IsError);
        Assert.Equal(ShellErrorKind.Conflict, again.Kind);
        Assert.Equal("skills copy: /Skills/create-skill already exists; rm it first, or copy to another vault", again.Output);
    }

    [Fact]
    public async Task Copy_refuses_a_read_only_vault()
    {
        var result = await Run("skills copy create-skill", new FixedCaller(Writers.Dev, v => v != "Skills"));

        Assert.True(result.IsError);
        Assert.Equal(ShellErrorKind.Refused, result.Kind);
        Assert.Equal("skills: this key can only read vault Skills", result.Output);
        Assert.Equal(Refusals.ReadOnly, result.Code);
        Assert.Empty(await _store.ListNotesUnderAsync(P("/Skills/create-skill"), CancellationToken.None));
    }

    [Fact]
    public async Task Copy_refuses_a_users_skill()
    {
        foreach (var line in new[] { "skills copy write-as-alice /Work", "skills copy /Skills/write-as-alice /Work" })
        {
            var result = await Run(line);
            Assert.True(result.IsError);
            Assert.Equal("skills copy: only built-in skills are copied here; moving a user's skill between vaults is for the user, in the Skills screen", result.Output);
        }

        Assert.Empty(await _store.ListNotesUnderAsync(P("/Work/_skills/write-as-alice"), CancellationToken.None));
    }

    [Theory, InlineData("install"), InlineData("share"), InlineData("update"), InlineData("mv"), InlineData("add"), InlineData("fetch")]
    public async Task No_subcommand_brings_in_shares_moves_or_updates(string sub) => Assert.StartsWith($"skills: no subcommand '{sub}'", (await Run($"skills {sub} x")).Output);

    [Fact]
    public async Task An_unknown_subcommand_points_to_the_Skills_screen()
    {
        var result = await Run("skills install x");

        Assert.Equal(
            "skills: no subcommand 'install'; skills reads and finds (ls, find, cat, copy, check). Bringing skills in, sharing and updates are the user's, in the Skills screen",
            result.Output);
        Assert.Equal(ShellErrorKind.Invalid, result.Kind);
    }

    [Fact]
    public async Task Help_skills_explains_them() => Assert.Contains("only reads and finds", (await Run("help skills")).Output);

    [Fact]
    public async Task The_tool_description_lists_skills() => Assert.Contains("skills ls|find|cat|copy|check", Manual.ToolDescription);
}
