// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Kumbu.Server.Tests.Api;

/// <summary>The build version: anonymous <c>GET /api/version</c> and <c>X-Kumbu-Version</c> on every API answer.</summary>
[Collection("app")]
public sealed class VersionTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Version_answers_without_signing_in()
    {
        using var response = await app.CreateClient().GetAsync("/api/version", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await response.Content.ReadFromJsonAsync<VersionView>(Ct);
        Assert.Equal(BuildVersion.Full, view!.Version);
    }

    [Fact]
    public async Task Every_api_response_carries_the_version()
    {
        var anonymous = app.CreateClient();
        var bearer = app.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", KumbuAppFactory.Token);

        using var version = await anonymous.GetAsync("/api/version", Ct);
        using var unauthorised = await anonymous.GetAsync("/api/vaults", Ct);
        using var notFound = await bearer.GetAsync("/api/nothing-here", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, unauthorised.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        foreach (var response in new[] { version, unauthorised, notFound })
        {
            Assert.Equal(BuildVersion.Full, response.Headers.GetValues("X-Kumbu-Version").Single());
        }
    }

    [Fact]
    public void Semver_drops_the_commit()
    {
        Assert.DoesNotContain('+', BuildVersion.Semver);
        Assert.StartsWith(BuildVersion.Semver, BuildVersion.Full);
    }
}
