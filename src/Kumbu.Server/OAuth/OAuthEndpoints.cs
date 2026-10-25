// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Nodes;
using Kumbu.Server.Api;

namespace Kumbu.Server.OAuth;

/// <summary>The authorisation server's endpoints (spec §4). CORS is applied by <see cref="OAuthCors"/>.</summary>
public static class OAuthEndpoints
{
    /// <summary>Registration metadata is a few hundred bytes; more is not a client.</summary>
    public const int MaxRegistrationBytes = 16 * 1024;

    public static void MapOAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/oauth/register", RegisterAsync).AllowAnonymous().ExcludeFromDescription();
    }

    private static async Task<IResult> RegisterAsync(HttpContext http, OAuthClients clients, OAuthLimits limits, TimeProvider time, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (limits.Registration(http) is { } retryAfter)
        {
            ApiErrors.SetRetryAfter(http, retryAfter);
            return Error(StatusCodes.Status429TooManyRequests, "invalid_client_metadata", "Too many registrations from this network; try again later.");
        }

        var body = await ReadBodyAsync(http.Request, ct);
        if (body is null) return Error(StatusCodes.Status413PayloadTooLarge, "invalid_client_metadata", "The request body is too large.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_client_metadata", "The body must be a JSON object.");
        }

        using (document)
        {
            OAuthClient client;
            try
            {
                client = await clients.RegisterAsync(document.RootElement, ct);
            }
            catch (RegistrationException e)
            {
                return Error(StatusCodes.Status400BadRequest, e.Code, e.Message);
            }

            return TypedResults.Json(new JsonObject
            {
                ["client_id"] = client.ClientId,
                ["client_id_issued_at"] = time.GetUtcNow().ToUnixTimeSeconds(),
                ["client_name"] = client.Name,
                ["redirect_uris"] = new JsonArray(client.RedirectUris.Select(u => (JsonNode)JsonValue.Create(u)!).ToArray()),
                ["grant_types"] = new JsonArray("authorization_code", "refresh_token"),
                ["response_types"] = new JsonArray("code"),
                ["token_endpoint_auth_method"] = "none",
            }, statusCode: StatusCodes.Status201Created);
        }
    }

    /// <summary>The whole body, or null if it exceeds <see cref="MaxRegistrationBytes"/>; read in pieces so a body without Content-Length is capped too.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength > MaxRegistrationBytes) return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxRegistrationBytes) return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    // RFC 7591 §3.2.2 / RFC 6749 §5.2: {"error", "error_description"}.
    private static IResult Error(int status, string error, string description) =>
        TypedResults.Json(new { error, error_description = description }, statusCode: status);
}
