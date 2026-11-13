// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Notes;

/// <summary>mv, archive and restore keep links by alias: the alias moves with its note (spec §1, §3.4).</summary>
public class AliasMoveTests
{
    private const string Kumbukumbu = "---\naliases: [Kumbu, K²]\n---\n# Kumbukumbu\n## Roadmap\n";
    private const string Diary = "Talked with Bob about [[Kumbu]] and the [[K²#Roadmap|roadmap]].\nKumbu needs a calendar.\n";
    private const string DiaryPath = "/Work/Diary/2026-11-12.md";

    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly InMemoryNoteStore _store = new((DiaryPath, Diary));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero));
    private readonly NoteWriter _writer;
    private readonly Shell _sh;

    public AliasMoveTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var caller = new FixedCaller(Writers.Dev);
        _writer = new NoteWriter(_store, _time, caller);
        _sh = new Shell(_store, _writer, new NoteMover(_store, _time, caller), new Checkpoints(_store, _writer, new Plans(), limit: 3));
    }

    private Task<ShellResult> Run(string line) => _sh.RunAsync(line, Ct);

    private static VaultPath P(string s) => VaultPath.TryParse(s, out var p) ? p : throw new ArgumentException(s);

    private async Task<string?> Content(string path) => await _store.ReadAsync(P(path), Ct);

    private Task Add(string path, string content) =>
        _store.TryCreateAsync(new StoredNote(P(path), content, 1, Writers.Dev, InMemoryNoteStore.Seeded, [], Ref: WriterRef.Server), Ct);

    private async Task Write(string path, string content)
    {
        var current = await Content(path);
        Assert.False((await _writer.WriteAsync(path, content, current is null ? null : VersionToken.Of(current), Ct)).IsError);
    }

    private async Task Ok(string line)
    {
        var result = await Run(line);
        Assert.False(result.IsError, result.Output);
    }

    private static string Plan(ShellResult preview) =>
        preview.Output.Split('\n').Single(l => l.StartsWith("plan: ", StringComparison.Ordinal))["plan: ".Length..];

    [Fact]
    public async Task Moving_an_aliased_note_leaves_its_alias_links()
    {
        await Add("/Work/Projects/Kumbukumbu.md", Kumbukumbu);

        await Ok("mv /Work/Projects/Kumbukumbu.md /Work/Done/Kumbukumbu.md");

        Assert.Equal(Diary, await Content(DiaryPath));
        Assert.Equal(
            "1: [[Kumbu]] → /Work/Done/Kumbukumbu.md (alias)\n1: [[K²#Roadmap|roadmap]] → /Work/Done/Kumbukumbu.md (alias)",
            (await Run($"links {DiaryPath}")).Output);
    }

    [Fact]
    public async Task A_link_by_name_to_a_moved_aliased_note_is_rewritten_as_today()
    {
        await Add("/Work/Projects/Kumbukumbu.md", Kumbukumbu);
        await Write(DiaryPath, Diary + "See [[Projects/Kumbukumbu]].\n");

        await Ok("mv /Work/Projects/Kumbukumbu.md /Work/Done/Kumbukumbu.md");

        Assert.Equal(Diary + "See [[Done/Kumbukumbu]].\n", await Content(DiaryPath));
    }

    [Fact]
    public async Task A_note_renamed_onto_an_alias_rewrites_the_alias_link_to_its_note()
    {
        await Add("/Work/Projects/Kumbukumbu.md", Kumbukumbu);
        await Add("/Work/Ideas/Other.md", "# Other\n");

        await Ok("mv /Work/Ideas/Other.md /Work/Ideas/Kumbu.md");

        // The link would now lead to the renamed note by its name; it keeps its note by the vault path instead.
        Assert.Equal(Diary.Replace("[[Kumbu]]", "[[Projects/Kumbukumbu]]", StringComparison.Ordinal), await Content(DiaryPath));
    }

    [Fact]
    public async Task Archiving_a_note_whose_alias_a_living_note_shares()
    {
        await Add("/Work/Diary/Kumbukumbu.md", "---\naliases: [Kumbu]\n---\n");
        await Add("/Work/Projects/Kumbu2.md", "---\naliases: [Kumbu]\n---\n");
        Assert.StartsWith("1: [[Kumbu]] → /Work/Diary/Kumbukumbu.md (alias)", (await Run($"links {DiaryPath}")).Output, StringComparison.Ordinal);

        await Ok("archive /Work/Diary/Kumbukumbu.md");

        // In the archive the note counts by alias only if no living one matches; the link must not slip to Kumbu2.
        Assert.DoesNotContain("[[Kumbu]]", (await Content(DiaryPath))!, StringComparison.Ordinal);
        Assert.StartsWith(
            "1: [[Kumbukumbu 2026-11-12 100000]] → /Work/_archive/Diary/Kumbukumbu 2026-11-12 100000.md\n",
            (await Run($"links {DiaryPath}")).Output,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restoring_a_checkpoint_keeps_alias_links()
    {
        await Add("/Work/Projects/Kumbukumbu.md", Kumbukumbu);
        await Ok("mkcp /Work/Projects before");
        _time.Advance(TimeSpan.FromMinutes(1));
        await Ok("mv /Work/Projects/Kumbukumbu.md /Work/Done/Kumbukumbu.md");

        var preview = await Run("restore --checkpoint before /Work/Projects");
        Assert.Contains("moved back  /Work/Done/Kumbukumbu.md → /Work/Projects/Kumbukumbu.md", preview.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(DiaryPath, preview.Output, StringComparison.Ordinal);
        await Ok($"restore --checkpoint before --plan {Plan(preview)} /Work/Projects");

        Assert.Equal(Diary, await Content(DiaryPath));
        Assert.Equal(
            "1: [[Kumbu]] → /Work/Projects/Kumbukumbu.md (alias)\n1: [[K²#Roadmap|roadmap]] → /Work/Projects/Kumbukumbu.md (alias)",
            (await Run($"links {DiaryPath}")).Output);
    }

    [Fact]
    public async Task Restoring_reports_an_alias_link_that_loses_its_note()
    {
        await Add("/Work/Projects/Plan.md", "# Plan\n");
        await Ok("mkcp /Work/Projects before");
        await Add("/Work/Projects/Kumbukumbu.md", Kumbukumbu);

        var lines = (await Run("restore --checkpoint before /Work/Projects")).Output.Split('\n');

        Assert.Contains($"  {DiaryPath}:1: [[Kumbu]] (now /Work/Projects/Kumbukumbu.md, going to the trash)", lines);
        Assert.Contains($"  {DiaryPath}:1: [[K²#Roadmap|roadmap]] (now /Work/Projects/Kumbukumbu.md, going to the trash)", lines);
    }

    [Fact]
    public async Task Restoring_the_aliases_a_note_lost_keeps_a_link_from_leading_nowhere()
    {
        await Add("/Work/Projects/Kumbukumbu.md", Kumbukumbu);
        await Ok("mkcp /Work/Projects before");
        await Write("/Work/Projects/Kumbukumbu.md", "# Kumbukumbu\n");
        await Add("/Work/Projects/Kumbu.md", "# Kumbu\n");

        // [[Kumbu]] leads to Kumbu.md now, which goes to the trash; the restored aliases of Kumbukumbu.md catch it.
        var preview = await Run("restore --checkpoint before /Work/Projects");

        Assert.Contains("to the trash", preview.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("lead nowhere", preview.Output, StringComparison.Ordinal);
    }
}
