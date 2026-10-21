// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Kumbu.Server.Auth;

/// <summary>
/// Guards <c>/mcp</c> with the static dev bearer token (<c>Kumbu:DevToken</c>). Replaced by API keys later.
/// Runs before the MCP SDK sees the request; never logs the token.
/// </summary>
public sealed class DevTokenMiddleware(RequestDelegate next, IOptions<KumbuOptions> options)
{
    private const string Scheme = "Bearer ";

    private readonly byte[] _expected = Encoding.UTF8.GetBytes(options.Value.DevToken);

    public Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/mcp", StringComparison.Ordinal) || IsAuthorized(context.Request))
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
