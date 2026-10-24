// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Kumbu.Server.Accounts;
using Kumbu.Server.Vaults;

namespace Kumbu.Server.Api;

/// <summary>Every error the REST API returns: one sentence for a person to read.</summary>
public sealed record ApiError(string Error);

/// <summary>Thrown by a handler to answer with <see cref="Status"/> and an <see cref="ApiError"/>; turned into
/// the response by <see cref="ApiErrors.Filter"/>, so handlers can keep their typed success results.</summary>
public sealed class ApiException(int status, string message, TimeSpan? retryAfter = null) : Exception(message)
{
    public int Status { get; } = status;

    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public static class ApiErrors
{
    public static ApiException NotFound(string message) => new(StatusCodes.Status404NotFound, message);

    public static ApiException BadRequest(string message) => new(StatusCodes.Status400BadRequest, message);

    public static ApiException TooMany(TimeSpan retryAfter) =>
        new(StatusCodes.Status429TooManyRequests, "Too many attempts; try again in a minute.", retryAfter);

    /// <summary>
    /// The endpoint filter of every <c>/api</c> group: the refusals the account and vault services throw become
    /// statuses. Only these: anything else is a fault and stays a 500, without its message.
    /// </summary>
    public static async ValueTask<object?> Filter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (ApiException e)
        {
            if (e.RetryAfter is { } retryAfter) SetRetryAfter(context.HttpContext, retryAfter);
            return Result(e.Status, e.Message);
        }
        catch (NotPermittedException e)
        {
            return Result(StatusCodes.Status403Forbidden, e.Message);
        }
        catch (Exception e) when (e is UsernameTakenException or ApiKeyNameTakenException or PrimaryAdminExistsException or VaultNameTakenException)
        {
            return Result(StatusCodes.Status409Conflict, e.Message);
        }
        catch (InvitationUnusableException e)
        {
            return Result(StatusCodes.Status400BadRequest, e.Message);
        }
        // Exactly ArgumentException: the services throw it for input they refuse, with a sentence meant for the
        // user. Its subclasses (ArgumentNullException, ...) come from bugs and say nothing a user could act on.
        catch (ArgumentException e) when (e.GetType() == typeof(ArgumentException))
        {
            return Result(StatusCodes.Status400BadRequest, Sentence(e));
        }
    }

    public static IResult Result(int status, string message) => TypedResults.Json(new ApiError(message), statusCode: status);

    public static void SetRetryAfter(HttpContext context, TimeSpan retryAfter) =>
        context.Response.Headers.RetryAfter = ((int)Math.Ceiling(Math.Max(1, retryAfter.TotalSeconds))).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A body that can't be bound (not JSON, a required field missing or null, no body at all) is refused by
    /// minimal APIs before any filter runs; <c>ThrowOnBadRequest</c> makes it an exception, caught here, so
    /// <c>/api</c> answers it in its own shape. Elsewhere (MCP) only the status is kept.
    /// </summary>
    public static async Task BadRequestsAsync(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (BadHttpRequestException e) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = e.StatusCode;
            if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            {
                // Not e.Message: it names .NET parameter types, which tell a client nothing useful.
                await context.Response.WriteAsJsonAsync(new ApiError(e.StatusCode == StatusCodes.Status415UnsupportedMediaType
                    ? "Send the body as JSON (Content-Type: application/json)."
                    : "The request body is missing, not JSON, or lacks a required field."));
            }
        }
    }

    /// <summary>The exception's message without the " (Parameter 'x')" that .NET appends.</summary>
    private static string Sentence(ArgumentException e) =>
        e.ParamName is { } name ? e.Message.Replace($" (Parameter '{name}')", "", StringComparison.Ordinal) : e.Message;
}
