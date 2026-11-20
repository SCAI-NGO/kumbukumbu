// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Sh.Commands;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Attachments;

/// <summary>An agent's loop on an SVG: <c>write</c> it whole, read its source with <c>cat</c>, write it again.</summary>
public sealed class SvgWriteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly FakeTimeProvider Time = new(InMemoryNoteStore.Seeded.AddDays(1));

    private static VaultPath P(string s) => VaultPath.TryParse(s, out var p) ? p : throw new ArgumentException(s);

    private const string Drawing = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 200 100\"><rect width=\"200\" height=\"100\"/></svg>\n";

    private static (InMemoryNoteStore Store, NoteWriter Writer, Shell Shell) Make() => Make(out _);

    private static (InMemoryNoteStore Store, NoteWriter Writer, Shell Shell) Make(out Replacer replacer)
    {
        var store = new InMemoryNoteStore { SkillsVault = "Skills" };
        store.AddVault("Skills"); store.AddVault("Work");
        var caller = new FixedCaller(Writers.Dev);
        var writer = new NoteWriter(store, Time, caller);
        replacer = new Replacer(store, Time, new Plans(), caller);
        return (store, writer, new Shell(store, writer, new NoteMover(store, Time, caller)));
    }

    [Fact]
    public async Task Write_creates_an_svg_with_its_size_and_replaces_it_with_a_version()
    {
        var (store, writer, _) = Make();

        var created = await writer.WriteAsync("/Work/diagram.svg", Drawing, null, Ct);
        Assert.Equal("created /Work/diagram.svg", created.Output);
        var stored = (await store.GetAttachmentAsync(P("/Work/diagram.svg"), Ct))!.Current;
        Assert.Equal(("image/svg+xml", 200, 100), (stored.MediaType, stored.Width, stored.Height));
        Assert.Null(await store.GetAsync(P("/Work/diagram.svg"), Ct));

        var again = await writer.WriteAsync("/Work/diagram.svg", Drawing, null, Ct);
        Assert.Equal(ShellErrorKind.Conflict, again.Kind);
        Assert.StartsWith("write: /Work/diagram.svg already exists (version ", again.Output);

        var replaced = await writer.WriteAsync("/Work/diagram.svg", Drawing.Replace("200 100", "400 100"), created.Version!.Token, Ct);
        Assert.Equal("wrote /Work/diagram.svg", replaced.Output);
        Assert.Equal(400, (await store.GetAttachmentAsync(P("/Work/diagram.svg"), Ct))!.Current.Width);
    }

    [Fact]
    public async Task Write_says_what_is_wrong_with_what_is_no_svg()
    {
        var (store, writer, _) = Make();

        var html = await writer.WriteAsync("/Work/page.svg", "<html><body/></html>", null, Ct);
        var broken = await writer.WriteAsync("/Work/broken.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\">", null, Ct);

        Assert.Equal("write: page.svg is not an SVG: the root element is html", html.Output);
        Assert.Equal(ShellErrorKind.Invalid, html.Kind);
        Assert.StartsWith("write: broken.svg is not an SVG: it isn't well-formed XML (", broken.Output);
        Assert.Null(await store.GetAttachmentAsync(P("/Work/page.svg"), Ct));
    }

    [Fact]
    public async Task Edit_and_replace_point_to_write()
    {
        var (_, writer, _) = Make(out var replacer);
        await writer.WriteAsync("/Work/diagram.svg", Drawing, null, Ct);

        Assert.Equal("edit: diagram.svg is an SVG; replace it whole with write", (await writer.EditAsync("/Work/diagram.svg", "v", "200", "300", Ct)).Output);
        Assert.Equal("replace: diagram.svg is an SVG; replace it whole with write", (await replacer.RunAsync("/Work/diagram.svg", "200", "300", false, null, Ct)).Output);
    }

    [Fact]
    public async Task Cat_prints_the_line_and_then_the_source()
    {
        var (_, writer, shell) = Make();
        await writer.WriteAsync("/Work/diagram.svg", Drawing, null, Ct);

        Assert.Equal($"diagram.svg: image (SVG), 200×100, 101 B\n{Drawing}", (await shell.RunAsync("cat /Work/diagram.svg", Ct)).Output);
        Assert.Equal("diagram.svg  image 200×100 101 B", (await shell.RunAsync("ls /Work", Ct)).Output.Split('\n').Single(l => l.StartsWith("diagram", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Cat_gives_the_version_to_write_it_again_and_no_embeds_block()
    {
        var (store, writer, shell) = Make();
        await writer.WriteAsync("/Work/a.md", "# A\n", null, Ct);
        var drawing = Drawing.Replace("</svg>", "<text>![[a.md]]</text></svg>");
        await writer.WriteAsync("/Work/diagram.svg", drawing, null, Ct);

        var cat = await shell.RunAsync("cat /Work/diagram.svg", Ct);

        var stored = (await store.GetAttachmentAsync(P("/Work/diagram.svg"), Ct))!;
        Assert.Equal(new NoteVersion("/Work/diagram.svg", stored.Version), cat.Version);
        Assert.DoesNotContain(Cat.EmbedsHeader, cat.Output);
        Assert.Equal("wrote /Work/diagram.svg", (await writer.WriteAsync("/Work/diagram.svg", Drawing, cat.Version!.Token, Ct)).Output);
        Assert.Contains(new NoteVersion("/Work/diagram.svg", (await store.GetAttachmentAsync(P("/Work/diagram.svg"), Ct))!.Version),
            (await shell.RunAsync("cat /Work/diagram.svg /Work/a.md", Ct)).Versions!);
    }

    [Fact]
    public async Task A_folder_with_an_svg_isnt_copied_into_a_skills_folder()
    {
        var (store, writer, _) = Make();
        await writer.WriteAsync("/Work/Icons/a.svg", Drawing, null, Ct);
        var trees = new TreeWriter(store, Time, new FixedCaller(Writers.Dev));

        var copied = await trees.CopyAsync(P("/Work/Icons"), P("/Work/_skills/x/icons"), deep: true, overwrite: false, Ct);

        Assert.Equal(ShellErrorKind.Invalid, copied.Result.Kind);
        Assert.Contains("a.svg is an SVG image, and inside a skill's folder an .svg is a text file", copied.Result.Output);
        Assert.Null(await store.GetAttachmentAsync(P("/Work/_skills/x/icons/a.svg"), Ct));
        Assert.False((await trees.CopyAsync(P("/Work/Icons"), P("/Work/Copies"), deep: true, overwrite: false, Ct)).Result.IsError);
    }

    [Fact]
    public async Task Rg_doesnt_search_an_svg()
    {
        var (_, writer, shell) = Make();
        await writer.WriteAsync("/Work/diagram.svg", Drawing, null, Ct);

        Assert.DoesNotContain("diagram.svg", (await shell.RunAsync("rg viewBox /Work", Ct)).Output);
    }

    [Fact]
    public async Task Inside_a_skills_folder_an_svg_stays_text()
    {
        var (store, writer, shell) = Make();

        await writer.WriteAsync("/Skills/pdf/logo.svg", "not even XML", null, Ct);

        Assert.Equal(AttachmentFormat.TextMediaType, (await store.GetAttachmentAsync(P("/Skills/pdf/logo.svg"), Ct))!.Current.MediaType);
        Assert.Equal("not even XML", (await shell.RunAsync("cat /Skills/pdf/logo.svg", Ct)).Output);
    }

    private const string NotKept = "\nmessage not kept: an SVG's or a skill file's history keeps no messages";

    [Fact]
    public async Task A_message_on_an_SVG_write_is_not_kept_and_the_answer_says_so()
    {
        var (_, writer, _) = Make();

        var created = await writer.WriteAsync("/Work/diagram.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"/>", null, Ct, message: "first sketch");

        Assert.False(created.IsError, created.Output);
        Assert.Equal("created /Work/diagram.svg" + NotKept, created.Output);
        Assert.NotNull(created.Version);
    }

    [Fact]
    public async Task A_failed_SVG_write_with_a_message_answers_the_error_alone()
    {
        var (_, writer, _) = Make();

        var broken = await writer.WriteAsync("/Work/diagram.svg", "<svg", null, Ct, message: "first sketch");

        Assert.True(broken.IsError);
        Assert.DoesNotContain("message not kept", broken.Output);
    }

    [Fact]
    public async Task An_SVG_write_without_a_message_answers_as_before()
    {
        var (_, writer, _) = Make();

        Assert.Equal("created /Work/diagram.svg", (await writer.WriteAsync("/Work/diagram.svg", Drawing, null, Ct)).Output);
        Assert.Equal("created /Work/blank.svg", (await writer.WriteAsync("/Work/blank.svg", Drawing, null, Ct, message: "  ")).Output);
    }

    [Fact]
    public async Task An_unchanged_SVG_write_with_a_message_answers_as_before()
    {
        var (_, writer, _) = Make();
        var created = await writer.WriteAsync("/Work/diagram.svg", Drawing, null, Ct);

        var again = await writer.WriteAsync("/Work/diagram.svg", Drawing, created.Version!.Token, Ct, message: "no change");

        Assert.False(again.IsError, again.Output);
        Assert.Equal("/Work/diagram.svg is unchanged", again.Output);
    }
}
