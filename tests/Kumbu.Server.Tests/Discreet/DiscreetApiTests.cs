// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Kumbu.Server.Auth;
using Kumbu.Server.Tests.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Server.Tests.Discreet;

/// <summary>The PIN, reveal, hide and mark endpoints of discreet vaults (discreet vaults design, Decisions 4–7, 17).</summary>
[Collection("app")]
public sealed class DiscreetApiTests(KumbuAppFactory app)
{
    private const string Pin = "4711";

    private const string WrongPin = "1234";

    private const string NoneBody = """{"error":"You have no discreet vaults."}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The real time, in whole seconds: MongoDB's TTL monitor sweeps slots made at a past fake time.</summary>
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private WebApplicationFactory<Program> Timed(int? pinChecksPerUser = null) => app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(s =>
    {
        s.RemoveAll<TimeProvider>();
        s.AddSingleton<TimeProvider>(_time);
        if (pinChecksPerUser is { } limit)
        {
            s.RemoveAll<AccountLimits>();
            s.AddSingleton(new AccountLimits(AccountLimits.RedeemFailuresPerNetwork, AccountLimits.RedeemFailuresPerServer, pinChecksPerUser: limit));
        }
    }));

    private static async Task<(HttpStatusCode Status, string Body)> SendAsync(RestClient client, HttpMethod method, string path, object? body = null)
    {
        using var response = await client.SendAsync(method, path, body);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private static Task<(HttpStatusCode Status, string Body)> SetPinAsync(RestClient client, string? pin, string? password = RestClient.Password) =>
        SendAsync(client, HttpMethod.Post, "/api/account/discreet/pin", password is null ? new { pin } : new { password, pin });

    private static Task<(HttpStatusCode Status, string Body)> RevealAsync(RestClient client, string pin, int? minutes = null) =>
        SendAsync(client, HttpMethod.Post, "/api/account/discreet/reveal", new { pin, minutes });

    private static Task<(HttpStatusCode Status, string Body)> HideAsync(RestClient client) => SendAsync(client, HttpMethod.Post, "/api/account/discreet/hide");

    private static Task<(HttpStatusCode Status, string Body)> MarkAsync(RestClient client, string vault, bool discreet) =>
        SendAsync(client, HttpMethod.Put, $"/api/account/discreet/vaults/{Uri.EscapeDataString(vault)}", new { discreet });

    /// <summary>The <c>discreet</c> of <c>GET /api/session</c>, as the server wrote it.</summary>
    private static async Task<string> SessionDiscreetAsync(RestClient client)
    {
        var (status, body) = await SendAsync(client, HttpMethod.Get, "/api/session");
        Assert.Equal(HttpStatusCode.OK, status);
        return JsonNode.Parse(body)!["discreet"]!.ToJsonString();
    }

    [Fact]
    public async Task A_PIN_set_with_the_password_reveals()
    {
        await using var api = Timed();
        var alice = await RestClient.NewAsync(api);

        Assert.Equal(HttpStatusCode.NoContent, (await SetPinAsync(alice, Pin)).Status);

        Assert.Equal(HttpStatusCode.OK, (await RevealAsync(alice, Pin)).Status);
    }

    [Fact]
    public async Task A_PIN_needs_the_right_password_and_4_to_6_digits()
    {
        await using var api = Timed();
        var alice = await RestClient.NewAsync(api);

        var wrong = await SetPinAsync(alice, Pin, password: "not the password");
        var missing = await SetPinAsync(alice, Pin, password: null);
        var tooShort = await SetPinAsync(alice, "123");

        Assert.Equal((HttpStatusCode.BadRequest, """{"error":"The password is wrong."}"""), wrong);
        Assert.Equal((HttpStatusCode.BadRequest, """{"error":"The password is wrong."}"""), missing);
        Assert.Equal((HttpStatusCode.BadRequest, """{"error":"A PIN has 4 to 6 digits."}"""), tooShort);
        // None of them set a PIN.
        Assert.Equal((HttpStatusCode.BadRequest, NoneBody), await RevealAsync(alice, Pin));
    }

    [Fact]
    public async Task A_wrong_password_for_the_PIN_counts_against_a_remembered_session()
    {
        await using var api = Timed();
        var alice = await RestClient.NewAsync(api, remember: true);

        // A remembered session ends at the fifth wrong password in a row.
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.BadRequest, (await SetPinAsync(alice, Pin, password: "not the password")).Status);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(alice, HttpMethod.Get, "/api/session")).Status);
    }

    [Fact]
    public async Task The_right_PIN_reveals_for_the_minutes_chosen()
    {
        await using var api = Timed();
        var w = await HiddenWorld.NewAsync(api);
        await w.HideAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await SetPinAsync(w.Alice, Pin)).Status);

        var (status, body) = await RevealAsync(w.Alice, Pin, minutes: 10);

        Assert.Equal(HttpStatusCode.OK, status);
        var view = JsonNode.Parse(body)!;
        Assert.True(view["revealed"]!.GetValue<bool>());
        var until = Now.AddMinutes(10);
        Assert.Equal(until, view["until"]!.GetValue<DateTime>().ToUniversalTime());
        var session = JsonNode.Parse(await SessionDiscreetAsync(w.Alice))!;
        Assert.True(session["revealed"]!.GetValue<bool>());
        Assert.Equal(until, session["until"]!.GetValue<DateTime>().ToUniversalTime());
        Assert.Equal([("Familie", true), ("Skills", false), ("Tagebuch", true), ("Work", false)], await w.VaultsAsync());

        // At its end the session tells no end that has passed.
        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal("""{"revealed":false,"until":null}""", await SessionDiscreetAsync(w.Alice));
        Assert.Equal([("Skills", false), ("Work", false)], await w.VaultsAsync());
    }

    [Fact]
    public async Task Never_reveals_without_an_end()
    {
        await using var api = Timed();
        var alice = await RestClient.NewAsync(api);
        await SetPinAsync(alice, Pin);

        Assert.Equal((HttpStatusCode.OK, """{"revealed":true,"until":null}"""), await RevealAsync(alice, Pin, minutes: null));
        Assert.Equal("""{"revealed":true,"until":null}""", await SessionDiscreetAsync(alice));
    }

    [Fact]
    public async Task Other_minutes_are_refused()
    {
        await using var api = Timed();
        var alice = await RestClient.NewAsync(api);
        await SetPinAsync(alice, Pin);

        Assert.Equal(
            (HttpStatusCode.BadRequest, """{"error":"Choose 5, 10, 30 or 60 minutes, or never."}"""), await RevealAsync(alice, Pin, minutes: 7));
        Assert.Equal("""{"revealed":false,"until":null}""", await SessionDiscreetAsync(alice));
    }

    [Fact]
    public async Task Every_PIN_that_does_not_reveal_answers_the_same_after_one_derivation()
    {
        await using var api = Timed();
        var kdf = api.Services.GetRequiredService<Argon2Kdf>();
        var withVaults = await HiddenWorld.NewAsync(api);
        await withVaults.HideAsync();
        await SetPinAsync(withVaults.Alice, Pin);
        var withPin = await RestClient.NewAsync(api);
        await SetPinAsync(withPin, Pin);
        var withoutPin = await RestClient.NewAsync(api);

        async Task<(HttpStatusCode, string)> EnteredAsync(RestClient client, string pin)
        {
            var before = kdf.Runs;
            var answer = await RevealAsync(client, pin, minutes: 10);
            Assert.Equal(before + 1, kdf.Runs);
            return answer;
        }

        var answers = new[]
        {
            await EnteredAsync(withVaults.Alice, WrongPin),
            await EnteredAsync(withPin, WrongPin),
            await EnteredAsync(withoutPin, WrongPin),
            await EnteredAsync(withPin, "12a4"),
            await EnteredAsync(withoutPin, "12a4"),
        };

        Assert.All(answers, a => Assert.Equal((HttpStatusCode.BadRequest, NoneBody), a));
        Assert.Equal([("Skills", false), ("Work", false)], await withVaults.VaultsAsync());
    }

    [Fact]
    public async Task The_sixth_PIN_a_minute_is_refused_alike_with_a_PIN_or_without()
    {
        await using var api = Timed();
        var withPin = await RestClient.NewAsync(api);
        await SetPinAsync(withPin, Pin);
        var withoutPin = await RestClient.NewAsync(api);

        async Task<(HttpStatusCode, string)[]> SixAsync(RestClient client)
        {
            var answers = new List<(HttpStatusCode, string)>();
            for (var i = 0; i < 6; i++) answers.Add(await RevealAsync(client, WrongPin, minutes: 10));
            return [.. answers];
        }

        var with = await SixAsync(withPin);
        var without = await SixAsync(withoutPin);

        Assert.Equal(with, without);
        Assert.All(with[..5], a => Assert.Equal((HttpStatusCode.BadRequest, NoneBody), a));
        Assert.Equal((HttpStatusCode.TooManyRequests, """{"error":"Too many attempts; try again in a minute."}"""), with[5]);
    }

    [Fact]
    public async Task The_tenth_PIN_entry_ends_the_session_even_when_it_is_right()
    {
        await using var api = Timed(pinChecksPerUser: 1000);
        var alice = await RestClient.NewAsync(api);
        await SetPinAsync(alice, Pin);
        for (var i = 1; i < Sessions.PinEntryLimit; i++) Assert.Equal((HttpStatusCode.BadRequest, NoneBody), await RevealAsync(alice, WrongPin));

        using var tenth = await alice.SendAsync(HttpMethod.Post, "/api/account/discreet/reveal", new { pin = Pin, minutes = 10 });

        var (status, body) = (tenth.StatusCode, await tenth.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Contains(tenth.Headers.GetValues("Set-Cookie"), c => c.StartsWith(CallerMiddleware.SessionCookie + "=;", StringComparison.Ordinal));
        // Exactly as any request of an ended session answers.
        Assert.Equal((status, body), await SendAsync(alice, HttpMethod.Get, "/api/session"));
        Assert.Equal((status, body), await RevealAsync(alice, Pin));
    }

    [Fact]
    public async Task Hiding_hides_again_and_always_answers_204()
    {
        await using var api = Timed();
        var w = await HiddenWorld.NewAsync(api);
        await w.HideAsync();
        await SetPinAsync(w.Alice, Pin);
        Assert.Equal(HttpStatusCode.NoContent, (await HideAsync(w.Alice)).Status);
        Assert.Equal(HttpStatusCode.OK, (await RevealAsync(w.Alice, Pin)).Status);

        Assert.Equal(HttpStatusCode.NoContent, (await HideAsync(w.Alice)).Status);

        Assert.Equal("""{"revealed":false,"until":null}""", await SessionDiscreetAsync(w.Alice));
        Assert.Equal([("Skills", false), ("Work", false)], await w.VaultsAsync());
    }

    [Fact]
    public async Task Marking_while_hidden_answers_as_for_a_vault_she_does_not_have()
    {
        await using var api = Timed();
        var w = await HiddenWorld.NewAsync(api);
        await w.HideAsync();

        await w.AsNowhereAsync("Tagebuch", HttpMethod.Put, "/api/account/discreet/vaults/{v}", _ => new { discreet = false });
        await w.AsNowhereAsync("Work", HttpMethod.Put, "/api/account/discreet/vaults/{v}", _ => new { discreet = true });
        await w.AsNowhereAsync("Familie", HttpMethod.Put, "/api/account/discreet/vaults/{v}", _ => new { discreet = false });

        var (status, body) = await MarkAsync(w.Alice, HiddenWorld.Nowhere, true);
        Assert.Equal((HttpStatusCode.NotFound, $$"""{"error":"You have no vault named '{{HiddenWorld.Nowhere}}'."}"""), (status, body));
        await SetPinAsync(w.Alice, Pin);
        await RevealAsync(w.Alice, Pin);
        await HideAsync(w.Alice);
        Assert.Equal([("Skills", false), ("Work", false)], await w.VaultsAsync());
    }

    [Fact]
    public async Task The_vault_Skills_cannot_be_marked()
    {
        await using var api = Timed();
        var alice = await RestClient.NewAsync(api);
        await SetPinAsync(alice, Pin);
        await RevealAsync(alice, Pin);

        Assert.Equal((HttpStatusCode.BadRequest, """{"error":"The vault Skills can't be discreet."}"""), await MarkAsync(alice, "Skills", true));
    }

    [Fact]
    public async Task Marks_made_while_revealed_hold_once_hidden()
    {
        await using var api = Timed();
        var w = await HiddenWorld.NewAsync(api);
        await w.HideAsync();
        await SetPinAsync(w.Alice, Pin);
        await RevealAsync(w.Alice, Pin);

        Assert.Equal(HttpStatusCode.NoContent, (await MarkAsync(w.Alice, "Work", true)).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await MarkAsync(w.Alice, "Tagebuch", false)).Status);
        Assert.Equal([("Familie", true), ("Skills", false), ("Tagebuch", false), ("Work", true)], await w.VaultsAsync());
        await HideAsync(w.Alice);

        Assert.Equal([("Skills", false), ("Tagebuch", false)], await w.VaultsAsync());
    }

    [Fact]
    public async Task A_hidden_session_says_the_same_with_a_PIN_or_without()
    {
        await using var api = Timed();
        var withPin = await RestClient.NewAsync(api);
        await SetPinAsync(withPin, Pin);
        var withoutPin = await RestClient.NewAsync(api);

        var with = await SessionDiscreetAsync(withPin);

        Assert.Equal("""{"revealed":false,"until":null}""", with);
        Assert.Equal(with, await SessionDiscreetAsync(withoutPin));
    }
}
