// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Kumbu.Core.Sh;
using Kumbu.Server.Mcp;
using ModelContextProtocol.Protocol;

namespace Kumbu.Server.Tests.Mcp;

public class ImageContentTests
{
    [Fact]
    public void Parts_become_one_block_each_in_order_and_the_version_follows()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0, 255];
        ShellPart[] parts = [new TextPart("a"), new ImagePart(png, "image/png"), new TextPart("b")];
        var result = ShellResult.Ok("a\nb") with { Parts = parts, Version = new NoteVersion("/V/x.png", "tok") };

        var call = ToolResult.From(result);

        Assert.Equal(4, call.Content.Count);
        Assert.Equal("a", Assert.IsType<TextContentBlock>(call.Content[0]).Text);
        var image = Assert.IsType<ImageContentBlock>(call.Content[1]);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(png, image.DecodedData.ToArray());
        Assert.Equal("b", Assert.IsType<TextContentBlock>(call.Content[2]).Text);
        Assert.Contains("tok", Assert.IsType<TextContentBlock>(call.Content[3]).Text);
    }

    [Fact]
    public void The_image_travels_as_base64_text_on_the_wire()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0, 255];
        var call = ToolResult.From(ShellResult.Ok("x") with { Parts = [new ImagePart(png, "image/png")] });

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(call, ModelContextProtocol.McpJsonUtilities.DefaultOptions));

        var block = json.RootElement.GetProperty("content")[0];
        Assert.Equal("image", block.GetProperty("type").GetString());
        Assert.Equal(Convert.ToBase64String(png), block.GetProperty("data").GetString());
        Assert.Equal("image/png", block.GetProperty("mimeType").GetString());
    }

    [Fact]
    public void A_result_without_parts_is_one_text_block_as_before()
    {
        var call = ToolResult.From(ShellResult.Ok("plain"));

        Assert.Equal("plain", Assert.IsType<TextContentBlock>(Assert.Single(call.Content)).Text);
    }

    [Fact]
    public void A_link_follows_each_version_block()
    {
        var result = ShellResult.Ok("x") with { Versions = [new NoteVersion("/V/a.md", "t1"), new NoteVersion("/V/b.md", "t2")] };

        var call = ToolResult.From(result, p => "https://h/n/id" + p[2..]);

        Assert.Equal(
            ["x", "\nversion of /V/a.md: t1", "\nlink to /V/a.md: https://h/n/id/a.md", "\nversion of /V/b.md: t2", "\nlink to /V/b.md: https://h/n/id/b.md"],
            call.Content.Select(c => Assert.IsType<TextContentBlock>(c).Text));
    }

    [Fact]
    public void No_link_block_when_linkOf_gives_null()
    {
        var result = ShellResult.Ok("x") with { Version = new NoteVersion("/V/a.md", "t1") };

        Assert.Equal(2, ToolResult.From(result, _ => null).Content.Count);
    }

    [Fact]
    public void No_link_block_without_linkOf()
    {
        var result = ShellResult.Ok("x") with { Version = new NoteVersion("/V/a.md", "t1") };

        Assert.Equal(2, ToolResult.From(result).Content.Count);
    }

    private static ShellResult Versions(int count) =>
        ShellResult.Ok("x") with { Versions = [.. Enumerable.Range(1, count).Select(n => new NoteVersion($"/V/n{n}.md", $"t{n}"))] };

    private static string[] Texts(CallToolResult call) => [.. call.Content.OfType<TextContentBlock>().Select(b => b.Text)];

    [Fact]
    public void Links_stop_at_ten_and_the_rest_are_counted()
    {
        var call = ToolResult.From(Versions(12), path => $"https://x/{path}");

        var texts = Texts(call);
        Assert.Equal(12, texts.Count(t => t.StartsWith("\nversion of ", StringComparison.Ordinal)));
        Assert.Equal(10, texts.Count(t => t.StartsWith("\nlink to ", StringComparison.Ordinal)));
        Assert.Equal("\n…and 2 more links: cat a note for its link", texts[^1]);
    }

    [Fact]
    public void Exactly_ten_links_need_no_note_about_more()
    {
        var texts = Texts(ToolResult.From(Versions(10), path => $"https://x/{path}"));

        Assert.Equal(10, texts.Count(t => t.StartsWith("\nlink to ", StringComparison.Ordinal)));
        Assert.DoesNotContain(texts, t => t.Contains("more links", StringComparison.Ordinal));
    }

    [Fact]
    public void A_version_without_a_link_does_not_count_towards_the_ten()
    {
        // Ten linkless versions first, then eleven with a link: one link is left out.
        var result = ShellResult.Ok("x") with
        {
            Versions = [.. Enumerable.Range(1, 21).Select(n => new NoteVersion($"/V/n{n}.md", $"t{n}"))],
        };

        var texts = Texts(ToolResult.From(result, path => int.Parse(path[(path.LastIndexOf('n') + 1)..^3]) <= 10 ? null : $"https://x/{path}"));

        Assert.Equal(10, texts.Count(t => t.StartsWith("\nlink to ", StringComparison.Ordinal)));
        Assert.Equal("\n…and 1 more link: cat a note for its link", texts[^1]);
    }
}
