// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Kumbu.Core.Attachments;
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Kumbu.Server.Auth;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.OAuth;
using Kumbu.Server.Tests.Sharing;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Kumbu.Server.Tests.Api;

/// <summary>
/// <c>PUT /api/agent-upload/{token}</c> end to end: each test gets its URL from the <c>upload</c> tool, then sends the
/// bytes with nothing but the URL, as <c>curl</c> does. The upload runs as the credential that asked, resolved afresh.
/// </summary>
[Collection("app")]
public sealed class AgentUploadApiTests(KumbuAppFactory app)
{
    private const string Callback = "https://claude.test/cb";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    /// <summary>An address of this test's own, so the wrong-key limit of other tests doesn't reach it.</summary>
    private readonly string _address = $"198.31.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}";

    /// <summary>The real time, in whole seconds: MongoDB's TTL monitor sweeps slots made at a past fake time.</summary>
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    /// <summary>The public app on <see cref="_time"/>, vaults locked after a minute idle.</summary>
    private WebApplicationFactory<Program> Timed() => Api.WithWebHostBuilder(b =>
    {
        b.UseSetting("Kumbu:Vaults:IdleMinutes", "1");
        b.ConfigureServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(_time);
        });
    });

    private static byte[] Png => FileApiTests.Png(4, 3);

    /// <summary>The upload URL the tool answers <paramref name="bearer"/> for <paramref name="path"/>.</summary>
    private static async Task<string> UrlAsync(RestClient user, string bearer, string path, string? version = null)
    {
        var args = new Dictionary<string, object?> { ["path"] = path };
        if (version is not null) args["version"] = version;
        var result = await user.ToolAsync("upload", args, bearer);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        Assert.True(result.IsError != true, text);
        return text.Split('\n')[0];
    }

    /// <summary>A PUT of <paramref name="content"/> to the URL's path on <paramref name="api"/>, from this test's address,
    /// with no credential unless <paramref name="change"/> adds one.</summary>
    private async Task<HttpResponseMessage> PutAsync(
        string url, HttpContent content, WebApplicationFactory<Program>? api = null, Action<HttpRequestMessage>? change = null, string scheme = "https")
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(url).PathAndQuery) { Content = content };
        request.Headers.Add("X-Forwarded-For", _address);
        change?.Invoke(request);
        using var client = (api ?? Api).CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false, AllowAutoRedirect = false, BaseAddress = new Uri($"{scheme}://kumbu.test"),
        });
        return await client.SendAsync(request, Ct);
    }

    private static ByteArrayContent Bytes(byte[] bytes) => new(bytes);

    private static async Task AssertAskAgainAsync(HttpStatusCode expected, HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(expected == response.StatusCode, $"{response.StatusCode}: {body}");
        Assert.EndsWith(" " + AgentUploadApi.AskAgain, JsonNode.Parse(body)!["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    private static async Task<JsonNode> CreatedAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.Created)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(expected == response.StatusCode, $"{response.StatusCode}: {body}");
        return JsonNode.Parse(body)!;
    }

    private static async Task AssertNoFileAsync(RestClient owner, string path)
    {
        using var get = await owner.GetAsync(owner.Url(path, "files"));
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    /// <summary>The one history entry of a file, as its owner's session reads it.</summary>
    private static async Task<JsonNode> OnlyVersionAsync(RestClient owner, string path)
    {
        using var history = await owner.GetAsync(owner.Url(path, "history"));
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        return Assert.Single((await RestClient.JsonAsync(history)).AsArray())!;
    }

    private static Task<(string Key, ApiKeyInfo Info)> KeyAsync(RestClient user, string name, IReadOnlyDictionary<string, KeyRight> vaults) =>
        user.Api.Services.GetRequiredService<ApiKeys>().CreateAsync(user.User, user.UserKey, DateTime.UtcNow, name, vaults, Ct);

    /// <summary>Alice's "Gartenarbeit", shared with Bob (who has his own "Work") as "garten", and a key of Bob's on both.</summary>
    private async Task<(RestClient Alice, RestClient Bob, string MembershipId, string BobsKey)> SharedAsync()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var membership = await alice.ShareWithAsync(bob, "garten", MemberRole.Write);
        var (key, _) = await KeyAsync(bob, "Claude", new Dictionary<string, KeyRight> { [await alice.VaultIdAsync()] = KeyRight.Write, [await bob.VaultIdAsync()] = KeyRight.Write });
        return (alice, bob, membership, key);
    }

    [Fact]
    public async Task Stores_the_file_as_the_keys_write()
    {
        var user = await RestClient.NewAsync(Api);
        var key = await user.KeyAsync("Claude", KeyRight.Write);
        var url = await UrlAsync(user, key, "/Arbeit/shots/a.png");

        using var put = await PutAsync(url, Bytes(Png));

        var file = (await CreatedAsync(put))["file"]!;
        Assert.Equal("/Arbeit/shots/a.png", file["path"]!.GetValue<string>());
        Assert.Equal("image/png", file["mediaType"]!.GetValue<string>());
        var entry = await OnlyVersionAsync(user, "shots/a.png");
        Assert.Equal("Claude", entry["writer"]!.GetValue<string>());
        Assert.Equal("agent", entry["writerKind"]!.GetValue<string>());
    }

    [Fact]
    public async Task Works_once()
    {
        var user = await RestClient.NewAsync(Api);
        var url = await UrlAsync(user, await user.KeyAsync("Claude", KeyRight.Write), "/Arbeit/a.png");
        using var first = await PutAsync(url, Bytes(Png));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var second = await PutAsync(url, Bytes(Png));

        await AssertAskAgainAsync(HttpStatusCode.Unauthorized, second);
    }

    [Fact]
    public async Task Expires_after_ten_minutes()
    {
        await using var api = Timed();
        var user = await RestClient.NewAsync(api);
        var url = await UrlAsync(user, await user.KeyAsync("Claude", KeyRight.Write), "/Arbeit/a.png");

        _time.Advance(UploadTickets.Lifetime);
        using var put = await PutAsync(url, Bytes(Png), api);

        await AssertAskAgainAsync(HttpStatusCode.Unauthorized, put);
        await AssertNoFileAsync(user, "a.png");
    }

    [Fact]
    public async Task Revoked_key_is_refused()
    {
        var user = await RestClient.NewAsync(Api);
        var (key, info) = await KeyAsync(user, "Claude", new Dictionary<string, KeyRight> { [await user.VaultIdAsync()] = KeyRight.Write });
        var url = await UrlAsync(user, key, "/Arbeit/a.png");
        Assert.True(await Api.Services.GetRequiredService<ApiKeys>().RevokeAsync(user.User, info.Id, Ct));

        using var put = await PutAsync(url, Bytes(Png));

        await AssertAskAgainAsync(HttpStatusCode.Forbidden, put);
        await AssertNoFileAsync(user, "a.png");
    }

    [Fact]
    public async Task Lowered_right_is_refused()
    {
        var (alice, _, membership, key) = await SharedAsync();
        var url = await UrlAsync(alice, key, "/garten/a.png");
        await Share.SetRoleAsync(app.Database, membership, MemberRole.Read);

        using var put = await PutAsync(url, Bytes(Png));

        await AssertAskAgainAsync(HttpStatusCode.Forbidden, put);
        await AssertNoFileAsync(alice, "a.png");
    }

    [Fact]
    public async Task Ended_membership_is_refused()
    {
        var (alice, _, membership, key) = await SharedAsync();
        var url = await UrlAsync(alice, key, "/garten/a.png");
        await Share.SetStatusAsync(app.Database, membership, MembershipStatus.Revoked);

        using var put = await PutAsync(url, Bytes(Png));

        // The key still opens Bob's own vault; the shared one is no longer there for it.
        await AssertAskAgainAsync(HttpStatusCode.NotFound, put);
        await AssertNoFileAsync(alice, "a.png");
    }

    [Fact]
    public async Task Rekeyed_vault_is_refused()
    {
        var user = await RestClient.NewAsync(Api);
        var key = await user.KeyAsync("Claude", KeyRight.Write);
        var url = await UrlAsync(user, key, "/Arbeit/a.png");
        await Api.Services.GetRequiredService<VaultCatalog>().RekeyAsync(await user.VaultIdAsync(), user.User.Id, user.UserKey, Ct);

        using var put = await PutAsync(url, Bytes(Png));

        // The re-keying revoked the key whole: it opens nothing any more.
        await AssertAskAgainAsync(HttpStatusCode.Forbidden, put);
        await AssertNoFileAsync(user, "a.png");
    }

    [Fact]
    public async Task Ignores_cookie_and_bearer()
    {
        var alice = await RestClient.NewAsync(Api);
        var mallory = await RestClient.NewAsync(Api);
        var mallorysKey = await mallory.KeyAsync("Mallory's agent", KeyRight.Write);
        void Mallorys(HttpRequestMessage r)
        {
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mallorysKey);
            r.Headers.Add("Cookie", $"{CallerMiddleware.SessionCookie}={mallory.Session}");
            r.Headers.Add("Origin", RestClient.Origin);
        }

        using var unknown = await PutAsync($"{KumbuAppFactory.PublicUrl}/api/agent-upload/{new string('A', 43)}", Bytes(Png), change: Mallorys);
        var url = await UrlAsync(alice, await alice.KeyAsync("Claude", KeyRight.Write), "/Arbeit/a.png");
        using var valid = await PutAsync(url, Bytes(Png), change: Mallorys);

        await AssertAskAgainAsync(HttpStatusCode.Unauthorized, unknown);
        await CreatedAsync(valid);
        Assert.Equal("Claude", (await OnlyVersionAsync(alice, "a.png"))["writer"]!.GetValue<string>());
        await AssertNoFileAsync(mallory, "a.png");
    }

    [Fact]
    public async Task Wrong_bytes_use_the_ticket()
    {
        var user = await RestClient.NewAsync(Api);
        var url = await UrlAsync(user, await user.KeyAsync("Claude", KeyRight.Write), "/Arbeit/a.png");

        using var wrong = await PutAsync(url, Bytes(FileApiTests.Pdf));
        using var again = await PutAsync(url, Bytes(Png));

        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        await AssertAskAgainAsync(HttpStatusCode.Unauthorized, again);
        await AssertNoFileAsync(user, "a.png");
    }

    [Fact]
    public async Task Too_large_chunked_body_is_413()
    {
        var user = await RestClient.NewAsync(Api);
        var url = await UrlAsync(user, await user.KeyAsync("Claude", KeyRight.Write), "/Arbeit/big.pdf");

        using var put = await PutAsync(url, new ChunkedPdf(AttachmentFormat.MaxBytes + 1));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, put.StatusCode);
        Assert.Contains("15 MB", await RestClient.ErrorAsync(put), StringComparison.Ordinal);
        await AssertNoFileAsync(user, "big.pdf");
    }

    /// <summary><paramref name="size"/> bytes of a PDF in 64 KiB pieces, without a Content-Length.</summary>
    private sealed class ChunkedPdf(long size) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var piece = new byte[64 * 1024];
            FileApiTests.Pdf.CopyTo(piece, 0);
            for (var sent = 0L; sent < size; sent += piece.Length)
            {
                await stream.WriteAsync(piece.AsMemory(0, (int)Math.Min(piece.Length, size - sent)));
                if (sent == 0) Array.Clear(piece);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [Fact]
    public async Task Curls_default_content_type_is_ignored()
    {
        var user = await RestClient.NewAsync(Api);
        var url = await UrlAsync(user, await user.KeyAsync("Claude", KeyRight.Write), "/Arbeit/a.png");
        var content = Bytes(Png);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");

        using var put = await PutAsync(url, content);

        Assert.Equal("image/png", (await CreatedAsync(put))["file"]!["mediaType"]!.GetValue<string>());
    }

    [Fact]
    public async Task Overwrites_with_the_tickets_version()
    {
        var user = await RestClient.NewAsync(Api);
        var version = await user.CreateFileAsync("kept.png", Png);
        var url = await UrlAsync(user, await user.KeyAsync("Claude", KeyRight.Write), "/Arbeit/kept.png", version);

        using var put = await PutAsync(url, Bytes(FileApiTests.Png(8, 8)));

        var file = (await CreatedAsync(put, HttpStatusCode.OK))["file"]!;
        Assert.NotEqual(version, file["version"]!.GetValue<string>());
        Assert.Equal(8, file["width"]!.GetValue<int>());
    }

    [Fact]
    public async Task Answers_a_conflict_with_the_file_as_it_is_now()
    {
        var user = await RestClient.NewAsync(Api);
        var version = await user.CreateFileAsync("kept.png", Png);
        var url = await UrlAsync(user, await user.KeyAsync("Claude", KeyRight.Write), "/Arbeit/kept.png", version);
        using (var meanwhile = await user.PutFileAsync("kept.png", FileApiTests.Png(6, 6), version)) Assert.Equal(HttpStatusCode.OK, meanwhile.StatusCode);

        using var put = await PutAsync(url, Bytes(FileApiTests.Png(8, 8)));

        var conflict = await CreatedAsync(put, HttpStatusCode.Conflict);
        Assert.Equal(6, conflict["current"]!["width"]!.GetValue<int>());
    }

    [Fact]
    public async Task Locked_vault_still_works()
    {
        await using var api = Timed();
        var user = await RestClient.NewAsync(api);
        var url = await UrlAsync(user, await user.KeyAsync("Claude", KeyRight.Write), "/Arbeit/a.png");
        var catalog = api.Services.GetRequiredService<VaultCatalog>();
        var vaultId = await user.VaultIdAsync();

        _time.Advance(TimeSpan.FromMinutes(2));
        catalog.Sweep();
        Assert.False(catalog.IsInWorkingSet(vaultId));
        using var put = await PutAsync(url, Bytes(Png), api);

        await CreatedAsync(put);
    }

    [Fact]
    public async Task Answers_markdown_and_link()
    {
        var user = await RestClient.NewAsync(Api, vault: "V");
        var key = await user.KeyAsync("Claude", KeyRight.Write);
        var vaultId = await user.VaultIdAsync();

        using var image = await PutAsync(await UrlAsync(user, key, "/V/My Shots/rose (1).png"), Bytes(Png));
        using var pdf = await PutAsync(await UrlAsync(user, key, "/V/Docs/manual.pdf"), Bytes(FileApiTests.Pdf));

        var shot = await CreatedAsync(image);
        Assert.Equal("![rose (1).png](/My%20Shots/rose%20%281%29.png)", shot["markdown"]!.GetValue<string>());
        Assert.Equal($"{KumbuAppFactory.PublicUrl}/n/{vaultId}/My%20Shots/rose%20%281%29.png", shot["link"]!.GetValue<string>());
        Assert.Equal("[manual.pdf](/Docs/manual.pdf)", (await CreatedAsync(pdf))["markdown"]!.GetValue<string>());
        using var sheet = await PutAsync(await UrlAsync(user, key, "/V/Docs/budget.xlsx"), Bytes([0x50, 0x4B, 0x03, 0x04, 0x00]));
        var budget = await CreatedAsync(sheet);
        Assert.Equal("[budget.xlsx](/Docs/budget.xlsx)", budget["markdown"]!.GetValue<string>());
        Assert.Equal("application/octet-stream", budget["file"]!["mediaType"]!.GetValue<string>());
        // What the answer says to write leads to the file.
        var written = await user.ToolAsync("write", new() { ["path"] = "/V/Garden.md", ["content"] = $"# Garden\n\n{shot["markdown"]}\n" }, key);
        Assert.NotEqual(true, written.IsError);
        var (_, links) = await user.ShAsync("links /V/Garden.md", key);
        Assert.Contains("→ /V/My Shots/rose (1).png", links, StringComparison.Ordinal);
    }

    /// <summary>A user with vault <c>Arbeit</c> on a connection named "Work" at write, authorised by a fresh client: the
    /// flow and its first access and refresh token.</summary>
    private async Task<(OAuthFlow Flow, string Access, string Refresh)> ConnectedAsync()
    {
        var owner = await RestClient.NewAsync(Api);
        using var created = await owner.SendAsync(
            HttpMethod.Post, "/api/connections", new { name = "Work", vaults = new Dictionary<string, string> { ["Arbeit"] = "write" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await RestClient.JsonAsync(created))["id"]!.GetValue<string>();
        var flow = await OAuthFlow.StartAsync(Api, owner, id, await OAuthFlow.RegisterAsync(Api, Callback), Callback);
        await flow.AllowAsync();
        using var token = await flow.TokenAsync();
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
        var pair = await RestClient.JsonAsync(token);
        return (flow, pair["access_token"]!.GetValue<string>(), pair["refresh_token"]!.GetValue<string>());
    }

    /// <summary>The upload URL the tool answers over the connection's own URL, with its access token.</summary>
    private async Task<string> ConnectionUrlAsync(OAuthFlow flow, string access, string path)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(flow.Resource),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {access}" },
            },
            Api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri(KumbuAppFactory.PublicUrl) }),
            NullLoggerFactory.Instance,
            ownsHttpClient: false);
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: Ct);
        var result = await client.CallToolAsync("upload", new Dictionary<string, object?> { ["path"] = path }, cancellationToken: Ct);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        Assert.True(result.IsError != true, text);
        return text.Split('\n')[0];
    }

    [Fact]
    public async Task Connection_ticket_resolves_through_the_connection()
    {
        var (flow, access, _) = await ConnectedAsync();
        var url = await ConnectionUrlAsync(flow, access, "/Arbeit/a.png");

        using var put = await PutAsync(url, Bytes(Png));

        await CreatedAsync(put);
        var entry = await OnlyVersionAsync(flow.Owner, "a.png");
        Assert.Equal("Work", entry["writer"]!.GetValue<string>());
        Assert.Equal("agent", entry["writerKind"]!.GetValue<string>());
    }

    [Fact]
    public async Task Connection_ticket_after_a_refresh_still_works()
    {
        var (flow, access, refresh) = await ConnectedAsync();
        var url = await ConnectionUrlAsync(flow, access, "/Arbeit/a.png");
        // A refresh rotates the refresh token and its slots; the old access token lives out its hour (RefreshTests).
        using (var refreshed = await flow.RefreshAsync(refresh)) Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);

        using var put = await PutAsync(url, Bytes(Png));

        await CreatedAsync(put);
    }

    [Fact]
    public async Task Connection_ticket_of_a_revoked_token_is_refused()
    {
        var (flow, access, _) = await ConnectedAsync();
        var url = await ConnectionUrlAsync(flow, access, "/Arbeit/a.png");
        using (var revoked = await flow.RevokeAsync(access)) Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);

        using var put = await PutAsync(url, Bytes(Png));

        await AssertAskAgainAsync(HttpStatusCode.Forbidden, put);
        await AssertNoFileAsync(flow.Owner, "a.png");
    }

    [Fact]
    public async Task Unknown_token_counts_as_a_wrong_key()
    {
        for (var i = 0; i < WrongKeyLimiter.PermitLimit; i++)
        {
            using var wrong = await PutAsync($"{KumbuAppFactory.PublicUrl}/api/agent-upload/guess{i}", Bytes(Png));
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using var limited = await PutAsync($"{KumbuAppFactory.PublicUrl}/api/agent-upload/guess", Bytes(Png));

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task Plain_http_is_refused_outside_development_and_spends_the_url()
    {
        await using var production = Api.WithWebHostBuilder(b => b.UseEnvironment("Production"));
        var user = (await RestClient.NewAsync(Api)).On(production);
        var url = await UrlAsync(user, await user.KeyAsync("Claude", KeyRight.Write), "/Arbeit/a.png");

        using var plain = await PutAsync(url, Bytes(Png), production, scheme: "http");
        using var again = await PutAsync(url, Bytes(Png), production);

        await AssertAskAgainAsync(HttpStatusCode.Forbidden, plain);
        Assert.Contains("HTTPS", await RestClient.ErrorAsync(plain), StringComparison.Ordinal);
        await AssertAskAgainAsync(HttpStatusCode.Unauthorized, again);
        await AssertNoFileAsync(user, "a.png");
    }
}
