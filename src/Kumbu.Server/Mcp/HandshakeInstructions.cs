// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Nodes;
using Kumbu.Core.Notes;
using Kumbu.Core.Sh;
using Kumbu.Core.Skills;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Kumbu.Server.Vaults;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kumbu.Server.Mcp;

/// <summary>How many skills sections were built: per app, so a test sees that no request but a handshake builds one.</summary>
internal sealed class HandshakeCounter
{
    private int _builds;

    public int Builds => Volatile.Read(ref _builds);

    public void Built() => Interlocked.Increment(ref _builds);
}

/// <summary>
/// The instructions per connection (Decision 1): the answer to <c>initialize</c> (revisions before 2026-07-28) or
/// <c>server/discover</c> gets <see cref="ServerInstructions.Text"/> plus the skills section of what the caller's
/// credential may read, in its <see cref="InstructionsMode"/>. Only that answer: no other method lists skills or opens
/// a vault for it. A snapshot of the handshake; later changes reach the agent through <c>skills ls</c>.
/// </summary>
internal static class HandshakeInstructions
{
    private const string HandshakeId = "kumbu.mcp.handshake";

    /// <summary>Notes the id of an <c>initialize</c> or <c>server/discover</c> request.</summary>
    public static McpMessageHandler Incoming(McpMessageHandler next) => async (context, ct) =>
    {
        // HttpContext.Items, not the message's own Items: the outgoing answer is another message with its own context.
        // The transport is stateless, so one HTTP request carries exactly one JSON-RPC request and its response; an
        // item set here belongs to that exchange and can't leak to another request.
        if (context.JsonRpcMessage is JsonRpcRequest { Method: RequestMethods.Initialize or RequestMethods.ServerDiscover } request
            && Http(context) is { } http)
        {
            http.Items[HandshakeId] = request.Id;
        }

        await next(context, ct);
    };

    /// <summary>For the answer to the noted request: <c>result.instructions</c> with the skills section.</summary>
    public static McpMessageHandler Outgoing(McpMessageHandler next) => async (context, ct) =>
    {
        if (context.JsonRpcMessage is JsonRpcResponse { Result: JsonObject result } response
            && Http(context) is { } http && http.Items.TryGetValue(HandshakeId, out var id) && id is RequestId handshake && handshake == response.Id)
        {
            http.Items.Remove(HandshakeId);
            result["instructions"] = await TextAsync(http.RequestServices, ct);
        }

        await next(context, ct);
    };

    private static HttpContext? Http(MessageContext context) =>
        (context.Services ?? context.Server.Services)?.GetService<IHttpContextAccessor>()?.HttpContext;

    private static async Task<string> TextAsync(IServiceProvider services, CancellationToken ct)
    {
        try
        {
            var caller = services.GetRequiredService<RequestCaller>();
            var mode = caller.Credential switch
            {
                ApiKeyCredential key => await services.GetRequiredService<ApiKeys>().InstructionsAsync(key.KeyId, ct),
                ConnectionCredential connection => await services.GetRequiredService<Connections>().InstructionsAsync(connection.ConnectionId, ct),
                _ => InstructionsMode.ListAndHint,
            };
            IReadOnlyList<Skill> skills = mode == InstructionsMode.HintOnly
                ? []
                : await new SkillDirectory(services.GetRequiredService<INoteStore>(), services.GetRequiredService<IWriterLabels>()).ListAsync(null, ct);
            services.GetRequiredService<HandshakeCounter>().Built();
            return ServerInstructions.Text + "\n\n" + SkillInstructions.Section(mode, skills);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The type only: a message could carry a vault's or a skill's name.
            services.GetService<ILoggerFactory>()?.CreateLogger(typeof(HandshakeInstructions).FullName!)
                .LogWarning("The skills section of the instructions failed ({Type}); the instructions go without it", e.GetType().Name);
            return ServerInstructions.Text;
        }
    }
}
