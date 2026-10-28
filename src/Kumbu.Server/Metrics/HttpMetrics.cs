// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.AspNetCore.Routing;
using Prometheus;

namespace Kumbu.Server.Metrics;

public static class HttpMetrics
{
    /// <summary>
    /// Request counts and durations by route template (the endpoint label is the pattern, never the path) and status
    /// class, plus a counter of 429s. Place it after routing, so the endpoint is known and CallerMiddleware's answers count.
    /// </summary>
    public static IApplicationBuilder UseKumbuHttpMetrics(this IApplicationBuilder app)
    {
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
