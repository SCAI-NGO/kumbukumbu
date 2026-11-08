// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Sh;
using Kumbu.Core.Skills;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Skills;

public class SystemSkillsTests
{
    private static string Skill(string name) => SystemSkills.Find(name)!.Files[0].Content;

    [Fact]
    public void Create_skill_is_built_in()
    {
        var skill = SystemSkills.Find("create-skill");
        Assert.NotNull(skill);
        Assert.Equal("SKILL.md", skill.Files[0].Path);
        Assert.Contains(SystemSkills.All, s => s.Name == "create-skill");
    }

    [Fact]
    public void Every_system_skill_follows_the_rules()
    {
        Assert.NotEmpty(SystemSkills.All);
        foreach (var s in SystemSkills.All)
        {
            Assert.Equal("SKILL.md", s.Files[0].Path);
            Assert.Empty(SkillManifest.Read(s.Name, s.Files[0].Content).Problems);
        }
    }

    [Fact]
    public void Create_skill_says_what_to_do_without_write() =>
        Assert.Contains("give the user each file's path and content", Skill("create-skill"));

    [Fact]
    public void Create_skill_ends_with_skills_check() =>
        Assert.Contains("Run sh `skills check <folder>` and fix what it names.", Skill("create-skill"));

    private const string WiringDiagram = "![Wiring diagram](/Pump%20manual/images/wiring%20diagram.png)";

    [Fact]
    public void Ingest_attachments_is_built_in()
    {
        var skill = SystemSkills.Find("ingest-attachments");
        Assert.NotNull(skill);
        Assert.Equal("SKILL.md", skill.Files[0].Path);
        var manifest = SkillManifest.Read(skill.Name, skill.Files[0].Content);
        Assert.Empty(manifest.Problems);
        Assert.Contains("make notes of this PDF", manifest.Description);
        Assert.Contains("ingest these screenshots", manifest.Description);
        Assert.Contains("what's in this manual, write it down", manifest.Description);
    }

    [Fact]
    public void Ingest_attachments_links_from_the_vault_root_with_spaces_as_percent_20()
    {
        var text = Skill("ingest-attachments");
        Assert.Contains("Link from the vault's root without the vault's name, spaces as %20", text);
        Assert.Contains(WiringDiagram, text);
        Assert.Contains("Source: [Pump manual.pdf](/Inbox/Pump%20manual.pdf)", text);
    }

    [Fact]
    public void Ingest_attachments_crops_only_drawings_and_looks_below_5_percent()
    {
        var text = Skill("ingest-attachments");
        Assert.Contains("Crop only the regions `pdf info` calls drawing", text);
        Assert.Contains("never its image regions", text);
        Assert.Contains("`pdf info` lists regions only on pages where figures cover at least 5 %", text);
        Assert.Contains("(`pdf <pdf> <pages> --images`) for smaller drawings", text);
        Assert.Contains("`pdfimages <pdf> <folder>/images/`", text);
    }

    [Fact]
    public void Ingest_attachments_asks_once_and_recommends_archive()
    {
        var text = Skill("ingest-attachments");
        Assert.Contains("Ask once, up front", text);
        Assert.Contains("\"Do you want to add a description or notes of your own?\"", text);
        Assert.Contains("keep it, archive it (`archive <path>`), or delete it (`rm <path>`", text);
        Assert.Contains("Recommend archive: links follow into the archive", text);
        Assert.Contains("in the language of the source", text);
        Assert.Contains("say that the pictures need pdfimages with write access", text);
    }

    [Fact]
    public async Task An_embed_written_as_ingest_attachments_says_resolves()
    {
        Assert.Contains(WiringDiagram, Skill("ingest-attachments"));
        var store = new InMemoryNoteStore(("/Work/Pump manual/Overview.md", $"# Pump manual\n\n{WiringDiagram}\n"));
        store.AddAttachment("/Work/Pump manual/images/wiring diagram.png");
        var time = new FakeTimeProvider(InMemoryNoteStore.Seeded.AddDays(1));
        var sh = new Shell(store, new NoteWriter(store, time, new FixedCaller(Writers.Dev)), new NoteMover(store, time, new FixedCaller(Writers.Dev)));

        Assert.Equal($"3: {WiringDiagram} → /Work/Pump manual/images/wiring diagram.png", (await sh.RunAsync("links \"/Work/Pump manual/Overview.md\"", CancellationToken.None)).Output);
        Assert.Equal("(no broken or ambiguous links below /Work)", (await sh.RunAsync("links /Work", CancellationToken.None)).Output);
    }

    [Fact]
    public void System_skills_name_no_organisation()
    {
        foreach (var s in SystemSkills.All)
        {
            foreach (var (_, content) in s.Files)
            {
                Assert.DoesNotContain("Anthropic", content);
                Assert.DoesNotContain("Claude", content);
                Assert.DoesNotContain("GitHub", content);
            }
        }
    }
}
