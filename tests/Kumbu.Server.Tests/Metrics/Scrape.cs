// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;

namespace Kumbu.Server.Tests.Metrics;

/// <summary>Reads the metrics port of a test host the way Prometheus does.</summary>
public static class Scrape
{
    public static async Task<string> TextAsync(KumbuAppFactory app)
    {
        var server = app.Services.GetRequiredService<Kumbu.Server.Metrics.MetricsServer>();
        using var http = new HttpClient();
        return await http.GetStringAsync(new Uri(server.Address!, "metrics"), TestContext.Current.CancellationToken);
    }

    /// <summary>The sample value of an exact series line such as <c>kumbu_vault_unlocks_total</c>; 0 if absent.</summary>
    public static double Value(string text, string series)
    {
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith(series + " ", StringComparison.Ordinal))
            {
                return double.Parse(line[(series.Length + 1)..].Trim().Split(' ')[0], CultureInfo.InvariantCulture);
            }
        }

        return 0;
    }
}
