// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kumbu.Core.Sh;
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Kumbu.Server.Auth;
using Kumbu.Server.Metrics;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Metrics;
using Kumbu.Server.Tests.Sharing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kumbu.Server.Tests.Api;

/// <summary>The web app's console: <c>POST /api/console</c> runs the shell's reading commands for a session, and refuses
/// every command that writes before anything of the line runs (console design §4, §5).</summary>
[Collection("app")]
public sealed class ConsoleApiTests(KumbuAppFactory app)
{
    private const string Note = "# Plan\nTomaten\n";

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<RestClient> WorkAsync(WebApplicationFactory<Program>? api = null)
    {
        var user = await RestClient.NewAsync(api ?? Api, vault: "Work");
        await user.CreateAsync("plan.md", Note);
        return user;
    }

    private static Task<HttpResponseMessage> ShAsync(RestClient user, string command) =>
        user.SendAsync(HttpMethod.Post, "/api/console", new { call = "sh", command });

    private static async Task<JsonNode> ConsoleAsync(RestClient user, string command)
    {
        using var response = await ShAsync(user, command);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await RestClient.JsonAsync(response);
    }

    private static string Text(JsonNode view) =>
        string.Concat(view["parts"]!.AsArray().Where(p => p!["type"]!.GetValue<string>() == "text").Select(p => p!["text"]!.GetValue<string>()));

    private static bool IsError(JsonNode view) => view["isError"]!.GetValue<bool>();

    [Fact]
    public async Task Runs_a_reading_command()
    {
        var user = await WorkAsync();

        var view = await ConsoleAsync(user, "cat /Work/plan.md");

        Assert.False(IsError(view));
        Assert.Contains("Tomaten", Text(view), StringComparison.Ordinal);
        Assert.Equal("/Work/plan.md", view["version"]!["path"]!.GetValue<string>());
        Assert.Equal((await user.ReadAsync("plan.md"))["version"]!.GetValue<string>(), view["version"]!["token"]!.GetValue<string>());
    }

    [Fact]
    public async Task Pipes_run()
    {
        var user = await WorkAsync();

        var view = await ConsoleAsync(user, "cat /Work/plan.md | rg -c Tomaten");

        Assert.False(IsError(view));
        Assert.Equal("1", Text(view).Trim());
    }

    [Fact]
    public async Task A_checkpoint_can_be_read()
    {
        var user = await WorkAsync();
        using var made = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/checkpoints", new { name = "cp" });
        Assert.True(made.IsSuccessStatusCode, made.StatusCode.ToString());

        var view = await ConsoleAsync(user, "cat --checkpoint=cp /Work/plan.md");

        Assert.False(IsError(view), Text(view));
        Assert.Contains("Tomaten", Text(view), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refused_commands_still_run_over_mcp()
    {
        var user = await WorkAsync();
        var key = await user.KeyAsync("maintain", KeyRight.Maintain);

        Assert.False((await user.ShAsync("mkdir /Work/new", key)).IsError);
        Assert.False((await user.ShAsync("mkcp /Work/plan.md cp1", key)).IsError);
        Assert.False((await user.ShAsync("rm /Work/plan.md", key)).IsError);

        Assert.Contains("cp1", (await user.ShAsync("lscp /Work", key)).Text, StringComparison.Ordinal);
        using var gone = await user.GetAsync(user.Url("plan.md"));
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        using var tree = await user.GetAsync($"/api/vaults/{user.Vault}/tree");
        Assert.Contains("/Work/new", (await RestClient.JsonAsync(tree)).ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_busy_vault_is_503()
    {
        var user = await WorkAsync();

        await using (await user.HoldRekeyAsync())
        {
            using var during = await ShAsync(user, "cat /Work/plan.md");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, during.StatusCode);
            Assert.True(during.Headers.RetryAfter is not null);
            Assert.Equal(Kumbu.Server.Vaults.VaultBusyException.BeingReencrypted, await RestClient.ErrorAsync(during));
        }
    }

    [Fact]
    public async Task Nothing_of_a_command_reaches_the_log()
    {
        var log = new CapturedLog();
        await using var logged = Api.WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(log)));
        const string command = "zzcommandmarker";
        const string output = "zzoutputmarker";
        const string failing = "zzerrormarker";
        var user = await RestClient.NewAsync(logged, vault: "Work");
        await user.CreateAsync("plan.md", $"# Plan\n{output}\n");

        var searched = await ConsoleAsync(user, $"rg {command} /Work");
        var read = await ConsoleAsync(user, "cat /Work/plan.md");
        var wrong = await ConsoleAsync(user, $"ls /Work | {failing}");

        Assert.False(IsError(searched));
        Assert.Contains(output, Text(read), StringComparison.Ordinal);
        Assert.True(IsError(wrong));
        Assert.NotEmpty(log.Lines);
        foreach (var marker in new[] { command, output, failing })
        {
            Assert.DoesNotContain(log.Lines, l => l.Contains(marker, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_body_is_limited()
    {
        var endpoint = Endpoints().Single(e => e.RoutePattern.RawText == "/api/console/");

        Assert.Equal(65536, endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize);
    }

    private IEnumerable<RouteEndpoint> Endpoints() =>
        Api.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();

    public static TheoryData<string, string> RefusedLines()
    {
        var data = new TheoryData<string, string>();
        foreach (var command in new[]
                 {
                     "restore /Work/plan.md", "mv /Work/plan.md /Work/b.md", "archive /Work/plan.md", "mkdir /Work/new", "rm /Work/plan.md", "mkcp /Work/plan.md cp1",
                     "rmcp /Work/plan.md cp1", "pdfimages --list /Work/plan.md",
                 })
        {
            var name = command.Split(' ')[0];
            data.Add(name, command);
            data.Add(name, $"cat /Work/plan.md | {command}");
            data.Add(name, $"{command} | head");
        }

        data.Add("rm", "\"rm\" /Work/plan.md");
        data.Add("rm", "r\\m /Work/plan.md");
        data.Add("rm", "'r'm /Work/plan.md");
        data.Add("rm", "rm --help");
        return data;
    }

    [Theory]
    [MemberData(nameof(RefusedLines))]
    public async Task Every_refused_command_stays_refused(string name, string line)
    {
        var user = await WorkAsync();
        var version = (await user.ReadAsync("plan.md"))["version"]!.GetValue<string>();

        var view = await ConsoleAsync(user, line);

        Assert.True(IsError(view));
        Assert.Equal($"{name}: not in this console, which only reads", Text(view));
        Assert.Equal(version, (await user.ReadAsync("plan.md"))["version"]!.GetValue<string>());
        using var trash = await user.GetAsync($"/api/vaults/{user.Vault}/trash");
        Assert.Empty((await RestClient.JsonAsync(trash))["notes"]!.AsArray());
        using var moved = await user.GetAsync(user.Url("b.md"));
        Assert.Equal(HttpStatusCode.NotFound, moved.StatusCode);
        using var tree = await user.GetAsync($"/api/vaults/{user.Vault}/tree");
        var folders = (await RestClient.JsonAsync(tree))["root"]!.ToJsonString();
        Assert.DoesNotContain("/Work/new", folders, StringComparison.Ordinal);
        using var checkpoints = await user.GetAsync($"/api/vaults/{user.Vault}/checkpoints");
        Assert.Empty((await RestClient.JsonAsync(checkpoints))["checkpoints"]!.AsArray());
    }

    [Fact]
    public async Task The_console_lists_publications()
    {
        var user = await WorkAsync();
        var key = await user.KeyAsync("Claude", KeyRight.Write, mayPublish: true);
        Assert.False((await user.ShAsync("publish add /Work/plan.md", key)).IsError);

        var view = await ConsoleAsync(user, "publish ls /Work");

        Assert.False(IsError(view));
        Assert.Matches(@"^/Work/plan\.md  \d{4}-\d\d-\d\d \d\d:\d\d UTC  older: no$", Text(view));
    }

    [Theory]
    [InlineData("add", "publish add /Work/a.md")]
    [InlineData("update", "publish update /Work/a.md")]
    [InlineData("rm", "publish rm /Work/a.md")]
    [InlineData("add", "ls | publish add /Work/a.md")]
    [InlineData("add", "publish add /Work/a.md | head")]
    public async Task The_console_refuses_publishing(string sub, string line)
    {
        var user = await WorkAsync();
        await user.CreateAsync("a.md", "# A\n");
        // Something to update or withdraw; for add, nothing is published before.
        if (sub != "add") Assert.False((await user.ShAsync("publish add /Work/a.md", await user.KeyAsync("Claude", KeyRight.Write, mayPublish: true))).IsError);
        var before = Text(await ConsoleAsync(user, "publish ls /Work"));

        var view = await ConsoleAsync(user, line);

        Assert.True(IsError(view));
        Assert.Equal($"publish {sub}: not in this console, which only reads", Text(view));
        Assert.Equal(before, Text(await ConsoleAsync(user, "publish ls /Work")));
        if (sub == "add") Assert.Equal("no published notes below /Work", before);
    }

    [Theory]
    [InlineData("publish")]
    [InlineData("publish --help")]
    public async Task The_console_refuses_publish_without_ls(string line)
    {
        var user = await WorkAsync();

        var view = await ConsoleAsync(user, line);

        Assert.True(IsError(view));
        Assert.EndsWith(": not in this console, which only reads", Text(view), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("skills copy create-skill")]
    [InlineData("skills copy create-skill /Work")]
    [InlineData("ls | skills copy create-skill")]
    public async Task The_console_refuses_copying_a_skill(string line)
    {
        var user = await WorkAsync();

        var view = await ConsoleAsync(user, line);

        Assert.True(IsError(view));
        Assert.Equal("skills copy: not in this console, which only reads", Text(view));
        Assert.DoesNotContain("create-skill (/", Text(await ConsoleAsync(user, "skills ls")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("calendar close /Work/plan.md", "calendar close")]
    [InlineData("calendar set /Work/plan.md --when 2026-11-20", "calendar set")]
    [InlineData("ls /Work | calendar unset /Work/plan.md", "calendar unset")]
    [InlineData("calendar -- close /Work/plan.md", "calendar close")]
    [InlineData("calendar --task set /Work/plan.md", "calendar set")]
    [InlineData("calendar --when=2026-12-01 set /Work/plan.md", "calendar set")]
    [InlineData("calendar --reason=x close /Work/plan.md", "calendar close")]
    public async Task The_console_reads_the_calendar_but_refuses_its_writes(string line, string refused)
    {
        var user = await WorkAsync();

        var view = await ConsoleAsync(user, line);

        Assert.True(IsError(view));
        Assert.Equal($"{refused}: not in this console, which only reads", Text(view));
        Assert.False(IsError(await ConsoleAsync(user, "calendar /Work")));
        Assert.False(IsError(await ConsoleAsync(user, "calendar notifications")));
    }

    [Theory]
    [InlineData("skills ls", "create-skill (system)")]
    [InlineData("skills cat create-skill", "name: create-skill")]
    [InlineData("skills find \"write a skill\"", "create-skill")]
    [InlineData("skills check", "")]
    public async Task The_console_reads_skills(string line, string expected)
    {
        var user = await WorkAsync();

        var view = await ConsoleAsync(user, line);

        Assert.DoesNotContain("not in this console", Text(view), StringComparison.Ordinal);
        Assert.Contains(expected, Text(view), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lines_the_shell_answers_itself()
    {
        var user = await WorkAsync();

        var grep = await ConsoleAsync(user, "grep x");
        var redirect = await ConsoleAsync(user, "ls / > x");
        var empty = await ConsoleAsync(user, "");

        Assert.Equal("command not found: grep — use rg (try 'help rg')", Text(grep));
        Assert.Contains("'>' is not supported", Text(redirect), StringComparison.Ordinal);
        Assert.Equal("sh: empty command", Text(empty));
        Assert.True(IsError(grep) && IsError(redirect) && IsError(empty));
    }

    [Fact]
    public async Task Every_shell_command_is_classified()
    {
        var user = await WorkAsync();

        var help = Text(await ConsoleAsync(user, "help"));

        var names = help.Split('\n').SkipWhile(l => l != "Commands:").Skip(1).TakeWhile(l => l.StartsWith("  ", StringComparison.Ordinal))
            .Select(l => l.Trim().Split(' ')[0]).ToList();
        Assert.NotEmpty(names);
        Assert.All(names.Concat(KumbuMetrics.KnownCommands), n => Assert.True(ConsoleApi.Allowed.Contains(n) ^ ConsoleApi.Refused.Contains(n), n));
        Assert.All(ConsoleApi.Allowed.Concat(ConsoleApi.Refused), n => Assert.Contains(n, KumbuMetrics.KnownCommands));
    }

    [Fact]
    public void The_lists_are_disjoint()
    {
        Assert.Empty(ConsoleApi.Allowed.Intersect(ConsoleApi.Refused));
        IReadOnlySet<string> allowed = ConsoleApi.Allowed;
        Assert.Contains("help", allowed);
        Assert.Contains("rg", allowed);
        Assert.DoesNotContain("rm", allowed);
    }

    [Fact]
    public async Task An_api_key_is_refused()
    {
        var user = await WorkAsync();
        var key = await user.KeyAsync("read", KeyRight.Read);

        using var response = await user.SendAsync(HttpMethod.Post, "/api/console", new { call = "sh", command = "ls /" }, bearer: key);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("This needs a signed-in session: API keys, connected apps and the dev token can't do it.", await RestClient.ErrorAsync(response));
    }

    [Fact]
    public async Task A_forged_origin_is_refused()
    {
        var user = await WorkAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/console")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { call = "sh", command = "cat /Work/plan.md" }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Cookie", $"{CallerMiddleware.SessionCookie}={user.Session}");
        request.Headers.Add("Origin", "https://evil.test");
        using var client = Api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") });

        using var forged = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        Assert.DoesNotContain("Tomaten", await forged.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_only_member_reads_a_shared_vault()
    {
        var alice = await WorkAsync();
        var bob = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.ShareWithAsync(bob, "Work", MemberRole.Read);

        var read = await ConsoleAsync(bob, "cat /Work/plan.md");
        var removed = await ConsoleAsync(bob, "rm /Work/plan.md");

        Assert.False(IsError(read), Text(read));
        Assert.Contains("Tomaten", Text(read), StringComparison.Ordinal);
        Assert.Equal("rm: not in this console, which only reads", Text(removed));
        Assert.Contains("Tomaten", (await alice.ReadAsync("plan.md"))["content"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_call_is_400()
    {
        var user = await WorkAsync();

        using var write = await user.SendAsync(HttpMethod.Post, "/api/console", new { call = "write" });
        using var board = await user.SendAsync(HttpMethod.Post, "/api/console", new { call = "board" });
        using var noCommand = await user.SendAsync(HttpMethod.Post, "/api/console", new { call = "sh" });

        Assert.Equal(
            (HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest),
            (write.StatusCode, board.StatusCode, noCommand.StatusCode));
    }

    [Theory]
    [InlineData("cat /Work/nope.md")]
    [InlineData("grep x")]
    [InlineData("ls | foo")]
    public async Task Errors_say_what_mcp_says(string line)
    {
        var user = await WorkAsync();
        var key = await user.KeyAsync("read", KeyRight.Read);

        var view = await ConsoleAsync(user, line);
        var (isError, text) = await user.ShAsync(line, key);

        Assert.True(isError);
        Assert.True(IsError(view));
        Assert.Equal(text, Text(view));
    }

    [Fact]
    public async Task Help_names_a_refused_command()
    {
        var user = await WorkAsync();
        var key = await user.KeyAsync("read", KeyRight.Read);

        var view = await ConsoleAsync(user, "help rm");

        Assert.False(IsError(view), Text(view));
        Assert.Equal((await user.ShAsync("help rm", key)).Text, Text(view));
        Assert.StartsWith("rm ", Text(view), StringComparison.Ordinal);
    }

    private static Task<HttpResponseMessage> CdAsync(RestClient user, string path) =>
        user.SendAsync(HttpMethod.Post, "/api/console", new { call = "cd", path });

    private static async Task<JsonNode> CdViewAsync(RestClient user, string path)
    {
        using var response = await CdAsync(user, path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await RestClient.JsonAsync(response);
    }

    /// <summary>A vault <c>Work</c> with a note, a folder of notes, a kept empty folder and a folder holding only a picture.</summary>
    private async Task<RestClient> FoldersAsync()
    {
        var user = await WorkAsync();
        await user.CreateAsync("projects/garden.md", "# Garden\n");
        using (var kept = await user.PutFileAsync("Drafts/.vaultkeep", []))
        {
            Assert.Equal(HttpStatusCode.Created, kept.StatusCode);
        }

        await user.CreateFileAsync("Pictures/shot.png", FileApiTests.Png(4, 4));
        return user;
    }

    [Theory]
    [InlineData("/", "/")]
    [InlineData("/Work", "/Work")]
    [InlineData("/Work/projects", "/Work/projects")]
    [InlineData("/Work/Drafts", "/Work/Drafts")]
    [InlineData("/Work/Pictures", "/Work/Pictures")]
    [InlineData("/Work/projects/../Drafts/", "/Work/Drafts")]
    public async Task Cd_takes_folders(string path, string directory)
    {
        var user = await FoldersAsync();

        var view = await CdViewAsync(user, path);

        Assert.False(IsError(view), Text(view));
        Assert.Equal(directory, Text(view));
        Assert.Single(view["parts"]!.AsArray());
    }

    [Theory]
    [InlineData("/Work/plan.md")]
    [InlineData("/Work/Pictures/shot.png")]
    [InlineData("/Work/nope")]
    [InlineData("/Nope")]
    [InlineData("Work")]
    [InlineData("/Work/Drafts/.vaultkeep")]
    public async Task Cd_refuses_the_rest(string path)
    {
        var user = await FoldersAsync();

        var view = await CdViewAsync(user, path);

        Assert.True(IsError(view));
        Assert.Equal($"cd: {path}: not a folder", Text(view));
    }

    [Fact]
    public async Task A_note_or_file_with_notes_below_its_path_is_not_a_folder()
    {
        // Writing refuses a path below a note or a file, but restoring one from the trash doesn't look below it.
        var user = await FoldersAsync();
        var key = await user.KeyAsync("maintain", KeyRight.Maintain);
        foreach (var path in new[] { "/Work/plan.md", "/Work/Pictures/shot.png" })
        {
            Assert.False((await user.ShAsync($"rm {path}", key)).IsError);
            await user.CreateAsync($"{path["/Work/".Length..]}/below.md", "# Below\n");
            var restored = await user.ShAsync($"restore {path}", key);
            Assert.False(restored.IsError, restored.Text);
        }

        foreach (var path in new[] { "/Work/plan.md", "/Work/Pictures/shot.png" })
        {
            var view = await CdViewAsync(user, path);

            Assert.True(IsError(view), path);
            Assert.Equal($"cd: {path}: not a folder", Text(view));
        }
    }

    [Fact]
    public async Task Cd_without_a_path_is_400()
    {
        var user = await WorkAsync();

        using var response = await user.SendAsync(HttpMethod.Post, "/api/console", new { call = "cd" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Cd_on_a_busy_vault_is_503()
    {
        var user = await FoldersAsync();

        await using (await user.HoldRekeyAsync())
        {
            using var during = await CdAsync(user, "/Work/projects");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, during.StatusCode);
            Assert.True(during.Headers.RetryAfter is not null);
            Assert.Equal(Kumbu.Server.Vaults.VaultBusyException.BeingReencrypted, await RestClient.ErrorAsync(during));
        }
    }

    [Fact]
    public async Task Cd_sees_a_member_s_name()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.CreateAsync("Saat/tomaten.md", "# Tomaten\n");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var membership = await alice.ShareWithAsync(bob, "garten");
        using (var renamed = await bob.SendAsync(HttpMethod.Put, $"/api/memberships/{membership}/alias", new { alias = "Beete" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        }

        var vault = await CdViewAsync(bob, "/Beete");
        var folder = await CdViewAsync(bob, "/Beete/Saat");
        var hers = await CdViewAsync(bob, "/Gartenarbeit");
        var former = await CdViewAsync(bob, "/garten/Saat");

        Assert.Equal(("/Beete", "/Beete/Saat"), (Text(vault), Text(folder)));
        Assert.False(IsError(vault) || IsError(folder));
        Assert.Equal(("cd: /Gartenarbeit: not a folder", "cd: /garten/Saat: not a folder"), (Text(hers), Text(former)));
        Assert.True(IsError(hers) && IsError(former));
    }

    [Fact]
    public async Task A_vault_of_another_user_is_not_a_folder()
    {
        var alice = await RestClient.NewAsync(Api, vault: "AlicesVault");
        await alice.CreateAsync("Saat/tomaten.md", "# Tomaten\n");
        var bob = await RestClient.NewAsync(Api, vault: "Work");

        foreach (var (path, missing) in new[] { ("/AlicesVault", "/Nope"), ("/AlicesVault/Saat", "/Nope/Saat") })
        {
            var theirs = await CdViewAsync(bob, path);
            var none = await CdViewAsync(bob, missing);

            Assert.True(IsError(theirs) && IsError(none));
            Assert.Equal(Text(none).Replace(missing, path, StringComparison.Ordinal), Text(theirs));
        }

        Assert.Equal("/AlicesVault/Saat", Text(await CdViewAsync(alice, "/AlicesVault/Saat")));
    }

    [Fact]
    public async Task History_takes_a_permit()
    {
        var user = await WorkAsync();

        // Refused before anything runs, so it takes no permit.
        Assert.Equal("rm: not in this console, which only reads", Text(await ConsoleAsync(user, "history /Work | rm /Work/plan.md")));
        for (var i = 0; i < AccountLimits.HistoryScansPerUser - 2; i++)
        {
            Assert.False(IsError(await ConsoleAsync(user, "history /Work")));
        }

        // The shell refuses it (history reads no pipe), but the permit is taken first.
        var piped = await ConsoleAsync(user, "cat /Work/plan.md | history /Work");
        Assert.True(IsError(piped));
        Assert.Contains("does not read from a pipe", Text(piped), StringComparison.Ordinal);
        Assert.False(IsError(await ConsoleAsync(user, "history /Work")));

        Assert.False(IsError(await ConsoleAsync(user, "cat /Work/plan.md")));
        Assert.False(IsError(await ConsoleAsync(user, "cat /Work/plan.md | head")));
        using var limited = await ShAsync(user, "history /Work");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        Assert.Equal(AccountLimits.HistoryScansRefused, await RestClient.ErrorAsync(limited));
        using var laterStage = await ShAsync(user, "cat /Work/plan.md | history /Work");
        Assert.Equal(HttpStatusCode.TooManyRequests, laterStage.StatusCode);
        Assert.Equal("rm: not in this console, which only reads", Text(await ConsoleAsync(user, "history /Work | rm /Work/plan.md")));
    }

    [Fact]
    public void Images_become_image_parts()
    {
        byte[] png = [0x89, 0x50, 0x4e, 0x47];
        var result = ShellResult.Ok("a picture") with { Parts = [new TextPart("a picture"), new ImagePart(png, "image/png")] };

        var view = ConsoleApi.View(result);

        Assert.Equal(2, view.Parts.Length);
        Assert.Equal(new ConsolePartView("text", "a picture", null, null), view.Parts[0]);
        Assert.Equal(new ConsolePartView("image", null, "image/png", Convert.ToBase64String(png)), view.Parts[1]);
        Assert.False(view.IsError);
        Assert.Null(view.Version);
    }

    [Fact]
    public async Task Tool_metrics_do_not_count_the_console()
    {
        const string calls = "kumbu_mcp_tool_calls_total{tool=\"sh\"}";
        var user = await WorkAsync();
        var before = await Scrape.TextAsync(app);

        Assert.False(IsError(await ConsoleAsync(user, "cat /Work/plan.md")));

        var after = await Scrape.TextAsync(app);
        Assert.Equal(Scrape.Value(before, calls), Scrape.Value(after, calls));
    }
}
