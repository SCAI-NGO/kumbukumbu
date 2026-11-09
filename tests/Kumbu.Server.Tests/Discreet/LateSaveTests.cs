// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using Kumbu.Server.Accounts;
using Kumbu.Server.Tests.Api;
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

    private WebApplicationFactory<Program> Timed() => app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(s =>
    {
        s.RemoveAll<TimeProvider>();
        s.AddSingleton<TimeProvider>(_time);
    }));

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
    public async Task A_late_save_that_fails_puts_the_marker_back_for_one_retry()
    {
        await using var api = Timed();
        var (w, version) = await RevealedAsync(api);
        Assert.Equal(HttpStatusCode.NoContent, await OpenNoteAsync(w, "Tagebuch", "geheim.md"));

        _time.Advance(TimeSpan.FromMinutes(6));
        var conflict = await SaveAsync(w, "geheim.md", "# Geheim\n\nFalsche Basis\n", "not-the-version");
        var retry = await SaveAsync(w, "geheim.md", "# Geheim\n\nRichtige Basis\n", version);
        var again = await SaveAsync(w, "geheim.md", "# Geheim\n\nNoch einmal\n", version);

        Assert.Equal(HttpStatusCode.Conflict, conflict.Status);
        Assert.Equal(HttpStatusCode.OK, retry.Status);
        Assert.Equal(HttpStatusCode.NotFound, again.Status);
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
