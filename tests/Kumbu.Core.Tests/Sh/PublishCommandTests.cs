// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Publishing;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Sh;

/// <summary><c>publish</c> (publish spec §3.9): the link once, after <c>add</c>, never in <c>ls</c>; a refusal keeps its code.</summary>
public class PublishCommandTests
{
    private const string Link = "https://kumbu.example.org/s#K7QM-4XDA-0000-1111-2222-3333-4444-5555";

    private static readonly DateTime At = new(2026, 11, 5, 14, 32, 7, DateTimeKind.Utc);

    private sealed class FakePublisher : IPublisher
    {
        public List<string> Calls { get; } = [];

        public IReadOnlyList<PublicationLine> Lines { get; init; } = [];

        public IReadOnlyList<string> Plain { get; init; } = [];

        public Exception? Throws { get; init; }

        public Task<IReadOnlyList<PublicationLine>> ListAsync(VaultPath under, CancellationToken ct)
        {
            Calls.Add($"ls {under}");
            return Throws is null ? Task.FromResult(Lines) : Task.FromException<IReadOnlyList<PublicationLine>>(Throws);
        }

        public Task<PublishOutcome> AddAsync(VaultPath note, CancellationToken ct)
        {
            Calls.Add($"add {note}");
            return Throws is null ? Task.FromResult(new PublishOutcome(Link, At, Plain)) : Task.FromException<PublishOutcome>(Throws);
        }

        public Task<PublishOutcome> UpdateAsync(VaultPath note, CancellationToken ct)
        {
            Calls.Add($"update {note}");
            return Throws is null ? Task.FromResult(new PublishOutcome(null, At.AddHours(1).AddMinutes(38), Plain)) : Task.FromException<PublishOutcome>(Throws);
        }

        public Task WithdrawAsync(VaultPath note, CancellationToken ct)
        {
            Calls.Add($"rm {note}");
            return Throws is null ? Task.CompletedTask : Task.FromException(Throws);
        }
    }

    private static Task<ShellResult> Run(IPublisher? publisher, string line)
    {
        var store = new InMemoryNoteStore();
        var time = new FakeTimeProvider(InMemoryNoteStore.Seeded);
        var caller = new FixedCaller(Writers.Dev);
        var shell = new Shell(store, new NoteWriter(store, time, caller), new NoteMover(store, time, caller), publisher: publisher);
        return shell.RunAsync(line, CancellationToken.None);
    }

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var p) ? p : throw new ArgumentException(path);

    [Fact]
    public async Task Add_prints_the_link_once_and_what_became_text()
    {
        var publisher = new FakePublisher { Plain = ["report.pdf", "missing.png", "PDFs are not published yet"] };

        var result = await Run(publisher, "publish add /Work/Routing/Questions.md");

        Assert.Equal(
            ShellResult.Ok($"""
                published /Work/Routing/Questions.md as a copy of 2026-11-05 14:32 UTC
                link (shown once): {Link}
                not copied, plain text instead: report.pdf, missing.png; PDFs are not published yet
                """),
            result);
        Assert.Equal(["add /Work/Routing/Questions.md"], publisher.Calls);

        var clean = await Run(new FakePublisher(), "publish add /Work/Routing/Questions.md");
        Assert.Equal(ShellResult.Ok($"published /Work/Routing/Questions.md as a copy of 2026-11-05 14:32 UTC\nlink (shown once): {Link}"), clean);

        var image = await Run(new FakePublisher { Plain = ["flow.png"] }, "publish add /Work/Routing/Questions.md");
        Assert.EndsWith("\nnot copied, plain text instead: flow.png", image.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_and_rm_say_what_happened_without_the_link()
    {
        var publisher = new FakePublisher();

        var updated = await Run(publisher, "publish update /Work/Routing/Questions.md");
        var withdrawn = await Run(publisher, "publish rm /Work/Routing/Questions.md");

        Assert.Equal(ShellResult.Ok("updated the published copy of /Work/Routing/Questions.md (2026-11-05 16:10 UTC); the link stays the same"), updated);
        Assert.Equal(ShellResult.Ok("withdrew the publication of /Work/Routing/Questions.md; its link reads nothing now"), withdrawn);
        Assert.Equal(["update /Work/Routing/Questions.md", "rm /Work/Routing/Questions.md"], publisher.Calls);
    }

    [Fact]
    public async Task Ls_never_prints_a_link()
    {
        var publisher = new FakePublisher
        {
            Lines =
            [
                new PublicationLine(P("/Work/Routing/Questions.md"), At, false),
                new PublicationLine(P("/Work/Ideas.md"), At.AddDays(-1), true),
            ],
        };

        var result = await Run(publisher, "publish ls /Work");

        Assert.Equal(
            ShellResult.Ok("""
                /Work/Ideas.md  2026-11-04 14:32 UTC  older: yes
                /Work/Routing/Questions.md  2026-11-05 14:32 UTC  older: no
                """),
            result);
        Assert.DoesNotContain("/s#", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("link", result.Output, StringComparison.Ordinal);
        Assert.Equal(["ls /Work"], publisher.Calls);
    }

    [Fact]
    public async Task Ls_defaults_to_every_vault_and_says_when_there_are_none()
    {
        var publisher = new FakePublisher();

        Assert.Equal(ShellResult.Ok("no published notes below /"), await Run(publisher, "publish ls"));
        Assert.Equal(ShellResult.Ok("no published notes below /Work"), await Run(publisher, "publish ls /Work"));
        Assert.Equal(["ls /", "ls /Work"], publisher.Calls);
    }

    [Fact]
    public async Task A_members_ls_is_paths_alone()
    {
        var publisher = new FakePublisher
        {
            Lines = [new PublicationLine(P("/alices-work/b.md"), null, null), new PublicationLine(P("/alices-work/a.md"), null, null)],
        };

        Assert.Equal(ShellResult.Ok("/alices-work/a.md\n/alices-work/b.md"), await Run(publisher, "publish ls /alices-work"));
    }

    [Fact]
    public async Task A_refusal_keeps_its_code()
    {
        const string sentence = "this key may not publish in Work; its owner can allow it (\"May publish\") under API keys or Connections in the web app";
        var publisher = new FakePublisher { Throws = new PublishRefusedException(sentence, ShellErrorKind.Refused, Refusals.NoPublishRight) };

        var result = await Run(publisher, "publish add /Work/a.md");

        Assert.True(result.IsError);
        Assert.Equal($"publish add: {sentence}", result.Output);
        Assert.Equal(Refusals.NoPublishRight, result.Code);
        Assert.Equal(ShellErrorKind.Refused, result.Kind);
    }

    [Fact]
    public async Task Other_failures_keep_their_kind()
    {
        var publisher = new FakePublisher { Throws = new PublishRefusedException("/Work/a.md is not published", ShellErrorKind.NotFound) };

        var result = await Run(publisher, "publish rm /Work/a.md");

        Assert.Equal(ShellResult.Error("publish rm: /Work/a.md is not published") with { Kind = ShellErrorKind.NotFound }, result);
    }

    [Theory]
    [InlineData("publish")]
    [InlineData("publish show /Work/a.md")]
    [InlineData("publish LS")]
    public async Task Without_a_subcommand_it_says_how(string line)
    {
        var publisher = new FakePublisher();

        var result = await Run(publisher, line);

        Assert.Equal(ShellResult.Error("publish: give ls, add, update or rm (help publish)") with { Kind = ShellErrorKind.Invalid }, result);
        Assert.Empty(publisher.Calls);
    }

    [Theory]
    [InlineData("publish add", "publish add: missing operand (publish add <note>)")]
    [InlineData("publish rm /Work/a.md /Work/b.md", "publish rm: too many arguments")]
    [InlineData("publish ls /Work /Ideas", "publish ls: too many arguments")]
    [InlineData("publish update Work/a.md", "publish update: Work/a.md: paths must be absolute (there is no working directory)")]
    public async Task Operands_are_checked_before_anything_runs(string line, string error)
    {
        var publisher = new FakePublisher();

        var result = await Run(publisher, line);

        Assert.True(result.IsError);
        Assert.StartsWith(error, result.Output, StringComparison.Ordinal);
        Assert.Empty(publisher.Calls);
    }

    [Theory]
    [InlineData("publish ls /Work")]
    [InlineData("publish add /Work/a.md")]
    public async Task Without_a_publisher_it_says_so(string line) =>
        Assert.Equal(ShellResult.Error("publish: publishing isn't available here"), await Run(null, line));

    [Fact]
    public async Task Help_names_every_subcommand()
    {
        var help = (await Run(null, "help publish")).Output;

        Assert.Equal(
            """
            publish ls [path]        the published notes below a path; a link is never shown again
            publish add <note>       publish a copy of one note behind a secret link, shown once
            publish update <note>    replace the copy with the note as it is now; the link stays
            publish rm <note>        withdraw the publication; its link reads nothing from then on
            Only the vault's owner publishes, with a key or connection she allowed to ("May publish").
            Links to other notes become plain text; frontmatter, tags, comments and images' metadata
            stay behind. PDFs are not published yet. Notes and folders the owner marked unpublishable
            can't be published at all.
            """,
            help.TrimEnd());
        Assert.Contains("\n  publish ls|add|update|rm <note> share one note as a read-only copy behind a secret link\n", Manual.ToolDescription);
    }
}
