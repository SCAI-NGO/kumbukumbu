// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Skills;
using Kumbu.Core.Tests.Fakes;

namespace Kumbu.Core.Tests.Skills;

/// <summary>Bob's view: his own vaults Skills and Private, and Alice's Work, shared with him.</summary>
public class SkillDirectoryTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly InMemoryNoteStore _store = new();
    private readonly SkillDirectory dir;

    public SkillDirectoryTests()
    {
        _store.SkillsVault = "Skills";
        Put("/Skills/write-as-alice/SKILL.md", "---\nname: write-as-alice\ndescription: Writes posts in Alice's voice.\n---\n# Write\n", WriterRef.ForOwner(WriterRef.Agent, "k1"), "claude");
        Put("/Work/_skills/release-notes/SKILL.md", "---\nname: release-notes\ndescription: Drafts release notes.\n---\n# Notes\n", WriterRef.ForMember("w-carol"));
        Put("/Work/plan.md", "# Plan\n", WriterRef.ForMember("w-carol"));
        Put("/Private/todo.md", "# Todo\n", WriterRef.ForOwner(WriterRef.Agent, "k1"), "claude");
        _store.Infos["Work"] = new VaultInfo("\"Alice\"", AccessLevel.Write, true);
        dir = new SkillDirectory(_store, new BobsLabels());
    }

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var p) ? p : throw new ArgumentException(path);

    private void Put(string path, string content, WriterRef? by = null, string label = "") => Put(P(path), content, by, label);

    private void Put(VaultPath path, string content, WriterRef? by = null, string label = "") =>
        _store.Put(new StoredNote(path, content, 1, label, InMemoryNoteStore.Seeded, [], content, Ref: by ?? WriterRef.ForOwner(WriterRef.Agent, "k1")));

    private async Task<Skill> One(string folder) => Assert.Single(await dir.ListAsync(null, Ct), s => s.Folder?.Value == folder);

    [Fact]
    public async Task Lists_system_then_Skills_then_vaults() =>
        Assert.Equal(["create-skill", "write-as-alice", "release-notes"], (await dir.ListAsync(null, Ct)).Select(s => s.Name));

    [Fact]
    public async Task System_skills_say_so()
    {
        var skill = (await dir.ListAsync(null, Ct))[0];
        Assert.True(skill.IsSystem);
        Assert.Null(skill.Vault);
        Assert.StartsWith("create-skill (system): Write a new skill for the user", skill.Line());
    }

    [Fact]
    public async Task Vaults_follow_in_text_order_and_folders_by_name()
    {
        Put("/Private/_skills/b-skill/SKILL.md", "---\nname: b-skill\ndescription: B.\n---\n");
        Put("/Private/_skills/a-skill/SKILL.md", "---\nname: a-skill\ndescription: A.\n---\n");
        Put("/Archiv/_skills/old/SKILL.md", "---\nname: old\ndescription: Old.\n---\n");

        Assert.Equal(["create-skill", "write-as-alice", "old", "a-skill", "b-skill", "release-notes"], (await dir.ListAsync(null, Ct)).Select(s => s.Name));
    }

    [Fact]
    public async Task A_vault_given_lists_only_its_skills()
    {
        Assert.Equal(["release-notes"], (await dir.ListAsync("Work", Ct)).Select(s => s.Name));
        Assert.Equal(["write-as-alice"], (await dir.ListAsync("Skills", Ct)).Select(s => s.Name));
        Assert.Empty(await dir.ListAsync("Private", Ct));
    }

    [Fact]
    public async Task A_vault_without_access_lists_nothing() => Assert.Empty(await dir.ListAsync("Dave", Ct));

    [Fact]
    public async Task A_skill_of_another_member_says_so()
    {
        var skill = await One("/Work/_skills/release-notes");

        Assert.True(skill.ByOther);
        Assert.Equal("Work", skill.Vault);
        Assert.Equal("release-notes (/Work/_skills/release-notes, shared by \"Alice\", last changed by Carol, another user): Drafts release notes.", skill.Line());
    }

    [Fact]
    public async Task A_shared_vaults_skill_says_so_even_when_he_last_changed_it()
    {
        Put("/Work/_skills/release-notes/SKILL.md", "---\nname: release-notes\ndescription: Drafts release notes.\n---\n", WriterRef.ForMember("w-bob"));

        var skill = await One("/Work/_skills/release-notes");

        Assert.False(skill.ByOther);
        Assert.Contains("shared by \"Alice\"", skill.Line());
        Assert.DoesNotContain("another user", skill.Line());
    }

    /// <summary>A skill is all its files: Carol rewriting the steps <c>SKILL.md</c> sends the agent to is a change to the
    /// skill, though <c>SKILL.md</c> is Bob's own.</summary>
    [Fact]
    public async Task Another_users_write_to_any_file_of_the_skill_marks_it()
    {
        Put("/Work/_skills/release-notes/SKILL.md", "---\nname: release-notes\ndescription: Drafts release notes.\n---\nFollow references/steps.md.\n", WriterRef.ForMember("w-bob"));
        _store.Put(new StoredNote(P("/Work/_skills/release-notes/references/steps.md"), "# Steps\n", 1, "", InMemoryNoteStore.Seeded.AddHours(1), [], "# Steps\n", Ref: WriterRef.ForMember("w-carol")));

        var skill = await One("/Work/_skills/release-notes");

        Assert.True(skill.ByOther);
        Assert.Equal("Carol", skill.LastEditor);
        Assert.Equal("release-notes (/Work/_skills/release-notes, shared by \"Alice\", last changed by Carol, another user): Drafts release notes.", skill.Line());
    }

    [Fact]
    public async Task The_last_editor_is_whoever_wrote_a_file_of_the_skill_last()
    {
        Put("/Work/_skills/release-notes/SKILL.md", "---\nname: release-notes\ndescription: Drafts release notes.\n---\n", WriterRef.ForMember("w-carol"));
        _store.Put(new StoredNote(P("/Work/_skills/release-notes/references/steps.md"), "# Steps\n", 1, "", InMemoryNoteStore.Seeded.AddHours(1), [], "# Steps\n", Ref: WriterRef.ForMember("w-bob")));

        var skill = await One("/Work/_skills/release-notes");

        // Bob wrote last, but Carol's SKILL.md is still hers.
        Assert.Equal("Bob", skill.LastEditor);
        Assert.True(skill.ByOther);
    }

    [Fact]
    public async Task An_own_skill_names_no_owner()
    {
        var skill = await One("/Skills/write-as-alice");

        Assert.Null(skill.SharedBy);
        Assert.Equal("write-as-alice (/Skills/write-as-alice, last changed by claude): Writes posts in Alice's voice.", skill.Line());
    }

    [Fact]
    public async Task A_name_that_breaks_the_rules_is_quoted()
    {
        Put("/Skills/x (system)/SKILL.md", "---\nname: create-skill\ndescription: Looks built in.\n---\n");

        var skill = await One("/Skills/x (system)");

        Assert.StartsWith("\"x (system)\" (/Skills/x (system), ", skill.Line());
    }

    [Fact]
    public async Task Control_and_format_characters_are_stripped()
    {
        var folder = VaultPath.Root.Child("Skills").Child("evil\u202E\u200B\u0007name");
        Put(folder.Child("SKILL.md"), "---\nname: evil\ndescription: \"Fine\\u202E\\u200B\\u0007 text\"\n---\n");

        var line = (await One(folder.Value)).Line();

        foreach (var c in new[] { '\u202E', '\u200B', '\u0007' }) Assert.DoesNotContain(c, line);
        Assert.StartsWith("\"evilname\" (", line);
        Assert.Contains("): Fine text [warning: ", line);
    }

    [Fact]
    public async Task Line_breaks_become_spaces()
    {
        Put("/Skills/multi/SKILL.md", "---\nname: multi\ndescription: |\n  One\n  two\n---\n");

        Assert.Equal("multi (/Skills/multi, last changed by Bob): One two", (await One("/Skills/multi")).Line());
    }

    [Fact]
    public async Task A_copy_names_its_source()
    {
        Put("/Skills/pdf/SKILL.md", "---\nname: pdf\ndescription: Reads PDFs.\nmetadata:\n  kumbu-source: \"github:alice-example/skills/pdf\"\n  kumbu-hash: \"sha256:00\"\n---\n");

        var skill = await One("/Skills/pdf");

        Assert.Equal("github:alice-example/skills/pdf", skill.Source);
        // "from" only for a source the sealed origin agrees with; the frontmatter alone proves nothing, whoever wrote it.
        Assert.Equal("pdf (/Skills/pdf, last changed by Bob, unverified origin): Reads PDFs.", skill.Line());
        Assert.Equal("pdf (/Skills/pdf, last changed by Bob, from github:alice-example/skills/pdf): Reads PDFs.", (skill with { SourceVerified = true }).Line());
        Assert.Equal("pdf (/Skills/pdf, last changed by Bob, origin edited): Reads PDFs.", (skill with { OriginEdited = true }).Line());
    }

    [Theory]
    [InlineData("no-frontmatter")]
    [InlineData("bad-yaml")]
    [InlineData("number-name")]
    [InlineData("crlf-bom")]
    [InlineData("tab-metadata")]
    public async Task A_broken_manifest_is_listed_with_its_warning(string which)
    {
        var (folder, content, warned) = which switch
        {
            "no-frontmatter" => (which, "# Just text\n", true),
            "bad-yaml" => (which, "---\nname: [x\n: :\n---\n", true),
            "number-name" => ("42", "---\nname: 42\ndescription: y\n---\n", false),
            "crlf-bom" => (which, "﻿---\r\nname: crlf-bom\r\ndescription: y\r\n---\r\n# Body\r\n", false),
            "tab-metadata" => (which, "---\nname: tab-metadata\ndescription: y\nmetadata:\n\tkumbu-source: x\n---\n", true),
            _ => throw new ArgumentException(which),
        };
        Put($"/Skills/{folder}/SKILL.md", content);

        var skill = await One($"/Skills/{folder}");

        Assert.Equal(folder, skill.Name);
        if (warned)
        {
            Assert.NotEmpty(skill.Warnings);
            Assert.EndsWith("]", skill.Line());
            Assert.Contains(" [warning: ", skill.Line());
        }
        else
        {
            Assert.Empty(skill.Warnings);
            Assert.Equal("y", skill.Description);
            Assert.DoesNotContain("warning", skill.Line());
        }
    }

    [Fact]
    public async Task A_folder_without_SKILL_md_is_listed_with_a_warning()
    {
        Put("/Skills/loose/notes.md", "# Notes\n");

        var skill = await One("/Skills/loose");

        Assert.Equal("", skill.Description);
        // Its files still have a writer.
        Assert.Equal("Bob", skill.LastEditor);
        Assert.Equal("loose (/Skills/loose, last changed by Bob) [warning: SKILL.md is missing]", skill.Line());
    }

    [Fact]
    public async Task A_folder_with_only_a_text_file_is_listed()
    {
        _store.AddAttachment("/Skills/script-only/run.py");

        Assert.Equal("script-only (/Skills/script-only, last changed by seed) [warning: SKILL.md is missing]", (await One("/Skills/script-only")).Line());
    }

    [Fact]
    public async Task A_note_loose_at_a_location_is_no_skill()
    {
        Put("/Skills/readme.md", "# Readme\n");
        Put("/Work/_skills/readme.md", "# Readme\n");

        Assert.Equal(["create-skill", "write-as-alice", "release-notes"], (await dir.ListAsync(null, Ct)).Select(s => s.Name));
    }

    [Fact]
    public async Task Two_skills_of_one_name_are_both_listed()
    {
        Put("/Skills/pdf/SKILL.md", "---\nname: pdf\ndescription: Mine.\n---\n");
        Put("/Work/_skills/pdf/SKILL.md", "---\nname: pdf\ndescription: Alice's.\n---\n");

        var pdfs = (await dir.ListAsync(null, Ct)).Where(s => s.Name == "pdf").Select(s => s.Folder!.Value);

        Assert.Equal(["/Skills/pdf", "/Work/_skills/pdf"], pdfs);
    }

    [Fact]
    public async Task An_unmarked_vault_named_Skills_holds_no_skills()
    {
        _store.SkillsVault = null;

        Assert.Equal(["create-skill", "release-notes"], (await dir.ListAsync(null, Ct)).Select(s => s.Name));
        Assert.Empty(await dir.ListAsync("Skills", Ct));
    }

    [Fact]
    public async Task Files_lists_notes_and_attachments_relative()
    {
        Put("/Skills/write-as-alice/examples/post.md", "# Post\n");
        _store.AddAttachment("/Skills/write-as-alice/scripts/run.py");
        var skill = await One("/Skills/write-as-alice");

        Assert.Equal(
            [new SkillFile("SKILL.md", true), new SkillFile("examples/post.md", true), new SkillFile("scripts/run.py", false)],
            await dir.FilesAsync(skill, Ct));
        Assert.StartsWith("---\nname: write-as-alice\n", await dir.ManifestAsync(skill, Ct));
    }

    [Fact]
    public async Task A_system_skills_files_and_manifest_come_from_the_server()
    {
        var skill = (await dir.ListAsync(null, Ct))[0];

        Assert.Equal([new SkillFile("SKILL.md", true)], await dir.FilesAsync(skill, Ct));
        Assert.Equal(SystemSkills.Find("create-skill")!.Files[0].Content, await dir.ManifestAsync(skill, Ct));
    }

    [Fact]
    public async Task A_description_is_cut_with_an_ellipsis()
    {
        var long_ = new string('a', 400);
        Put("/Skills/long/SKILL.md", $"---\nname: long\ndescription: {long_}\n---\n");
        var skill = await One("/Skills/long");

        var line = skill.Line(300);

        Assert.Equal("long (/Skills/long, last changed by Bob): " + new string('a', 299) + "…", line);
        Assert.EndsWith(long_, skill.Line());
        Assert.Equal("long (/Skills/long, last changed by Bob): " + long_, skill.Line(400));
    }

    /// <summary>Bob's labels: a writer reference's id names the member; the owner's agent shows its label.</summary>
    private sealed class BobsLabels : IWriterLabels
    {
        public ValueTask<(string Display, string? Kind)> RenderAsync(string vault, string label, WriterRef? reference, CancellationToken ct) =>
            ValueTask.FromResult<(string, string?)>((reference?.Writer switch { "w-carol" => "Carol", "w-bob" => "Bob", _ => label.Length > 0 ? label : "Bob" }, null));

        public Task<bool> IsOwnAsync(string vault, WriterRef? reference, CancellationToken ct) =>
            Task.FromResult(vault == "Work" ? reference is { Owner: false, Writer: "w-bob" } : reference is { Owner: true });
    }
}
