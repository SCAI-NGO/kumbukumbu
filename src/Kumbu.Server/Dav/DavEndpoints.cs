// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Xml.Linq;
using Kumbu.Core.Notes;
using Kumbu.Server.Api;
using Kumbu.Server.Vaults;
using Microsoft.Net.Http.Headers;

namespace Kumbu.Server.Dav;

/// <summary>
/// Everything under <c>/dav</c> (WebDAV design §3): one endpoint for the methods WebDAV has, which dispatches by method.
/// <see cref="Auth.CallerMiddleware"/> has run first: it lets <c>OPTIONS</c> through without a caller and everything
/// else only with a share's. <c>OPTIONS</c> is answered before anything else, the same on every path, so it touches no
/// store and needs no caller. The methods that write are <see cref="DavWrites"/>'s; one WebDAV doesn't have is a 405
/// with the methods it does. Every answer carries <see cref="Security.FileContentPolicy"/>, as an attachment's bytes on
/// <c>/api/files</c> do (brief open point 1): what a share serves is a file's, and a file opened in a tab runs nothing
/// at the app's origin, whatever its name or bytes.
/// </summary>
public static class DavEndpoints
{
    public static IEndpointRouteBuilder MapDav(this IEndpointRouteBuilder app)
    {
        // The pattern matches /dav itself too, so the web app never answers there.
        app.MapMethods(DavPaths.Prefix + "/{**path}", DavMethod.All, DispatchAsync).WithMetadata(FileContentMetadata.Instance).ExcludeFromDescription();
        // Any other method, after the ones above (a higher order loses when both match).
        app.Map(DavPaths.Prefix + "/{**path}", (HttpContext http) =>
        {
            http.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            http.Response.Headers.Allow = DavHeaders.DavMethods;
        }).WithOrder(1).WithMetadata(FileContentMetadata.Instance).ExcludeFromDescription();
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

            if (await Tree(http).ResolveAsync(target.Path, ct) is var resolved && resolved != target.Path)
            {
                target = target with { Path = resolved, Asked = target.Path };
            }

            switch (request.Method)
            {
                case DavMethod.Propfind:
                    await PropfindAsync(http, target, ct);
                    break;
                case DavMethod.Proppatch:
                    await ProppatchAsync(http, target, ct);
                    break;
                case DavMethod.Get or DavMethod.Head:
                    await GetAsync(http, target, ct);
                    break;
                case DavMethod.Put:
                    await Writes(http).PutAsync(target, ct);
                    break;
                case DavMethod.Mkcol:
                    await Writes(http).MkcolAsync(target, ct);
                    break;
                case DavMethod.Delete:
                    await Writes(http).DeleteAsync(target, ct);
                    break;
                case DavMethod.Copy or DavMethod.Move:
                    await Writes(http).TransferAsync(target, move: request.Method == DavMethod.Move, ct);
                    break;
                case DavMethod.Lock:
                    await Writes(http).LockAsync(target, ct);
                    break;
                case DavMethod.Unlock:
                    await Writes(http).UnlockAsync(ct);
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
        // A published note a write would trash (a DELETE, a COPY over it): a share never may withdraw one (publish
        // design §3.8), and the store refuses the whole batch. WebDAV design §3.6 case 6 holds such a note instead.
        catch (Kumbu.Core.Publishing.PublishedNoteException e) when (!response.HasStarted)
        {
            await DavXml.WriteTextAsync(response, StatusCodes.Status403Forbidden, e.Message, ct);
        }
        catch (VaultDeletedException e) when (!response.HasStarted)
        {
            await DavXml.WriteTextAsync(response, StatusCodes.Status404NotFound, e.Message, ct);
        }
        // An attachment's bytes are gone or don't open: the server's fault, not a deletion, as on /api/files.
        catch (ContentMissingException e) when (!response.HasStarted)
        {
            await DavXml.WriteTextAsync(response, StatusCodes.Status500InternalServerError, e.Message, ct);
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
        var properties = new DavProperties(DavCaller.Of(http), http.RequestServices.GetRequiredService<DavLocks>());
        var responses = new List<DavResponse>(resources.Count);
        foreach (var r in resources)
        {
            // The resource asked about answers under the name it was asked by (Plan.MD), what is below it under its own.
            var href = r == resource && target.Asked is { } asked ? DavPaths.Href(asked, r.IsCollection) : r.Href;
            responses.Add(DavXml.Response(href, await properties.PropertiesAsync(r, ct), propfind));
        }
        await DavXml.WriteMultistatusAsync(http.Response, responses, ct);
    }

    /// <summary>
    /// <c>PROPPATCH</c> (RFC 4918 §9.2): 207 with every property it sets or removes reported 200, and nothing stored
    /// (WebDAV design §3.4). Windows and Finder set times and properties of their own after a copy, and a refusal breaks
    /// the copy. A share that may only read may ask too: it changes nothing.
    /// </summary>
    private static async Task ProppatchAsync(HttpContext http, DavTarget target, CancellationToken ct)
    {
        var names = DavXml.ParsePropertyUpdate(await DavXml.ReadBodyAsync(http.Request, ct));
        var resource = await Tree(http).FindAsync(target.Path, ct) ?? throw NotFound();
        await DavXml.WriteMultistatusAsync(
            http.Response, [new DavResponse(resource.Href, [(StatusCodes.Status200OK, [.. names.Select(n => new XElement(n))])])], ct);
    }

    /// <summary><c>GET</c> and <c>HEAD</c> of a note or an attachment; a collection has no bytes to give (405).</summary>
    private static async Task GetAsync(HttpContext http, DavTarget target, CancellationToken ct)
    {
        var tree = Tree(http);
        if (await tree.ContentAsync(target.Path, ct) is { } content)
        {
            await ServeAsync(http, content);
            return;
        }

        if (await tree.FindAsync(target.Path, ct) is not { IsCollection: true }) throw NotFound();
        http.Response.Headers.Allow = DavHeaders.CollectionMethods;
        await DavXml.WriteTextAsync(http.Response, StatusCodes.Status405MethodNotAllowed, "A folder has no content to read; PROPFIND lists it.", ct);
    }

    /// <summary>
    /// The one place a <c>GET</c> or <c>HEAD</c> under <c>/dav</c> is answered. The headers are those of
    /// <c>GET /api/files</c> for the same file: the type the server decided, <see cref="FileApi.Disposition"/>, and
    /// (through the endpoint's <see cref="FileContentMetadata"/>) <see cref="Security.FileContentPolicy"/> and
    /// <c>nosniff</c>; then the <c>ETag</c> of the version and <c>Last-Modified</c>. <c>If-None-Match</c> naming the
    /// version (weakly compared, RFC 9110 §13.1.2) or <c>*</c> is a 304. <c>HEAD</c> gets the length and no bytes, and
    /// never opens them.
    /// </summary>
    public static async Task ServeAsync(HttpContext http, DavContent content)
    {
        var (request, response) = (http.Request, http.Response);
        var etag = new EntityTagHeaderValue($"\"{content.Version}\"");
        var headers = response.GetTypedHeaders();
        response.Headers.ContentDisposition = FileApi.Disposition(content.Name, content.Download);
        headers.ETag = etag;
        headers.LastModified = new DateTimeOffset(DateTime.SpecifyKind(content.LastWrite, DateTimeKind.Utc));

        if (EntityTagHeaderValue.TryParseList(request.Headers.IfNoneMatch, out var tags)
            && tags.Any(t => t.Equals(EntityTagHeaderValue.Any) || t.Compare(etag, useStrongComparison: false)))
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = content.ContentType;
        response.ContentLength = content.Length;
        if (HttpMethods.IsHead(request.Method)) return;

        await using var bytes = await content.Open(http.RequestAborted);
        await bytes.CopyToAsync(response.Body, http.RequestAborted);
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

    /// <summary>The share's tree through the request's store, with what it parked, resolved only now: <c>OPTIONS</c> has
    /// no caller to open it.</summary>
    private static DavTree Tree(HttpContext http)
    {
        var store = http.RequestServices.GetRequiredService<EncryptedNoteStore>();
        return new DavTree(store, store, DavCaller.Of(http), http.RequestServices.GetRequiredService<DavParking>());
    }

    private static DavWrites Writes(HttpContext http) =>
        new(http, DavCaller.Of(http), Tree(http), http.RequestServices.GetRequiredService<DavParking>(), http.RequestServices.GetRequiredService<DavLocks>(),
            http.RequestServices.GetRequiredService<NoteWriter>(), http.RequestServices.GetRequiredService<TreeWriter>(), http.RequestServices.GetRequiredService<DavHolds>());

    private static DavException NotFound() => new(StatusCodes.Status404NotFound, "Not found.");
}
