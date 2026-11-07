// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;
using Kumbu.Core.Notes;
using Kumbu.Core.Sh;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Kumbu.Server.Publishing;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Sharing;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Kumbu.Server.Tests.Publishing;

/// <summary><c>sh publish</c> over MCP (publish spec §3.9): only the owner's credential with "May publish" adds,
/// updates and withdraws; the link is shown once, by <c>add</c>; <c>ls</c> never shows a code; a member sees paths.</summary>
[Collection("app")]
public sealed partial class PublishShellTests(KumbuAppFactory app)
{
    private const string Questions = "/Work/Routing/Questions.md";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private PublicationStore Store => Api.Services.GetRequiredService<PublicationStore>();

    [GeneratedRegex(@"^link \(shown once\): (https?://[^/]+/s#(?<code>([0-9A-HJKMNP-TV-Z]{4}-){7}[0-9A-HJKMNP-TV-Z]{4}))$", RegexOptions.Multiline)]
    private static partial Regex LinkLine();

    [GeneratedRegex("[0-9A-HJKMNP-TV-Z]{32}|([0-9A-HJKMNP-TV-Z]{4}-){7}[0-9A-HJKMNP-TV-Z]{4}")]
    private static partial Regex AnyCode();

    private static async Task<RestClient> AliceAsync(WebApplicationFactory<Program> api)
    {
        var alice = await RestClient.NewAsync(api, vault: "Work");
        await alice.CreateAsync("Routing/Questions.md", "# Questions on routing\n\nShould the chatbot move the user between screens?\n");
        return alice;
    }

    /// <summary>The normalised code of the link <c>publish add</c> printed.</summary>
    private static string CodeOf(string added) => ShareCode.Parse(LinkLine().Match(added).Groups["code"].Value)!;

    /// <summary>One <c>sh</c> call over MCP on <paramref name="api"/>, which may be a factory with other settings.</summary>
    private static async Task<(bool IsError, string Text)> ShAsync(WebApplicationFactory<Program> api, string command, string bearer)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(api.Server.BaseAddress, "mcp"),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {bearer}" },
            },
            api.CreateClient(),
            NullLoggerFactory.Instance,
            ownsHttpClient: false);
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: Ct);
        var result = await client.CallToolAsync("sh", new Dictionary<string, object?> { ["command"] = command }, cancellationToken: Ct);
        return (result.IsError ?? false, Assert.IsType<TextContentBlock>(result.Content[0]).Text);
    }

    /// <summary>The caller of an API key, as CallerMiddleware makes it, and the scope's <see cref="Shell"/> run as it: for
    /// what MCP doesn't carry, a refusal's code and the vault the request used.</summary>
    private static async Task<ShellResult> ScopedShAsync(WebApplicationFactory<Program> api, string command, string apiKey)
    {
        var resolved = await api.Services.GetRequiredService<ApiKeys>().ResolveAsync(apiKey, Ct);
        return await ScopedShAsync(api, command, await RequestCaller.ForApiKeyAsync(resolved!, apiKey, api.Services.GetRequiredService<VaultCatalog>(), Ct));
    }

    private static async Task<ShellResult> ScopedShAsync(WebApplicationFactory<Program> api, string command, RequestCaller caller)
    {
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
    public async Task A_key_that_may_publish_adds_lists_updates_and_withdraws()
    {
        var alice = await AliceAsync(Api);
        var key = await alice.KeyAsync("Claude", KeyRight.Write, mayPublish: true);

        var added = await alice.ShAsync($"publish add {Questions}", key);

        Assert.False(added.IsError, added.Text);
        Assert.Matches($@"^published {Regex.Escape(Questions)} as a copy of \d{{4}}-\d\d-\d\d \d\d:\d\d UTC\nlink \(shown once\): ", added.Text);
        Assert.StartsWith(KumbuAppFactory.PublicUrl + "/s#", LinkLine().Match(added.Text).Groups[1].Value, StringComparison.Ordinal);
        var code = CodeOf(added.Text);
        var opened = await Store.OpenCopyAsync(code, Ct);
        Assert.NotNull(opened);
        Assert.Equal(alice.User.Id, opened.Value.Owner);

        var listed = await alice.ShAsync("publish ls /Work", key);
        Assert.False(listed.IsError, listed.Text);
        Assert.Matches($@"^{Regex.Escape(Questions)}  \d{{4}}-\d\d-\d\d \d\d:\d\d UTC  older: no$", listed.Text);

        var version = (await alice.ReadAsync("Routing/Questions.md"))["version"]!.GetValue<string>();
        using (var edited = await alice.SendAsync(HttpMethod.Put, alice.Url("Routing/Questions.md"), new { content = "# Questions on routing\n\nAnswered.\n", version }))
        {
            Assert.True(edited.IsSuccessStatusCode, await edited.Content.ReadAsStringAsync(Ct));
        }

        Assert.EndsWith("  older: yes", (await alice.ShAsync("publish ls /Work/Routing", key)).Text, StringComparison.Ordinal);

        var updated = await alice.ShAsync($"publish update {Questions}", key);
        Assert.False(updated.IsError, updated.Text);
        Assert.Matches($@"^updated the published copy of {Regex.Escape(Questions)} \(\d{{4}}-\d\d-\d\d \d\d:\d\d UTC\); the link stays the same$", updated.Text);
        Assert.Contains("Answered.", (await Store.OpenCopyAsync(code, Ct))!.Value.Copy.Markdown, StringComparison.Ordinal);
        Assert.EndsWith("  older: no", (await alice.ShAsync("publish ls", key)).Text, StringComparison.Ordinal);

        var withdrawn = await alice.ShAsync($"publish rm {Questions}", key);
        Assert.Equal((false, $"withdrew the publication of {Questions}; its link reads nothing now"), withdrawn);
        Assert.Null(await Store.OpenCopyAsync(code, Ct));
        Assert.Equal((false, "no published notes below /Work"), await alice.ShAsync("publish ls /Work", key));

        foreach (var output in new[] { listed.Text, updated.Text, withdrawn.Text })
        {
            Assert.DoesNotContain("/s#", output, StringComparison.Ordinal);
            Assert.DoesNotMatch(AnyCode(), output);
        }
    }

    [Fact]
    public async Task A_key_without_the_right_is_refused()
    {
        var alice = await AliceAsync(Api);
        var key = await alice.KeyAsync("Claude", KeyRight.Write);

        var refused = await alice.ShAsync($"publish add {Questions}", key);

        Assert.True(refused.IsError);
        Assert.StartsWith("publish add: this key may not publish in Work", refused.Text, StringComparison.Ordinal);
        Assert.Contains("may not publish", refused.Text, StringComparison.Ordinal);
        Assert.Equal((false, "no published notes below /Work"), await alice.ShAsync("publish ls /Work", key));
    }

    [Fact]
    public async Task A_member_is_refused_and_lists_paths_alone()
    {
        var alice = await AliceAsync(Api);
        var bob = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.ShareWithAsync(bob, "alices-work", MemberRole.Write);
        var aliceKey = await alice.KeyAsync("Claude", KeyRight.Write, mayPublish: true);
        Assert.False((await alice.ShAsync($"publish add {Questions}", aliceKey)).IsError);
        var (bobKey, _) = await Api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            bob.User, bob.UserKey, DateTime.UtcNow, "Claude", new Dictionary<string, KeyRight> { [await alice.VaultIdAsync()] = KeyRight.Write }, Ct);

        var refused = await bob.ShAsync("publish add /alices-work/Routing/Questions.md", bobKey);
        var withdraw = await bob.ShAsync("publish rm /alices-work/Routing/Questions.md", bobKey);

        Assert.Equal((true, "publish add: only the owner of alices-work can publish its notes"), refused);
        Assert.Equal((true, "publish rm: only the owner of alices-work can publish its notes"), withdraw);
        Assert.Equal((false, "/alices-work/Routing/Questions.md"), await bob.ShAsync("publish ls /alices-work", bobKey));
        Assert.Equal((false, "/alices-work/Routing/Questions.md"), await bob.ShAsync("publish ls", bobKey));
        Assert.Matches($@"^{Regex.Escape(Questions)}  .* UTC  older: no$", (await alice.ShAsync("publish ls /Work", aliceKey)).Text);
    }

    [Fact]
    public async Task Ls_output_holds_no_code()
    {
        var alice = await AliceAsync(Api);
        await alice.CreateAsync("Ideas.md", "# Ideas\n");
        var key = await alice.KeyAsync("Claude", KeyRight.Write, mayPublish: true);
        var codes = new List<string>();
        foreach (var note in new[] { Questions, "/Work/Ideas.md" })
        {
            var added = await alice.ShAsync($"publish add {note}", key);
            Assert.False(added.IsError, added.Text);
            codes.Add(CodeOf(added.Text));
        }

        foreach (var line in new[] { "publish ls", "publish ls /", "publish ls /Work", "publish ls /Work/Routing", $"publish ls {Questions}", "publish ls /Work | rg Work" })
        {
            var (isError, text) = await alice.ShAsync(line, key);

            Assert.False(isError, text);
            Assert.Contains("/Work/", text, StringComparison.Ordinal);
            Assert.DoesNotContain("/s#", text, StringComparison.Ordinal);
            Assert.DoesNotMatch(AnyCode(), text);
            Assert.All(codes, code => Assert.DoesNotContain(code, text, StringComparison.Ordinal));
            Assert.All(codes, code => Assert.DoesNotContain(ShareCode.Format(code), text, StringComparison.Ordinal));
        }

        Assert.Equal($"/Work/Ideas.md\n{Questions}", string.Join('\n', (await alice.ShAsync("publish ls /Work", key)).Text.Split('\n').Select(l => l.Split("  ")[0])));
    }

    [Fact]
    public async Task Switched_off_it_still_withdraws()
    {
        var alice = await AliceAsync(Api);
        await alice.CreateAsync("Ideas.md", "# Ideas\n");
        var key = await alice.KeyAsync("Claude", KeyRight.Write, mayPublish: true);
        var code = CodeOf((await alice.ShAsync($"publish add {Questions}", key)).Text);
        await using var off = Api.WithWebHostBuilder(b => b.UseSetting("Kumbu:Publish:Enabled", "false"));

        var add = await ShAsync(off, "publish add /Work/Ideas.md", key);
        var update = await ShAsync(off, $"publish update {Questions}", key);

        Assert.Equal((true, "publish add: publishing is switched off on this server"), add);
        Assert.Equal((true, "publish update: publishing is switched off on this server"), update);
        Assert.Matches($@"^{Regex.Escape(Questions)}  .* UTC  older: no$", (await ShAsync(off, "publish ls /Work", key)).Text);
        Assert.Equal((false, $"withdrew the publication of {Questions}; its link reads nothing now"), await ShAsync(off, $"publish rm {Questions}", key));
        Assert.Null(await Store.OpenCopyAsync(code, Ct));
        Assert.Equal((false, "no published notes below /Work"), await ShAsync(off, "publish ls /Work", key));
    }

    [Fact]
    public async Task Refusals_carry_their_codes()
    {
        var alice = await AliceAsync(Api);
        var bob = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.ShareWithAsync(bob, "alices-work", MemberRole.Write);
        var without = await alice.KeyAsync("Claude", KeyRight.Write);
        var with = await alice.KeyAsync("Publisher", KeyRight.Write, mayPublish: true);
        var (bobKey, _) = await Api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            bob.User, bob.UserKey, DateTime.UtcNow, "Claude", new Dictionary<string, KeyRight> { [await alice.VaultIdAsync()] = KeyRight.Write }, Ct);
        await using var off = Api.WithWebHostBuilder(b => b.UseSetting("Kumbu:Publish:Enabled", "false"));

        var noRight = await ScopedShAsync(Api, $"publish add {Questions}", without);
        var member = await ScopedShAsync(Api, "publish rm /alices-work/Routing/Questions.md", bobKey);
        var switchedOff = await ScopedShAsync(off, $"publish update {Questions}", with);

        Assert.Equal((true, ShellErrorKind.Refused, Refusals.NoPublishRight), (noRight.IsError, noRight.Kind, noRight.Code));
        Assert.Equal((true, ShellErrorKind.Refused, Refusals.NotOwner), (member.IsError, member.Kind, member.Code));
        Assert.Equal((true, ShellErrorKind.Refused, Refusals.PublishingOff), (switchedOff.IsError, switchedOff.Kind, switchedOff.Code));
        Assert.StartsWith("publish update: publishing is switched off", switchedOff.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_members_ls_counts_as_using_the_vault()
    {
        var alice = await AliceAsync(Api);
        var bob = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.ShareWithAsync(bob, "alices-work", MemberRole.Write);
        var (bobKey, _) = await Api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            bob.User, bob.UserKey, DateTime.UtcNow, "Claude", new Dictionary<string, KeyRight> { [await alice.VaultIdAsync()] = KeyRight.Write }, Ct);

        foreach (var line in new[] { "publish ls /alices-work", "publish ls" })
        {
            var opened = (await Api.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(alice.User.Id, alice.UserKey, Ct)).Single(v => v.Name == "Work").Vault;
            var before = opened.LastUsed;
            await Task.Delay(20, Ct);

            var result = await ScopedShAsync(Api, line, bobKey);

            Assert.False(result.IsError, result.Output);
            Assert.True(opened.LastUsed > before, line);
        }
    }

    [Fact]
    public async Task A_vault_being_reencrypted_is_left_out_at_the_root_and_busy_by_name()
    {
        var alice = await AliceAsync(Api);
        var key = await alice.KeyAsync("Claude", KeyRight.Write, mayPublish: true);
        Assert.False((await alice.ShAsync($"publish add {Questions}", key)).IsError);
        var bob = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.ShareWithAsync(bob, "alices-work", MemberRole.Write);

        // Sessions: a key whose only vault is being re-keyed opens nothing.
        RequestCaller Session(RestClient who) => RequestCaller.ForSession(who.User, who.UserKey, DateTime.UtcNow, windowOpen: true, Api.Services.GetRequiredService<VaultCatalog>());
        RequestCaller Alice() => Session(alice);

        await using (await alice.HoldRekeyAsync())
        {
            var root = await ScopedShAsync(Api, "publish ls", Alice());
            Assert.Equal((false, "no published notes below /"), (root.IsError, root.Output));
            foreach (var line in new[] { "publish ls /Work", $"publish add {Questions}", $"publish update {Questions}", $"publish rm {Questions}" })
            {
                var busy = await Assert.ThrowsAsync<VaultBusyException>(() => ScopedShAsync(Api, line, Alice()));
                Assert.Equal(VaultBusyException.BeingReencrypted, busy.Message);
            }

            // Busy before anything else is asked: a member hears that, not that she isn't the owner.
            var member = await Assert.ThrowsAsync<VaultBusyException>(() => ScopedShAsync(Api, "publish rm /alices-work/Routing/Questions.md", Session(bob)));
            Assert.Equal(VaultBusyException.BeingReencrypted, member.Message);
        }

        Assert.Matches($@"^{Regex.Escape(Questions)}  .* UTC  older: no$", (await ScopedShAsync(Api, "publish ls", Alice())).Output);
    }

    [Fact]
    public async Task Embeds_that_became_text_and_the_pdf_sentence_are_one_line()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Work");
        await alice.CreateFileAsync("Routing/report.pdf", System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF\n"));
        await alice.CreateAsync("Routing/Questions.md", "![[gone.png]]\n![[report.pdf]]\n");
        var key = await alice.KeyAsync("Claude", KeyRight.Write, mayPublish: true);

        var added = await alice.ShAsync($"publish add {Questions}", key);

        Assert.False(added.IsError, added.Text);
        Assert.EndsWith("\nnot copied, plain text instead: gone.png, report.pdf; PDFs are not published yet", added.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trace_logging_keeps_the_link_out_of_the_log()
    {
        var alice = await AliceAsync(Api);
        var key = await alice.KeyAsync("Claude", KeyRight.Write, mayPublish: true);
        var log = new CapturedLog();
        await using var logged = Api.WithWebHostBuilder(b => b
            .UseSetting("Logging:LogLevel:Default", "Trace")
            .ConfigureLogging(l => l.AddProvider(log)));

        var added = await ShAsync(logged, $"publish add {Questions}", key);

        Assert.False(added.IsError, added.Text);
        var code = CodeOf(added.Text);
        // The setting took: the log has lines below Information.
        Assert.Contains(log.Lines, l => l.StartsWith("Trace ", StringComparison.Ordinal) || l.StartsWith("Debug ", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Lines, l => l.Contains(code, StringComparison.Ordinal) || l.Contains(ShareCode.Format(code), StringComparison.Ordinal));
    }
}
