// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Sh;

/// <summary>mv and cp of a skill's text files, which leave its folder as files, and of files into a skill's folder, which take text only.</summary>
public sealed class SkillFolderMoveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly FakeTimeProvider Time = new(InMemoryNoteStore.Seeded.AddDays(1));

    private static VaultPath P(string s) => VaultPath.TryParse(s, out var p) ? p : throw new ArgumentException(s);

    private static async Task<(Shell Shell, InMemoryNoteStore Store)> SkillAsync(params string[] files)
    {
        var store = new InMemoryNoteStore(("/Work/_skills/pdf/SKILL.md", "---\nname: pdf\ndescription: Reads PDFs.\n---\n")) { SkillsVault = "Skills" };
        store.AddVault("Skills"); store.AddVault("Work");
        var time = Time;
        var writer = new NoteWriter(store, time, new FixedCaller(Writers.Dev));
        foreach (var file in files.Length > 0 ? files : ["scripts/run.py", "LICENSE"])
        {
            await writer.WriteAsync($"/Work/_skills/pdf/{file}", "print(1)\n", null, Ct);
        }

        return (new Shell(store, writer, new NoteMover(store, time, new FixedCaller(Writers.Dev))), store);
    }

    [Fact]
    public async Task A_skill_folder_moves_within__skills_with_its_text_files()
    {
        var (shell, _) = await SkillAsync();
        Assert.False((await shell.RunAsync("mv /Work/_skills/pdf/ /Work/_skills/pdf2/", Ct)).IsError);
        Assert.Equal("print(1)\n", (await shell.RunAsync("cat /Work/_skills/pdf2/scripts/run.py", Ct)).Output);
    }

    [Fact]
    public async Task A_skill_folder_moved_out_takes_its_text_files_along_as_files()
    {
        var (shell, store) = await SkillAsync();
        Assert.False((await shell.RunAsync("mv /Work/_skills/pdf/ /Work/pdf/", Ct)).IsError);
        Assert.Equal("print(1)\n", (await shell.RunAsync("cat /Work/pdf/scripts/run.py", Ct)).Output);
        Assert.Equal(AttachmentFormat.TextMediaType, (await store.GetAttachmentAsync(P("/Work/pdf/LICENSE"), Ct))!.Current.MediaType);
        Assert.Equal("file", AttachmentFormat.KindOf("/Work/pdf/LICENSE"));
    }

    [Fact]
    public async Task One_text_file_moved_out_becomes_a_file()
    {
        var (shell, store) = await SkillAsync();
        Assert.False((await shell.RunAsync("mv /Work/_skills/pdf/scripts/run.py /Work/", Ct)).IsError);
        Assert.Equal(AttachmentFormat.TextMediaType, (await store.GetAttachmentAsync(P("/Work/run.py"), Ct))!.Current.MediaType);
        Assert.False((await shell.RunAsync("mv /Work/_skills/ /Work/old/", Ct)).IsError);
    }

    [Fact]
    public async Task A_skill_folder_copied_out_keeps_its_text_files_as_files()
    {
        var (_, store) = await SkillAsync();
        var trees = new TreeWriter(store, Time, new FixedCaller(Writers.Dev));

        Assert.False((await trees.CopyAsync(P("/Work/_skills/pdf"), P("/Work/backup"), deep: true, overwrite: false, Ct)).Result.IsError);
        Assert.Equal(AttachmentFormat.TextMediaType, (await store.GetAttachmentAsync(P("/Work/backup/scripts/run.py"), Ct))!.Current.MediaType);
    }

    [Fact]
    public async Task A_file_goes_into_a_skill_folder_under_a_text_name_only_if_it_is_text()
    {
        var (shell, store) = await SkillAsync();
        var files = new AttachmentWriter(store, store, Time, new FixedCaller(Writers.Dev));
        Assert.False((await files.WriteAsync("/Work/notes.txt", "some text\n"u8.ToArray(), null, null, Ct)).Result.IsError);
        Assert.False((await files.WriteAsync("/Work/blob.txt", "a\0b"u8.ToArray(), null, null, Ct)).Result.IsError);

        Assert.False((await shell.RunAsync("mv /Work/notes.txt /Work/_skills/pdf/", Ct)).IsError);
        Assert.Equal("text", AttachmentFormat.KindOf("/Work/_skills/pdf/notes.txt"));
        Assert.Equal($"mv: /Work/_skills/pdf/blob.txt: blob.txt isn't text; {AttachmentFormat.SkillFolderMessage}",
            (await shell.RunAsync("mv /Work/blob.txt /Work/_skills/pdf/", Ct)).Output);
    }

    [Fact]
    public async Task A_skills_vault_of_another_name_keeps_no_binary_file()
    {
        var store = new InMemoryNoteStore { SkillsVault = "Toolbox" };
        store.AddVault("Toolbox"); store.AddVault("Work");
        var caller = new FixedCaller(Writers.Dev);
        var files = new AttachmentWriter(store, store, Time, caller);
        var shell = new Shell(store, new NoteWriter(store, Time, caller), new NoteMover(store, Time, caller));

        var upload = await files.WriteAsync("/Toolbox/a/tool.bin", "a\0b"u8.ToArray(), null, null, Ct);
        Assert.Equal($"upload: tool.bin: {AttachmentFormat.SkillFolderMessage}", upload.Result.Output);
        Assert.Equal("upload: run.py contains a NUL byte; a skill's folder keeps text only",
            (await files.WriteAsync("/Toolbox/a/run.py", "a\0b"u8.ToArray(), null, null, Ct)).Result.Output);
        Assert.False((await files.WriteAsync("/Toolbox/a/run.py", "print(1)\n"u8.ToArray(), null, null, Ct)).Result.IsError);
        Assert.False((await files.WriteAsync("/Toolbox/blob.bin", "a\0b"u8.ToArray(), null, null, Ct)).Result.IsError);
        Assert.Equal($"mv: /Toolbox/a/blob.bin: blob.bin: {AttachmentFormat.SkillFolderMessage}", (await shell.RunAsync("mv /Toolbox/blob.bin /Toolbox/a/", Ct)).Output);
    }
}
