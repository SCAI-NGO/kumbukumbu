// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Kumbu.Server.Tests.Auth;

[Collection("app")]
public sealed class DevTokenTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StringContent InitializeBody() => new(
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"raw","version":"0"}}}""",
        Encoding.UTF8,
        "application/json");

    [Fact]
    public async Task Mcp_without_authorization_is_401_with_bearer_challenge()
    {
        using var http = app.CreateClient();

        using var response = await http.PostAsync("/mcp", InitializeBody(), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
    }

    [Fact]
    public async Task Mcp_with_wrong_token_is_401()
    {
        using var http = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = InitializeBody() };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");

        using var response = await http.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Endpoint routing matches literal segments case-insensitively, so /MCP reaches MapMcp("/mcp").
    // The middleware therefore fails closed: everything except /healthz needs the token.
    [Theory]
    [InlineData("/MCP")]
    [InlineData("/Mcp")]
    [InlineData("/mcp/")]
    public async Task Mcp_path_variants_without_authorization_are_401(string path)
    {
        using var http = app.CreateClient();

        using var response = await http.PostAsync(path, InitializeBody(), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
    }

    [Fact]
    public async Task Unknown_path_without_authorization_is_401()
    {
        using var http = app.CreateClient();

        using var response = await http.GetAsync("/anything", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Healthz_in_upper_case_needs_no_token()
    {
        using var http = app.CreateClient();

        using var response = await http.GetAsync("/HEALTHZ", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Healthz_needs_no_token()
    {
        using var http = app.CreateClient();

        using var response = await http.GetAsync("/healthz", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync(Ct));
    }
}
