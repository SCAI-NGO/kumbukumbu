// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Kumbu.Server.Auth;

/// <summary>
/// Fails closed: every request needs the static dev bearer token (<c>Kumbu:DevToken</c>), except
/// <c>/healthz</c> (matched case-insensitively). Guarding only <c>/mcp</c> would be bypassable, because
/// endpoint routing matches literal segments case-insensitively (<c>/MCP</c> reaches <c>MapMcp("/mcp")</c>).
/// Runs before the MCP SDK sees the request; never logs the token. Replaced by API keys later.
/// </summary>
public sealed class DevTokenMiddleware(RequestDelegate next, IOptions<KumbuOptions> options)
{
    private const string Scheme = "Bearer ";

    private readonly byte[] _expected = Encoding.UTF8.GetBytes(options.Value.DevToken);

    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.Equals("/healthz", StringComparison.OrdinalIgnoreCase) || IsAuthorized(context.Request))
        {
            return next(context);
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }

    private bool IsAuthorized(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var presented = Encoding.UTF8.GetBytes(header[Scheme.Length..]);
        return CryptographicOperations.FixedTimeEquals(presented, _expected);
    }
}
