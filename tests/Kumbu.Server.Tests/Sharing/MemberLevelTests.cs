// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Kumbu.Server.Tests.Sharing;

/// <summary>
/// Effective levels of members (collab design §3.3): a member's session acts at her role, her own keys and connections
/// at <c>min(role, sealed level)</c>, worked out on every request; the refusals and their codes are phase 0's.
/// </summary>
[Collection("app")]
public sealed class MemberLevelTests(KumbuAppFactory app)
{
    private const string Board = "/api/notes/garten/Board/Board.md";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    /// <summary>Alice's "Gartenarbeit", shared with Bob as "garten" at <paramref name="role"/>; the membership's id.</summary>
    private async Task<(RestClient Bob, string VaultId, string MembershipId)> SharedAsync(MemberRole role)
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var membership = await alice.ShareWithAsync(bob, "garten", role);
        return (bob, await alice.VaultIdAsync(), membership);
    }

    private async Task<string> KeyAsync(RestClient bob, string vaultId, KeyRight right)
    {
        var (key, _) = await Api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            bob.User, bob.UserKey, DateTime.UtcNow, "Claude", new Dictionary<string, KeyRight> { [vaultId] = right }, Ct);
        return key;
    }

    private static Task<HttpResponseMessage> PutAsync(RestClient bob, string url, string? bearer = null) =>
        bob.SendAsync(HttpMethod.Put, url, new { content = "# Beete\n" }, bearer: bearer);

    private static async Task AssertRefusedAsync(HttpResponseMessage response, string code, string sentence)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await RestClient.JsonAsync(response);
        Assert.Equal(code, body["code"]!.GetValue<string>());
        Assert.Contains(sentence, body["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_member_session_is_refused_writes_with_read_only()
    {
        var (bob, _, _) = await SharedAsync(MemberRole.Read);

        using var refused = await PutAsync(bob, "/api/notes/garten/a.md");

        // A session is no key (decision 4).
        await AssertRefusedAsync(refused, "read-only", "you can only read vault garten");
        using var own = await PutAsync(bob, "/api/notes/Work/a.md");
        Assert.Equal(HttpStatusCode.Created, own.StatusCode);
    }

    [Fact]
    public async Task A_write_member_may_write_notes_but_not_the_board()
    {
        var (bob, _, _) = await SharedAsync(MemberRole.Write);

        using (var note = await PutAsync(bob, "/api/notes/garten/a.md")) Assert.Equal(HttpStatusCode.Created, note.StatusCode);
        using var board = await PutAsync(bob, Board);

        await AssertRefusedAsync(board, "structural", "Only those who may maintain *garten*");
    }

    [Fact]
    public async Task A_maintain_key_of_a_read_member_acts_as_read()
    {
        var (bob, vaultId, _) = await SharedAsync(MemberRole.Read);
        var key = await KeyAsync(bob, vaultId, KeyRight.Maintain);

        using var refused = await PutAsync(bob, "/api/notes/garten/a.md", key);

        await AssertRefusedAsync(refused, "read-only", "this key can only read vault garten");
    }

    [Fact]
    public async Task A_maintain_key_of_a_write_member_acts_as_write()
    {
        var (bob, vaultId, _) = await SharedAsync(MemberRole.Write);
        var key = await KeyAsync(bob, vaultId, KeyRight.Maintain);

        using (var note = await PutAsync(bob, "/api/notes/garten/a.md", key)) Assert.Equal(HttpStatusCode.Created, note.StatusCode);
        using var board = await PutAsync(bob, Board, key);

        await AssertRefusedAsync(board, "structural", "Only those who may maintain *garten*");
    }

    [Fact]
    public async Task A_read_key_of_a_maintain_member_acts_as_read()
    {
        var (bob, vaultId, _) = await SharedAsync(MemberRole.Maintain);
        var key = await KeyAsync(bob, vaultId, KeyRight.Read);

        using var refused = await PutAsync(bob, "/api/notes/garten/a.md", key);

        await AssertRefusedAsync(refused, "read-only", "this key can only read vault garten");
        // Her session may change the board.
        using var board = await PutAsync(bob, Board);
        Assert.Equal(HttpStatusCode.Created, board.StatusCode);
    }

    [Fact]
    public async Task A_raised_role_lifts_the_cap_on_the_next_request()
    {
        var (bob, vaultId, membershipId) = await SharedAsync(MemberRole.Read);
        var key = await KeyAsync(bob, vaultId, KeyRight.Maintain);
        using (var before = await PutAsync(bob, Board, key)) Assert.Equal(HttpStatusCode.Forbidden, before.StatusCode);
        using (var session = await PutAsync(bob, "/api/notes/garten/a.md")) Assert.Equal(HttpStatusCode.Forbidden, session.StatusCode);

        await Share.SetRoleAsync(app.Database, membershipId, MemberRole.Maintain);

        using (var board = await PutAsync(bob, Board, key)) Assert.Equal(HttpStatusCode.Created, board.StatusCode);
        using var note = await PutAsync(bob, "/api/notes/garten/a.md");
        Assert.Equal(HttpStatusCode.Created, note.StatusCode);
    }
}
