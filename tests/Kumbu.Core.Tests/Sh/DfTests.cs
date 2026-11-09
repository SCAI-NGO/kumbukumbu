// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Sh;

/// <summary><c>df</c> (WebDAV design §3.12): what the caller's own vaults take, against her limit.</summary>
public class DfTests
{
    private const long Mb = 1024 * 1024;

    private const long TwoGb = 2L * 1024 * 1024 * 1024;

    /// <summary>182.0 MB and 41.3 MB, as in the spec's example.</summary>
    private static readonly VaultFigure[] Example =
    [
        new("Work", 43_306_189, 127, 9),
        new("Personal", 182 * Mb, 412, 38),
    ];

    private sealed class Figures(IReadOnlyList<VaultFigure> owned, long? limit, bool allOwned) : IStorageFigures
    {
        public Task<OwnFigures> OwnedAsync(CancellationToken ct) => Task.FromResult(new OwnFigures(owned, limit, allOwned));
    }

    private static Task<ShellResult> Run(string line, long? limit = TwoGb, InMemoryNoteStore? store = null, bool allOwned = true)
    {
        store ??= new InMemoryNoteStore(("/Work/a.md", "A"), ("/Personal/b.md", "B"));
        var time = new FakeTimeProvider(InMemoryNoteStore.Seeded);
        var caller = new FixedCaller(Writers.Dev);
        var shell = new Shell(store, new NoteWriter(store, time, caller), new NoteMover(store, time, caller), figures: new Figures(Example, limit, allOwned));
        return shell.RunAsync(line, CancellationToken.None);
    }

    [Fact]
    public async Task Df_lists_own_vaults_with_totals_and_limit()
    {
        Assert.Equal(
            ShellResult.Ok("""
                Vault        Used     Notes  Attachments
                /Personal    182.0 MB   412           38
                /Work         41.3 MB   127            9
                ──────────────────────────────────────────
                Total        223.3 MB  of 2.0 GB (11 %)
                """),
            await Run("df"));
    }

    [Fact]
    public async Task Df_without_a_limit_says_no_limit()
    {
        var result = await Run("df", limit: null);

        Assert.EndsWith("\nTotal        223.3 MB (no limit)", result.Output);
    }

    /// <summary>A key for some of the caller's vaults: the rows are those it opens, so a percentage of the limit would read
    /// as room the quota doesn't give (fix round 1, item 1).</summary>
    [Fact]
    public async Task Df_with_a_key_for_some_vaults_names_the_limit_without_a_percentage()
    {
        var result = await Run("df", allOwned: false);

        Assert.EndsWith("\n──────────────────────────────────────────\nTotal        223.3 MB  (vaults this key opens; your other vaults count against the 2.0 GB limit too)", result.Output);
        Assert.DoesNotContain("%", result.Output);
        Assert.EndsWith("\nTotal        223.3 MB (no limit)", (await Run("df", limit: null, allOwned: false)).Output);
    }

    [Theory]
    [InlineData(1023, "1023 B")]
    [InlineData(1024L * 1024 - 1, "1.0 MB")]
    [InlineData(1_073_636_966, "1023.9 MB")]
    [InlineData(1024L * 1024 * 1024 - 1, "1.0 GB")]
    [InlineData(1024L * 1024 * 1024, "1.0 GB")]
    [InlineData(2_147_483_648, "2.0 GB")]
    public void Sizes_go_from_MB_to_GB_at_1024_MB(long bytes, string shown)
    {
        Assert.Equal(shown, AttachmentText.Size(bytes));
        Assert.Equal(bytes.ToString(System.Globalization.CultureInfo.InvariantCulture), AttachmentText.Size(bytes, exact: true));
    }

    [Theory]
    [InlineData("df -h")]
    [InlineData("df --human-readable")]
    [InlineData("df -h /")]
    public async Task Df_dash_h_changes_nothing(string line) => Assert.Equal(await Run("df"), await Run(line));

    [Fact]
    public async Task Df_dash_b_gives_bytes()
    {
        Assert.Equal(
            ShellResult.Ok("""
                Vault        Used      Notes  Attachments
                /Personal    190840832   412           38
                /Work         43306189   127            9
                ───────────────────────────────────────────
                Total        234147021  of 2147483648 (11 %)
                """),
            await Run("df -b"));
    }

    [Fact]
    public async Task Df_of_one_vault()
    {
        Assert.Equal(
            ShellResult.Ok("""
                Vault    Used    Notes  Attachments
                /Work    41.3 MB   127            9
                """),
            await Run("df /Work"));
        Assert.Equal(ShellResult.Error("df: /Nope: No such vault"), await Run("df /Nope"));
        Assert.Equal(ShellResult.Error("df: /Work/projects: df shows whole vaults; du sizes a folder"), await Run("df /Work/projects"));
    }

    [Fact]
    public async Task Df_of_a_shared_vault_explains()
    {
        var store = new InMemoryNoteStore(("/Work/a.md", "A"), ("/garten/plan.md", "Beete planen\n"));
        store.Infos["garten"] = new VaultInfo("\"Alice\"", AccessLevel.Write, true);

        var result = await Run("df /garten", store: store);

        Assert.Equal(ShellResult.Error("df: /garten: df shows only vaults you own; a shared vault counts against its owner."), result);
        // Listed nowhere, not even as "n/a".
        Assert.DoesNotContain("garten", (await Run("df", store: store)).Output);
    }

    [Fact]
    public async Task Df_without_a_server_has_nothing_to_show()
    {
        var store = new InMemoryNoteStore(("/Work/a.md", "A"));
        Assert.Equal(ShellResult.Ok("(no vaults of your own)"), await TestShell.Of(store).RunAsync("df", CancellationToken.None));
    }

    [Fact]
    public async Task Help_df_and_help_du_explain_used()
    {
        var df = (await Run("help df")).Output;
        Assert.StartsWith("df [-h] [-b] [/vault]", df);
        Assert.Contains("history", df);
        Assert.Contains("trash", df);
        Assert.Contains("embeddings", df);
        Assert.Contains("counts against its owner", df);
        Assert.Contains("your other vaults count against the limit too", df);

        var du = (await Run("help du")).Output;
        Assert.StartsWith("du [-h] [-b] [-s] [-d N] [path]", du);
        Assert.Contains("smaller than its line in df", du);

        Assert.Contains("\n  df [-h] [-b] [/vault] ", Manual.ToolDescription);
        Assert.Contains("\n  du [-h] [-b] [-s] [-d N] [path] ", Manual.ToolDescription);
        Assert.Equal(ShellResult.Error("df: too many arguments" + Kumbu.Core.Sh.Commands.CommandArgs.QuoteHint), await Run("df /Work /Personal"));
    }
}
