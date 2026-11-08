// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO.Compression;
using System.Net;
using Kumbu.Server.Sharing;
using Kumbu.Server.Skills;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Sharing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Server.Tests.Skills;

/// <summary><c>GET /api/skills/zip</c> takes a skill out as the files ZIP takes files out: the ticket when the user asks
/// for the password for downloads or the password window is closed, an event for the owner when a member takes a skill
/// of her vault, a log line, and a skill's limits (200 files, 10 MB) checked before its content is read; Copy to… and
/// Move to… read within the same limits.</summary>
[Collection("app")]
public sealed class SkillZipApiTests(KumbuAppFactory app)
{
    private const string Manifest = "---\nname: deploy\ndescription: Deploys.\n---\n# Deploy\n";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private WebApplicationFactory<Program> Timed() => app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(s =>
    {
        s.RemoveAll<TimeProvider>();
        s.AddSingleton<TimeProvider>(_time);
    }));

    private static string Zip(string path, string? ticket = null) =>
        "/api/skills/zip?path=" + Uri.EscapeDataString(path) + (ticket is null ? "" : "&ticket=" + Uri.EscapeDataString(ticket));

    private static async Task<string> TicketAsync(RestClient user)
    {
        using var response = await user.SendAsync(HttpMethod.Post, "/api/export/ticket", new { password = RestClient.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await RestClient.JsonAsync(response))["ticket"]!.GetValue<string>();
    }

    private static async Task TurnPasswordOffAsync(RestClient user)
    {
        using var off = await user.SendAsync(HttpMethod.Put, "/api/account/preferences", new { askPasswordForDownloads = false, password = RestClient.Password });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
    }

    private static async Task<RestClient> WithSkillAsync(WebApplicationFactory<Program> api, bool remember = false)
    {
        var user = await RestClient.NewAsync(api, vault: "Work", remember: remember);
        await user.CreateAsync("_skills/deploy/SKILL.md", Manifest);
        await user.CreateAsync("_skills/deploy/references/steps.md", "# Steps\n");
        return user;
    }

    private static async Task<string[]> EntriesAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync(Ct)));
        return [.. zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal)];
    }

    private static byte[] Pdf(int size)
    {
        var bytes = new byte[size];
        "%PDF-1.4\n"u8.CopyTo(bytes);
        return bytes;
    }

    [Fact]
    public async Task A_skill_zip_needs_a_ticket_by_default()
    {
        var alice = await WithSkillAsync(app.PublicApi);

        using var without = await alice.GetAsync(Zip("/Work/_skills/deploy"));
        using var with = await alice.GetAsync(Zip("/Work/_skills/deploy", await TicketAsync(alice)));

        Assert.Equal(HttpStatusCode.Forbidden, without.StatusCode);
        Assert.Contains("POST /api/export/ticket", await RestClient.ErrorAsync(without), StringComparison.Ordinal);
        Assert.Equal(["deploy/SKILL.md", "deploy/references/steps.md"], await EntriesAsync(with));
        // A system skill is the server's own, nothing of hers: no ticket.
        using var system = await alice.GetAsync(Zip("system:create-skill"));
        Assert.Equal(HttpStatusCode.OK, system.StatusCode);
    }

    [Fact]
    public async Task With_the_preference_off_it_needs_the_ticket_only_while_the_window_is_closed()
    {
        await using var api = Timed();
        var alice = await WithSkillAsync(api, remember: true);
        await TurnPasswordOffAsync(alice);

        using (var open = await alice.GetAsync(Zip("/Work/_skills/deploy"))) Assert.Equal(2, (await EntriesAsync(open)).Length);

        _time.Advance(TimeSpan.FromMinutes(31));
        using (var closed = await alice.GetAsync(Zip("/Work/_skills/deploy")))
        {
            Assert.Equal(HttpStatusCode.Forbidden, closed.StatusCode);
            Assert.Contains("POST /api/export/ticket", await RestClient.ErrorAsync(closed), StringComparison.Ordinal);
        }

        using var withTicket = await alice.GetAsync(Zip("/Work/_skills/deploy", await TicketAsync(alice)));
        Assert.Equal(2, (await EntriesAsync(withTicket)).Length);
    }

    [Fact]
    public async Task A_members_zip_is_an_event_for_the_owner_and_is_logged()
    {
        var log = new CapturedLog();
        await using var logged = app.PublicApi.WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(log)));
        var alice = await WithSkillAsync(logged);
        var bob = await RestClient.NewAsync(logged, vault: "Private");
        await alice.ShareWithAsync(bob, "Garten", MemberRole.Write, label: "Bob");

        using (var hers = await alice.GetAsync(Zip("/Work/_skills/deploy", await TicketAsync(alice)))) Assert.Equal(HttpStatusCode.OK, hers.StatusCode);
        Assert.DoesNotContain(await EventTextsAsync(alice), t => t.Contains("exported", StringComparison.Ordinal));

        using (var his = await bob.GetAsync(Zip("/Garten/_skills/deploy", await TicketAsync(bob)))) Assert.Equal(2, (await EntriesAsync(his)).Length);
        Assert.Contains("Bob exported Work.", await EventTextsAsync(alice));
        Assert.Contains(log.Lines, l => l.Contains($"User {bob.User.Id} downloaded the skill", StringComparison.Ordinal) && l.Contains("2 files", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Lines, l => l.Contains("deploy", StringComparison.Ordinal));
    }

    private static async Task<string[]> EventTextsAsync(RestClient owner)
    {
        using var response = await owner.GetAsync($"/api/vaults/{owner.Vault}/activity");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await RestClient.JsonAsync(response))["events"]!.AsArray().Select(e => e!["text"]!.GetValue<string>())];
    }

    [Fact]
    public async Task A_skill_over_200_files_is_refused_by_zip_copy_and_move()
    {
        var alice = await WithSkillAsync(app.PublicApi);
        await TurnPasswordOffAsync(alice);
        for (var i = 0; i < 199; i++) await alice.CreateAsync($"_skills/deploy/references/r{i:000}.md", "x\n");

        using var zip = await alice.GetAsync(Zip("/Work/_skills/deploy"));
        using var copy = await alice.SendAsync(HttpMethod.Post, "/api/skills/copy", new { from = "/Work/_skills/deploy", to = "Skills" });
        using var move = await alice.SendAsync(HttpMethod.Post, "/api/skills/move", new { from = "/Work/_skills/deploy", to = "Skills" });

        foreach (var refused in new[] { zip, copy, move })
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains($"at most {MarketplaceIndex.MaxSkillFiles} files", await RestClient.ErrorAsync(refused), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_skill_over_10_MB_is_refused_by_zip_and_copy()
    {
        var alice = await WithSkillAsync(app.PublicApi);
        await TurnPasswordOffAsync(alice);
        await alice.CreateFileAsync("_skills/deploy/assets/a.pdf", Pdf(6 * 1024 * 1024));
        await alice.CreateFileAsync("_skills/deploy/assets/b.pdf", Pdf(5 * 1024 * 1024));

        using var zip = await alice.GetAsync(Zip("/Work/_skills/deploy"));
        using var copy = await alice.SendAsync(HttpMethod.Post, "/api/skills/copy", new { from = "/Work/_skills/deploy", to = "Skills" });

        foreach (var refused in new[] { zip, copy })
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("at most 10 MB", await RestClient.ErrorAsync(refused), StringComparison.Ordinal);
        }
    }
}
