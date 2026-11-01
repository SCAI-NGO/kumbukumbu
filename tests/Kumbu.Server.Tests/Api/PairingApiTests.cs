// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Sharing;
using Kumbu.Server.Vaults;
using MongoDB.Driver;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Mvc.Testing;
using static Kumbu.Server.Tests.Api.RestClient;

namespace Kumbu.Server.Tests.Api;

/// <summary><c>POST /api/vaults/{vault}/pairing</c> and <c>POST /api/pairing/redeem</c> (sync spec §4).</summary>
[Collection("app")]
public sealed class PairingApiTests(KumbuAppFactory app) : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program>? _api;

    /// <summary>Wrong codes a minute per network and server-wide here: few enough to reach in a test, the network's above
    /// the 10 attempts an address may make.</summary>
    private const int PerNetwork = 15;
    private const int PerServer = 40;

    /// <summary>With limits of its own: the failure counts of one test must not reach another's.</summary>
    private WebApplicationFactory<Program> Api => _api ??= app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(s =>
    {
        s.RemoveAll<AccountLimits>();
        s.AddSingleton(new AccountLimits(PerNetwork, PerServer));
    }));

    public async ValueTask DisposeAsync()
    {
        if (_api is not null) await _api.DisposeAsync();
    }

    private static async Task<JsonNode> CodeAsync(RestClient user, object? body = null)
    {
        using var response = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/pairing", body ?? new { });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await JsonAsync(response);
    }

    /// <summary>As Obsidian sends it: no Origin, no cookie, no bearer; from <paramref name="address"/>.</summary>
    private async Task<HttpResponseMessage> RedeemAsync(string code, string device, string? address = null, string? plugin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/pairing/redeem")
        {
            Content = new StringContent(
                plugin is null ? JsonSerializer.Serialize(new { code, device }) : JsonSerializer.Serialize(new { code, device, plugin }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Forwarded-For", address ?? $"198.29.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}");
        using var client = Api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") });
        return await client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task The_session_creates_a_code_for_its_own_vault()
    {
        var user = await NewAsync(Api);

        var code = await CodeAsync(user, new { expiresInDays = 90 });

        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$", code["code"]!.GetValue<string>());
        Assert.NotNull(code["expiresAt"]);
        Assert.Equal(600, code["expiresIn"]!.GetValue<int>());
    }

    [Fact]
    public async Task An_api_key_cannot_create_codes()
    {
        var user = await NewAsync(Api);
        var key = await user.KeyAsync("agent", KeyRight.Write);

        using var response = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/pairing", new { }, bearer: key);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Another_users_vault_is_404()
    {
        var user = await NewAsync(Api);
        await NewAsync(Api, "Fremd");

        using var response = await user.SendAsync(HttpMethod.Post, "/api/vaults/Fremd/pairing", new { });
        using var badExpiry = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/pairing", new { expiresInDays = 7 });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badExpiry.StatusCode);
    }

    [Fact]
    public async Task Redeem_needs_no_origin_and_no_credential()
    {
        var user = await NewAsync(Api);
        var code = (await CodeAsync(user))["code"]!.GetValue<string>();

        using var response = await RedeemAsync(code.ToLowerInvariant().Replace("-", " "), "Laptop");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Matches("^kumbu_[0-9A-Za-z]{43}$", body["key"]!.GetValue<string>());
        Assert.Equal("Arbeit", body["vault"]!.GetValue<string>());
        Assert.Equal("Arbeit – Obsidian Laptop", body["keyName"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("DELETE", "/api/session")]
    [InlineData("POST", "/api/sessions/end-others")]
    public async Task Signing_out_or_ending_the_other_sessions_drops_the_open_code(string method, string path)
    {
        var user = await NewAsync(Api);
        var code = (await CodeAsync(user))["code"]!.GetValue<string>();

        using var done = await user.SendAsync(new HttpMethod(method), path);
        using var redeemed = await RedeemAsync(code, "Laptop");

        Assert.Equal(HttpStatusCode.NoContent, done.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, redeemed.StatusCode);
        Assert.Equal(0, Api.Services.GetRequiredService<Pairings>().Count);
    }

    [Fact]
    public async Task The_redeemed_key_works_on_the_versions_endpoint()
    {
        var user = await NewAsync(Api);
        await user.CreateAsync("plan.md", "x\n");
        using var redeemed = await RedeemAsync((await CodeAsync(user))["code"]!.GetValue<string>(), "Laptop");
        var key = (await JsonAsync(redeemed))["key"]!.GetValue<string>();

        using var versions = await user.GetAsync($"/api/vaults/{user.Vault}/versions", key);
        using var keys = await user.GetAsync("/api/keys");

        Assert.Equal(HttpStatusCode.OK, versions.StatusCode);
        Assert.Equal("/Arbeit/plan.md", (await JsonAsync(versions))["notes"]![0]!["path"]!.GetValue<string>());
        Assert.Equal("device", Assert.Single((await JsonAsync(keys)).AsArray())!["kind"]!.GetValue<string>());
    }

    // Green before task 0.5 too: from then on the board's definition is refused below maintain, and this guards that a
    // device paired to an own vault keeps changing it (collab design §10.2).
    [Fact]
    public async Task A_device_paired_to_an_own_vault_may_change_the_board()
    {
        var user = await NewAsync(Api, vault: "Gartenarbeit");
        var version = await user.CreateAsync("Board/Board.md", "---\nkumbu-board: 1\ncolumns:\n  - Ideas\n  - Done\n---\n");
        using var redeemed = await RedeemAsync((await CodeAsync(user))["code"]!.GetValue<string>(), "Laptop");
        var key = (await JsonAsync(redeemed))["key"]!.GetValue<string>();

        using var put = await user.SendAsync(
            HttpMethod.Put, user.Url("Board/Board.md"), new { content = "---\nkumbu-board: 1\ncolumns:\n  - Ideas\n  - Doing\n  - Done\n---\n", version }, bearer: key);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
    }

    [Fact]
    public async Task A_device_paired_to_an_own_vault_key_is_listed_as_maintain()
    {
        var user = await NewAsync(Api, vault: "Gartenarbeit");
        using var redeemed = await RedeemAsync((await CodeAsync(user))["code"]!.GetValue<string>(), "Laptop");
        Assert.Equal(HttpStatusCode.Created, redeemed.StatusCode);

        using var keys = await user.GetAsync("/api/keys");

        var device = Assert.Single((await JsonAsync(keys)).AsArray())!;
        Assert.Equal("device", device["kind"]!.GetValue<string>());
        Assert.Equal("maintain", device["vaults"]!["Gartenarbeit"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_wrong_code_says_only_that_it_is_not_valid()
    {
        using var response = await RedeemAsync("ZZZZ-ZZZZ", "Laptop");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("This pairing code is not valid (any more).", await ErrorAsync(response));
    }

    [Fact]
    public async Task A_bad_device_name_is_400_and_the_code_stays()
    {
        var user = await NewAsync(Api);
        var code = (await CodeAsync(user))["code"]!.GetValue<string>();

        using var bad = await RedeemAsync(code, "a (b");
        using var good = await RedeemAsync(code, "Laptop");

        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.Created, good.StatusCode);
    }

    [Fact]
    public async Task Redeem_is_limited_per_address()
    {
        var address = $"198.30.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}";
        for (var i = 0; i < 10; i++)
        {
            using var attempt = await RedeemAsync("ZZZZ-ZZZZ", "Laptop", address);
            Assert.Equal(HttpStatusCode.NotFound, attempt.StatusCode);
        }

        using var limited = await RedeemAsync("ZZZZ-ZZZZ", "Laptop", address);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
    }

    [Fact]
    public async Task Wrong_codes_from_one_network_hold_off_that_network_and_no_other()
    {
        var user = await NewAsync(Api);
        var code = (await CodeAsync(user))["code"]!.GetValue<string>();
        // A /48 of the attacker's own: a /64 for each guess, so that no address reaches its own limit.
        var network = $"2001:db8:{Random.Shared.Next(1, 0xffff):x}";
        for (var i = 0; i < PerNetwork; i++)
        {
            using var wrong = await RedeemAsync("ZZZZ-ZZZZ", "Laptop", $"{network}:{i:x}::1");
            Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
        }

        // The right code from a fresh /64 of the same /48 is refused like every other guess from there.
        using var same = await RedeemAsync(code, "Laptop", $"{network}:ffff::1");
        Assert.Equal(HttpStatusCode.TooManyRequests, same.StatusCode);
        Assert.NotNull(same.Headers.RetryAfter);

        // Another network pairs as ever.
        using var other = await RedeemAsync(code, "Laptop", $"2001:db8:{Random.Shared.Next(1, 0xffff):x}:1::1");
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
    }

    [Fact]
    public async Task Wrong_codes_from_one_ipv4_network_count_by_its_24()
    {
        var user = await NewAsync(Api);
        var code = (await CodeAsync(user))["code"]!.GetValue<string>();
        var network = $"198.31.{Random.Shared.Next(256)}";
        for (var i = 0; i < PerNetwork; i++)
        {
            using var wrong = await RedeemAsync("ZZZZ-ZZZZ", "Laptop", $"{network}.{i + 1}");
            Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
        }

        using var same = await RedeemAsync(code, "Laptop", $"{network}.200");
        using var other = await RedeemAsync(code, "Laptop", $"198.32.{Random.Shared.Next(256)}.1");

        Assert.Equal(HttpStatusCode.TooManyRequests, same.StatusCode);
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
    }

    private static Microsoft.AspNetCore.Http.DefaultHttpContext From(string address) =>
        new() { Connection = { RemoteIpAddress = IPAddress.Parse(address) } };

    [Fact]
    public void With_the_real_limits_one_network_is_held_off_after_100_wrong_codes_and_everyone_after_10000()
    {
        using var limits = new AccountLimits();
        Assert.Equal(100, AccountLimits.RedeemFailuresPerNetwork);
        Assert.Equal(10_000, AccountLimits.RedeemFailuresPerServer);

        for (var i = 1; i <= AccountLimits.RedeemFailuresPerNetwork; i++)
        {
            Assert.False(limits.RedeemsBlocked(From($"2001:db8:1:{i:x}::1")));
            Assert.Equal(i < AccountLimits.RedeemFailuresPerNetwork ? RedeemBlock.None : RedeemBlock.Network, limits.RedeemFailed(From($"2001:db8:1:{i:x}::1")));
        }

        Assert.True(limits.RedeemsBlocked(From("2001:db8:1:ffff:1:2:3:4")));
        Assert.False(limits.RedeemsBlocked(From("2001:db8:2::1")));
        Assert.False(limits.RedeemsBlocked(From("2001:db8::1")));

        // Many networks, none of them over its own limit, until the server's backstop holds for everyone.
        var failed = AccountLimits.RedeemFailuresPerNetwork;
        var block = RedeemBlock.None;
        for (var n = 0; failed < AccountLimits.RedeemFailuresPerServer; n++)
        {
            for (var i = 0; i < AccountLimits.RedeemFailuresPerNetwork - 1 && failed < AccountLimits.RedeemFailuresPerServer; i++, failed++)
            {
                var from = From($"203.{n / 256}.{n % 256}.{i + 1}");
                Assert.False(limits.RedeemsBlocked(from));
                block = limits.RedeemFailed(from);
                Assert.NotEqual(RedeemBlock.Network, block);
            }
        }

        Assert.Equal(RedeemBlock.Server, block);
        Assert.True(limits.RedeemsBlocked(From("2001:db8:2::1")));
        Assert.True(limits.RedeemsBlocked(From("192.0.2.1")));
    }

    [Theory]
    [InlineData("2001:db8:1:2:3:4:5:6", "2001:db8:1::/48")]
    [InlineData("2001:db8:1:ffff::1", "2001:db8:1::/48")]
    [InlineData("203.0.113.77", "203.0.113.0/24")]
    [InlineData("::ffff:203.0.113.77", "203.0.113.0/24")]
    public void A_network_is_an_ipv6_48_or_an_ipv4_24(string address, string network) =>
        Assert.Equal(network, Kumbu.Server.Auth.WrongKeyLimiter.Network(IPAddress.Parse(address)));

    /// <summary>Wrong codes from as many networks as it takes to use up the server's minute.</summary>
    private async Task TripTheServerWideLimitAsync()
    {
        for (var i = 0; i < PerServer; i++)
        {
            using var wrong = await RedeemAsync("ZZZZ-ZZZZ", "Laptop");
            Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
        }
    }

    [Fact]
    public async Task While_the_server_wide_limit_holds_no_guess_redeems_a_valid_code()
    {
        var user = await NewAsync(Api);
        var code = (await CodeAsync(user))["code"]!.GetValue<string>();
        await TripTheServerWideLimitAsync();

        // A guess that happens to be right, from an address of its own: refused like every other.
        using var right = await RedeemAsync(code, "Laptop");

        Assert.Equal(HttpStatusCode.TooManyRequests, right.StatusCode);
        Assert.NotNull(right.Headers.RetryAfter);
        using var keys = await user.GetAsync("/api/keys");
        Assert.Empty((await JsonAsync(keys)).AsArray());
    }

    [Fact]
    public async Task While_the_server_wide_limit_holds_guesses_from_many_addresses_are_all_refused()
    {
        var user = await NewAsync(Api);
        await CodeAsync(user);
        await TripTheServerWideLimitAsync();

        var refused = 0;
        for (var i = 0; i < 50; i++)
        {
            using var guess = await RedeemAsync($"{i:D4}-AAAA", "Laptop");
            if (guess.StatusCode == HttpStatusCode.TooManyRequests) refused++;
        }

        Assert.Equal(50, refused);
    }

    /// <summary>Alice's "Gartenarbeit", shared with Bob (who has his own "Work") as "garten".</summary>
    private async Task<(RestClient Alice, RestClient Bob, string VaultId, string MembershipId)> SharedAsync()
    {
        var alice = await NewAsync(Api, vault: "Gartenarbeit");
        var bob = await NewAsync(Api, vault: "Work");
        var membership = await alice.ShareWithAsync(bob, "garten", MemberRole.Write);
        return (alice, bob, await alice.VaultIdAsync(), membership);
    }

    private static async Task<string> CodeForAsync(RestClient user, string vault)
    {
        using var response = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{Uri.EscapeDataString(vault)}/pairing", new { });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response))["code"]!.GetValue<string>();
    }

    private bool IsOpen(string code) => Api.Services.GetRequiredService<Pairings>().IsOpen(code);

    private async Task RefusedAsTooOldAsync(string code, string? plugin)
    {
        using var refused = await RedeemAsync(code, "Laptop", plugin: plugin);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("Update the Kumbu² plugin to 0.2.0 or newer to sync a shared vault.", await ErrorAsync(refused));
        Assert.True(IsOpen(code), plugin);
    }

    [Fact]
    public async Task Pairing_a_shared_vault_without_a_plugin_version_is_refused_and_the_code_stays_usable()
    {
        var (_, bob, _, _) = await SharedAsync();
        var code = await CodeForAsync(bob, "garten");

        await RefusedAsTooOldAsync(code, null);

        // The updated plugin redeems the same code.
        using var redeemed = await RedeemAsync(code, "Laptop", plugin: "0.2.0");
        Assert.Equal(HttpStatusCode.Created, redeemed.StatusCode);
        Assert.False(IsOpen(code));
    }

    [Fact]
    public async Task Pairing_a_shared_vault_with_plugin_0_1_9_is_refused()
    {
        var (alice, bob, _, _) = await SharedAsync();

        // The owner's device as much as a member's.
        await RefusedAsTooOldAsync(await CodeForAsync(alice, "Gartenarbeit"), "0.1.9");
        await RefusedAsTooOldAsync(await CodeForAsync(bob, "garten"), "0.1.9");
    }

    [Fact]
    public async Task Pairing_a_shared_vault_with_plugin_0_2_0_pairs_with_GrantedVia()
    {
        var (alice, bob, vaultId, membership) = await SharedAsync();

        using var redeemed = await RedeemAsync(await CodeForAsync(bob, "garten"), "Laptop", plugin: "0.2.0");
        Assert.Equal(HttpStatusCode.Created, redeemed.StatusCode);
        var body = await JsonAsync(redeemed);
        Assert.Equal("garten", body["vault"]!.GetValue<string>());
        var slot = await app.Database.GetCollection<SlotDocument>("slots").Find(s => s.Vault == vaultId && s.Kind == SlotDocument.ApiKey).SingleAsync(Ct);
        Assert.Equal(membership, slot.GrantedVia);
        using (var versions = await bob.GetAsync("/api/vaults/garten/versions", body["key"]!.GetValue<string>()))
        {
            Assert.Equal(HttpStatusCode.OK, versions.StatusCode);
        }

        // Versions are numbers: 0.10.0 is after 0.2.0.
        using var owners = await RedeemAsync(await CodeForAsync(alice, "Gartenarbeit"), "Desk", plugin: "0.10.0");
        Assert.Equal(HttpStatusCode.Created, owners.StatusCode);
    }

    [Fact]
    public async Task A_write_members_device_may_write_notes_but_not_the_board()
    {
        var (_, bob, _, _) = await SharedAsync();
        using var redeemed = await RedeemAsync(await CodeForAsync(bob, "garten"), "Laptop", plugin: "0.2.0");
        Assert.Equal(HttpStatusCode.Created, redeemed.StatusCode);
        var key = (await JsonAsync(redeemed))["key"]!.GetValue<string>();
        // Sealed at maintain, as every device key is; capped at his role through its membership.
        using (var keys = await bob.GetAsync("/api/keys"))
        {
            Assert.Equal("maintain", Assert.Single((await JsonAsync(keys)).AsArray())!["vaults"]!["garten"]!.GetValue<string>());
        }

        using (var note = await bob.SendAsync(HttpMethod.Put, "/api/notes/garten/a.md", new { content = "Beete\n" }, bearer: key))
        {
            Assert.Equal(HttpStatusCode.Created, note.StatusCode);
        }

        using var board = await bob.SendAsync(HttpMethod.Put, "/api/notes/garten/Board/Board.md", new { content = "---\nkumbu-board: 1\ncolumns: [Ideas]\n---\n" }, bearer: key);
        Assert.Equal(HttpStatusCode.Forbidden, board.StatusCode);
        Assert.Equal("structural", (await JsonAsync(board))["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_own_vault_pairs_without_a_plugin_version()
    {
        var (_, bob, _, _) = await SharedAsync();

        using var redeemed = await RedeemAsync(await CodeForAsync(bob, "Work"), "Laptop");

        Assert.Equal(HttpStatusCode.Created, redeemed.StatusCode);
        using var keys = await bob.GetAsync("/api/keys");
        Assert.Equal("maintain", Assert.Single((await JsonAsync(keys)).AsArray())!["vaults"]!["Work"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_malformed_plugin_version_is_refused_as_too_old()
    {
        var (_, bob, _, _) = await SharedAsync();
        var code = await CodeForAsync(bob, "garten");

        foreach (var plugin in new[] { "", "0.2", "v0.2.0", "0.2.0.1", "latest", "0.2.0-beta.1", " 0.2.0", new string('9', 70) })
        {
            await RefusedAsTooOldAsync(code, plugin);
        }

        using var redeemed = await RedeemAsync(code, "Laptop", plugin: "0.2.1-beta.1+build.7");
        Assert.Equal(HttpStatusCode.Created, redeemed.StatusCode);
    }
}
