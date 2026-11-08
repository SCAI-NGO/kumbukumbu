// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Kumbu.Server.Accounts;
using Kumbu.Server.Attachments;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Attachments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using static Kumbu.Server.Tests.Attachments.PdfJobTests;

namespace Kumbu.Server.Tests.Mcp;

/// <summary><c>archive</c> over MCP <c>sh</c>, end to end: the stamp in the server's time zone, links following, <c>ls</c> hiding the archive.</summary>
[Collection("app")]
public sealed class ArchiveToolTests(KumbuAppFactory app)
{
    [Fact]
    public async Task An_archived_pdf_takes_the_time_in_its_name_and_its_link_along()
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        time.SetLocalTimeZone(berlin);
        await using var host = app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(time);
            s.RemoveAll<IPdfSidecar>();
            s.AddSingleton<IPdfSidecar>(new FakePdfSidecar());
        }));
        var user = await RestClient.NewAsync(host);
        await user.CreateFileAsync("Inbox/report.pdf", Pdf("a report"));
        await user.CreateAsync("plan.md", "# Plan\n\nSee the [report](/Inbox/report.pdf).\n");
        await user.CreateAsync("ideas.md", "# Ideas\n\nNothing to read yet.\n");
        var key = await user.KeyAsync("archive", KeyRight.Write);
        var vault = $"/{user.Vault}";
        var stamp = TimeZoneInfo.ConvertTime(time.GetUtcNow(), berlin).ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);
        // Berlin is never at UTC: the stamp is the server's local time, not UTC's.
        Assert.NotEqual(time.GetUtcNow().ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture), stamp);

        var archived = await user.ShAsync($"archive {vault}/Inbox/report.pdf", key);

        Assert.False(archived.IsError, archived.Text);
        Assert.Equal($"moved {vault}/Inbox/report.pdf to {vault}/_archive/Inbox/report {stamp}.pdf; rewrote 1 link in {vault}/plan.md", archived.Text);
        Assert.Equal(
            $"# Plan\n\nSee the [report](/_archive/Inbox/report%20{stamp.Replace(" ", "%20", StringComparison.Ordinal)}.pdf).\n",
            (await user.ReadAsync("plan.md"))["content"]!.GetValue<string>());
        Assert.Equal("# Ideas\n\nNothing to read yet.\n", (await user.ReadAsync("ideas.md"))["content"]!.GetValue<string>());

        var listed = await user.ShAsync($"ls {vault}", key);
        var all = await user.ShAsync($"ls --archive {vault}", key);
        var inside = await user.ShAsync($"ls {vault}/_archive/Inbox/", key);

        Assert.False(listed.IsError, listed.Text);
        Assert.DoesNotContain("_archive/", listed.Text);
        Assert.Contains("plan.md", listed.Text);
        Assert.Contains("_archive/", all.Text);
        Assert.Contains($"report {stamp}.pdf", inside.Text);
    }
}
