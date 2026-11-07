// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Api;
using Kumbu.Server.Vaults;

namespace Kumbu.Server.Dav;

/// <summary>
/// Everything under <c>/dav</c> (WebDAV design §3): one endpoint for the methods WebDAV has, which dispatches by method.
/// <see cref="Auth.CallerMiddleware"/> has run first: it lets <c>OPTIONS</c> through without a caller and everything
/// else only with a share's. <c>OPTIONS</c> is answered before anything else, the same on every path, so it touches no
/// store and needs no caller. A method that comes in a later step answers 501 until then; one WebDAV doesn't have, 405
/// with the methods it does.
/// </summary>
public static class DavEndpoints
{
    public static IEndpointRouteBuilder MapDav(this IEndpointRouteBuilder app)
    {
        // The pattern matches /dav itself too, so the web app never answers there.
        app.MapMethods(DavPaths.Prefix + "/{**path}", DavMethod.All, DispatchAsync).ExcludeFromDescription();
        // Any other method, after the ones above (a higher order loses when both match).
        app.Map(DavPaths.Prefix + "/{**path}", (HttpContext http) =>
        {
            http.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            http.Response.Headers.Allow = DavHeaders.DavMethods;
        }).WithOrder(1).ExcludeFromDescription();
        return app;
    }

    private static async Task DispatchAsync(HttpContext http)
    {
        var (request, response) = (http.Request, http.Response);
        if (request.Method == DavMethod.Options)
        {
            DavHeaders.Options(response, DavHeaders.DavMethods);
            return;
        }

        var ct = http.RequestAborted;
        try
        {
            var target = DavPaths.Parse(request);
            // A client that names the root without its slash gets it with one, for what it reads; the query stays behind,
            // since nothing under /dav reads one. Any other method acts on the root as if the slash were there.
            if (target.Bare && request.Method is DavMethod.Get or DavMethod.Head or DavMethod.Propfind)
            {
                response.StatusCode = StatusCodes.Status308PermanentRedirect;
                response.Headers.Location = DavPaths.Prefix + "/";
                return;
            }

            switch (request.Method)
            {
                case DavMethod.Propfind:
                    await PropfindAsync(http, target, ct);
                    break;
                default:
                    // GET, PUT, DELETE, MKCOL, COPY, MOVE, PROPPATCH, LOCK, UNLOCK: the steps after this one.
                    response.StatusCode = StatusCodes.Status501NotImplemented;
                    break;
            }
        }
        catch (DavException e) when (!response.HasStarted)
        {
            if (e.Body is { } body) await DavXml.WriteAsync(response, e.Status, body, ct);
            else await DavXml.WriteTextAsync(response, e.Status, e.Message, ct);
        }
        catch (VaultBusyException e) when (!response.HasStarted)
        {
            ApiErrors.SetRetryAfter(http, ApiErrors.BusyRetryAfter);
            await DavXml.WriteTextAsync(response, StatusCodes.Status503ServiceUnavailable, e.Message, ct);
        }
        catch (VaultDeletedException e) when (!response.HasStarted)
        {
            await DavXml.WriteTextAsync(response, StatusCodes.Status404NotFound, e.Message, ct);
        }
    }

    /// <summary><c>PROPFIND</c> (RFC 4918 §9.1) with <c>Depth: 0</c> or <c>1</c>. <c>infinity</c>, which a missing header
    /// means, is refused as RFC 4918 lets a server: a share's whole tree in one answer is what a client never needs.</summary>
    private static async Task PropfindAsync(HttpContext http, DavTarget target, CancellationToken ct)
    {
        var depth = Depth(http.Request);
        var propfind = DavXml.ParsePropfind(await DavXml.ReadBodyAsync(http.Request, ct));
        var tree = Tree(http);
        var resource = await tree.FindAsync(target.Path, ct) ?? throw NotFound();
        var resources = depth == 1 && resource.IsCollection ? [resource, .. await tree.ChildrenAsync(resource, ct)] : new List<DavResource> { resource };
        var properties = new DavProperties();
        var responses = new List<DavResponse>(resources.Count);
        foreach (var r in resources) responses.Add(DavXml.Response(r.Href, await properties.PropertiesAsync(r, ct), propfind));
        await DavXml.WriteMultistatusAsync(http.Response, responses, ct);
    }

    /// <summary>The <c>Depth</c> header, 0 or 1; <c>infinity</c> or none is 403 with <c>propfind-finite-depth</c>
    /// (RFC 4918 §9.1), anything else 400.</summary>
    private static int Depth(HttpRequest request)
    {
        var depth = request.Headers["Depth"].ToString().Trim();
        return depth switch
        {
            "0" => 0,
            "1" => 1,
            _ when depth.Length == 0 || depth.Equals("infinity", StringComparison.OrdinalIgnoreCase) =>
                throw new DavException(StatusCodes.Status403Forbidden, "Depth: infinity is not supported.", DavXml.Error("propfind-finite-depth")),
            _ => throw new DavException(StatusCodes.Status400BadRequest, "Depth is 0, 1 or infinity."),
        };
    }

    /// <summary>The share's tree through the request's store, resolved only now: <c>OPTIONS</c> has no caller to open it.</summary>
    private static DavTree Tree(HttpContext http)
    {
        var store = http.RequestServices.GetRequiredService<EncryptedNoteStore>();
        return new DavTree(store, store);
    }

    private static DavException NotFound() => new(StatusCodes.Status404NotFound, "Not found.");
}
