// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Attachments;
using Kumbu.Server.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kumbu.Server.Tests.Metrics;

/// <summary>Sets environment variables, which every host of the process reads: so it runs on its own.</summary>
[CollectionDefinition("environment", DisableParallelization = true)]
public sealed class EnvironmentCollection;

[Collection("environment")]
public sealed class MetricsServerConfigurationTests
{
    // The app's own Kestrel endpoints (configured for its port) must not move the metrics listener onto them.
    [Fact]
    public async Task The_metrics_server_listens_on_its_url_whatever_kestrel_is_configured_with()
    {
        const string endpoint = "Kestrel__Endpoints__Main__Url";
        Environment.SetEnvironmentVariable(endpoint, "http://127.0.0.2:0");
        var options = Options.Create(new KumbuOptions { Metrics = { Url = "http://127.0.0.1:0" } });
        var server = new MetricsServer(options, new SidecarPeers(options, NullLogger<SidecarPeers>.Instance, TimeProvider.System));
        try
        {
            await server.StartAsync(TestContext.Current.CancellationToken);

            Assert.Equal("127.0.0.1", server.Address!.Host);
            using var http = new HttpClient();
            Assert.Contains("# TYPE", await http.GetStringAsync(new Uri(server.Address, "metrics"), TestContext.Current.CancellationToken));
        }
        finally
        {
            Environment.SetEnvironmentVariable(endpoint, null);
            await server.StopAsync(CancellationToken.None);
        }
    }
}
