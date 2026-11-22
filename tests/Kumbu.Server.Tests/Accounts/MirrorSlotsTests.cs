// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Sharing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using static Kumbu.Server.Tests.Api.RestClient;

namespace Kumbu.Server.Tests.Accounts;

/// <summary>The offline mirror's slots (<c>/api/account/mirror-slots</c>, offline mode design §4.2, §4.4): a browser's
/// mirror key sealed under the user key, the vaults it may mirror, and the signals that make it purge.</summary>
[Collection("app")]
public sealed class MirrorSlotsTests(KumbuAppFactory app)
{
    private const string Url = "/api/account/mirror-slots";

    private const string Contact = Url + "/contact";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Created(string Id, string Token, string Key, JsonArray Vaults);

    private static async Task<Created> CreateAsync(RestClient user, string label = "Firefox on Linux")
    {
        using var response = await user.SendAsync(HttpMethod.Post, Url, new { label });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await JsonAsync(response);
        return new Created(
            json["id"]!.GetValue<string>(), json["token"]!.GetValue<string>(), json["key"]!.GetValue<string>(), json["vaults"]!.AsArray());
    }

    private static Task<HttpResponseMessage> ContactAsync(RestClient user, string token, int waiting = 0) =>
        user.SendAsync(HttpMethod.Post, Contact, new { token, waiting });

    private static async Task<JsonNode> ContactedAsync(RestClient user, string token, int waiting = 0)
    {
        using var response = await ContactAsync(user, token, waiting);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    /// <summary>Asserts the answer tells the browser to purge its whole mirror.</summary>
    private static async Task AssertWipeAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.NotFound)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("mirror-wipe", (await JsonAsync(response))["code"]?.GetValue<string>());
    }

    private static async Task<JsonArray> ListAsync(RestClient user)
    {
        using var response = await user.GetAsync(Url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonAsync(response)).AsArray();
    }

    private static string[] VaultIds(JsonNode contact) => [.. contact["vaults"]!.AsArray().Select(v => v!["id"]!.GetValue<string>()).Order(StringComparer.Ordinal)];

    private static string TagOf(JsonNode contact, string vaultId) =>
        contact["vaults"]!.AsArray().Single(v => v!["id"]!.GetValue<string>() == vaultId)!["tag"]!.GetValue<string>();

    /// <summary>A request with the session's cookie and the given <c>Origin</c> (none for null), and the browser's mirror token.</summary>
    private static async Task<HttpResponseMessage> RawAsync(
        RestClient user, HttpMethod method, string path, object? body, string? origin, string? session = null, string? mirrorToken = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        if (session is not null) request.Headers.Add("Cookie", $"{CallerMiddleware.SessionCookie}={session}");
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (mirrorToken is not null) request.Headers.Add("Kumbu-Mirror", mirrorToken);
        request.Headers.Add("X-Forwarded-For", $"198.21.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}");
        using var client = user.Api.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") });
        return await client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task Contact_answers_the_key_the_slot_was_made_with()
    {
        var alice = await NewAsync(app.PublicApi, vault: "Work");
        var slot = await CreateAsync(alice);

        Assert.Equal(32, Convert.FromBase64String(Base64(slot.Key)).Length);
        var contact = await ContactedAsync(alice, slot.Token);
        Assert.Equal(slot.Id, contact["id"]!.GetValue<string>());
        Assert.Equal(slot.Key, contact["key"]!.GetValue<string>());
        Assert.Null(contact["token"]);
        // Hers, and the vault Skills every user has.
        Assert.Contains(await alice.VaultIdAsync(), VaultIds(contact));
        Assert.Equal(2, VaultIds(contact).Length);
    }

    [Fact]
    public async Task Each_slot_has_its_own_key_and_token()
    {
        var alice = await NewAsync(app.PublicApi, vault: "Work");
        var laptop = await CreateAsync(alice, "Laptop");
        var phone = await CreateAsync(alice, "Phone");

        Assert.NotEqual(laptop.Key, phone.Key);
        Assert.NotEqual(laptop.Token, phone.Token);
        Assert.NotEqual(laptop.Id, phone.Id);
        var vault = await alice.VaultIdAsync();
        Assert.NotEqual(TagOf(await ContactedAsync(alice, laptop.Token), vault), TagOf(await ContactedAsync(alice, phone.Token), vault));
    }

    [Fact]
    public async Task The_database_holds_neither_the_key_nor_the_token()
    {
        var alice = await NewAsync(app.PublicApi, vault: "Work");
        var slot = await CreateAsync(alice);

        var db = alice.Api.Services.GetRequiredService<IMongoDatabase>();
        var raw = (await db.GetCollection<BsonDocument>("mirror_slots").Find(d => d["user"] == alice.User.Id).SingleAsync(Ct)).ToBson();
        Assert.False(Contains(raw, Convert.FromBase64String(Base64(slot.Key))));
        Assert.False(Contains(raw, Convert.FromBase64String(Base64(slot.Token))));
        Assert.False(Contains(raw, Encoding.UTF8.GetBytes(slot.Token)));
    }

    [Fact]
    public async Task Contact_records_the_changes_waiting_for_the_device_list()
    {
        var alice = await NewAsync(app.PublicApi, vault: "Work");
        var slot = await CreateAsync(alice, "Firefox on Linux");
        await ContactedAsync(alice, slot.Token, waiting: 4);

        var entry = Assert.Single(await ListAsync(alice))!;
        Assert.Equal(slot.Id, entry["id"]!.GetValue<string>());
        Assert.Equal("Firefox on Linux", entry["label"]!.GetValue<string>());
        Assert.Equal(4, entry["waiting"]!.GetValue<int>());
        Assert.NotNull(entry["contactAt"]);
        Assert.True(entry["current"]!.GetValue<bool>());
        Assert.Null(entry["key"]);
        Assert.Null(entry["token"]);

        // Another session of hers sees it, and that it isn't the browser that asks.
        var other = await alice.SignInAgainAsync();
        Assert.False(Assert.Single(await ListAsync(other))!["current"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_session_of_one_user_never_reads_another_users_slot()
    {
        var alice = await NewAsync(app.PublicApi, vault: "Work");
        var bob = await NewAsync(app.PublicApi, vault: "Garden");
        var slot = await CreateAsync(alice);

        using (var stolen = await ContactAsync(bob, slot.Token, waiting: 99)) await AssertWipeAsync(stolen);

        // Untouched: her key still answers, and his attempt recorded nothing on it.
        var contact = await ContactedAsync(alice, slot.Token);
        Assert.Equal(slot.Key, contact["key"]!.GetValue<string>());
        Assert.Equal(0, Assert.Single(await ListAsync(alice))!["waiting"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_session_of_one_user_never_deletes_another_users_slot()
    {
        var alice = await NewAsync(app.PublicApi, vault: "Work");
        var bob = await NewAsync(app.PublicApi, vault: "Garden");
        var slot = await CreateAsync(alice);

        using (var deleted = await bob.SendAsync(HttpMethod.Delete, $"{Url}/{slot.Id}")) Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);

        Assert.Single(await ListAsync(alice));
        Assert.Equal(slot.Key, (await ContactedAsync(alice, slot.Token))["key"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_session_of_one_user_never_lists_another_users_slots()
    {
        var alice = await NewAsync(app.PublicApi, vault: "Work");
        var bob = await NewAsync(app.PublicApi, vault: "Garden");
        await CreateAsync(alice);

        Assert.Empty(await ListAsync(bob));
    }

    [Theory]
    [InlineData("GET", Url)]
    [InlineData("POST", Url)]
    [InlineData("POST", Contact)]
    [InlineData("DELETE", Url + "/000000000000000000000000")]
    [InlineData("PUT", Url + "/000000000000000000000000/label")]
    public async Task Api_keys_are_refused(string method, string path)
    {
        var alice = await NewAsync(app.PublicApi, vault: "Work");
        var key = await alice.KeyAsync("agent", KeyRight.Write);

        using var response = await alice.SendAsync(new HttpMethod(method), path, new { label = "x", token = "x", waiting = 0 }, bearer: key);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("POST", Url, null)]
    [InlineData("POST", Url, "https://evil.example")]
    [InlineData("POST", Contact, null)]
    [InlineData("POST", Contact, "https://evil.example")]
    [InlineData("DELETE", "{slot}", null)]
    [InlineData("DELETE", "{slot}", "https://evil.example")]
    [InlineData("PUT", "{slot}/label", null)]
    [InlineData("PUT", "{slot}/label", "https://evil.example")]
    public async Task Changes_need_our_own_origin(string method, string path, string? origin)
    {
        var alice = await NewAsync(app.PublicApi, vault: "Work");
        var slot = await CreateAsync(alice);

        using var response = await RawAsync(
            alice, new HttpMethod(method), path.Replace("{slot}", $"{Url}/{slot.Id}", StringComparison.Ordinal),
            new { label = "x", token = slot.Token, waiting = 7 }, origin, session: alice.Session);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var entry = Assert.Single(await ListAsync(alice))!;
        Assert.Equal(0, entry["waiting"]!.GetValue<int>());
        Assert.Equal("Firefox on Linux", entry["label"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_deleted_slot_answers_wipe_and_tells_her_sockets()
    {
        var alice = await NewAsync(app.PublicApi, vault: "Work");
        var slot = await CreateAsync(alice);
        using var socket = await SyncSockets.OpenAsync(alice.Api, alice);

        using (var deleted = await alice.SendAsync(HttpMethod.Delete, $"{Url}/{slot.Id}")) Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        Assert.Contains("""{"type":"mirror"}""", await SyncSockets.DrainAsync(socket, TimeSpan.FromSeconds(2)));
        using (var contact = await ContactAsync(alice, slot.Token)) await AssertWipeAsync(contact);
        Assert.Empty(await ListAsync(alice));
        using (var again = await alice.SendAsync(HttpMethod.Delete, $"{Url}/{slot.Id}")) Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task A_signed_out_browser_learns_from_the_401_whether_its_slot_is_gone()
    {
        var alice = await NewAsync(app.PublicApi, vault: "Work");
        var slot = await CreateAsync(alice);
        var other = await alice.SignInAgainAsync();
        using (var signedOut = await alice.SendAsync(HttpMethod.Delete, "/api/session")) Assert.Equal(HttpStatusCode.NoContent, signedOut.StatusCode);

        // Ended, the slot still there: sign in again, and keep the mirror.
        using (var kept = await RawAsync(alice, HttpMethod.Get, "/api/vaults", null, Origin, alice.Session, slot.Token))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, kept.StatusCode);
            Assert.Null((await JsonAsync(kept))["code"]);
        }

        using (var removed = await other.SendAsync(HttpMethod.Delete, $"{Url}/{slot.Id}")) Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        using var wipe = await RawAsync(alice, HttpMethod.Get, "/api/vaults", null, Origin, alice.Session, slot.Token);
        await AssertWipeAsync(wipe, HttpStatusCode.Unauthorized);

        // Without a session at all, too, and without a token only the usual sentence.
        using var anonymous = await RawAsync(alice, HttpMethod.Get, "/api/vaults", null, Origin, mirrorToken: slot.Token);
        await AssertWipeAsync(anonymous, HttpStatusCode.Unauthorized);
        using var plain = await RawAsync(alice, HttpMethod.Get, "/api/vaults", null, Origin);
        Assert.Null((await JsonAsync(plain))["code"]);
    }

    [Fact]
    public async Task A_malformed_token_in_the_401_is_no_signal()
    {
        var alice = await NewAsync(app.PublicApi, vault: "Work");

        using var response = await RawAsync(alice, HttpMethod.Get, "/api/vaults", null, Origin, mirrorToken: "not a token");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null((await JsonAsync(response))["code"]);
    }

    [Fact]
    public async Task Discreet_vaults_are_never_listed_revealed_or_not()
    {
        var alice = await NewAsync(app.PublicApi, vault: "Work");
        using (var diary = await alice.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Diary" })) Assert.Equal(HttpStatusCode.Created, diary.StatusCode);
        var slot = await CreateAsync(alice);
        var before = VaultIds(await ContactedAsync(alice, slot.Token));
        Assert.Equal(3, before.Length);

        using (var pin = await alice.SendAsync(HttpMethod.Post, "/api/account/discreet/pin", new { password = Password, pin = "4711" })) Assert.True(pin.IsSuccessStatusCode);
        using (var reveal = await alice.SendAsync(HttpMethod.Post, "/api/account/discreet/reveal", new { pin = "4711" })) Assert.True(reveal.IsSuccessStatusCode);
        using (var mark = await alice.SendAsync(HttpMethod.Put, "/api/account/discreet/vaults/Diary", new { discreet = true })) Assert.Equal(HttpStatusCode.NoContent, mark.StatusCode);

        var diaryId = (await alice.Api.Services.GetRequiredService<Kumbu.Server.Vaults.VaultCatalog>().FindForUserAsync(alice.User.Id, alice.UserKey, "Diary", Ct))!;
        Assert.Contains(diaryId, before);
        string[] rest = [.. before.Where(v => v != diaryId)];
        Assert.Equal(rest, VaultIds(await ContactedAsync(alice, slot.Token)));
        using (var hide = await alice.SendAsync(HttpMethod.Post, "/api/account/discreet/hide")) Assert.True(hide.IsSuccessStatusCode);
        Assert.Equal(rest, VaultIds(await ContactedAsync(alice, slot.Token)));
    }

    [Fact]
    public async Task A_vault_whose_membership_is_revoked_leaves_the_list()
    {
        var alice = await NewAsync(app.PublicApi, vault: "Work");
        var bob = await NewAsync(app.PublicApi, vault: "Garden");
        var membership = await alice.ShareWithAsync(bob, "Alice's work", MemberRole.Read);
        var shared = await alice.VaultIdAsync();
        var slot = await CreateAsync(bob);
        var listed = await ContactedAsync(bob, slot.Token);
        Assert.Contains(shared, VaultIds(listed));
        Assert.False(listed["vaults"]!.AsArray().Single(v => v!["id"]!.GetValue<string>() == shared)!["canWrite"]!.GetValue<bool>());
        var own = await bob.VaultIdAsync();
        Assert.True(listed["vaults"]!.AsArray().Single(v => v!["id"]!.GetValue<string>() == own)!["canWrite"]!.GetValue<bool>());
        using var socket = await SyncSockets.OpenAsync(bob.Api, bob);

        using (var revoked = await alice.SendAsync(HttpMethod.Post, $"/api/vaults/Work/memberships/{membership}/revoke")) Assert.True(revoked.IsSuccessStatusCode);

        Assert.Contains("""{"type":"mirror"}""", await SyncSockets.DrainAsync(socket, TimeSpan.FromSeconds(2)));
        Assert.DoesNotContain(shared, VaultIds(await ContactedAsync(bob, slot.Token)));
    }

    [Fact]
    public async Task A_deleted_vault_leaves_the_list()
    {
        var alice = await NewAsync(app.PublicApi, vault: "Work");
        var slot = await CreateAsync(alice);
        var work = await alice.VaultIdAsync();

        using (var deleted = await alice.SendAsync(HttpMethod.Post, "/api/vaults/Work/delete", new { password = Password, confirm = "Work" }))
        {
            Assert.True(deleted.IsSuccessStatusCode);
        }

        Assert.DoesNotContain(work, VaultIds(await ContactedAsync(alice, slot.Token)));
    }

    [Fact]
    public async Task A_rekeyed_vault_gets_a_new_tag()
    {
        var alice = await NewAsync(app.PublicApi, vault: "Work");
        var slot = await CreateAsync(alice);
        var work = await alice.VaultIdAsync();
        var before = TagOf(await ContactedAsync(alice, slot.Token), work);
        Assert.Equal(before, TagOf(await ContactedAsync(alice, slot.Token), work));

        using (var rekeyed = await alice.SendAsync(HttpMethod.Post, "/api/vaults/Work/rekey", new { password = Password })) Assert.Equal(HttpStatusCode.OK, rekeyed.StatusCode);

        Assert.NotEqual(before, TagOf(await ContactedAsync(alice, slot.Token), work));
    }

    [Fact]
    public async Task Reencrypting_the_account_removes_every_slot()
    {
        var alice = await NewAsync(app.PublicApi, vault: "Work");
        var slot = await CreateAsync(alice);
        await CreateAsync(alice, "Phone");

        using (var reencrypted = await alice.SendAsync(HttpMethod.Post, "/api/account/reencrypt", new { password = Password })) Assert.Equal(HttpStatusCode.OK, reencrypted.StatusCode);

        // The session is kept, the mirrors aren't: they are synced again under a new mirror key.
        Assert.Empty(await ListAsync(alice));
        using var contact = await ContactAsync(alice, slot.Token);
        await AssertWipeAsync(contact);
    }

    [Fact]
    public async Task Ending_other_sessions_removes_the_other_browsers_slots()
    {
        var alice = await NewAsync(app.PublicApi, vault: "Work");
        var here = await CreateAsync(alice, "Here");
        var elsewhere = await alice.SignInAgainAsync();
        var there = await CreateAsync(elsewhere, "There");

        using (var ended = await alice.SendAsync(HttpMethod.Post, "/api/sessions/end-others")) Assert.Equal(HttpStatusCode.NoContent, ended.StatusCode);

        Assert.Equal([here.Id], (await ListAsync(alice)).Select(s => s!["id"]!.GetValue<string>()));
        Assert.Equal(here.Key, (await ContactedAsync(alice, here.Token))["key"]!.GetValue<string>());
        using var gone = await RawAsync(alice, HttpMethod.Get, "/api/vaults", null, Origin, elsewhere.Session, there.Token);
        await AssertWipeAsync(gone, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Deleting_the_account_removes_its_slots_and_the_401_says_wipe()
    {
        var alice = await NewAsync(app.PublicApi, vault: "Work");
        var slot = await CreateAsync(alice);

        using (var deleted = await alice.SendAsync(HttpMethod.Post, "/api/account/delete", new { password = Password, confirm = alice.Username }))
        {
            Assert.True(deleted.IsSuccessStatusCode);
        }

        var db = alice.Api.Services.GetRequiredService<IMongoDatabase>();
        Assert.Equal(0, await db.GetCollection<BsonDocument>("mirror_slots").CountDocumentsAsync(d => d["user"] == alice.User.Id, cancellationToken: Ct));
        using var wipe = await RawAsync(alice, HttpMethod.Get, "/api/vaults", null, Origin, alice.Session, slot.Token);
        await AssertWipeAsync(wipe, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_slot_is_renamed_and_keeps_its_key()
    {
        var alice = await NewAsync(app.PublicApi, vault: "Work");
        var slot = await CreateAsync(alice);

        using (var renamed = await alice.SendAsync(HttpMethod.Put, $"{Url}/{slot.Id}/label", new { label = "  Work laptop  " }))
        {
            Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        }

        Assert.Equal("Work laptop", Assert.Single(await ListAsync(alice))!["label"]!.GetValue<string>());
        Assert.Equal(slot.Key, (await ContactedAsync(alice, slot.Token))["key"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_session_of_one_user_never_renames_another_users_slot()
    {
        var alice = await NewAsync(app.PublicApi, vault: "Work");
        var bob = await NewAsync(app.PublicApi, vault: "Garden");
        var slot = await CreateAsync(alice);

        using (var renamed = await bob.SendAsync(HttpMethod.Put, $"{Url}/{slot.Id}/label", new { label = "Mine now" }))
        {
            Assert.Equal(HttpStatusCode.NotFound, renamed.StatusCode);
        }
        using (var nothing = await alice.SendAsync(HttpMethod.Put, $"{Url}/not-an-id/label", new { label = "Nothing" }))
        {
            Assert.Equal(HttpStatusCode.NotFound, nothing.StatusCode);
        }

        Assert.Equal("Firefox on Linux", Assert.Single(await ListAsync(alice))!["label"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("line\nbreak")]
    [InlineData("a label of more than sixty-four characters, which is longer than any browser's")]
    public async Task A_new_label_is_a_short_line(string label)
    {
        var alice = await NewAsync(app.PublicApi, vault: "Work");
        var slot = await CreateAsync(alice);

        using var response = await alice.SendAsync(HttpMethod.Put, $"{Url}/{slot.Id}/label", new { label });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Firefox on Linux", Assert.Single(await ListAsync(alice))!["label"]!.GetValue<string>());
    }

    [Fact]
    public async Task Slots_are_capped()
    {
        var alice = await NewAsync(app.PublicApi, vault: "Work");
        for (var i = 0; i < MirrorSlots.Cap; i++) await CreateAsync(alice, $"Browser {i}");

        using var refused = await alice.SendAsync(HttpMethod.Post, Url, new { label = "One too many" });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(MirrorSlots.Cap, (await ListAsync(alice)).Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("line\nbreak")]
    [InlineData("a label of more than sixty-four characters, which is longer than any browser's")]
    public async Task A_label_is_a_short_line(string label)
    {
        var alice = await NewAsync(app.PublicApi, vault: "Work");

        using var response = await alice.SendAsync(HttpMethod.Post, Url, new { label });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await ListAsync(alice));
    }

    [Theory]
    [InlineData("not a token", 0)]
    [InlineData(null, 0)]
    [InlineData("{token}", -1)]
    [InlineData("{token}", 1_000_001)]
    public async Task A_contact_is_checked(string? token, int waiting)
    {
        var alice = await NewAsync(app.PublicApi, vault: "Work");
        var slot = await CreateAsync(alice);

        using var response = await ContactAsync(alice, token?.Replace("{token}", slot.Token, StringComparison.Ordinal)!, waiting);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static string Base64(string base64Url)
    {
        var s = base64Url.Replace('-', '+').Replace('_', '/');
        return s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '=');
    }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;
}
