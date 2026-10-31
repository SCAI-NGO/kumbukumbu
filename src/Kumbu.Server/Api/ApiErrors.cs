// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.Json.Serialization;
using Kumbu.Server.Accounts;
using Kumbu.Server.Vaults;

namespace Kumbu.Server.Api;

/// <summary>Every error the REST API returns: one sentence for a person to read. <c>type</c>, only on errors a client
/// answers by itself: <c>password-required</c> (<see cref="ApiErrors.PasswordRequired"/>). <c>code</c>, only on a
/// refused write: why it was refused (<see cref="Kumbu.Core.Notes.Refusals"/>: <c>read-only</c>, <c>structural</c>, …).</summary>
public sealed record ApiError(
    string Error,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Type = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Code = null);

/// <summary>Thrown by a handler to answer with <see cref="Status"/> and an <see cref="ApiError"/>; turned into
/// the response by <see cref="ApiErrors.Filter"/>, so handlers can keep their typed success results.</summary>
public sealed class ApiException(int status, string message, TimeSpan? retryAfter = null, string? type = null, string? code = null) : Exception(message)
{
    public int Status { get; } = status;

    public TimeSpan? RetryAfter { get; } = retryAfter;

    /// <summary>The <see cref="ApiError.Type"/> of the answer, if any.</summary>
    public string? Type { get; } = type;

    /// <summary>The <see cref="ApiError.Code"/> of the answer, if any.</summary>
    public string? Code { get; } = code;
}

public static class ApiErrors
{
    /// <summary>A remembered session whose normal window is closed asked for something that needs the password lately
    /// given (<see cref="Security.RequirePasswordWindow"/>): a 401 that doesn't end the session; the web app asks for the
    /// password, confirms it and repeats the request.</summary>
    public const string PasswordRequired = "password-required";

    public static ApiException NotFound(string message) => new(StatusCodes.Status404NotFound, message);

    public static ApiException BadRequest(string message) => new(StatusCodes.Status400BadRequest, message);

    public static ApiException TooMany(TimeSpan retryAfter, string message = "Too many attempts; try again in a minute.") =>
        new(StatusCodes.Status429TooManyRequests, message, retryAfter);

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
            return TypedResults.Json(new ApiError(e.Message, e.Type, e.Code), statusCode: e.Status);
        }
        catch (CredentialsChangedException e)
        {
            return Result(StatusCodes.Status401Unauthorized, e.Message);
        }
        catch (NotPermittedException e)
        {
            return Result(StatusCodes.Status403Forbidden, e.Message);
        }
        catch (Exception e) when (e is UsernameTakenException or ApiKeyNameTakenException or ConnectionNameTakenException or PrimaryAdminExistsException or VaultNameTakenException
            or RotationConflictException or RecoveryConflictException or PasswordConflictException)
        {
            return Result(StatusCodes.Status409Conflict, e.Message);
        }
        catch (InvitationUnusableException e)
        {
            return Result(StatusCodes.Status400BadRequest, e.Message);
        }
        catch (Kumbu.Server.Vaults.VaultDeletedException e)
        {
            return Result(StatusCodes.Status404NotFound, e.Message);
        }
        // Only the busy vault of a re-keying: every other VaultUnavailableException keeps its mapping.
        catch (Kumbu.Server.Vaults.VaultBusyException e)
        {
            SetRetryAfter(context.HttpContext, BusyRetryAfter);
            return Result(StatusCodes.Status503ServiceUnavailable, e.Message);
        }
        // An attachment's bytes are gone or don't open: the server's fault, not a deletion (a client reads 404 as one).
        catch (Kumbu.Server.Vaults.ContentMissingException e)
        {
            return Result(StatusCodes.Status500InternalServerError, e.Message);
        }
        // A vault this user key can't re-key (not the user's own, or a dev-token vault): the request is at fault.
        catch (Kumbu.Server.Vaults.VaultNotRekeyableException e)
        {
            return Result(StatusCodes.Status400BadRequest, e.Message);
        }
        // Exactly ArgumentException: the services throw it for input they refuse, with a sentence meant for the
        // user. Its subclasses (ArgumentNullException, ...) come from bugs and say nothing a user could act on.
        catch (ArgumentException e) when (e.GetType() == typeof(ArgumentException))
        {
            return Result(StatusCodes.Status400BadRequest, Sentence(e));
        }
        // Counted before the answer, whatever happens to the request now: a remembered session ends at the fifth in a row.
        catch (WrongPasswordException e)
        {
            await AccountApi.PasswordWrongAsync(context.HttpContext, CancellationToken.None);
            return Result(StatusCodes.Status400BadRequest, e.Message);
        }
    }

    /// <summary>Declares the error responses in the OpenAPI document, all in the one <see cref="ApiError"/> shape.</summary>
    internal static RouteHandlerBuilder Errors(this RouteHandlerBuilder builder, params int[] statuses)
    {
        foreach (var status in statuses) builder.Produces<ApiError>(status);
        return builder;
    }

    /// <summary>How long a client waits before asking a vault being re-encrypted again.</summary>
    internal static readonly TimeSpan BusyRetryAfter = TimeSpan.FromSeconds(5);

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
                await context.Response.WriteAsJsonAsync(new ApiError(e.StatusCode switch
                {
                    StatusCodes.Status415UnsupportedMediaType => "Send the body as JSON (Content-Type: application/json).",
                    StatusCodes.Status413PayloadTooLarge when context.Request.Path.StartsWithSegments("/api/upload", StringComparison.OrdinalIgnoreCase) => UploadApi.Limits,
                    StatusCodes.Status413PayloadTooLarge when context.Request.Path.StartsWithSegments("/api/files", StringComparison.OrdinalIgnoreCase) => FileApi.TooLarge,
                    StatusCodes.Status413PayloadTooLarge => "The request body is too large.",
                    _ => "The request body is missing, not JSON, or lacks a required field.",
                }));
            }
        }
    }

    /// <summary>The exception's message without the " (Parameter 'x')" that .NET appends.</summary>
    private static string Sentence(ArgumentException e) =>
        e.ParamName is { } name ? e.Message.Replace($" (Parameter '{name}')", "", StringComparison.Ordinal) : e.Message;
}
