// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.WebSockets;
using System.Text;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Vaults;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Sharing;

/// <summary>
/// HARD RULE 5, the marker test (collab design §8.2): a label written by member A never reaches member B, in any REST
/// answer, shell output or socket message. Carol names her key and her device with a marker and works on Alice's
/// "Gartenarbeit" through every kind of write; Bob, a member too, then reads everything that carries writers or text
/// of the vault, and finds neither the marker nor anything else of Carol's: her username, user id, key ids or her alias
/// "Beete", which is hers alone (§2.3): no text the server writes for her, such as a new board's title, may carry it.
/// Phase 3 adds events and notices to it, phase 5 the socket's further messages. Never weakened for another test.
/// </summary>
[Collection("app")]
public sealed class MarkerTests(KumbuAppFactory app)
{
    private const string Marker = "MARKER";
    private const string KeyName = "MARKER-c4f1";
    private const string DeviceName = "MARKER-dev";
    private const string CarolsAlias = "Beete";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0, 0x1F, 0x15, 0xC4, 0x89];

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private sealed record World(RestClient Alice, RestClient Bob, RestClient Carol, string VaultId, string BobKey, string BobReadKey, string[] Forbidden);

    private async Task<(string Key, string Id)> KeyAsync(RestClient user, string name, string vaultId, KeyRight right, ApiKeyKind kind = ApiKeyKind.Agent, string? writer = null)
    {
        var (key, info) = await Api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            user.User, user.UserKey, DateTime.UtcNow, name, new Dictionary<string, KeyRight> { [vaultId] = right }, Ct, kind: kind, writerName: writer);
        return (key, info.Id);
    }

    /// <summary>Alice's "Gartenarbeit", shared with Bob ("garten") and Carol ("Beete"), both to maintain; Carol has worked
    /// on it through her session, her key "MARKER-c4f1" and her device "MARKER-dev".</summary>
    private async Task<World> WorldAsync()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.CreateAsync("plan.md", "Gemüse planen\n");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var carol = await RestClient.NewAsync(Api, vault: "Privat");
        await alice.ShareWithAsync(bob, "garten", MemberRole.Maintain);
        var carols = await alice.ShareWithAsync(carol, CarolsAlias, MemberRole.Maintain);
        var vaultId = await alice.VaultIdAsync();

        var (key, keyId) = await KeyAsync(carol, KeyName, vaultId, KeyRight.Maintain);
        var (device, deviceId) = await KeyAsync(carol, "Beete – Obsidian " + DeviceName, vaultId, KeyRight.Write, ApiKeyKind.Device, DeviceName);

        // Her session, her key and her device write; her key edits, moves, removes, restores, checkpoints, uploads and plans.
        await PutAsync(carol, "/api/notes/Beete/salat.md", new { content = "Salat\n" }, null);
        var kohl = await PutAsync(carol, "/api/notes/Beete/kohl.md", new { content = "Kohl\n" }, key);
        await PutAsync(carol, "/api/notes/Beete/kohl.md", new { content = "Kohl und Lauch\n", version = kohl }, key);
        await PutAsync(carol, "/api/notes/Beete/weg.md", new { content = "weg\n" }, key);
        await PutAsync(carol, "/api/notes/Beete/fort.md", new { content = "fort\n" }, key);
        await PutAsync(carol, "/api/notes/Beete/dill.md", new { content = "Dill\n" }, device);
        foreach (var command in new[]
        {
            "mv /Beete/kohl.md /Beete/kohl2.md", "rm /Beete/weg.md", "restore /Beete/weg.md", "rm /Beete/fort.md", "mkcp /Beete vorher",
            "restore /Beete/kohl2.md " + kohl,
        })
        {
            var (failed, text) = await carol.ShAsync(command, key);
            Assert.False(failed, $"{command}: {text}");
        }

        // The web app's folder upload, through her key: a write path of the server's own.
        using (var folder = await carol.SendAsync(
            HttpMethod.Post, "/api/upload/Beete/ord", new { files = new[] { new { path = "hoch.md", content = "Hochbeet\n" } }, onConflict = "ask" }, key))
        {
            Assert.True(folder.IsSuccessStatusCode, await folder.Content.ReadAsStringAsync(Ct));
        }

        using (var upload = await PutFileAsync(carol, "/api/files/Beete/bild.png", key)) Assert.True(upload.IsSuccessStatusCode, await upload.Content.ReadAsStringAsync(Ct));
        using (var board = await carol.SendAsync(HttpMethod.Post, "/api/vaults/Beete/board", bearer: key)) Assert.True(board.IsSuccessStatusCode, await board.Content.ReadAsStringAsync(Ct));
        using (var card = await carol.SendAsync(HttpMethod.Post, "/api/vaults/Beete/board/cards", new { title = "Mulchen", column = "Ideas" }, bearer: key))
        {
            Assert.True(card.IsSuccessStatusCode, await card.Content.ReadAsStringAsync(Ct));
        }

        var (bobKey, _) = await KeyAsync(bob, "Claude", vaultId, KeyRight.Maintain);
        var (bobReadKey, _) = await KeyAsync(bob, "Leser", vaultId, KeyRight.Read);
        // Her writer ids too: nothing Bob gets may group her writes, such as an activity row's key.
        var writerIds = Memberships.OpenWriterIds(await Share.MembershipAsync(app.Database, carols), await alice.VaultKeyAsync()).Keys;
        Assert.Equal(3, writerIds.Count());
        return new(alice, bob, carol, vaultId, bobKey, bobReadKey, [Marker, carol.Username, carol.User.Id, keyId, deviceId, CarolsAlias, .. writerIds]);
    }

    private static async Task<string?> PutAsync(RestClient user, string path, object body, string? bearer)
    {
        using var response = await user.SendAsync(HttpMethod.Put, path, body, bearer);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await RestClient.JsonAsync(response))["version"]?.GetValue<string>();
    }

    private static Task<HttpResponseMessage> PutFileAsync(RestClient user, string path, string? bearer) =>
        user.SendContentAsync(
            HttpMethod.Put, path, new ByteArrayContent(Png) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream") } }, bearer);

    private static void AssertClean(World w, string what, string text)
    {
        foreach (var name in w.Forbidden) Assert.False(text.Contains(name, StringComparison.OrdinalIgnoreCase), $"{what} names Carol ({name}): {text}");
    }

    [Fact]
    public async Task A_label_written_by_member_A_never_reaches_member_B()
    {
        var w = await WorldAsync();
        var bob = w.Bob;
        var since = Uri.EscapeDataString(DateTime.UtcNow.AddHours(-1).ToString("O"));
        string[] gets =
        [
            "/api/vaults", "/api/vaults/garten/tree", "/api/notes/garten/kohl2.md", "/api/notes/garten/salat.md", "/api/notes/garten/dill.md",
            "/api/history/garten/kohl2.md", "/api/history/garten/weg.md", "/api/history/garten/bild.png", "/api/history/garten/Board/Board.md",
            "/api/search/earlier?q=Kohl", "/api/version/garten/kohl2.md", "/api/version/garten/bild.png", "/api/version/garten/dill.md",
            $"/api/changes?since={since}", "/api/vaults/garten/activity", "/api/vaults/garten/trash", "/api/vaults/garten/checkpoints",
            "/api/vaults/garten/checkpoints/vorher/tree", "/api/vaults/garten/checkpoints/vorher/notes/kohl2.md", "/api/vaults/garten/board",
            "/api/vaults/garten/versions", "/api/links/garten/kohl2.md", "/api/backlinks/garten/kohl2.md", "/api/keys", "/api/session",
            "/api/notes/garten/ord/hoch.md", "/api/history/garten/ord/hoch.md", "/api/mentions/garten/kohl2.md",
        ];
        var answers = new List<(string What, string Text)>();
        foreach (var path in gets)
        {
            using var response = await bob.GetAsync(path);
            Assert.True(response.IsSuccessStatusCode, $"{path}: {response.StatusCode}");
            answers.Add((path, await response.Content.ReadAsStringAsync(Ct)));
        }

        // The same reads through his key, where a key may make them.
        foreach (var path in new[] { "/api/notes/garten/kohl2.md", "/api/history/garten/kohl2.md", "/api/vaults/garten/trash", "/api/vaults/garten/checkpoints", "/api/version/garten/bild.png" })
        {
            using var response = await bob.GetAsync(path, w.BobKey);
            Assert.True(response.IsSuccessStatusCode, $"{path} (key): {response.StatusCode}");
            answers.Add((path + " (key)", await response.Content.ReadAsStringAsync(Ct)));
        }

        // Conflicts carry the state they met: the file's, the note's.
        using (var file = await PutFileAsync(bob, "/api/files/garten/bild.png", null))
        {
            Assert.Equal(HttpStatusCode.Conflict, file.StatusCode);
            answers.Add(("file state", await file.Content.ReadAsStringAsync(Ct)));
        }

        using (var note = await bob.SendAsync(HttpMethod.Put, "/api/notes/garten/kohl2.md", new { content = "Lauch\n" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, note.StatusCode);
            answers.Add(("note conflict", await note.Content.ReadAsStringAsync(Ct)));
        }

        // A restore's conflict carries the state it met; a checkpoint restore's preview what it would do.
        var kohl2 = (await RestClient.JsonAsync(await bob.GetAsync("/api/notes/garten/kohl2.md")))["version"]!.GetValue<string>();
        using (var restore = await bob.SendAsync(HttpMethod.Post, "/api/restore/garten/kohl2.md", new { version = kohl2, expected = "0000000000" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, restore.StatusCode);
            answers.Add(("restore conflict", await restore.Content.ReadAsStringAsync(Ct)));
        }

        var bild = (await RestClient.JsonAsync(await bob.GetAsync("/api/version/garten/bild.png")))["version"]!.GetValue<string>();
        using (var restore = await bob.SendAsync(HttpMethod.Post, "/api/restore/garten/bild.png", new { version = bild, expected = "0000000000" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, restore.StatusCode);
            answers.Add(("restore file conflict", await restore.Content.ReadAsStringAsync(Ct)));
        }

        using (var preview = await bob.SendAsync(HttpMethod.Post, "/api/vaults/garten/checkpoints/vorher/preview"))
        {
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            answers.Add(("checkpoint preview", await preview.Content.ReadAsStringAsync(Ct)));
        }

        // Refusals.
        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Put, "/api/notes/garten/neu.md", new { content = "x\n" }),
            (HttpMethod.Delete, "/api/notes/garten/kohl2.md", null),
            (HttpMethod.Post, "/api/vaults/garten/board/cards", new { title = "Gießen", column = "Ideas" }),
        })
        {
            using var refused = await bob.SendAsync(method, path, body, w.BobReadKey);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            answers.Add(($"refused {path}", await refused.Content.ReadAsStringAsync(Ct)));
        }

        // The shell, as his agent reads writers.
        foreach (var command in new[]
        {
            "history /garten/kohl2.md", "history /garten", "history /garten/bild.png", "history /", "diff /garten/kohl2.md", "trash /garten", "trash /",
            "lscp /garten", "ls /", "ls /garten", "cat /garten/kohl2.md", "cat /garten/Board/Board.md", "rg MARKER /", "rg -i marker /",
            "diff --checkpoint vorher /garten", "restore --checkpoint vorher /garten", "history /garten/ord/hoch.md",
        })
        {
            var (_, text) = await bob.ShAsync(command, w.BobKey);
            answers.Add((command, text));
        }

        // Carol's entries are there, as "another user", in every answer that names a writer: through the renderer, not as
        // her empty label, so a route that skipped it couldn't pass on absence alone. (The tree, links, board, versions,
        // a note conflict and a checkpoint preview name no writer; changes lists only the viewer's own agents.)
        string[] rendered =
        [
            "lscp /garten", "trash /garten", "trash /", "history /garten/kohl2.md", "history /garten", "history /", "history /garten/bild.png",
            "history /garten/ord/hoch.md", "diff /garten/kohl2.md", "diff --checkpoint vorher /garten", "restore --checkpoint vorher /garten",
            "/api/vaults/garten/activity", "/api/vaults/garten/trash", "/api/vaults/garten/checkpoints", "/api/history/garten/kohl2.md",
            "/api/history/garten/weg.md", "/api/history/garten/bild.png", "/api/history/garten/Board/Board.md", "/api/history/garten/ord/hoch.md",
            "/api/notes/garten/kohl2.md", "/api/notes/garten/salat.md", "/api/notes/garten/dill.md", "/api/notes/garten/ord/hoch.md",
            "/api/search/earlier?q=Kohl", "/api/version/garten/kohl2.md", "/api/version/garten/bild.png", "/api/version/garten/dill.md",
            "/api/notes/garten/kohl2.md (key)", "/api/history/garten/kohl2.md (key)", "/api/vaults/garten/trash (key)", "/api/vaults/garten/checkpoints (key)",
            "/api/version/garten/bild.png (key)", "file state", "restore file conflict",
        ];
        foreach (var what in rendered)
        {
            Assert.True(answers.Single(a => a.What == what).Text.Contains(Kumbu.Server.Api.WriterLabels.AnotherUser, StringComparison.Ordinal), $"{what} shows no writer");
        }

        Assert.Contains(answers, a => a.Text.Contains(Kumbu.Server.Api.WriterLabels.AnotherUser, StringComparison.Ordinal));
        foreach (var (what, text) in answers) AssertClean(w, what, text);
    }

    [Fact]
    public async Task A_sync_hint_to_member_B_names_nothing_of_member_A()
    {
        var w = await WorldAsync();
        using var response = await w.Bob.SendAsync(HttpMethod.Post, "/api/ws-ticket", bearer: w.BobKey);
        var ticket = (await RestClient.JsonAsync(response))["ticket"]!.GetValue<string>();
        using var socket = await Api.Server.CreateWebSocketClient().ConnectAsync(new Uri($"wss://kumbu.test/api/ws?ticket={Uri.EscapeDataString(ticket)}"), Ct);

        await PutAsync(w.Carol, "/api/notes/Beete/neu.md", new { content = "neu\n" }, null);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var buffer = new byte[4096];
        var received = await socket.ReceiveAsync(buffer, timeout.Token);
        var message = Encoding.UTF8.GetString(buffer, 0, received.Count);
        Assert.Contains("garten", message, StringComparison.Ordinal);
        AssertClean(w, "socket", message);
    }

    /// <summary>
    /// What Bob's tab and his key's socket receive while Carol edits and writes in the web app under
    /// <paramref name="carolsAlias"/>: both must hear something (a hint, and Carol as "another user" in the tab), and
    /// nothing of hers (checked by the callers).
    /// </summary>
    private async Task<IReadOnlyList<string>> BobsTranscriptAsync(World w, string carolsAlias)
    {
        using var tab = await SyncSockets.OpenAsync(Api, w.Bob);
        using var device = await SyncSockets.OpenAsync(Api, w.Bob, w.BobKey);
        using var carols = await SyncSockets.OpenAsync(Api, w.Carol);

        await SyncSockets.EditingAsync(carols, carolsAlias, "lauch.md", on: true);
        // Heard before the off: the last state per note wins, and an off soon after would replace it unsent.
        var editing = (await SyncSockets.PresenceAsync(tab, TimeSpan.FromSeconds(3)))!;
        Assert.Single(SyncSockets.Editors(editing));
        await PutAsync(w.Carol, $"/api/notes/{carolsAlias}/lauch.md", new { content = "Lauch\n" }, null);
        await SyncSockets.EditingAsync(carols, carolsAlias, "lauch.md", on: false);

        var heard = (await SyncSockets.DrainAsync(tab, TimeSpan.FromSeconds(2))).Concat(await SyncSockets.DrainAsync(device, TimeSpan.FromSeconds(1))).Append(editing.ToJsonString()).ToList();
        // Matched loosely, so that a hint carrying more than it should is caught by what Bob mustn't read, not here.
        Assert.Contains(heard, m => m.Contains("\"type\":\"changed\"", StringComparison.Ordinal) && m.Contains("\"vault\":\"garten\"", StringComparison.Ordinal));
        return heard;
    }

    [Fact]
    public async Task A_label_written_by_member_A_never_reaches_member_B_over_the_socket()
    {
        var w = await WorldAsync();
        await app.Database.GetCollection<UserDocument>("users").UpdateOneAsync(
            u => u.Id == w.Carol.User.Id, Builders<UserDocument>.Update.Set(u => u.Display, "MARKER-Carol"), cancellationToken: Ct);

        var heard = await BobsTranscriptAsync(w, "Beete");
        foreach (var message in heard) AssertClean(w, "socket", message);
        Assert.Contains(heard, m => m.Contains(Kumbu.Server.Api.WriterLabels.AnotherUser, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_members_alias_never_reaches_another_member_over_the_socket()
    {
        var w = await WorldAsync();
        var carols = await Share.MembershipOfAsync(app.Database, w.Carol, w.VaultId);
        using (var renamed = await w.Carol.SendAsync(HttpMethod.Put, $"/api/memberships/{carols}/alias", new { alias = "MARKER-beet" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        }

        var heard = await BobsTranscriptAsync(w, "MARKER-beet");
        foreach (var message in heard) AssertClean(w, "socket", message);
        Assert.Contains(heard, m => m.Contains(Kumbu.Server.Api.WriterLabels.AnotherUser, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Writer_names_are_never_written_into_note_content()
    {
        var w = await WorldAsync();
        var vaultKey = await w.Alice.VaultKeyAsync();

        foreach (var collection in new[] { "notes", "checkpoint_texts", "trash", "moves", "checkpoints", "attachments", "attachment_trash" })
        {
            foreach (var payload in await SealedPayloads.OpenAllAsync(app.Database, w.VaultId, vaultKey, collection, Ct))
            {
                var json = payload.ToJsonString();
                Assert.DoesNotContain(Marker, json, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(w.Carol.Username, json, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(CarolsAlias, json, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.NotEmpty(await SealedPayloads.OpenAllAsync(app.Database, w.VaultId, vaultKey, "checkpoint_texts", Ct));
    }

    [Fact]
    public async Task A_members_alias_never_reaches_the_owner()
    {
        var w = await WorldAsync();
        var answers = new List<(string What, string Text)>();
        foreach (var path in new[]
        {
            "/api/vaults", "/api/vaults/Gartenarbeit/tree", "/api/notes/Gartenarbeit/Board/Board.md", "/api/history/Gartenarbeit/Board/Board.md",
            "/api/vaults/Gartenarbeit/board", "/api/vaults/Gartenarbeit/activity", "/api/vault-events", "/api/vaults/Gartenarbeit/access",
        })
        {
            using var response = await w.Alice.GetAsync(path);
            Assert.True(response.IsSuccessStatusCode, $"{path}: {response.StatusCode}");
            answers.Add((path, await response.Content.ReadAsStringAsync(Ct)));
        }

        var key = await w.Alice.KeyAsync("Claude", KeyRight.Read);
        foreach (var command in new[] { "cat /Gartenarbeit/Board/Board.md", "history /Gartenarbeit", "rg -i beete /" })
        {
            answers.Add((command, string.Join("\n", await w.Alice.ShBlocksAsync(command, key))));
        }

        Assert.Contains(answers, a => a.What == "/api/notes/Gartenarbeit/Board/Board.md" && a.Text.Contains("kumbu-board", StringComparison.Ordinal));
        foreach (var (what, text) in answers) Assert.False(text.Contains(CarolsAlias, StringComparison.OrdinalIgnoreCase), $"{what} holds Carol's alias: {text}");
    }

    [Fact]
    public async Task Events_and_notices_never_name_another_member_to_a_member()
    {
        var w = await WorldAsync();
        var carols = await Share.MembershipOfAsync(app.Database, w.Carol, w.VaultId);
        var bobs = await Share.MembershipOfAsync(app.Database, w.Bob, w.VaultId);
        // Alice lowers both roles (no password window needed), then removes Carol.
        foreach (var id in new[] { carols, bobs })
        {
            using var lowered = await w.Alice.SendAsync(HttpMethod.Put, $"/api/vaults/Gartenarbeit/memberships/{id}/role", new { role = "write" });
            Assert.Equal(HttpStatusCode.OK, lowered.StatusCode);
        }

        using (var revoked = await w.Alice.SendAsync(HttpMethod.Post, $"/api/vaults/Gartenarbeit/memberships/{carols}/revoke")) Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);

        var answers = new List<(string What, string Text)>();
        foreach (var path in new[] { "/api/vaults/garten/activity", "/api/vault-events", "/api/vaults", "/api/vaults/garten/access" })
        {
            using var response = await w.Bob.GetAsync(path);
            answers.Add((path, await response.Content.ReadAsStringAsync(Ct)));
        }

        // His agent is told once, and of his own role only.
        var told = string.Join("\n", await w.Bob.ShBlocksAsync("ls /", w.BobKey));
        Assert.Contains("Notice: Your role in /garten is now write.", told, StringComparison.Ordinal);
        answers.Add(("notices", told));

        // Bob sees his own role change, through the renderer, and nothing of Carol's.
        Assert.Contains("changed your role to write", answers.Single(a => a.What == "/api/vaults/garten/activity").Text, StringComparison.Ordinal);
        Assert.Single((await RestClient.JsonAsync(await w.Bob.GetAsync("/api/vault-events"))).AsArray());
        Assert.DoesNotContain(carols, string.Concat(answers.Select(a => a.Text)), StringComparison.Ordinal);
        foreach (var (what, text) in answers) AssertClean(w, what, text);
        // The owner, by contrast, sees Carol by her label.
        using var owners = await w.Alice.GetAsync("/api/vaults/Gartenarbeit/activity");
        Assert.Contains($"You removed {w.Carol.Username}.", await owners.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_only_member_never_sees_the_owners_credential_name_in_a_move_hint()
    {
        var w = await WorldAsync();
        const string OwnersKeyName = "MARKER-owner-key";
        var (ownersKey, _) = await KeyAsync(w.Alice, OwnersKeyName, w.VaultId, KeyRight.Write);
        var (moved, text) = await w.Alice.ShAsync("mv /Gartenarbeit/plan.md /Gartenarbeit/plan2.md", ownersKey);
        Assert.False(moved, text);

        var answers = new List<(string What, string Text)>();
        using (var gone = await w.Bob.SendAsync(HttpMethod.Get, "/api/notes/garten/plan.md", bearer: w.BobReadKey))
        {
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
            answers.Add(("404", await gone.Content.ReadAsStringAsync(Ct)));
        }

        answers.Add(("cat", (await w.Bob.ShAsync("cat /garten/plan.md", w.BobReadKey)).Text));
        // The commands that write answer a read-only key with a refusal first; his maintaining key gets to the hint.
        foreach (var command in new[] { "mv /garten/plan.md /garten/x.md", "rm /garten/plan.md", "mkcp /garten/plan.md nachher" })
        {
            answers.Add((command, (await w.Bob.ShAsync(command, w.BobKey)).Text));
        }

        foreach (var (what, answer) in answers)
        {
            Assert.DoesNotContain(Marker, answer, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(OwnersKeyName, answer, StringComparison.OrdinalIgnoreCase);
            Assert.False(answer.Contains(w.Alice.Username, StringComparison.OrdinalIgnoreCase), $"{what} names Alice");
        }

        // The hint is still there, and says where the note went.
        Assert.All(answers, a => Assert.Contains("it was moved to /garten/plan2.md", a.Text, StringComparison.Ordinal));
    }
}
