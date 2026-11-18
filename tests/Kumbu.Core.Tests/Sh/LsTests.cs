// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Sh.Commands;
using Microsoft.Extensions.Time.Testing;
using Kumbu.Core.Tests.Fakes;

namespace Kumbu.Core.Tests.Sh;

public class LsTests
{
    private static readonly InMemoryNoteStore Reader = new(
        ("/Work/a.md", "A"), ("/Work/projects/x.md", "X"), ("/Work/projects/y.md", "Y"),
        ("/Workshop/w.md", "W"), ("/Ideas/Meeting Notes.md", "M"));

    private static Task<ShellResult> Run(params string[] args) =>
        new Ls(Reader).RunAsync(args, null, CancellationToken.None);

    [Fact]
    public void NameIsLs() => Assert.Equal("ls", new Ls(Reader).Name);

    [Theory]
    [InlineData]
    [InlineData("/")]
    public async Task ListsVaults(params string[] args) =>
        Assert.Equal(ShellResult.Ok("Ideas/\nWork/\nWorkshop/"), await Run(args));

    [Theory]
    [InlineData("/Work")]
    [InlineData("/Work/")]
    public async Task ListsDirectory(string arg)
    {
        var result = await Run(arg);
        Assert.Equal(ShellResult.Ok("a.md\nprojects/"), result);
        Assert.DoesNotContain("w.md", result.Output);
    }

    [Fact]
    public async Task NoteEchoesPath() =>
        Assert.Equal(ShellResult.Ok("/Work/projects/x.md"), await Run("/Work/projects/x.md"));

    [Fact]
    public async Task NotFound() =>
        Assert.Equal(ShellResult.Error("ls: cannot access '/Nope': No such file or directory"), await Run("/Nope"));

    [Fact]
    public async Task NormalizesDotDot() =>
        Assert.Equal(ShellResult.Ok("Meeting Notes.md"), await Run("/Work/../Ideas"));

    [Fact]
    public async Task WithoutVaultsItSaysSo() =>
        Assert.Equal(ShellResult.Ok("(no vaults)"), await new Ls(new InMemoryNoteStore()).RunAsync([], null, CancellationToken.None));

    [Fact]
    public async Task LockedVaultsAreMentioned()
    {
        var store = new InMemoryNoteStore { LockedVaults = 3 };
        Assert.Equal(
            ShellResult.Ok("(no vaults)\n(3 vaults in the database don't open with this token; the server log says how to recover them)"),
            await new Ls(store).RunAsync([], null, CancellationToken.None));
    }

    [Fact]
    public async Task RelativePath() =>
        Assert.Equal(ShellResult.Error("ls: Work/a.md: paths must be absolute (there is no working directory)"), await Run("Work/a.md"));

    [Fact]
    public async Task InvalidOption() =>
        Assert.Equal(ShellResult.Error("ls: invalid option -- 'x'"), await Run("-x", "/Work"));

    private static InMemoryNoteStore Timed()
    {
        var store = new InMemoryNoteStore(("/Work/a.md", "A"), ("/Work/projects/x.md", "Xä"), ("/Work/projects/y.md", "Y"), ("/Work/_archive/old.md", "O"));
        At(store, "/Work/a.md", new(2026, 11, 1, 9, 5, 0, DateTimeKind.Utc));
        At(store, "/Work/projects/x.md", new(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc));
        At(store, "/Work/projects/y.md", new(2026, 11, 2, 8, 30, 0, DateTimeKind.Utc));
        At(store, "/Work/_archive/old.md", new(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc));
        return store;
    }

    private static void At(InMemoryNoteStore store, string path, DateTime at)
    {
        var note = store.GetAsync(P(path), CancellationToken.None).Result!;
        store.Put(note with { LastWriteAt = at });
    }

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var p) ? p : throw new ArgumentException(path);

    private static Task<ShellResult> RunOn(InMemoryNoteStore store, params string[] args) =>
        new Ls(store).RunAsync(args, null, CancellationToken.None);

    [Fact]
    public async Task Long_lists_notes_with_size_and_time_and_folders_with_the_newest_change_below()
    {
        var result = await RunOn(Timed(), "-l", "/Work");

        Assert.Equal(ShellResult.Ok("a.md       1 B  2026-11-01 09:05 UTC\nprojects/       2026-11-03 10:00 UTC"), result);
    }

    [Fact]
    public async Task Long_counts_a_notes_size_in_utf8_bytes()
    {
        var result = await RunOn(Timed(), "-l", "/Work/projects");

        Assert.Equal(ShellResult.Ok("x.md  3 B  2026-11-03 10:00 UTC\ny.md  1 B  2026-11-02 08:30 UTC"), result);
    }

    [Fact]
    public async Task Long_shows_an_attachments_summary_and_its_time()
    {
        var store = Timed();
        var png = store.AddAttachment("/Work/shot.png");

        var result = await RunOn(store, "-l", "/Work");

        Assert.Contains($"shot.png   {AttachmentText.Summary(png.Current)}  2026-10-01 12:00 UTC", result.Output);
    }

    [Fact]
    public async Task Long_on_a_notes_path_lists_that_notes_line() =>
        Assert.Equal(ShellResult.Ok("/Work/projects/x.md  3 B  2026-11-03 10:00 UTC"), await RunOn(Timed(), "-l", "/Work/projects/x.md"));

    [Fact]
    public async Task Long_on_an_attachments_path_lists_its_line()
    {
        var store = Timed();
        var png = store.AddAttachment("/Work/shot.png");

        Assert.Equal(ShellResult.Ok($"/Work/shot.png  {AttachmentText.Summary(png.Current)}  2026-10-01 12:00 UTC"), await RunOn(store, "-l", "/Work/shot.png"));
    }

    [Fact]
    public async Task Long_at_the_root_adds_nothing() =>
        Assert.Equal(await RunOn(Timed(), "/"), await RunOn(Timed(), "-l", "/"));

    [Theory]
    [InlineData("-a")]
    [InlineData("--archive")]
    public async Task The_archive_is_listed_with_a(string flag)
    {
        Assert.DoesNotContain("_archive/", (await RunOn(Timed(), "/Work")).Output);
        Assert.Contains("_archive/", (await RunOn(Timed(), flag, "/Work")).Output);
    }

    [Theory]
    [InlineData("-la")]
    [InlineData("-al")]
    [InlineData("-l", "-a")]
    [InlineData("-l", "--archive")]
    public async Task Long_and_archive_combine(params string[] flags)
    {
        var result = await RunOn(Timed(), [.. flags, "/Work"]);

        Assert.Equal(ShellResult.Ok("_archive/       2026-12-01 00:00 UTC\na.md       1 B  2026-11-01 09:05 UTC\nprojects/       2026-11-03 10:00 UTC"), result);
    }

    [Fact]
    public async Task Long_leaves_a_newer_archive_out_of_the_listing_unless_asked()
    {
        var store = Timed();

        var without = (await RunOn(store, "-l", "/Work")).Output;
        var with = (await RunOn(store, "-la", "/Work")).Output;

        Assert.DoesNotContain("_archive/", without);
        Assert.DoesNotContain("2026-12-01", without);
        Assert.Contains("_archive/       2026-12-01 00:00 UTC", with);
    }

    [Fact]
    public async Task Long_on_a_checkpoint_shows_the_checkpoints_time_for_notes_and_the_note_itself()
    {
        var store = Timed();
        var time = new FakeTimeProvider(new DateTime(2026, 11, 20, 8, 15, 0, DateTimeKind.Utc));
        var writer = new NoteWriter(store, time, new FixedCaller(Writers.Dev));
        var shell = new Shell(store, writer, new NoteMover(store, time, new FixedCaller(Writers.Dev)), new Checkpoints(store, writer, new Plans()));
        await shell.RunAsync("mkcp /Work before", CancellationToken.None);
        time.Advance(TimeSpan.FromDays(1));

        Assert.Equal(
            "a.md       1 B  2026-11-20 08:15 UTC\nprojects/       2026-11-20 08:15 UTC",
            (await shell.RunAsync("ls -l --checkpoint before /Work", CancellationToken.None)).Output);
        Assert.Equal(
            "/Work/projects/x.md  3 B  2026-11-20 08:15 UTC",
            (await shell.RunAsync("ls -l --checkpoint before /Work/projects/x.md", CancellationToken.None)).Output);
    }

    [Fact]
    public async Task TooManyArguments() =>
        Assert.Equal(ShellResult.Error("ls: too many arguments"), await Run("/Work", "/Ideas"));

    [Fact]
    public async Task A_shared_vault_shows_its_owner_and_the_callers_level()
    {
        var store = SharedVaults.SharedGarten(asMember: true);
        store.Infos["Garten"] = new VaultInfo("\"Alice\"", AccessLevel.Read, true);
        store.AddVault("Work");

        var result = await TestShell.Of(store).RunAsync("ls", CancellationToken.None);

        Assert.Equal("Garten/ (shared by \"Alice\", read)\nWork/", result.Output);
    }
}
