// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Sh;

/// <summary>mv of text files and of folders holding them: kept only inside a skill's folder, refused whole otherwise.</summary>
public sealed class SkillFolderMoveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Shell> SkillAsync(params string[] files)
    {
        var store = new InMemoryNoteStore(("/Work/_skills/pdf/SKILL.md", "---\nname: pdf\ndescription: Reads PDFs.\n---\n")) { SkillsVault = "Skills" };
        store.AddVault("Skills"); store.AddVault("Work");
        var time = new FakeTimeProvider(InMemoryNoteStore.Seeded.AddDays(1));
        var writer = new NoteWriter(store, time, new FixedCaller(Writers.Dev));
        foreach (var file in files.Length > 0 ? files : ["scripts/run.py", "LICENSE"])
        {
            await writer.WriteAsync($"/Work/_skills/pdf/{file}", "print(1)\n", null, Ct);
        }

        return new Shell(store, writer, new NoteMover(store, time, new FixedCaller(Writers.Dev)));
    }

    [Fact]
    public async Task A_skill_folder_moves_within__skills_with_its_text_files()
    {
        var shell = await SkillAsync();
        Assert.False((await shell.RunAsync("mv /Work/_skills/pdf/ /Work/_skills/pdf2/", Ct)).IsError);
        Assert.Equal("print(1)\n", (await shell.RunAsync("cat /Work/_skills/pdf2/scripts/run.py", Ct)).Output);
    }

    [Fact]
    public async Task A_skill_folder_moved_out_is_refused_naming_its_text_files()
    {
        var shell = await SkillAsync();
        Assert.Equal("mv: /Work/_skills/pdf/ can't move to /Work/pdf/: text files are kept only inside a skill's folder: LICENSE, scripts/run.py (move them into a skill's folder first, or remove them)",
            (await shell.RunAsync("mv /Work/_skills/pdf/ /Work/pdf/", Ct)).Output);
        Assert.False((await shell.RunAsync("cat /Work/_skills/pdf/SKILL.md", Ct)).IsError);
    }

    [Fact]
    public async Task One_text_file_moved_out_is_refused()
    {
        var shell = await SkillAsync();
        Assert.Equal("mv: /Work/run.py: run.py is a text file, kept only inside a skill's folder (/Skills/<skill>/ or /<vault>/_skills/<skill>/)",
            (await shell.RunAsync("mv /Work/_skills/pdf/scripts/run.py /Work/run.py", Ct)).Output);
    }

    [Fact]
    public async Task Moving__skills_itself_is_refused_while_it_holds_text_files()
    {
        var result = await (await SkillAsync()).RunAsync("mv /Work/_skills/ /Work/old/", Ct);
        Assert.StartsWith("mv: /Work/_skills/ can't move to /Work/old/:", result.Output);
        Assert.Contains("pdf/LICENSE, pdf/scripts/run.py", result.Output);
    }

    [Fact]
    public async Task Six_or_more_files_are_named_five_and_counted()
    {
        var shell = await SkillAsync("a1.py", "a2.py", "a3.py", "a4.py", "a5.py", "a6.py", "a7.py");
        Assert.Equal("mv: /Work/_skills/pdf/ can't move to /Work/pdf/: text files are kept only inside a skill's folder: a1.py, a2.py, a3.py, a4.py, a5.py and 2 more (move them into a skill's folder first, or remove them)",
            (await shell.RunAsync("mv /Work/_skills/pdf/ /Work/pdf/", Ct)).Output);
    }
}
