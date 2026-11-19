// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Attachments;

public sealed class TextAttachmentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly FakeTimeProvider Time = new(InMemoryNoteStore.Seeded.AddDays(1));

    private static VaultPath P(string s) => VaultPath.TryParse(s, out var p) ? p : throw new ArgumentException(s);

    private static (InMemoryNoteStore Store, NoteWriter Writer, AttachmentWriter Files, Shell Shell) Make(string? skillsVault = "Skills", Func<string, bool>? canWrite = null)
    {
        var store = new InMemoryNoteStore { SkillsVault = skillsVault };
        store.AddVault("Skills"); store.AddVault("Work"); store.AddVault("Tools");
        var caller = new FixedCaller(Writers.Dev, canWrite);
        var writer = new NoteWriter(store, Time, caller);
        return (store, writer, writer.ForAttachments()!, new Shell(store, writer, new NoteMover(store, Time, caller)));
    }

    [Fact]
    public async Task An_upload_into_a_skill_folder_is_stored_as_text()
    {
        var (_, _, files, _) = Make();
        var result = await files.WriteAsync("/Skills/pdf/scripts/run.py", "print(1)\n"u8.ToArray(), null, null, Ct);
        Assert.False(result.Result.IsError, result.Result.Output);
        Assert.Equal(AttachmentFormat.TextMediaType, result.Attachment!.Current.MediaType);
    }

    [Fact]
    public async Task An_unmarked_vault_named_Skills_keeps_files()
    {
        var (store, _, files, _) = Make(skillsVault: null);
        Assert.False((await files.WriteAsync("/Skills/pdf/run.py", "print(1)\n"u8.ToArray(), null, null, Ct)).Result.IsError);
        Assert.False((await files.WriteAsync("/Skills/pdf/tool.bin", "a\0b"u8.ToArray(), null, null, Ct)).Result.IsError);
        Assert.Equal(AttachmentFormat.TextMediaType, (await store.GetAttachmentAsync(P("/Skills/pdf/run.py"), Ct))!.Current.MediaType);
    }

    [Fact]
    public async Task A_vaults__skills_folder_needs_no_mark()
    {
        var (_, _, files, _) = Make(skillsVault: null);
        Assert.False((await files.WriteAsync("/Work/_skills/x/a.py", "x\n"u8.ToArray(), null, null, Ct)).Result.IsError);
    }

    [Fact]
    public async Task Write_creates_and_replaces_a_text_file_with_a_version()
    {
        var (store, writer, _, _) = Make();
        var created = await writer.WriteAsync("/Skills/pdf/run.py", "print(1)\n", null, Ct);
        Assert.Equal("created /Skills/pdf/run.py", created.Output);
        Assert.NotNull(created.Version);
        var replaced = await writer.WriteAsync("/Skills/pdf/run.py", "print(2)\n", created.Version!.Token, Ct);
        Assert.Equal("wrote /Skills/pdf/run.py", replaced.Output);
        Assert.Null(await store.GetAsync(P("/Skills/pdf/run.py"), Ct));           // never stored as a note
    }

    [Fact]
    public async Task A_message_on_a_skill_text_file_write_is_not_kept_and_the_answer_says_so()
    {
        var (_, writer, _, _) = Make();
        var created = await writer.WriteAsync("/Skills/pdf/run.py", "print(1)\n", null, Ct);

        var replaced = await writer.WriteAsync("/Skills/pdf/run.py", "print(2)\n", created.Version!.Token, Ct, message: "print two");

        Assert.False(replaced.IsError, replaced.Output);
        Assert.Equal("wrote /Skills/pdf/run.py\nmessage not kept: an SVG's or a skill file's history keeps no messages", replaced.Output);
    }

    [Fact]
    public async Task Write_outside_a_skill_folder_answers_as_before()
    {
        var (_, writer, _, _) = Make();
        Assert.Equal("write: /Work/run.py: note names end in .md; write keeps notes, SVGs and a skill's text files, and other files are uploaded (the upload tool)", (await writer.WriteAsync("/Work/run.py", "x", null, Ct)).Output);
    }

    [Fact]
    public async Task Write_creates_a_text_file_in_a_skill_of_a_marked_vault_of_another_name()
    {
        var (store, writer, _, _) = Make(skillsVault: "Tools");
        var created = await writer.WriteAsync("/Tools/x/scripts/run.py", "print(1)\n", null, Ct);
        Assert.False(created.IsError, created.Output);
        Assert.Equal("created /Tools/x/scripts/run.py", created.Output);
        Assert.Equal(AttachmentFormat.TextMediaType, (await store.GetAttachmentAsync(P("/Tools/x/scripts/run.py"), Ct))!.Current.MediaType);
    }

    [Fact]
    public async Task Edit_refuses_a_text_file_in_a_skill_of_a_marked_vault_of_another_name()
    {
        var (_, writer, files, _) = Make(skillsVault: "Tools");
        var seeded = await files.WriteAsync("/Tools/x/run.py", "print(1)\n"u8.ToArray(), null, null, Ct);
        Assert.False(seeded.Result.IsError, seeded.Result.Output);
        var edited = await writer.EditAsync("/Tools/x/run.py", seeded.Result.Version!.Token, null, "x", Ct);
        Assert.Equal("edit: run.py is a text file in a skill; replace it whole with write", edited.Output);
    }

    [Fact]
    public async Task Replace_refuses_a_text_file_in_a_skill_of_a_marked_vault_of_another_name()
    {
        var (store, _, files, _) = Make(skillsVault: "Tools");
        await files.WriteAsync("/Tools/x/run.py", "print(1)\n"u8.ToArray(), null, null, Ct);
        var replaced = await new Replacer(store, Time, new Plans(), new FixedCaller(Writers.Dev)).RunAsync("/Tools/x/run.py", "1", "2", false, null, Ct);
        Assert.Equal("replace: run.py is a text file in a skill; replace it whole with write", replaced.Output);
    }

    [Fact]
    public async Task Write_refuses_a_text_name_in_an_unmarked_vault_named_Skills_as_anywhere()
    {
        var (store, writer, _, _) = Make(skillsVault: null);
        var work = await writer.WriteAsync("/Work/x/run.py", "print(1)\n", null, Ct);
        var skills = await writer.WriteAsync("/Skills/x/run.py", "print(1)\n", null, Ct);
        Assert.Equal("write: /Work/x/run.py: note names end in .md; write keeps notes, SVGs and a skill's text files, and other files are uploaded (the upload tool)", work.Output);
        Assert.Equal("write: /Skills/x/run.py: note names end in .md; write keeps notes, SVGs and a skill's text files, and other files are uploaded (the upload tool)", skills.Output);
        Assert.Null(await store.GetAttachmentAsync(P("/Skills/x/run.py"), Ct));
    }

    [Theory]
    [InlineData("/Tools/run.py")]
    [InlineData("/Tools/x/tool.bin")]
    public async Task Write_keeps_refusing_what_upload_refuses_in_a_marked_vault(string path)
    {
        var (store, writer, _, _) = Make(skillsVault: "Tools");
        var written = await writer.WriteAsync(path, "print(1)\n", null, Ct);
        Assert.True(written.IsError, written.Output);
        Assert.Equal($"write: {path}: note names end in .md; write keeps notes, SVGs and a skill's text files, and other files are uploaded (the upload tool)", written.Output);
        Assert.Null(await store.GetAttachmentAsync(P(path), Ct));
        Assert.Null(await store.GetAsync(P(path), Ct));
    }

    [Fact]
    public async Task Write_refuses_a_read_only_vault()
    {
        var (store, writer, _, _) = Make(canWrite: v => v != "Skills");
        Assert.True((await writer.WriteAsync("/Skills/pdf/run.py", "x", null, Ct)).IsError);
        Assert.Null(await store.GetAttachmentAsync(P("/Skills/pdf/run.py"), Ct));
    }

    [Fact]
    public async Task Edit_points_a_text_file_to_write()
    {
        var (_, writer, _, _) = Make();
        await writer.WriteAsync("/Skills/pdf/run.py", "print(1)\n", null, Ct);
        Assert.Equal("edit: run.py is a text file in a skill; replace it whole with write", (await writer.EditAsync("/Skills/pdf/run.py", "v", null, "x", Ct)).Output);
    }

    [Fact]
    public async Task Cat_prints_a_text_file_and_ls_lists_it()
    {
        var (_, writer, _, shell) = Make();
        await writer.WriteAsync("/Skills/pdf/run.py", "print(1)\n", null, Ct);
        Assert.Equal("print(1)\n", (await shell.RunAsync("cat /Skills/pdf/run.py", Ct)).Output);
        Assert.Equal("run.py  text 9 B", (await shell.RunAsync("ls /Skills/pdf", Ct)).Output);
    }

    [Fact]
    public async Task Invalid_utf8_is_refused()
    {
        var (_, _, files, _) = Make();
        var result = (await files.WriteAsync("/Skills/pdf/a.py", new byte[] { 0xC3, 0x28 }, null, null, Ct)).Result;
        Assert.True(result.IsError);
        Assert.Equal(ShellErrorKind.Invalid, result.Kind);
        Assert.Contains("isn't UTF-8 text", result.Output);
    }

    [Fact]
    public async Task Write_errors_name_write_not_upload()
    {
        var (_, writer, _, _) = Make(canWrite: v => v != "Work");
        var refused = await writer.WriteAsync("/Work/_skills/x/a.py", "x", null, Ct);
        Assert.StartsWith("write:", refused.Output);

        await writer.WriteAsync("/Skills/pdf/run.py", "1\n", null, Ct);
        var exists = await writer.WriteAsync("/Skills/pdf/run.py", "2\n", null, Ct);
        Assert.True(exists.IsError);
        Assert.StartsWith("write:", exists.Output);
        Assert.DoesNotContain("upload", exists.Output);
    }

    [Fact]
    public async Task A_long_text_file_is_cut_by_the_output_limit_never_outlined()
    {
        var (_, writer, _, shell) = Make();
        await writer.WriteAsync("/Skills/pdf/run.py", string.Concat(Enumerable.Repeat("# a comment that looks like a heading\n", 2000)), null, Ct);
        var output = (await shell.RunAsync("cat /Skills/pdf/run.py", Ct)).Output;
        Assert.StartsWith("# a comment that looks like a heading\n# a comment", output);
        Assert.DoesNotContain("note too long", output);
        Assert.Contains("[truncated", output);
    }

    [Fact]
    public async Task Cat_marks_a_text_file_another_user_wrote_last()
    {
        var store = SharedVaults.SharedGarten(asMember: true);
        store.AddVault("Garten");
        var other = new NoteWriter(store, Time, new FixedCaller(Writers.Dev));
        await other.WriteAsync("/Garten/_skills/x/run.py", "print(1)\n", null, Ct);
        var result = await SharedVaults.AsBob(store).RunAsync("cat /Garten/_skills/x/run.py", Ct);
        Assert.Equal("[/Garten/_skills/x/run.py: last written by another user]\nprint(1)\n", result.Output);
    }
}
