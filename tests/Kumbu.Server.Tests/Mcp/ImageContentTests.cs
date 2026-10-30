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
}
