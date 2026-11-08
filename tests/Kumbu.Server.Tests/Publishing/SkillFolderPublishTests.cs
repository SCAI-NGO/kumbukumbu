// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Kumbu.Core.Notes;
using Kumbu.Core.Publishing;
using Kumbu.Core.Sh;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Kumbu.Server.Publishing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Kumbu.Server.Tests.Publishing;

/// <summary>A skill's place isn't published as notes: a skill is shared as a whole, by link, from the Skills screen. A
/// published note moved into one is withdrawn.</summary>
[Collection("app")]
public sealed class SkillFolderPublishTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<RestClient> AliceAsync()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        await alice.CreateAsync("_skills/x/SKILL.md", "---\nname: x\ndescription: Does x.\n---\n\nSteps.\n");
        await alice.CreateAsync("plan.md", "# Plan\n\nThe plan.\n");
        return alice;
    }

    /// <summary>One <c>sh</c> call as the key's caller, for the refusal's code, which MCP doesn't carry.</summary>
    private async Task<ShellResult> ScopedShAsync(string command, string apiKey)
    {
        var api = app.PublicApi;
        var resolved = await api.Services.GetRequiredService<ApiKeys>().ResolveAsync(apiKey, Ct);
        var caller = await RequestCaller.ForApiKeyAsync(resolved!, apiKey, api.Services.GetRequiredService<VaultCatalog>(), Ct);
        await using var scope = api.Services.CreateAsyncScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("kumbu.test");
        http.Features.Set(caller);
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = http;
        try
        {
            return await scope.ServiceProvider.GetRequiredService<Shell>().RunAsync(command, Ct);
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    [Fact]
    public async Task A_note_in__skills_isnt_published_by_sh()
    {
        var alice = await AliceAsync();
        var key = await alice.KeyAsync("Claude", KeyRight.Write, mayPublish: true);
        var result = await ScopedShAsync("publish add /Work/_skills/x/SKILL.md", key);
        Assert.True(result.IsError, result.Output);
        Assert.Equal(Refusals.Unpublishable, result.Code);
        Assert.Contains("is in a skill's place; a skill is shared as a whole, by link, from the Skills screen", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_note_in__skills_isnt_published_by_the_web_app()
    {
        var alice = await AliceAsync();
        using var published = await alice.SendAsync(HttpMethod.Post, "/api/publications/Work/_skills/x/SKILL.md", new { });
        Assert.Equal(HttpStatusCode.Forbidden, published.StatusCode);
    }

    [Fact]
    public async Task A_note_beside__skills_still_is()
    {
        var alice = await AliceAsync();
        using var published = await alice.SendAsync(HttpMethod.Post, "/api/publications/Work/plan.md", new { });
        Assert.Equal(HttpStatusCode.Created, published.StatusCode);
    }

    [Fact]
    public async Task A_published_note_moved_into__skills_is_withdrawn()
    {
        var alice = await AliceAsync();
        using var published = await alice.SendAsync(HttpMethod.Post, "/api/publications/Work/plan.md", new { });
        Assert.Equal(HttpStatusCode.Created, published.StatusCode);
        var code = ShareCode.Parse((await RestClient.JsonAsync(published))["link"]!.GetValue<string>().Split('#')[1])!;
        var store = app.PublicApi.Services.GetRequiredService<PublicationStore>();
        Assert.NotNull(await store.OpenCopyAsync(code, Ct));

        var key = await alice.KeyAsync("Claude", KeyRight.Write, mayPublish: true);
        var moved = await alice.ShAsync("mv /Work/plan.md /Work/_skills/x/plan.md", key);
        Assert.False(moved.IsError, moved.Text);

        Assert.Null(await store.OpenCopyAsync(code, Ct));
        var vault = (await app.PublicApi.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(alice.User.Id, alice.UserKey, Ct)).Single(v => v.Name == "Work").Vault;
        var record = (await store.ReadAsync(null, vault, Ct)).Record;
        Assert.Equal((PublishingKinds.Withdrawn, WithdrawalReasons.Removed), (record.Log[^1].Kind, record.Log[^1].Reason));
    }
}
