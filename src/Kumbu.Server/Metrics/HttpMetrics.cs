// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.AspNetCore.Routing;
using Prometheus;

namespace Kumbu.Server.Metrics;

public static class HttpMetrics
{
    private static readonly HashSet<string> Methods = new(StringComparer.Ordinal) { "GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS" };

    /// <summary>
    /// Request counts and durations by route template (the endpoint label is the pattern, never the path) and status
    /// class, plus a counter of 429s. Place it after routing, so the endpoint is known and CallerMiddleware's answers count.
    /// </summary>
    public static IApplicationBuilder UseKumbuHttpMetrics(this IApplicationBuilder app)
    {
        // prometheus-net labels by Request.Method and Kestrel accepts any token as one, so a client could mint series
        // without bound; the app maps no other methods, so they are refused before they reach the metrics.
        app.Use(async (context, next) =>
        {
            if (!Methods.Contains(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }

            await next();
        });
        app.UseHttpMetrics(o => o.ReduceStatusCodeCardinality());
        return app.Use(async (context, next) =>
        {
            await next();
            if (context.Response.StatusCode == StatusCodes.Status429TooManyRequests)
            {
                var endpoint = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "none";
                KumbuMetrics.RateLimited.WithLabels(endpoint).Inc();
            }
        });
    }
}
