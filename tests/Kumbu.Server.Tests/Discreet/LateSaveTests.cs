// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.Json;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Kumbu.Server.Tests.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Discreet;

/// <summary>
/// One late save of the open discreet note after a reveal timed out (discreet vaults design, Decision 12, ruling 10):
/// exactly one <c>PUT</c> of that very note, from that session, until five minutes after the end.
/// </summary>
[Collection("app")]
public sealed class LateSaveTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The real time, in whole seconds: MongoDB's TTL monitor sweeps slots made at a past fake time.</summary>
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    /// <summary>Run as each answer starts, before what the server runs then itself (see <see cref="Watch"/>).</summary>
    private Func<Task>? _onStarting;

    private WebApplicationFactory<Program> Timed() => app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(s =>
    {
        s.RemoveAll<TimeProvider>();
        s.AddSingleton<TimeProvider>(_time);
        s.AddSingleton<IStartupFilter>(new Watch(() => _onStarting));
    }));

    /// <summary>Outside every middleware of the server, so its callback, registered first, runs last as an answer starts.</summary>
    private sealed class Watch(Func<Func<Task>?> onStarting) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => builder =>
        {
            builder.Use(async (context, inner) =>
            {
                if (onStarting() is { } callback) context.Response.OnStarting(callback);
                await inner(context);
            });
            next(builder);
        };
    }

    /// <summary>Alice's world with <c>Tagebuch</c> and <c>Familie</c> discreet, shown for five minutes, and
    /// <c>Tagebuch/geheim.md</c>'s version.</summary>
    private static async Task<(HiddenWorld World, string Version)> RevealedAsync(WebApplicationFactory<Program> api)
    {
        var w = await HiddenWorld.NewAsync(api);
        await w.HideAsync();
        Assert.True((await api.Services.GetRequiredService<Sessions>().RevealAsync(w.Alice.Session, TimeSpan.FromMinutes(5), Ct)).Revealed);
        return (w, (await w.In("Tagebuch").ReadAsync("geheim.md"))["version"]!.GetValue<string>());
    }

    private static async Task<HttpStatusCode> OpenNoteAsync(HiddenWorld w, string? vault, string? path)
    {
        using var response = await w.Alice.SendAsync(HttpMethod.Put, "/api/account/discreet/open-note", new { vault, path });
        return response.StatusCode;
    }

    private static async Task<(HttpStatusCode Status, string Body)> SaveAsync(HiddenWorld w, string path, string content, string? version)
    {
        using var response = await w.Alice.SendAsync(HttpMethod.Put, w.In("Tagebuch").Url(path), new { content, version });
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private Task<UserSlotDocument> SlotAsync(HiddenWorld w) =>
        app.Database.GetCollection<UserSlotDocument>("user_slots").Find(s => s.User == w.Alice.User.Id && s.Kind == UserSlotDocument.Session).SingleAsync(Ct);

    [Fact]
    public async Task One_save_of_the_open_note_goes_through_after_the_time_out()
    {
        await using var api = Timed();
        var (w, version) = await RevealedAsync(api);
        Assert.Equal(HttpStatusCode.NoContent, await OpenNoteAsync(w, "Tagebuch", "geheim.md"));

        // Within Sessions.IdleTimeout (30 minutes): the session itself lives on.
        _time.Advance(TimeSpan.FromMinutes(6));
        var first = await SaveAsync(w, "geheim.md", "# Geheim\n\nSpät gespeichert\n", version);
        var second = await SaveAsync(w, "geheim.md", "# Geheim\n\nNoch einmal\n", version);

        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal(HttpStatusCode.NotFound, second.Status);
        await w.AsNowhereAsync("Tagebuch", HttpMethod.Put, "/api/notes/{v}/geheim.md", _ => new { content = "x", version });
        // The vault stays hidden from everything else.
        Assert.DoesNotContain(("Tagebuch", false), await w.VaultsAsync());
        await w.RevealAsync();
        Assert.Contains("Spät gespeichert", (await w.In("Tagebuch").ReadAsync("geheim.md"))["content"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Another_note_of_the_vault_does_not_go_through()
    {
        await using var api = Timed();
        var (w, version) = await RevealedAsync(api);
        var alt = (await w.In("Tagebuch").ReadAsync("alt.md"))["version"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.NoContent, await OpenNoteAsync(w, "Tagebuch", "geheim.md"));

        // Within Sessions.IdleTimeout (30 minutes): the session itself lives on.
        _time.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(HttpStatusCode.NotFound, (await SaveAsync(w, "alt.md", "# Alt\n", alt)).Status);
        // It took nothing: the open note still goes through.
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(w, "geheim.md", "# Geheim\n", version)).Status);
    }

    [Fact]
    public async Task Eleven_minutes_after_the_reveal_it_is_too_late()
    {
        await using var api = Timed();
        var (w, version) = await RevealedAsync(api);
        Assert.Equal(HttpStatusCode.NoContent, await OpenNoteAsync(w, "Tagebuch", "geheim.md"));

        // Touched within the idle time, so the session itself lives on.
        _time.Advance(TimeSpan.FromMinutes(1));
        await w.VaultsAsync();
        _time.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(HttpStatusCode.NotFound, (await SaveAsync(w, "geheim.md", "# Zu spät\n", version)).Status);
        // Met past its grace, the marker goes.
        Assert.Null((await SlotAsync(w)).LateSave);
    }

    [Fact]
    public async Task Hiding_with_the_cross_forgets_the_note()
    {
        await using var api = Timed();
        var (w, version) = await RevealedAsync(api);
        Assert.Equal(HttpStatusCode.NoContent, await OpenNoteAsync(w, "Tagebuch", "geheim.md"));

        using (var hidden = await w.Alice.SendAsync(HttpMethod.Post, "/api/account/discreet/hide"))
        {
            Assert.Equal(HttpStatusCode.NoContent, hidden.StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await SaveAsync(w, "geheim.md", "# Nach dem Kreuz\n", version)).Status);
        // Within Sessions.IdleTimeout (30 minutes): the session itself lives on.
        _time.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(HttpStatusCode.NotFound, (await SaveAsync(w, "geheim.md", "# Nach dem Kreuz\n", version)).Status);
    }

    [Fact]
    public async Task Telling_the_note_while_hidden_records_nothing()
    {
        await using var api = Timed();
        var w = await HiddenWorld.NewAsync(api);
        await w.HideAsync();

        Assert.Equal(HttpStatusCode.NoContent, await OpenNoteAsync(w, "Tagebuch", "geheim.md"));
        Assert.Equal(HttpStatusCode.NoContent, await OpenNoteAsync(w, HiddenWorld.Nowhere, "geheim.md"));
        Assert.Equal(HttpStatusCode.NoContent, await OpenNoteAsync(w, "Work", "plan.md"));
        Assert.Equal(HttpStatusCode.NoContent, await OpenNoteAsync(w, null, null));

        Assert.Null((await SlotAsync(w)).LateSave);
    }

    [Fact]
    public async Task A_vault_that_is_not_discreet_records_nothing()
    {
        await using var api = Timed();
        var (w, _) = await RevealedAsync(api);

        Assert.Equal(HttpStatusCode.NoContent, await OpenNoteAsync(w, "Work", "plan.md"));
        Assert.Equal(HttpStatusCode.NoContent, await OpenNoteAsync(w, HiddenWorld.Nowhere, "plan.md"));

        Assert.Null((await SlotAsync(w)).LateSave);
    }

    [Fact]
    public async Task A_null_vault_clears_the_note()
    {
        await using var api = Timed();
        var (w, version) = await RevealedAsync(api);
        Assert.Equal(HttpStatusCode.NoContent, await OpenNoteAsync(w, "Tagebuch", "geheim.md"));
        Assert.NotNull((await SlotAsync(w)).LateSave);

        Assert.Equal(HttpStatusCode.NoContent, await OpenNoteAsync(w, null, null));

        Assert.Null((await SlotAsync(w)).LateSave);
        // Within Sessions.IdleTimeout (30 minutes): the session itself lives on.
        _time.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(HttpStatusCode.NotFound, (await SaveAsync(w, "geheim.md", "# Weg\n", version)).Status);
    }

    [Fact]
    public async Task The_marker_names_neither_vault_nor_path_in_clear()
    {
        await using var api = Timed();
        var (w, _) = await RevealedAsync(api);
        Assert.Equal(HttpStatusCode.NoContent, await OpenNoteAsync(w, "Tagebuch", "geheim.md"));

        var slot = await SlotAsync(w);
        var raw = await app.Database.GetCollection<BsonDocument>("user_slots")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", slot.Id)).SingleAsync(Ct);

        Assert.NotNull(slot.LateSave);
        var bytes = raw.ToBson();
        foreach (var clear in new[] { "Tagebuch", "geheim", w.TagebuchId })
        {
            Assert.False(Contains(bytes, Encoding.UTF8.GetBytes(clear)), $"The slot holds {clear} in clear.");
        }
    }

    [Fact]
    public async Task A_late_save_that_fails_can_be_tried_once_more()
    {
        await using var api = Timed();
        var (w, version) = await RevealedAsync(api);
        Assert.Equal(HttpStatusCode.NoContent, await OpenNoteAsync(w, "Tagebuch", "geheim.md"));

        // Within Sessions.IdleTimeout (30 minutes): the session itself lives on.
        _time.Advance(TimeSpan.FromMinutes(6));
        var conflict = await SaveAsync(w, "geheim.md", "# Geheim\n\nFalsche Basis\n", "not-the-version");
        var retry = await SaveAsync(w, "geheim.md", "# Geheim\n\nRichtige Basis\n", version);
        var again = await SaveAsync(w, "geheim.md", "# Geheim\n\nNoch einmal\n", version);

        Assert.Equal(HttpStatusCode.Conflict, conflict.Status);
        Assert.Equal(HttpStatusCode.OK, retry.Status);
        Assert.Equal(HttpStatusCode.NotFound, again.Status);
    }

    [Fact]
    public async Task A_failed_late_save_puts_the_marker_back_before_it_answers()
    {
        await using var api = Timed();
        var (w, _) = await RevealedAsync(api);
        Assert.Equal(HttpStatusCode.NoContent, await OpenNoteAsync(w, "Tagebuch", "geheim.md"));

        // Within Sessions.IdleTimeout (30 minutes): the session itself lives on.
        _time.Advance(TimeSpan.FromMinutes(6));
        bool? backAtAnswer = null;
        _onStarting = async () => backAtAnswer = (await SlotAsync(w)).LateSave is not null;
        var conflict = await SaveAsync(w, "geheim.md", "# Geheim\n\nFalsche Basis\n", "not-the-version");
        _onStarting = null;

        Assert.Equal(HttpStatusCode.Conflict, conflict.Status);
        Assert.True(backAtAnswer);
    }

    [Fact]
    public async Task A_second_failure_ends_the_late_save()
    {
        await using var api = Timed();
        var (w, version) = await RevealedAsync(api);
        Assert.Equal(HttpStatusCode.NoContent, await OpenNoteAsync(w, "Tagebuch", "geheim.md"));

        // Within Sessions.IdleTimeout (30 minutes): the session itself lives on.
        _time.Advance(TimeSpan.FromMinutes(6));
        var first = await SaveAsync(w, "geheim.md", "# Geheim\n\nFalsche Basis\n", "not-the-version");
        var second = await SaveAsync(w, "geheim.md", "# Geheim\n\nWieder falsch\n", "not-the-version");
        var correct = await SaveAsync(w, "geheim.md", "# Geheim\n\nRichtige Basis\n", version);

        Assert.Equal(HttpStatusCode.Conflict, first.Status);
        Assert.Equal(HttpStatusCode.Conflict, second.Status);
        Assert.Equal(HttpStatusCode.NotFound, correct.Status);
    }

    [Fact]
    public async Task A_cross_origin_save_neither_takes_nor_spends_the_marker()
    {
        await using var api = Timed();
        var (w, version) = await RevealedAsync(api);
        Assert.Equal(HttpStatusCode.NoContent, await OpenNoteAsync(w, "Tagebuch", "geheim.md"));

        // Within Sessions.IdleTimeout (30 minutes): the session itself lives on.
        _time.Advance(TimeSpan.FromMinutes(6));
        foreach (var origin in new[] { "https://sibling.localhost", null })
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, w.In("Tagebuch").Url("geheim.md"))
            {
                Content = new StringContent(JsonSerializer.Serialize(new { content = "# Fremd\n", version }), Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("Cookie", $"{CallerMiddleware.SessionCookie}={w.Alice.Session}");
            if (origin is not null) request.Headers.Add("Origin", origin);
            using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") });
            using var forged = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        }

        var slot = await SlotAsync(w);
        Assert.NotNull(slot.LateSave);
        Assert.False(slot.LateSaveRetried);
        // The tab's own save still has its one more try.
        Assert.Equal(HttpStatusCode.Conflict, (await SaveAsync(w, "geheim.md", "# Geheim\n\nFalsche Basis\n", "not-the-version")).Status);
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(w, "geheim.md", "# Geheim\n\nRichtige Basis\n", version)).Status);
    }

    [Fact]
    public async Task A_late_conflict_tells_neither_the_note_nor_a_path()
    {
        await using var api = Timed();
        var (w, _) = await RevealedAsync(api);
        Assert.Equal(HttpStatusCode.NoContent, await OpenNoteAsync(w, "Tagebuch", "geheim.md"));

        // Within Sessions.IdleTimeout (30 minutes): the session itself lives on.
        _time.Advance(TimeSpan.FromMinutes(6));
        var (status, body) = await SaveAsync(w, "geheim.md", "# Geheim\n\nFalsche Basis\n", "not-the-version");

        Assert.Equal(HttpStatusCode.Conflict, status);
        foreach (var told in new[] { HiddenWorld.TagebuchWord, "Siehe", "geheim", "Tagebuch", "content" })
        {
            Assert.DoesNotContain(told, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_late_save_of_a_moved_note_tells_not_where_it_went()
    {
        await using var api = Timed();
        var (w, version) = await RevealedAsync(api);
        Assert.Equal(HttpStatusCode.NoContent, await OpenNoteAsync(w, "Tagebuch", "geheim.md"));
        using (var moved = await w.Alice.SendAsync(HttpMethod.Post, "/api/move", new { from = "/Tagebuch/geheim.md", to = "/Tagebuch/versteckt.md" }))
        {
            Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        }

        // Within Sessions.IdleTimeout (30 minutes): the session itself lives on.
        _time.Advance(TimeSpan.FromMinutes(6));
        var (status, body) = await SaveAsync(w, "geheim.md", "# Geheim\n", version);

        Assert.Equal(HttpStatusCode.NotFound, status);
        foreach (var told in new[] { "versteckt", "geheim", "Tagebuch", "moved" })
        {
            Assert.DoesNotContain(told, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_marker_copied_to_another_session_opens_nothing()
    {
        await using var api = Timed();
        var (w, version) = await RevealedAsync(api);
        Assert.Equal(HttpStatusCode.NoContent, await OpenNoteAsync(w, "Tagebuch", "geheim.md"));
        var first = await SlotAsync(w);

        var other = await w.Alice.SignInAgainAsync();
        Assert.True((await api.Services.GetRequiredService<Sessions>().RevealAsync(other.Session, TimeSpan.FromMinutes(5), Ct)).Revealed);
        var slots = app.Database.GetCollection<UserSlotDocument>("user_slots");
        await slots.UpdateOneAsync(
            s => s.User == w.Alice.User.Id && s.Kind == UserSlotDocument.Session && s.Id != first.Id,
            Builders<UserSlotDocument>.Update.Set(s => s.LateSave, first.LateSave).Set(s => s.RevealedAt, first.RevealedAt).Set(s => s.RevealUntil, first.RevealUntil),
            cancellationToken: Ct);

        // Within Sessions.IdleTimeout (30 minutes): the session itself lives on.
        _time.Advance(TimeSpan.FromMinutes(6));
        using var response = await other.SendAsync(HttpMethod.Put, RestClient.Existing(api, other.User, other.UserKey, other.Session, "Tagebuch").Url("geheim.md"), new { content = "# Fremd\n", version });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_reveal_after_the_time_out_and_a_hide_forget_the_note()
    {
        await using var api = Timed();
        var (w, version) = await RevealedAsync(api);
        Assert.Equal(HttpStatusCode.NoContent, await OpenNoteAsync(w, "Tagebuch", "geheim.md"));

        // Within Sessions.IdleTimeout (30 minutes): the session itself lives on.
        _time.Advance(TimeSpan.FromMinutes(6));
        await w.RevealAsync();
        using (var hidden = await w.Alice.SendAsync(HttpMethod.Post, "/api/account/discreet/hide"))
        {
            Assert.Equal(HttpStatusCode.NoContent, hidden.StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await SaveAsync(w, "geheim.md", "# Danach\n", version)).Status);
    }

    [Fact]
    public async Task Only_the_reveal_that_ended_takes_the_marker()
    {
        await using var api = Timed();
        var (w, _) = await RevealedAsync(api);
        Assert.Equal(HttpStatusCode.NoContent, await OpenNoteAsync(w, "Tagebuch", "geheim.md"));
        var slot = await SlotAsync(w);
        var sessions = api.Services.GetRequiredService<Sessions>();

        Assert.False(await sessions.TakeLateSaveAsync(w.Alice.Session, slot.LateSave!, slot.RevealUntil!.Value.AddSeconds(1), Ct));
        Assert.NotNull((await SlotAsync(w)).LateSave);
        Assert.True(await sessions.TakeLateSaveAsync(w.Alice.Session, slot.LateSave!, slot.RevealUntil!.Value, Ct));
    }

    [Fact]
    public async Task A_rotation_of_the_user_key_forgets_the_note()
    {
        await using var api = Timed();
        var (w, _) = await RevealedAsync(api);
        Assert.Equal(HttpStatusCode.NoContent, await OpenNoteAsync(w, "Tagebuch", "geheim.md"));
        Assert.NotNull((await SlotAsync(w)).LateSave);

        using (var reencrypted = await w.Alice.SendAsync(HttpMethod.Post, "/api/account/reencrypt", new { password = RestClient.Password }))
        {
            Assert.Equal(HttpStatusCode.OK, reencrypted.StatusCode);
        }

        Assert.Null((await SlotAsync(w)).LateSave);
    }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;
}
