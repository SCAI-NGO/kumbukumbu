// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
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

    /// <summary>With limits of its own: the server-wide failure count of one test must not reach another's.</summary>
    private WebApplicationFactory<Program> Api => _api ??= app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(s =>
    {
        s.RemoveAll<AccountLimits>();
        s.AddSingleton<AccountLimits>();
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
    private async Task<HttpResponseMessage> RedeemAsync(string code, string device, string? address = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/pairing/redeem")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { code, device }), Encoding.UTF8, "application/json"),
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

    /// <summary>Wrong codes from as many addresses as it takes to use up the server's minute.</summary>
    private async Task TripTheServerWideLimitAsync()
    {
        for (var i = 0; i < AccountLimits.RedeemFailuresPerServer; i++)
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
}
