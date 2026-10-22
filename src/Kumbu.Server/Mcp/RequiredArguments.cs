// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kumbu.Server.Mcp;

/// <summary>
/// Answers a call that lacks a required argument like every other error, with the next step, instead of
/// the SDK's generic "An error occurred invoking 'edit'". Reads the tool's own input schema, so a new tool
/// is covered without listing its parameters here.
/// </summary>
public static class RequiredArguments
{
    private static readonly Dictionary<string, string> Hints = new(StringComparer.Ordinal)
    {
        ["version"] = "get it from 'cat <path>' (it comes as a separate block)",
        ["path"] = "an absolute path like /Work/plan.md",
        ["command"] = "a command line like ls /",
        ["content"] = "the note's full Markdown text",
        ["new_text"] = "the replacement text, or the text to append",
    };

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, ct) =>
        {
            if (request.MatchedPrimitive is McpServerTool tool && Missing(tool.ProtocolTool.InputSchema, request.Params?.Arguments) is { } name)
            {
                var hint = Hints.TryGetValue(name, out var h) ? $"; {h}" : "";
                return new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = $"{tool.ProtocolTool.Name}: {name} is required{hint}" }],
                };
            }

            return await next(request, ct);
        };

    /// <summary>The first required property that is absent or null, or null if all are there.</summary>
    public static string? Missing(JsonElement schema, IDictionary<string, JsonElement>? arguments)
    {
        if (!schema.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in required.EnumerateArray())
        {
            var name = item.GetString();
            if (name is null) continue;
            if (arguments is null || !arguments.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null) return name;
        }

        return null;
    }
}
