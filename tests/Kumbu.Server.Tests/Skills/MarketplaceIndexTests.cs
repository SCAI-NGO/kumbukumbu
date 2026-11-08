// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Kumbu.Server.Skills;

namespace Kumbu.Server.Tests.Skills;

/// <summary>What a marketplace's archive offers: its two layouts, the paths it may name, the files a skill keeps and
/// skips, and the skill limits (spec §3.9; Decision 24).</summary>
public sealed class MarketplaceIndexTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const string MarketplaceJson = ".claude-plugin/marketplace.json";

    private static byte[] Manifest(string name, string description = "Does a thing.") =>
        Encoding.UTF8.GetBytes($"---\nname: {name}\ndescription: {description}\n---\n# {name}\n");

    private static RepoArchive Archive(params (string Path, byte[] Bytes)[] files) =>
        new(Sha, [.. files.Select(f => new ArchiveFile(f.Path, f.Bytes))], []);

    private static (string, byte[]) Skill(string folder) => ($"{folder}/SKILL.md", Manifest(folder.Split('/')[^1]));

    private static (string, byte[]) Json(string json) => (MarketplaceJson, Encoding.UTF8.GetBytes(json));

    private static (string, byte[]) Text(string path, string text = "x") => (path, Encoding.UTF8.GetBytes(text));

    [Fact]
    public void The_marketplace_json_layout_lists_its_plugins_skills()
    {
        var listing = MarketplaceIndex.Of(Archive(
            Json("""{ "name": "alice", "plugins": [{ "name": "document-skills", "source": "./", "skills": ["./skills/pdf", "./skills/xlsx/"] }] }"""),
            Skill("skills/pdf"), Skill("skills/xlsx"), Skill("skills/other")));

        Assert.Equal(Sha, listing.Commit);
        Assert.Null(listing.Note);
        Assert.Equal(["skills/pdf", "skills/xlsx"], listing.Skills.Select(s => s.Path));
        Assert.All(listing.Skills, s =>
        {
            Assert.Equal("document-skills", s.Plugin);
            Assert.True(s.Installable);
            Assert.Null(s.Reason);
            Assert.Empty(s.Warnings);
            Assert.Equal("Does a thing.", s.Description);
        });
        Assert.Equal(new MarketplacePlugin("document-skills", "./", true), Assert.Single(listing.Plugins));
    }

    [Fact]
    public void A_plugin_without_a_skills_list_has_its_skills_folder()
    {
        var listing = MarketplaceIndex.Of(Archive(
            Json("""{ "plugins": [{ "name": "office", "source": "./plugins/office" }] }"""),
            Skill("plugins/office/skills/docx"), Skill("plugins/office/skills/deep/inner"), Skill("plugins/office/other"), Skill("skills/pdf")));

        var skill = Assert.Single(listing.Skills);
        Assert.Equal("plugins/office/skills/docx", skill.Path);
        Assert.Equal("docx", skill.Name);
        Assert.Equal("office", skill.Plugin);
        Assert.True(skill.Installable);
    }

    [Fact]
    public void A_plugin_of_another_repository_is_listed_not_installable()
    {
        var listing = MarketplaceIndex.Of(Archive(
            Json("""
                 { "plugins": [
                   { "name": "local", "source": "./" },
                   { "name": "remote", "source": { "source": "github", "repo": "bob-example/tools" }, "skills": ["./skills/tools"] },
                   { "name": "far", "source": "https://kumbu.example.org/far.git" }
                 ] }
                 """),
            Skill("skills/pdf"), Skill("skills/tools")));

        Assert.Equal(
            [new MarketplacePlugin("local", "./", true), new MarketplacePlugin("remote", "bob-example/tools", false), new MarketplacePlugin("far", "https://kumbu.example.org/far.git", false)],
            listing.Plugins);
        var local = listing.Skills.Where(s => s.Plugin == "local").ToList();
        Assert.Equal(["skills/pdf", "skills/tools"], local.Select(s => s.Path));
        Assert.All(local, s => Assert.True(s.Installable));
        Assert.DoesNotContain(listing.Skills, s => s.Plugin is "remote" or "far" && s.Installable);
    }

    [Fact]
    public void A_skill_of_a_plugin_of_another_repository_is_listed_with_its_reason()
    {
        var listing = MarketplaceIndex.Of(Archive(
            Json("""{ "plugins": [{ "name": "remote", "source": { "source": "url", "url": "https://kumbu.example.org/r.git" }, "skills": ["./skills/tools"] }] }"""),
            Skill("skills/tools")));

        var skill = Assert.Single(listing.Skills);
        Assert.Equal(("skills/tools", "tools", "remote", false), (skill.Path, skill.Name, skill.Plugin, skill.Installable));
        Assert.Contains("another repository", skill.Reason);
        Assert.Equal(new MarketplacePlugin("remote", "https://kumbu.example.org/r.git", false), Assert.Single(listing.Plugins));
    }

    [Fact]
    public void Without_marketplace_json_every_SKILL_md_up_to_six_levels()
    {
        var listing = MarketplaceIndex.Of(Archive(
            Text("SKILL.md", "---\nname: root\n---\n"),
            Skill("a"), Skill("a/b/c/d/e/f"), Skill("a/b/c/d/e/f/g"), Skill(".github/x"), Skill("docs/.hidden/y"),
            Text("b/skill.md"), Text("c/README.md")));

        Assert.Null(listing.Note);
        Assert.Empty(listing.Plugins);
        Assert.Equal(["a", "a/b/c/d/e/f"], listing.Skills.Select(s => s.Path));
        Assert.All(listing.Skills, s => Assert.Null(s.Plugin));
        Assert.Equal("f", listing.Skills[1].Name);
    }

    public static TheoryData<string> Unparsable =>
    [
        "{ plugins: [",
        "[]",
        """{ "plugins": 3 }""",
        """{ "plugins": [{ "name": "x", "source": "./" }], }""",
        new string('[', 10_000) + new string(']', 10_000),
        """{ "plugins": [""" + new string('[', 100) + new string(']', 100) + "] }",
    ];

    [Theory, MemberData(nameof(Unparsable))]
    public void A_marketplace_json_that_doesnt_parse_falls_back(string json)
    {
        var listing = MarketplaceIndex.Of(Archive(Json(json), Skill("skills/pdf"), Skill("tools/x")));

        Assert.Equal("marketplace.json doesn't parse; listing every folder with a SKILL.md", listing.Note);
        Assert.Empty(listing.Plugins);
        Assert.Equal(["skills/pdf", "tools/x"], listing.Skills.Select(s => s.Path));
    }

    [Fact]
    public void A_marketplace_json_over_1_MB_isnt_read()
    {
        var json = """{ "plugins": [{ "name": "x", "source": "./" }] }""" + new string(' ', 1024 * 1024);
        var listing = MarketplaceIndex.Of(Archive(Json(json), Skill("tools/x")));

        Assert.Equal("marketplace.json is over 1 MB; listing every folder with a SKILL.md", listing.Note);
        Assert.Empty(listing.Plugins);
        Assert.Equal(["tools/x"], listing.Skills.Select(s => s.Path));
    }

    [Fact]
    public void A_skills_entry_that_escapes_is_ignored()
    {
        var listing = MarketplaceIndex.Of(Archive(
            Json("""{ "plugins": [{ "name": "sub", "source": "./sub", "skills": ["../x", "/abs", ".hidden/y", 42, "./../x", "a\\b"] }] }"""),
            Skill("x"), Skill("abs"), Skill("sub/abs"), Skill("sub/.hidden/y"), Skill("sub/a/b")));

        Assert.Empty(listing.Skills);
        Assert.Equal(new MarketplacePlugin("sub", "./sub", true), Assert.Single(listing.Plugins));
    }

    [Fact]
    public void A_source_that_escapes_isnt_installable()
    {
        var listing = MarketplaceIndex.Of(Archive(
            Json("""{ "plugins": [{ "name": "up", "source": "./../other" }, { "name": "dot", "source": "./.github" }] }"""),
            Skill("other/skills/x"), Skill(".github/skills/y")));

        Assert.Empty(listing.Skills);
        Assert.All(listing.Plugins, p => Assert.False(p.Installable));
    }

    [Fact]
    public void Files_kept_and_skipped_are_named()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };
        var archive = new RepoArchive(Sha,
        [
            new("skills/pdf/SKILL.md", Manifest("pdf")),
            new("skills/pdf/reference.md", "# Reference\n"u8.ToArray()),
            new("skills/pdf/scripts/fill.py", "print('x')\n"u8.ToArray()),
            new("skills/pdf/LICENSE", "MIT\n"u8.ToArray()),
            new("skills/pdf/logo.png", png),
            new("skills/pdf/form.pdf", "%PDF-1.7\n"u8.ToArray()),
            new("skills/pdf/fake.png", "not a png at all"u8.ToArray()),
            new("skills/pdf/tool.exe", [0x4D, 0x5A, 0x90, 0]),
            new("skills/pdf/bad.txt", [0x61, 0xC3, 0x28]),
            new("skills/pdf/nul.json", [0x7B, 0, 0x7D]),
            new("skills/pdf/big.md", Encoding.UTF8.GetBytes(new string('a', 1024 * 1024 + 1))),
            new("skills/pdf/latin1.md", [0x63, 0x61, 0x66, 0xE9]),
            new("skills/pdf/Makefile2", "all:\n"u8.ToArray()),
            new("skills/pdf/con.txt", "x"u8.ToArray()),
            new("skills/pdf/what?.txt", "x"u8.ToArray()),
            new("skills/pdfx/other.txt", "x"u8.ToArray()),
        ], ["skills/pdf/link-to-etc", "skills/other/link"]);

        var files = MarketplaceIndex.FilesOf(archive, "skills/pdf");

        Assert.NotNull(files);
        Assert.Equal(["SKILL.md", "reference.md", "scripts/fill.py", "LICENSE", "logo.png", "form.pdf"], files.Files.Select(f => f.Path));
        Assert.Equal(png, files.Files.Single(f => f.Path == "logo.png").Bytes);
        Assert.Equal(["fake.png", "tool.exe", "bad.txt", "nul.json", "big.md", "latin1.md", "Makefile2", "con.txt", "what?.txt", "link-to-etc"], files.Skipped);
    }

    [Fact]
    public void FilesOf_needs_a_skill_path_with_a_SKILL_md()
    {
        var archive = Archive(Skill("skills/pdf"), Text("skills/notes/a.md"), Skill("x"));
        Assert.Null(MarketplaceIndex.FilesOf(archive, "skills/notes"));
        Assert.Null(MarketplaceIndex.FilesOf(archive, "skills/../x"));
        Assert.Null(MarketplaceIndex.FilesOf(archive, "skills/pdf/"));
        Assert.Null(MarketplaceIndex.FilesOf(archive, ""));
        Assert.NotNull(MarketplaceIndex.FilesOf(archive, "skills/pdf"));
    }

    [Fact]
    public void Entry_names_are_normalised_and_dot_names_skipped()
    {
        var archive = Archive(
            Skill("skills/café"),
            Text("skills/café/notes/résumé.md"),
            Text("skills/café/á.txt"),
            Text("skills/café/á.txt"),
            Text("skills/café/.gitignore"),
            Text("skills/café/.github/ci.yml"),
            Text("skills/café/./x.txt"),
            Text("skills/café/a\\b.txt"),
            Text("skills/café/a//b.txt"));

        var listing = MarketplaceIndex.Of(archive);
        var skill = Assert.Single(listing.Skills);
        Assert.Equal(("skills/café", "café"), (skill.Path, skill.Name));

        var files = MarketplaceIndex.FilesOf(archive, "skills/café");
        Assert.NotNull(files);
        Assert.Equal(["SKILL.md", "notes/résumé.md", "á.txt"], files.Files.Select(f => f.Path));
        Assert.Equal(["á.txt", ".gitignore", ".github/ci.yml", "./x.txt", "a\\b.txt", "a//b.txt"], files.Skipped);
        Assert.Equal(files.Files.Select(f => f.Path), MarketplaceIndex.FilesOf(archive, "skills/café")!.Files.Select(f => f.Path));
    }

    [Fact]
    public void A_skill_over_200_files_isnt_installable()
    {
        var full = Enumerable.Range(1, 199).Select(i => Text($"skills/full/f{i}.txt")).Append(Skill("skills/full"));
        var over = Enumerable.Range(1, 200).Select(i => Text($"skills/over/f{i}.txt")).Append(Skill("skills/over"));
        var listing = MarketplaceIndex.Of(Archive([.. full, .. over, Text("skills/over/skipped.exe")]));

        var fits = listing.Skills.Single(s => s.Path == "skills/full");
        Assert.True(fits.Installable);
        Assert.Null(fits.Reason);
        var big = listing.Skills.Single(s => s.Path == "skills/over");
        Assert.False(big.Installable);
        Assert.Equal("it has 201 files; a skill has at most 200", big.Reason);
    }

    [Fact]
    public void A_skill_over_10_MB_isnt_installable()
    {
        var manifest = Manifest("fits");
        var rest = (int)MarketplaceIndex.MaxSkillBytes - manifest.Length;
        var listing = MarketplaceIndex.Of(Archive(
            ("skills/fits/SKILL.md", manifest), ("skills/fits/data.txt", Encoding.UTF8.GetBytes(new string('a', rest))),
            ("skills/over/SKILL.md", Manifest("over")), ("skills/over/data.txt", Encoding.UTF8.GetBytes(new string('a', rest + 1)))));

        Assert.True(listing.Skills.Single(s => s.Path == "skills/fits").Installable);
        var big = listing.Skills.Single(s => s.Path == "skills/over");
        Assert.False(big.Installable);
        Assert.StartsWith("it has 10.0 MB; a skill has at most 10 MB", big.Reason);
    }

    [Fact]
    public void A_skill_whose_SKILL_md_cant_be_stored_isnt_installable()
    {
        var listing = MarketplaceIndex.Of(Archive(("skills/bad/SKILL.md", [0x2D, 0x2D, 0x2D, 0xFF])));

        var skill = Assert.Single(listing.Skills);
        Assert.False(skill.Installable);
        Assert.Equal("its SKILL.md isn't UTF-8 text of at most 1 MiB", skill.Reason);
        Assert.Equal("", skill.Description);
        Assert.NotEmpty(skill.Warnings);
    }

    [Fact]
    public void Scripts_are_noticed()
    {
        var listing = MarketplaceIndex.Of(Archive(
            Skill("skills/a"), Text("skills/a/scripts/run.txt"),
            Skill("skills/b"), Text("skills/b/tool.sh"),
            Skill("skills/c"), Text("skills/c/notes.md"), Text("skills/c/scripts.txt"),
            Skill("skills/d"), Text("skills/d/.hidden.py")));

        Assert.Equal([true, true, false, false], listing.Skills.Select(s => s.HasScripts));
    }

    [Fact]
    public void A_manifest_name_doesnt_name_the_skill()
    {
        var listing = MarketplaceIndex.Of(Archive(("skills/pdf/SKILL.md", Manifest("../evil", "Fills forms."))));

        var skill = Assert.Single(listing.Skills);
        Assert.Equal("pdf", skill.Name);
        Assert.Equal("skills/pdf", skill.Path);
        Assert.Equal("Fills forms.", skill.Description);
        Assert.True(skill.Installable);
        Assert.Contains(skill.Warnings, w => w.Contains("../evil", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("../x", false), InlineData("a/../b", false), InlineData("/a", false), InlineData("a\\b", false), InlineData(".github/x", false)]
    [InlineData("a/b/c/d/e/f/g", false), InlineData("", false), InlineData("a//b", false), InlineData("a/", false), InlineData("./a", false), InlineData("a/.", false)]
    [InlineData("skills/pdf", true), InlineData("a/b/c/d/e/f", true), InlineData("pdf", true)]
    public void IsSkillPath_refuses_escapes(string path, bool expected) => Assert.Equal(expected, MarketplaceIndex.IsSkillPath(path));
}
