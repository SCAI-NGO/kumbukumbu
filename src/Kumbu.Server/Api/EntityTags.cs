// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.Net.Http.Headers;

namespace Kumbu.Server.Api;

/// <summary>Conditional requests against an entity tag.</summary>
internal static class EntityTags
{
    /// <summary>
    /// Whether <c>If-None-Match</c> names <paramref name="etag"/> or is <c>*</c>. Compared weakly (RFC 9110 §8.8.3.2,
    /// §13.1.2): a proxy that compresses the answer hands the tag on as <c>W/"…"</c>, and the client sends that back.
    /// </summary>
    public static bool NoneMatchNames(HttpRequest request, EntityTagHeaderValue etag) =>
        EntityTagHeaderValue.TryParseList(request.Headers.IfNoneMatch, out var tags)
        && tags.Any(t => t.Equals(EntityTagHeaderValue.Any) || t.Compare(etag, useStrongComparison: false));
}
