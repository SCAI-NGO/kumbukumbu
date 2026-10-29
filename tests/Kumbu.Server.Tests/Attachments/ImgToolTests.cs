// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Server.Accounts;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Metrics;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using static Kumbu.Server.Tests.Attachments.PdfJobTests;

namespace Kumbu.Server.Tests.Attachments;

/// <summary><c>img</c> over MCP, end to end: an uploaded image, the shell, the fake sidecar, the content blocks.</summary>
[Collection("app")]
public sealed class ImgToolTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<CallToolResult> ShAsync(WebApplicationFactory<Program> host, string key, string command)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(host.Server.BaseAddress, "mcp"),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {key}" },
            },
            host.CreateClient(),
            NullLoggerFactory.Instance,
            ownsHttpClient: false);
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: Ct);
        return await client.CallToolAsync("sh", new Dictionary<string, object?> { ["command"] = command }, cancellationToken: Ct);
    }

    private static async Task<(WebApplicationFactory<Program> Host, RestClient User, string Key, string Version)> SetUpAsync(KumbuAppFactory app, FakePdfSidecar sidecar)
    {
        var host = Host(app, sidecar);
        var user = await RestClient.NewAsync(host);
        var version = await user.CreateFileAsync("Samples/shot.png", FakePdfSidecar.SolidPng(1440, 3120));
        return (host, user, await user.KeyAsync("img", KeyRight.Read), version);
    }

    [Fact]
    public async Task An_image_comes_back_as_text_and_an_image_block()
    {
        var sidecar = new FakePdfSidecar();
        var (host, user, key, version) = await SetUpAsync(app, sidecar);
        await using var _ = host;

        var result = await ShAsync(host, key, $"img /{user.Vault}/Samples/shot.png");

        Assert.False(result.IsError, string.Join(' ', result.Content.OfType<TextContentBlock>().Select(t => t.Text)));
        Assert.Equal(2, result.Content.Count);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        Assert.StartsWith($"/{user.Vault}/Samples/shot.png (version {version}): image (PNG), original 1440×3120 px; medium: 723×1568 px PNG, ", text);
        Assert.EndsWith("If no image follows, your client dropped it: tell the user so instead of guessing what it shows.", text);
        var image = Assert.IsType<ImageContentBlock>(result.Content[1]);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(FakePdfSidecar.SolidPng(723, 1568), image.DecodedData.ToArray());
        Assert.Equal((SizeSteps.Of(SizeStep.Medium), (Crop?)null), Assert.Single(sidecar.ImageRequests));
    }

    [Fact]
    public async Task A_crop_and_a_step_reach_the_sidecar_with_the_same_numbers()
    {
        var sidecar = new FakePdfSidecar();
        var (host, user, key, _) = await SetUpAsync(app, sidecar);
        await using var _ = host;

        var result = await ShAsync(host, key, $"img /{user.Vault}/Samples/shot.png --crop 0,1200,1440,800 --size large");

        Assert.False(result.IsError);
        Assert.Equal((SizeSteps.Of(SizeStep.Large), (Crop?)new Crop(0, 1200, 1440, 800)), Assert.Single(sidecar.ImageRequests));
        Assert.Contains("; crop 0,1200,1440,800; large: 1440×800 px PNG, ", Assert.IsType<TextContentBlock>(result.Content[0]).Text);
    }

    [Fact]
    public async Task A_crop_outside_the_image_never_reaches_the_sidecar()
    {
        var sidecar = new FakePdfSidecar();
        var (host, user, key, _) = await SetUpAsync(app, sidecar);
        await using var _ = host;

        var result = await ShAsync(host, key, $"img /{user.Vault}/Samples/shot.png --crop 10,10,5000,5");

        Assert.True(result.IsError);
        Assert.Equal("img: the crop 10,10,5000,5 lies outside the image (1440×3120 px)", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Empty(sidecar.ImageRequests);
    }

    [Fact]
    public async Task With_the_sidecar_down_it_is_a_tool_error_that_says_unavailable()
    {
        var sidecar = new FakePdfSidecar { Down = true };
        var (host, user, key, _) = await SetUpAsync(app, sidecar);
        await using var _ = host;
        var before = Scrape.Value(await Scrape.TextAsync(app), ErrorsOf("unavailable"));

        var result = await ShAsync(host, key, $"img /{user.Vault}/Samples/shot.png");

        Assert.True(result.IsError);
        Assert.Contains("unavailable", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.True(Scrape.Value(await Scrape.TextAsync(app), ErrorsOf("unavailable")) >= before + 1);
    }

    [Fact]
    public async Task Img_in_a_pipeline_is_refused_as_invalid()
    {
        var before = Scrape.Value(await Scrape.TextAsync(app), ErrorsOf("invalid"));
        await using var client = await app.ConnectAsync();

        var result = await client.CallToolAsync("sh", new Dictionary<string, object?> { ["command"] = "img /Ideas/x.png | head" }, cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.True(Scrape.Value(await Scrape.TextAsync(app), ErrorsOf("invalid")) >= before + 1);
    }

    private static string ErrorsOf(string kind) => $"kumbu_mcp_tool_errors_total{{tool=\"sh\",kind=\"{kind}\"}}";

    [Fact]
    public async Task A_note_is_refused_and_help_img_is_there()
    {
        var (host, user, key, _) = await SetUpAsync(app, new FakePdfSidecar());
        await using var _ = host;
        await user.CreateAsync("Samples/n.md", "text\n");

        var note = await ShAsync(host, key, $"img /{user.Vault}/Samples/n.md");
        var help = await ShAsync(host, key, "help img");

        Assert.True(note.IsError);
        Assert.Equal($"img: /{user.Vault}/Samples/n.md is a note; cat reads notes", Assert.IsType<TextContentBlock>(note.Content[0]).Text);
        Assert.Contains("--crop x,y,w,h", Assert.IsType<TextContentBlock>(help.Content[0]).Text);
    }
}
